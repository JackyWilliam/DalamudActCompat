using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Plugin;

internal static class DiscordBindingSmokeTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static async Task RunAsync(string root)
    {
        var paths = new PluginPaths(Path.Combine(root, "discord-gate")); paths.EnsureCreated();
        var saved = new CloudStoredCredentials("binding_user", "fixture-session", DateTimeOffset.UtcNow.AddDays(1),
            new PortableConfigurationBackupService().GenerateRecoveryKey());
        var store = new CloudCredentialStore(paths.CloudCredentialFile); store.Save(saved);
        CloudRecoveryStatus? status = new(true, true, false, 3, 0, true);
        var unavailable = false;
        var friendRequests = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/auth/me") return unavailable
                ? Json(new { message = "offline" }, HttpStatusCode.ServiceUnavailable)
                : Json(new { recovery = status });
            if (path.EndsWith("/backups")) return Json(new { backups = Array.Empty<object>() });
            if (path.EndsWith("/invitations")) return Json(new { invitations = Array.Empty<object>() });
            if (path.EndsWith("/discord/link-code")) return Json(new CloudDiscordLinkCode("fixture-code", DateTimeOffset.UtcNow.AddMinutes(10)));
            if (path.Contains("/friends")) Interlocked.Increment(ref friendRequests);
            if (path.Contains("events") || path.Contains("friends") || path.Contains("presence"))
                await Task.Delay(Timeout.Infinite, ct);
            return Json(new { });
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://fixture.invalid/") };
        using var api = new CloudApiClient(http);
        using var client = new CloudClientService(paths, api, store, new(paths.CloudBanFile), new(paths.CloudDeviceFile), new(), new());
        Check(!client.Snapshot.CanUseDact, "Saved credentials bypassed the gate before server confirmation.");
        await client.InitializeAsync(default);
        await Until(() => client.Snapshot.Recovery is not null);
        Check(client.Snapshot.IsSignedIn && !client.Snapshot.CanUseDact, "Unbound remembered login started DACT.");
        Check(!client.FriendsSession.IsSignedIn && friendRequests == 0, "Pending binding advertised social presence.");
        await client.CreateDiscordLinkCodeAsync(default);
        Check(client.Snapshot.DiscordLinkCode is not null && !client.Snapshot.CanUseDact, "Generating a code bypassed binding.");
        await client.RefreshRecoveryStatusAsync(default);
        Check(!client.Snapshot.CanUseDact, "The manual check button granted access without a server link.");
        unavailable = true; await client.RefreshRecoveryStatusAsync(default);
        Check(!client.Snapshot.CanUseDact && client.Snapshot.RecoveryStatusError is not null, "A network error opened the gate or left no retry explanation.");
        unavailable = false; status = null; await client.RefreshRecoveryStatusAsync(default);
        Check(!client.Snapshot.CanUseDact && client.Snapshot.RecoveryStatusError is not null, "An older server without binding status granted access.");
        status = new(true, true, true, 3, 1, true); await client.RefreshRecoveryStatusAsync(default);
        Check(client.Snapshot.CanUseDact && client.FriendsSession.IsSignedIn && client.Snapshot.DiscordLinkCode is null,
            "Server-confirmed binding did not unlock DACT and clear the one-time code.");
        typeof(CloudClientService).GetMethod("ApplyRecoveryStatus", Hidden)!.Invoke(client,
            [saved, new CloudRecoveryStatus(true, true, false), 0L]);
        Check(client.Snapshot.CanUseDact, "An older in-flight status replaced the confirmed binding.");

        // Exercise the real runtime predicate, including a network update that lands
        // between Framework reconciliation and a command or maintenance callback.
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).GetField("cloudClient", Hidden)!.SetValue(plugin, client);
        typeof(Plugin).GetField("cloudRuntimeAuthorized", Hidden)!.SetValue(plugin, 1);
        bool RuntimeAllowed() => (bool)typeof(Plugin).GetMethod("IsDactAccessAllowed", Hidden)!.Invoke(plugin, null)!;
        Check(RuntimeAllowed(), "Bound session could not start runtime.");
        Check(store.Load()?.RecoveryKey == saved.RecoveryKey, "Binding checks destroyed the original backup key.");
        // Even signing in again as the same username must confirm the new token;
        // display-name equality is not authority to carry a cached Discord link.
        var fresh = saved with { Token = "replacement-session" };
        typeof(CloudClientService).GetMethod("SaveAuthentication", Hidden)!.Invoke(client,
            [new CloudAuthenticationResponse(fresh.Token, "Bearer", fresh.ExpiresAt, new("fixture-user", fresh.Username), null), fresh.RecoveryKey, true]);
        Check(!RuntimeAllowed() && client.Snapshot.Recovery is null, "Reauthentication reused an old session's binding.");
        typeof(CloudClientService).GetMethod("ApplyRecoveryStatus", Hidden)!.Invoke(client, [saved, status, long.MaxValue]);
        Check(!RuntimeAllowed(), "The old token restored access over a newer login.");
        typeof(CloudClientService).GetMethod("ApplyRecoveryStatus", Hidden)!.Invoke(client, [fresh, status, 100L]);
        Check(RuntimeAllowed(), "The current token could not confirm its own binding.");
        await client.LogoutAsync(default);
        Check(!RuntimeAllowed() && !client.Snapshot.CanUseDact, "A stale framework authorization survived logout.");
        typeof(CloudClientService).GetMethod("ApplyRecoveryStatus", Hidden)!.Invoke(client, [saved, status, long.MaxValue]);
        Check(!client.Snapshot.CanUseDact && client.Snapshot.Recovery is null, "A late old-session response restored access after logout.");
        Console.WriteLine("Discord gate: remembered login, unbound/code-only/manual bypass, offline/legacy server, confirmed link, friends/runtime and stale response passed.");
    }

    internal static CloudClientService BoundFixture(PluginPaths paths)
    {
        var api = new CloudApiClient(new HttpClient { BaseAddress = new("https://fixture.invalid/") });
        var service = new CloudClientService(paths, api, new(paths.CloudCredentialFile), new(paths.CloudBanFile), new(paths.CloudDeviceFile), new(), new());
        typeof(CloudClientService).GetField("snapshot", Hidden)!.SetValue(service,
            CloudClientSnapshot.SignedOut() with { IsSignedIn = true, Recovery = new(DiscordBound: true) });
        return service;
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = JsonContent.Create(value) };
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
