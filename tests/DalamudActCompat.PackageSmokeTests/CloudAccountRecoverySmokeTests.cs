using System.Net;
using System.Net.Http.Json;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;

internal static class CloudAccountRecoverySmokeTests
{
    public static async Task RunAsync(string root)
    {
        await ValidateCredentialErrorsAsync(root);
        if (Environment.GetEnvironmentVariable("DACT_RECOVERY_TEST_URL") is { Length: > 0 } url)
            await ValidateRealRecoveryAsync(root, url);
        Console.WriteLine("Account recovery smoke tests passed.");
    }

    private static CloudClientService Create(PluginPaths paths, CloudApiClient api, CloudCredentialStore? store = null)
    {
        paths.EnsureCreated();
        return new(paths, api, store ?? new(paths.CloudCredentialFile), new(paths.CloudBanFile),
            new(paths.CloudDeviceFile), new(), new());
    }

    private static async Task ValidateCredentialErrorsAsync(string root)
    {
        var paths = new PluginPaths(Path.Combine(root, "credential-errors")); paths.EnsureCreated();
        var store = new CloudCredentialStore(paths.CloudCredentialFile);
        var saved = new CloudStoredCredentials("original", "existing-token", DateTimeOffset.UtcNow.AddDays(1), new PortableConfigurationBackupService().GenerateRecoveryKey());
        store.Save(saved);
        var before = File.ReadAllBytes(paths.CloudCredentialFile);
        using var handler = new ScriptedHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent.Create(request.RequestUri!.AbsolutePath.EndsWith("/auth/login")
                ? new { error = "invalid_credentials", message = "用户名或密码错误。" }
                : new { error = "invalid_reset_code", message = "用户名或重置码无效。" }),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.invalid/") };
        using var api = new CloudApiClient(http);
        using var client = Create(paths, api, store);
        await client.LoginAsync("other", "wrong-password", "", true, default);
        Check(client.Snapshot.StatusMessage == "用户名或密码错误。", "Credential error was replaced by session-expired text.");
        Check(before.SequenceEqual(File.ReadAllBytes(paths.CloudCredentialFile)), "A failed login overwrote the saved original key.");
        Check(client.HasRecoveryKeyFor("original") && !client.HasRecoveryKeyFor("other"), "Local key availability crossed accounts.");
        await client.ResetPasswordAsync("other", "invalid-reset-code", "new-password", "", true, CloudPasswordResetMethod.AdministratorCode, default);
        Check(client.Snapshot.StatusMessage == "用户名或重置码无效。" && !client.Snapshot.IsBusy, "A code-only reset was blocked by a local key requirement.");
        Check(before.SequenceEqual(File.ReadAllBytes(paths.CloudCredentialFile)), "A failed reset destroyed the old local credentials.");
    }

    private static async Task ValidateRealRecoveryAsync(string root, string url)
    {
        // These credentials come only from the isolated Node fixture, never a
        // production account. The second client intentionally starts with no key.
        var key = Environment.GetEnvironmentVariable("DACT_RECOVERY_TEST_KEY")!;
        var token = Environment.GetEnvironmentVariable("DACT_RECOVERY_TEST_TOKEN")!;
        var code = Environment.GetEnvironmentVariable("DACT_RECOVERY_TEST_CODE")!;
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        using var api = new CloudApiClient(http);
        var backup = new PortableConfigurationBackupService();
        if (Environment.GetEnvironmentVariable("DACT_RECOVERY_TEST_ENROLL_ONLY") == "1")
        {
            var paths = new PluginPaths(Path.Combine(root, "remembered-device")); paths.EnsureCreated();
            var savedStore = new CloudCredentialStore(paths.CloudCredentialFile);
            savedStore.Save(new("client_recovery", token, DateTimeOffset.UtcNow.AddDays(1), key));
            using var remembered = Create(paths, api, savedStore);
            await remembered.InitializeAsync(default);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (remembered.Snapshot.Recovery?.Protected != true)
                await Task.Delay(20, deadline.Token);
            Check(remembered.Snapshot.IsSignedIn, "Automatic protection blocked remembered login.");
            await remembered.CreateDiscordLinkCodeAsync(default);
            Check(remembered.Snapshot.DiscordLinkCode is not null, "A protected account could not generate a binding code.");
            Console.WriteLine("Remembered login automatically protected the original backup key.");
            return;
        }
        var original = new PluginPaths(Path.Combine(root, "old-device", "DalamudActCompat")); original.EnsureCreated();
        var configPath = Path.Combine(Path.GetDirectoryName(original.ConfigDirectory)!, "DalamudActCompat.json");
        await File.WriteAllTextAsync(configPath, "{\"Version\":16,\"RecoveryFixture\":\"original-cloud-settings\"}");
        var trigger = Path.Combine(original.ConfigDirectory, "Config", "Triggernometry.config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(trigger)!);
        await File.WriteAllTextAsync(trigger, "<triggers>before-password-reset</triggers>");
        var encrypted = Path.Combine(root, "before-reset.dactcloud");
        var exported = await backup.ExportEncryptedAsync(original.ConfigDirectory, encrypted, key, default);
        var uploaded = await api.UploadBackupAsync(token, encrypted, exported.ContentId, default);

        var clean = new PluginPaths(Path.Combine(root, "new-device", "DalamudActCompat"));
        var store = new CloudCredentialStore(clean.CloudCredentialFile);
        using var client = Create(clean, api, store);
        Check(!client.HasRecoveryKeyFor("client_recovery"), "Reset fixture accidentally retained a local key.");
        await client.ResetPasswordAsync("client_recovery", code, "new-password-from-discord", "", true, CloudPasswordResetMethod.AdministratorCode, default);
        Check(client.Snapshot.IsSignedIn && !client.Snapshot.StatusIsError, "Code-only reset did not sign in: " + client.Snapshot.StatusMessage);
        var recovered = store.Load()!;
        Check(recovered.RecoveryKey == key, "Reset rotated the original data key.");
        Check(client.Snapshot.Backups.Single().Id == uploaded.Id, "Reset lost the pre-existing backup.");
        var download = Path.Combine(root, "after-reset.dactcloud");
        await api.DownloadBackupAsync(recovered.Token, client.Snapshot.Backups.Single(), download, default);
        await backup.RestoreEncryptedAsync(download, clean.ConfigDirectory, Path.Combine(root, "rollback.dactcloud"), recovered.RecoveryKey, default);
        Check(await File.ReadAllTextAsync(Path.Combine(clean.ConfigDirectory, "Config", "Triggernometry.config.xml")) == "<triggers>before-password-reset</triggers>",
            "Restored settings differ from the old encrypted backup.");
        Console.WriteLine("Original cloud backup recovered after a code-only reset with no local key.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
