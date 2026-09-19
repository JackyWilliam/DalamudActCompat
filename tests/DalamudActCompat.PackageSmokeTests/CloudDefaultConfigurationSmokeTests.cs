using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Plugin;
using DalamudActCompat.Protocol;
using DalamudActCompat.UI;
using Newtonsoft.Json;

internal static class CloudDefaultConfigurationSmokeTests
{
    public static async Task RunAsync(string root)
    {
        var configRoot = Path.Combine(root, "cloud-defaults");
        var paths = new PluginPaths(Path.Combine(configRoot, "DalamudActCompat"));
        paths.EnsureCreated();
        var mainFile = Path.Combine(configRoot, "DalamudActCompat.json");
        var backups = new PortableConfigurationBackupService();
        var key = backups.GenerateRecoveryKey();
        var original = new CloudBackupVersion("existing-custom-backup", DateTimeOffset.UtcNow.AddDays(-1), 100, "test-sha", "old-content");
        using var handler = new BackupHandler(original);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.invalid/") };
        using var api = new CloudApiClient(http);
        using var service = new CloudClientService(paths, api, new(paths.CloudCredentialFile),
            new(paths.CloudBanFile), new(paths.CloudDeviceFile), new(), backups);
        // Use the real service/API boundary with an in-memory server; never log in
        // to a live account or let background heartbeat traffic affect the assertion.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var credentials = new CloudStoredCredentials("fixture", "fixture-token", DateTimeOffset.UtcNow.AddDays(1), key);
        typeof(CloudClientService).GetField("credentials", flags)!.SetValue(service, credentials);
        typeof(CloudClientService).GetField("snapshot", flags)!.SetValue(service,
            CloudClientSnapshot.SignedOut() with { IsSignedIn = true, Username = credentials.Username, Backups = [original] });

        async Task Save(PluginConfiguration value) => await File.WriteAllTextAsync(mainFile,
            JsonConvert.SerializeObject(value, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects }));
        async Task AssertSkipped(string scenario)
        {
            var previous = service.Snapshot.Backups.ToArray();
            var requestCount = handler.RequestCount;
            Check(!await service.AutoUploadIfChangedAsync(paths.ConfigDirectory, default), $"Auto upload accepted {scenario}.");
            Check(!await service.UploadAsync(paths.ConfigDirectory, default), $"Manual upload accepted {scenario}.");
            Check(handler.RequestCount == requestCount && service.Snapshot.Backups.SequenceEqual(previous),
                $"Skipping {scenario} sent a request or replaced the current cloud backup.");
            Check(service.Snapshot is { IsBusy: false, StatusIsError: false } &&
                  service.Snapshot.StatusMessage.Contains("默认配置", StringComparison.Ordinal),
                $"Skipping {scenario} did not explain the result or release the busy state.");
        }

        await Save(new());
        await AssertSkipped("fresh defaults with generated slot IDs");
        var config = new PluginConfiguration();
        config.ApplyMigrations();
        config.LogDirectory = @"D:\AnotherMachine\CombatLogs";
        config.ActPluginDirectory = @"D:\AnotherMachine\ACT";
        await Save(config);
        await AssertSkipped("initialized defaults with local machine paths");
        await File.WriteAllTextAsync(mainFile, "{\"Version\":16}");
        await AssertSkipped("older sparse defaults");
        await Save(config);
        FoxTtsConfigurationDefaults.Ensure(paths.ConfigDirectory);
        await AssertSkipped("first-start FoxTTS configuration");

        config.Appearance.UnlockedEasterEggs.Add(SkinCatalog.NeonPink);
        await Save(config);
        Check(await service.AutoUploadIfChangedAsync(paths.ConfigDirectory, default) && handler.UploadCount == 1,
            "An unlock-only change was incorrectly treated as default.");
        Check(!await service.AutoUploadIfChangedAsync(paths.ConfigDirectory, default) && handler.UploadCount == 1,
            "Existing content-ID deduplication stopped working.");
        config.Appearance.SelectedSkin = SkinCatalog.NeonPink;
        await Save(config);
        Check(await service.UploadAsync(paths.ConfigDirectory, default) && handler.UploadCount == 2,
            "Selected skin preferences were not uploaded.");
        config.ResetToDefaults(config.LogDirectory);
        await Save(config);
        await AssertSkipped("reset defaults after a successful custom upload");
        config.Meter.ClassicWindow.ItemWidth += 12;
        await Save(config);
        Check(await service.UploadAsync(paths.ConfigDirectory, default) && handler.UploadCount == 3,
            "A customized meter was incorrectly treated as default.");
        await Save(new());
        var triggers = Path.Combine(paths.ConfigDirectory, "Config", "Triggernometry.config.xml");
        await File.WriteAllTextAsync(triggers, "<Triggernometry><Trigger Name=\"custom fixture\" /></Triggernometry>");
        Check(await service.AutoUploadIfChangedAsync(paths.ConfigDirectory, default) && handler.UploadCount == 4,
            "Default main configuration hid custom extension settings.");
        File.Delete(triggers);
        await File.WriteAllTextAsync(mainFile, "{\"FutureCustomField\":\"keep-me\"}");
        Check(await service.UploadAsync(paths.ConfigDirectory, default) && handler.UploadCount == 5,
            "Unknown configuration fields were discarded as defaults.");
        var snapshot = await backups.ExportEncryptedAsync(paths.ConfigDirectory,
            Path.Combine(root, "cloud-default-export.enc"), key, default);
        Check(!snapshot.IsDefaultConfiguration, "Unknown data was labeled as a default export.");
        Console.WriteLine("Cloud defaults: fresh/reset/local paths/generated IDs/legacy/FoxTTS skips, no HTTP or backup replacement, unlocks/selection/custom meter/extensions/unknown data uploads and deduplication passed (offline).");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BackupHandler(CloudBackupVersion latest) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public int UploadCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Check(request.RequestUri!.AbsolutePath == "/api/v1/backups", "Unexpected cloud request.");
            if (request.Method == HttpMethod.Post)
            {
                var payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Check(payload.Length > 0 && request.Content.Headers.ContentType!.MediaType == "application/octet-stream",
                    "Upload did not use the encrypted archive transport.");
                UploadCount++;
                latest = new($"upload-{UploadCount}", DateTimeOffset.UtcNow, payload.Length, "fixture-sha",
                    request.Headers.GetValues("X-DACT-Content-Id").Single());
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(latest) };
            }
            Check(request.Method == HttpMethod.Get, "Unexpected backup mutation.");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { backups = new[] { latest } }) };
        }
    }
}
