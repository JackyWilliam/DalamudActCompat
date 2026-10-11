using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.UI;

internal static class UltimateSkinCloudSmokeTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static CloudStoredCredentials Account(string name, string token) => new(name, token, DateTimeOffset.UtcNow.AddDays(1),
        new PortableConfigurationBackupService().GenerateRecoveryKey());
    private static CloudClientService Service(PluginPaths paths, HttpClient http) => new(paths, new CloudApiClient(http),
        new(paths.CloudCredentialFile), new(paths.CloudBanFile), new(paths.CloudDeviceFile), new(), new());
    private static void Apply(CloudClientService service, CloudStoredCredentials account, string id, bool unlocked)
        => typeof(CloudClientService).GetMethod("ApplySkinUnlockStatus", Hidden)!.Invoke(service,
            [account, new CloudAccountStatus(User: new(id, account.Username), Skins: new(unlocked))]);
    private static Task Sync(CloudClientService service, CloudStoredCredentials account)
        => (Task)typeof(CloudClientService).GetMethod("SyncUltimateSkinsAsync", Hidden)!.Invoke(service, [account, CancellationToken.None])!;
    private static void Seed(CloudClientService service, CloudStoredCredentials account, string id)
    {
        typeof(CloudClientService).GetField("credentials", Hidden)!.SetValue(service, account);
        typeof(CloudClientService).GetField("snapshot", Hidden)!.SetValue(service, CloudClientSnapshot.SignedOut() with
            { IsSignedIn = true, Username = account.Username, Recovery = new(DiscordBound: true) });
        Apply(service, account, id, false);
    }

    public static async Task RunAsync(string root)
    {
        var loggedIn = false; ulong character = 1; long clock = 0; var queries = 0;
        var game = new UltimateClearState(() => loggedIn, () => character, id => { queries++; return character == 1 && id == 30086; }, () => clock);
        Check(game.CompletedDuty() == 0 && queries == 0, "Title screen queried character progress.");
        loggedIn = true;
        Check(game.CompletedDuty() == 30086 && queries == 3, "Any one clear must be observed.");
        game.CompletedDuty(); Check(queries == 3, "Per-frame calls bypassed the progress cache.");
        character = 2;
        Check(game.CompletedDuty() == 0, "A character switch inherited the previous character's cached clear.");
        character = 1; game.CompletedDuty(); loggedIn = false;
        Check(game.CompletedDuty() == 0, "Logout retained the cached character clear.");

        var paths = new PluginPaths(Path.Combine(root, "ultimate-account-outbox")); paths.EnsureCreated();
        using var handler = new GrantHandler(); using var http = new HttpClient(handler) { BaseAddress = new("https://fixture.invalid/") };
        var a = Account("owner", "owner-token"); var b = Account("other", "other-token");
        using (var service = Service(paths, http))
        {
            Seed(service, a, "account-a");
            service.ObserveUltimateClear("account-b", 30086); service.ObserveUltimateClear("account-a", 30058);
            Check(!service.Snapshot.UltimateSkinsUnlocked, "Wrong owner or non-ultimate progress unlocked skins.");
            service.ObserveUltimateClear("account-a", 30086);
            Check(service.Snapshot is { UltimateSkinsUnlocked: true, UltimateSkinSyncPending: true }, "Observed clear was not immediately available.");
            handler.Fail = true;
            try { await Sync(service, a); throw new Exception("Offline upload unexpectedly succeeded."); }
            catch (CloudApiException) { }
            Check(service.Snapshot.UltimateSkinSyncPending, "Failed upload discarded the outbox.");
        }
        using var coldHttp = new HttpClient(handler, disposeHandler: false) { BaseAddress = new("https://fixture.invalid/") };
        using (var cold = Service(paths, coldHttp))
        {
            Seed(cold, a, "account-a");
            Check(cold.Snapshot.UltimateSkinSyncPending, "Cold start lost an unsynced clear.");
            handler.Fail = false;
            await Sync(cold, a);
            Apply(cold, a, "account-a", false);
            Check(cold.Snapshot is { UltimateSkinsConfirmed: true, UltimateSkinSyncPending: false }, "An old heartbeat revoked the new permanent grant.");
            // Same username with a different immutable ID must never reuse the old outbox.
            Seed(cold, a, "replacement-account-a");
            Check(!cold.Snapshot.UltimateSkinsUnlocked, "Recreated account inherited a previous user's reward.");
            Seed(cold, a, "account-a");
            handler.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Sync(cold, a);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cold.LogoutAsync(default);
            Seed(cold, b, "account-b");
            handler.Hold.SetResult(); await pending;
            Apply(cold, a, "account-a", true);
            Check(!cold.Snapshot.UltimateSkinsUnlocked && cold.Snapshot.SkinAccountId == "account-b", "Late grant/heartbeat crossed account boundaries.");
        }
        var files = Directory.GetFiles(Path.Combine(paths.ConfigDirectory, "account-skin-unlocks"));
        Check(files.Length == 1 && !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(files[0])).Contains("account-a"), "Outbox was not protected or was written for the wrong account.");
        Console.WriteLine("Ultimate account skins: character cache, title-screen observation, durable offline retry, monotonic grants, immutable owners and late responses passed.");
    }

    public static async Task ApiAsync(string root)
    {
        using var data = JsonDocument.Parse(Environment.GetEnvironmentVariable("DACT_ULTIMATE_FIXTURE")!);
        var user = data.RootElement.GetProperty("user"); var other = data.RootElement.GetProperty("other");
        var a = Account(user.GetProperty("username").GetString()!, user.GetProperty("token").GetString()!);
        var b = Account(other.GetProperty("username").GetString()!, other.GetProperty("token").GetString()!);
        var baseUrl = new Uri(data.RootElement.GetProperty("baseUrl").GetString()!);
        async Task UseDevice(string name, CloudStoredCredentials account, bool observe, bool expected, bool logout)
        {
            var paths = new PluginPaths(Path.Combine(root, name)); paths.EnsureCreated();
            new CloudCredentialStore(paths.CloudCredentialFile).Save(account);
            using var http = new HttpClient { BaseAddress = baseUrl };
            using var service = Service(paths, http);
            await service.InitializeAsync(default);
            Check(service.Snapshot is { CanUseDact: true, SkinAccountId: not null }, "Account skin identity was not fetched during login.");
            if (observe)
            {
                service.ObserveUltimateClear(service.Snapshot.SkinAccountId!, 30143);
                await Sync(service, account);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!service.Snapshot.UltimateSkinsConfirmed && DateTime.UtcNow < deadline) await Task.Delay(25);
            }
            Check(service.Snapshot.UltimateSkinsUnlocked == expected && service.Snapshot.UltimateSkinsConfirmed == expected,
                "Real HTTP grant did not survive a fresh device, or leaked to another account.");
            var previous = SkinCatalog.UltimateUnlocked;
            try
            {
                SkinCatalog.UltimateUnlocked = () => service.Snapshot.UltimateSkinsUnlocked;
                // No game services exist in this process: this is the title-screen path.
                Check(SkinCatalog.All.Where(s => s.Ultimate).All(s => SkinCatalog.IsAvailable(s.Id, new(), true, 0) == expected),
                    "Account grant still depended on a logged-in game character.");
                if (logout)
                {
                    await service.LogoutAsync(default);
                    Check(!service.Snapshot.UltimateSkinsUnlocked, "Logout left the old account's grant active.");
                }
            }
            finally { SkinCatalog.UltimateUnlocked = previous; }
        }
        // Do not revoke the test owner's token until both device logins have used it.
        await UseDevice("ultimate-first-device", a, true, true, false);
        await UseDevice("ultimate-fresh-device", a, false, true, true);
        await UseDevice("ultimate-other-account", b, false, false, true);
        Console.WriteLine("Ultimate skin HTTP: grant, fresh device title-screen access, account isolation and logout passed.");
    }

    private sealed class GrantHandler : HttpMessageHandler
    {
        public bool Fail;
        public TaskCompletionSource? Hold, Entered;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/logout")) return new(HttpStatusCode.NoContent);
            if (!request.RequestUri.AbsolutePath.EndsWith("/skins/ultimate")) throw new Exception("Unexpected HTTP request.");
            if (Hold is not null) { Entered!.TrySetResult(); await Hold.Task.WaitAsync(token); }
            return Fail ? new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { error = "offline", message = "fixture offline" }) }
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ultimate = true, ultimateUnlockedAt = DateTimeOffset.UtcNow }) };
        }
    }
}
