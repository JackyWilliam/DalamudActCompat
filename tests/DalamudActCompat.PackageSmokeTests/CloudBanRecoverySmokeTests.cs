using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;
using Raynording.Accounts;

internal static class CloudBanRecoverySmokeTests
{
    internal static async Task RunAsync(string root)
    {
        foreach (var result in new[] { "released", "wrong-password", "account", "device", "cascade", "network", "inactive-session", "broken-shared-store", "damaged-marker", "directory-marker" })
            await VerifyAsync(root, result);
        await VerifyDelayedMonitorAsync(root);
        Console.WriteLine("Ban recovery: missing old token, rejected credentials, active bans, network failure, invalid session, and unreadable shared state passed.");
    }

    private static async Task VerifyDelayedMonitorAsync(string root)
    {
        var paths = new PluginPaths(Path.Combine(root, "ban-recovery-delayed")); paths.EnsureCreated();
        var store = new CloudCredentialStore(paths.CloudCredentialFile);
        store.Save(new("fixture", "old-token", DateTimeOffset.UtcNow.AddDays(1), new PortableConfigurationBackupService().GenerateRecoveryKey()));
        var ban = new CloudBanNotice("account_banned", "account", DateTimeOffset.UtcNow.AddDays(-1), null, "fixture");
        new CloudBanStore(paths.CloudBanFile).Save(ban);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
                return Json(HttpStatusCode.OK, new { token = "new-token", tokenType = "Bearer", expiresAt = DateTimeOffset.UtcNow.AddDays(1), user = new { id = "id", username = "fixture" } });
            if (request.RequestUri.AbsolutePath.EndsWith("/auth/logout")) return Json(HttpStatusCode.OK, new { ok = true });
            if (request.Headers.Authorization?.Parameter == "old-token")
            {
                started.TrySetResult(); await release.Task.WaitAsync(token);
                return Json(HttpStatusCode.OK, new { banned = true, sessionActive = false, banType = "account", bannedAt = ban.BannedAt });
            }
            return Json(HttpStatusCode.OK, new { banned = false, sessionActive = true, wasBanRevoked = false });
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://fixture.invalid/") };
        using var api = new CloudApiClient(http);
        using var client = new CloudClientService(paths, api, store, new(paths.CloudBanFile), new(paths.CloudDeviceFile), new(), new());
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await client.RecheckBanAsync("fixture", "fixture-password", default);
            release.TrySetResult();
            // Allow the pending response and the next marker-maintenance tick to run.
            await Task.Delay(1200);
            Check(client.ActiveBan is null && !File.Exists(paths.CloudBanFile), "A delayed old-token response restored a cleared ban.");
        }
        finally { release.TrySetResult(); }
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static async Task VerifyAsync(string root, string result)
    {
        var paths = new PluginPaths(Path.Combine(root, "ban-recovery-" + result));
        paths.EnsureCreated();
        var banStore = new CloudBanStore(paths.CloudBanFile);
        var notice = new CloudBanNotice("account_banned", "account", DateTimeOffset.UtcNow.AddDays(-2), null, "fixture");
        banStore.Save(notice);
        if (result == "damaged-marker") File.WriteAllText(paths.CloudBanFile, "unreadable-fixture");
        if (result == "directory-marker") { File.Delete(paths.CloudBanFile); Directory.CreateDirectory(paths.CloudBanFile); }
        var sharedPath = Path.Combine(paths.ConfigDirectory, "shared.dat");
        var sharedStore = new SharedAccountStore(sharedPath);
        // Reproduce a restarted client whose revoked token has already been removed.
        sharedStore.Publish(0, null, false);
        if (result == "broken-shared-store") File.WriteAllText(sharedPath, "unreadable-fixture");
        var originalShared = File.ReadAllBytes(sharedPath);
        var loginCalls = 0; var logoutCalls = 0; var lifted = 0;
        using var handler = new ScriptedHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/login"))
            {
                loginCalls++;
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(body.RootElement.GetProperty("username").GetString() == "recovery_user" &&
                    body.RootElement.GetProperty("password").GetString() == "fixture-password" &&
                    body.RootElement.GetProperty("deviceId").GetString()!.StartsWith("dact-device-v1_"),
                    "Verification did not use password login with the current machine identity.");
                if (result == "network") throw new HttpRequestException("fixture offline");
                if (result == "wrong-password") return Json(HttpStatusCode.Unauthorized, new { error = "invalid_credentials", message = "Wrong password" });
                if (result is "account" or "device" or "cascade") return Json(HttpStatusCode.Forbidden, new {
                    error = result == "device" ? "device_banned" : "account_banned", message = "Still banned",
                    banType = result, bannedAt = notice.BannedAt, banExpiresAt = (DateTimeOffset?)null, banReason = "fixture",
                });
                return Json(HttpStatusCode.OK, new { token = "verification-only-token", tokenType = "Bearer",
                    expiresAt = DateTimeOffset.UtcNow.AddDays(1), user = new { id = "fixture-id", username = "recovery_user" } });
            }
            if (path.EndsWith("/auth/access-status"))
            {
                Check(request.Headers.Authorization?.Parameter == "verification-only-token", "Used an old or missing token to confirm unban.");
                return Json(HttpStatusCode.OK, new { banned = false, sessionActive = result != "inactive-session", wasBanRevoked = false });
            }
            if (path.EndsWith("/auth/logout")) { logoutCalls++; return Json(HttpStatusCode.OK, new { ok = true }); }
            throw new InvalidOperationException("Unexpected request: " + path);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") };
        using var api = new CloudApiClient(http);
        using var client = new CloudClientService(paths, api, new(paths.CloudCredentialFile), banStore,
            new(paths.CloudDeviceFile), new(), new(), sharedStore);
        client.BanLifted += _ => lifted++;
        await client.RecheckBanAsync(" recovery_user ", "fixture-password", default);
        var released = result is "released" or "broken-shared-store" or "damaged-marker";
        Check(loginCalls == 1 && (client.ActiveBan is null) == released && (File.Exists(paths.CloudBanFile) || Directory.Exists(paths.CloudBanFile)) != released,
            "Unexpected recovery outcome: " + result);
        Check(lifted == (released ? 1 : 0) && !client.Snapshot.IsSignedIn && !client.Snapshot.CanUseDact && !client.Snapshot.IsBusy,
            "Verification activated runtime or lost the completion state: " + result);
        Check(logoutCalls == (released || result is "inactive-session" or "directory-marker" ? 1 : 0), "Verification token was not discarded.");
        Check(originalShared.SequenceEqual(File.ReadAllBytes(sharedPath)) && !File.Exists(paths.CloudCredentialFile),
            "Verification replaced shared login or persisted a new session.");
        if (released) Check(new CloudBanStore(paths.CloudBanFile).Load() is null, "Unban did not survive a fresh marker read.");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object content) => new(status) { Content = JsonContent.Create(content) };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
