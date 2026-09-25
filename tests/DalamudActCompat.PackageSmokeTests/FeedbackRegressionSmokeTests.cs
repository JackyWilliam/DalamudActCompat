using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Advanced_Combat_Tracker;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Raynording.Accounts;

internal static class FeedbackRegressionSmokeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-22T16:21:28.219+08:00");
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static async Task RunAsync(string root, bool native = false)
    {
        Clock();
        foreach (var offset in new[] { -90d, 30.325, 3600 }) RuntimeClock(offset);
        SharedStore(root);
        foreach (var mode in new[] { "success", "wrong-password", "cancel", "concurrent-login" })
            await SharedLoginAsync(root, mode);
        if (native) NativeMeter();
        Console.WriteLine("Feedback regression: skewed clocks, live/header/history/reset timing and shared-session recovery passed.");
    }

    private static void Clock()
    {
        var time = new TestTime();
        var clock = new EncounterEventClock(time);
        clock.Observe(At.AddSeconds(30.325));
        time.Advance(20); time.Wall = At.AddHours(-4);
        Check(Math.Abs((clock.Now - At.AddSeconds(50.325)).TotalSeconds) < .001, "Wall-clock correction changed encounter time.");
        clock.Observe(At.AddSeconds(40));
        Check(Math.Abs((clock.Now - At.AddSeconds(50.325)).TotalSeconds) < .001, "Late packet moved encounter time backwards.");
        Check(!EncounterEventClock.IsNetworkEvent("00|local chat") &&
              !EncounterEventClock.IsNetworkEvent("253|local startup") &&
              EncounterEventClock.IsNetworkEvent("37|effect result"), "Local lines can reanchor the network clock.");
        clock.Reset(); clock.Observe(At.AddSeconds(-30));
        Check(clock.Now == At.AddSeconds(-30), "Parser restart retained its previous clock.");
    }

    private static void RuntimeClock(double skew)
    {
        var previous = ActGlobals.oFormActMain;
        ActGlobals.Init();
        var form = (FormActMain)RuntimeHelpers.GetUninitializedObject(typeof(FormActMain)); GC.SuppressFinalize(form);
        ActGlobals.oFormActMain = form;
        typeof(FormActMain).GetField("inCombat", Private)!.SetValue(form, true);
        Advanced_Combat_Tracker.Resources.NotActMainFormatter.SetupEnvironment();
        var time = new TestTime(); var inCombat = true;
        var start = At.AddSeconds(skew);
        // This fixture supplies ACT swings, not HP result packets, so leave the
        // entity ID absent and use ACT totals instead of the effective-HP ledger.
        ActPlayerIdentity[] roster = [new("Self", "", "PLD", true, false)];
        try
        {
            using var runtime = new SelfHostedActRuntime(null!, DispatchProxy.Create<IPluginLog, MeterRuntimeLogProxy>(), null!,
                () => true, () => "Self", () => roster, () => null, null!, DispatchProxy.Create<IFramework, NoOpPluginLogProxy>(), null!,
                () => new(134, 0, EncounterMode.DutyAttempt, inCombat, false, false), null!, null!, null!, _ => null,
                () => false, _ => new(), () => new Dictionary<string, HtmlOverlayWindowSettings>(), () => { },
                () => false, () => false, (_, _) => false, getEncounterResetOptions: () => new(EncounterResetMode.AfterCombat, 5));
            // Keep the runtime's public constructor binary-compatible; inject the
            // deterministic clock only inside this offline fixture.
            typeof(SelfHostedActRuntime).GetField("eventClock", Private)!.SetValue(runtime, new EncounterEventClock(time));
            var snapshots = new List<(ActEncounterSnapshot Data, bool Finished)>(); var resets = 0;
            runtime.EncounterChanged += (data, finished) => snapshots.Add((data, finished));
            runtime.StatisticsReset += _ => resets++;
            runtime.UpdateFrameworkState(null);
            var raw = $"20|{start:O}|10000001|Self|1|Cast|40000001|Boss|1.0";
            typeof(SelfHostedActRuntime).GetMethod("OnBeforeLogLineRead", Private)!.Invoke(runtime,
                [false, new LogLineEventArgs(raw, 20, start.LocalDateTime, "Room", true)]);
            var encounter = new EncounterData("Self", "Room", null!) { Active = true };
            encounter.StartTimes.Add(start.LocalDateTime);
            var tracker = (EncounterDurationTracker)typeof(SelfHostedActRuntime).GetField("encounterDurationTracker", Private)!.GetValue(runtime)!;
            void Hit(int seconds)
            {
                var at = start.AddSeconds(seconds);
                var swing = new MasterSwing(2, false, 100_000, at.LocalDateTime, seconds, "Hit", "Self", "damage", "Boss");
                encounter.AddCombatAction(swing);
                typeof(SelfHostedActRuntime).GetMethod("OnAfterCombatAction", Private)!.Invoke(runtime, [false, new CombatActionEventArgs(swing)]);
            }
            Hit(0);
            tracker.ObserveConfirmedDamage(start, "10000001", "Self", "", "40000001", "Boss");
            time.Advance(20); time.Wall = At.AddHours(4);
            tracker.ObserveConfirmedDamage(start.AddSeconds(20), "10000001", "Self", "", "40000001", "Boss");
            Hit(20);
            var snapshot = snapshots[^1].Data;
            Check(Math.Abs(snapshot.CombatDuration!.Value.TotalSeconds - 20) < .001 &&
                  Math.Abs(snapshot.Combatants.Single().Dps - 10_000) < .001,
                $"Live skew={skew}: duration={snapshot.CombatDuration}, rows={JsonSerializer.Serialize(snapshot.Combatants)}.");
            var mapped = ActEncounterMapper.Map(snapshot);
            Check(Math.Abs(mapped.Duration.TotalSeconds - 20) < .2, "Live header did not inherit event time.");
            var delayed = mapped with { TimeAnchor = new(start.AddSeconds(20), Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency) };
            Check(Math.Abs(delayed.Duration.TotalSeconds - 22) < .2, "Header stopped between damage events.");
            var cumulative = new DutyEncounterAccumulator();
            var live = cumulative.Update(mapped, false, time.Wall);
            Check(Math.Abs(live.Duration.TotalSeconds - 20) < .2 &&
                  Math.Abs((cumulative.CurrentTime - start).TotalSeconds - 20) < .2, "Cumulative meter lost the event clock.");
            var finished = mapped with { EndTime = start.AddSeconds(20) };
            Check(finished.Duration.TotalSeconds == 20, "Finished timing changed after wall-clock correction.");
            var json = JsonSerializer.Serialize(finished);
            Check(!json.Contains("TimeAnchor") && JsonSerializer.Deserialize<Encounter>(json)!.Duration.TotalSeconds == 20,
                "History persisted a process clock or lost its stable duration.");
            inCombat = false; runtime.UpdateFrameworkState(null);
            time.Advance(4.9); runtime.UpdateFrameworkState(null);
            Check(resets == 0, "Clock skew ended the statistics segment early.");
            time.Advance(.2); runtime.UpdateFrameworkState(null);
            Check(resets == 1 && snapshots[^1].Finished, "Monotonic reset deadline did not complete the segment.");
            inCombat = true; time.Advance(1); runtime.UpdateFrameworkState(null); Hit(27);
            Check(!snapshots[^1].Finished && snapshots[^1].Data.Combatants.Single().TotalDamage == 100_000,
                "Event-time cutoff dropped the new pull or retained old damage.");
        }
        finally { ActGlobals.oFormActMain = previous; }
    }

    private static void SharedStore(string root)
    {
        var directory = Path.Combine(root, "shared-store"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.dat");
        var store = new SharedAccountStore(path);
        var account = new SharedAccount("new", "new-token", DateTimeOffset.UtcNow.AddDays(1), "new-key");
        // Include a valid DPAPI envelope with invalid JSON as well as undecryptable
        // bytes: both must preserve evidence and demand fresh authentication.
        foreach (var corrupt in new[] { "broken-dpapi"u8.ToArray(), ProtectedData.Protect("not-json"u8.ToArray(), Encoding.UTF8.GetBytes("Raynording.SharedAccount.v1"), DataProtectionScope.CurrentUser) })
        {
            File.WriteAllBytes(path, corrupt);
            var expected = store.PrepareAuthentication();
            Check(File.ReadAllBytes(path).SequenceEqual(corrupt) && expected.UnreadableFingerprint is not null, "Preparation changed unreadable credentials.");
            var recovered = store.PublishAuthenticated(expected, account, true);
            Check(store.Read().UsableAccount == account && recovered.Revision > 1, "Recovered shared session cannot be read by the existing v1 contract.");
            Check(Directory.GetFiles(directory, "*.bak").Any(file => File.ReadAllBytes(file).SequenceEqual(corrupt)), "Recovery lost the original encrypted bytes.");
            try { store.PublishAuthenticated(expected, account with { Token = "late" }, true); throw new Exception("Stale recovery overwrote the newer login."); }
            catch (OperationCanceledException) { }
            var tombstone = store.Publish(recovered.Revision, null, false);
            try { store.Publish(recovered.Revision, account, true); throw new Exception("Late writer undid logout."); }
            catch (OperationCanceledException) { }
            Check(store.Read().Account is null && store.Read().Revision == tombstone.Revision, "Logout did not remain authoritative.");
        }
        File.WriteAllBytes(path, "broken"u8.ToArray());
        var ticket = store.PrepareAuthentication();
        File.WriteAllBytes(path, "changed-broken"u8.ToArray());
        try { store.PublishAuthenticated(ticket, account, true); throw new Exception("Changed unreadable file was overwritten."); }
        catch (OperationCanceledException) { }
    }

    private static async Task SharedLoginAsync(string root, string mode)
    {
        var paths = new PluginPaths(Path.Combine(root, "shared-login-" + mode)); paths.EnsureCreated();
        var file = Path.Combine(paths.ConfigDirectory, "session.dat");
        var corrupt = "unreadable-login"u8.ToArray(); File.WriteAllBytes(file, corrupt);
        var shared = new SharedAccountStore(file);
        var backup = new PortableConfigurationBackupService(); var envelopes = new CloudKeyEnvelopeService();
        var recovery = backup.GenerateRecoveryKey(); var envelope = envelopes.Create(recovery, "password");
        var loginRequests = 0; using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/login"))
            {
                loginRequests++;
                if (mode == "cancel") { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
                if (mode == "wrong-password") return Json(new { error = "invalid_credentials", message = "wrong password" }, HttpStatusCode.Unauthorized);
                if (mode == "concurrent-login")
                    shared.PublishAuthenticated(shared.PrepareAuthentication(), new("other", "other-token", DateTimeOffset.UtcNow.AddDays(1), recovery), true);
                return Json(new { token = "fresh-token", expiresAt = DateTimeOffset.UtcNow.AddDays(1), user = new { id = "id", username = "new" }, keyEnvelope = envelope });
            }
            if (path.EndsWith("/backups")) return Json(new { backups = Array.Empty<object>() });
            if (path.EndsWith("/invitations")) return Json(new { quota = 3, used = 0, remaining = 3, invitations = Array.Empty<object>() });
            if (path.EndsWith("/auth/me")) return Json(new { username = "new" });
            return Json(new { });
        })) { BaseAddress = new("https://isolated.test/") };
        using var api = new CloudApiClient(http);
        var credentialStore = new CloudCredentialStore(paths.CloudCredentialFile);
        credentialStore.Save(new("obsolete", "obsolete-token", DateTimeOffset.UtcNow.AddDays(1), recovery));
        using var service = new CloudClientService(paths, api, credentialStore, new(paths.CloudBanFile), new(paths.CloudDeviceFile), envelopes, backup, shared);
        await service.InitializeAsync(CancellationToken.None);
        Check(!service.Snapshot.IsSignedIn && !service.Snapshot.IsBusy && File.ReadAllBytes(file).SequenceEqual(corrupt),
            "Startup imported stale credentials, changed unreadable state, or left the login UI busy.");
        await service.LoginAsync("new", "password", "", true, cancel.Token);
        Check(loginRequests == 1 && !service.Snapshot.IsBusy, "Recovery login never reached authentication or retained its operation gate.");
        if (mode == "success")
        {
            // Let the existing one-second shared monitor read the rebuilt state;
            // a one-shot successful login is insufficient for this regression.
            await Task.Delay(1100);
            Check(service.Snapshot.IsSignedIn && shared.Read().Account?.Token == "fresh-token", "Authenticated recovery did not survive the shared monitor.");
        }
        else if (mode == "concurrent-login") Check(!service.Snapshot.IsSignedIn && shared.Read().Account?.Token == "other-token", "Late login replaced another plugin's session.");
        else Check(!service.Snapshot.IsSignedIn && File.ReadAllBytes(file).SequenceEqual(corrupt) && Directory.GetFiles(paths.ConfigDirectory, "session.dat*.bak").Length == 0,
            "Failed/cancelled authentication replaced the unreadable credentials.");
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = JsonContent.Create(body) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Wall = At;
        private long ticks;
        public void Advance(double seconds) => ticks += (long)(seconds * TimeSpan.TicksPerSecond);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => Wall;
    }

    private static unsafe void NativeMeter()
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI");
        if (string.IsNullOrEmpty(library)) { Console.WriteLine("Feedback native meter skipped: DACT_TEST_CIMGUI not set."); return; }
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true); NativeLibrary.Load(library);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DisplaySize = new(1000, 800); io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0xff, 0x3000, 0x303f, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges); io.Fonts.Build();
            var raster = new NativeUiRasterizer(io.Fonts);
            foreach (var scale in new[] { .75f, 1f, 1.06f, 1.4f, 1.8f })
            foreach (var width in new[] { 480f, 700f })
            {
                var config = new PluginConfiguration(); config.Fflogs.Enabled = false; config.Meter.CompactMode = true; config.Meter.FontScale = scale;
                var store = new EncounterStateStore(); var sample = SampleEncounterFactory.Create(DateTimeOffset.UtcNow);
                store.UpdateCurrent(sample with { EndTime = DateTimeOffset.UtcNow, Combatants = sample.Combatants.Take(1).ToArray() });
                var service = new MeterService(store, config.Meter);
                var icons = new JobIconTextureSet(null!, Path.Combine(AppContext.BaseDirectory, "missing-icons"));
                using var logo = new SkinSmokeTests.EmptyTexture();
                var window = new MeterWindow(service, null!, config, new UiText(config), icons, logo, logo, logo, (_, name) => name, () => { });
                foreach (var compact in new[] { true, false, true })
                {
                    config.Meter.CompactMode = compact; var heights = new List<float>();
                    for (var frame = 0; frame < 100; frame++)
                    {
                        ImGui.NewFrame(); window.PreDraw(); ImGui.SetNextWindowPos(new(0, 0));
                        if (frame == 0) ImGui.SetNextWindowSize(new(width, compact ? 119 : 260));
                        ImGui.Begin(window.WindowName, window.Flags | ImGuiWindowFlags.NoSavedSettings); window.Draw();
                        if (frame >= 60) heights.Add(ImGui.GetWindowSize().Y);
                        ImGui.End(); window.PostDraw(); ImGui.Render();
                    }
                    Check(heights.Max() == heights.Min(), $"Meter height still oscillates: font={scale}, width={width}, compact={compact}.");
                    Check(!(bool)typeof(MeterWindow).GetField("isHeightAnimationActive", Private)!.GetValue(window)!, "Settled window keeps restarting animation.");
                    if (scale == 1.06f && width == 700 && compact && Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT") is { } output)
                    {
                        Directory.CreateDirectory(output);
                        raster.Save(ImGui.GetDrawData(), Path.Combine(output, "meter-stable.png"));
                    }
                }
            }
        }
        finally { ImGui.DestroyContext(context); }
        Console.WriteLine("Feedback native meter: 5 font sizes, 2 widths, compact/expanded/re-entry, 3000 frames exercised; all 1200 settled frames stable.");
    }
}
