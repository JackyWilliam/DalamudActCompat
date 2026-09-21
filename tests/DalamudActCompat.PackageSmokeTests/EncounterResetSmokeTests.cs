using System.Reflection;
using System.Runtime.CompilerServices;
using Advanced_Combat_Tracker;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Plugin;
using Newtonsoft.Json;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Parser;

internal static class EncounterResetSmokeTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run()
    {
        var timer = new EncounterResetTimer();
        var options = new EncounterResetOptions(EncounterResetMode.AfterCombat, 5);
        timer.ObserveActivity();
        Check(!timer.Observe(default, false, At.AddHours(1)), "DACT default started resetting encounters.");
        Check(!timer.Observe(options, true, At), "Reset while in combat.");
        Check(!timer.Observe(options, false, At.AddSeconds(1)) && !timer.Observe(options, false, At.AddSeconds(5.999)), "Reset before the chosen delay.");
        Check(!timer.Observe(options, true, At.AddSeconds(6)), "Re-entry failed to cancel reset.");
        Check(!timer.Observe(options, false, At.AddSeconds(7)) && timer.Observe(options, false, At.AddSeconds(12)), "The new countdown did not restart from re-entry.");
        Check(!timer.Observe(options, false, At.AddHours(2)), "An empty encounter reset twice.");
        timer.ObserveActivity(); timer.Observe(options, true, At);
        Check(timer.Observe(new(EncounterResetMode.AfterCombat, 0), false, At.AddSeconds(1)), "Zero did not reset at combat exit.");
        timer.ObserveActivity(); timer.Observe(options, true, At);
        timer.Observe(options, false, At.AddSeconds(1)); timer.ObserveActivity();
        Check(!timer.Observe(options, false, At.AddSeconds(6)), "New damage lost a race with the delayed combat flag.");
        timer.ObserveActivity();
        Check(!timer.Observe(new(EncounterResetMode.AfterCombat, 0), false, At.AddSeconds(7)), "Zero-second reset cleared newly arriving damage before the combat flag updated.");

        var old = JsonConvert.DeserializeObject<PluginConfiguration>("{}")!;
        Check(old.EncounterResetMode == EncounterResetMode.DactDefault, "An old configuration opted into reset.");
        var selected = new PluginConfiguration { EncounterResetMode = EncounterResetMode.AfterCombat, EncounterResetSeconds = 37 };
        old.RestoreFrom(JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(selected))!);
        Check(old.EncounterResetMode == EncounterResetMode.AfterCombat && old.EncounterResetSeconds == 37, "Restore lost the reset option or user-entered seconds.");
        old.ResetToDefaults("logs");
        Check(old.EncounterResetMode == EncounterResetMode.DactDefault, "Factory reset changed the default behavior.");
        foreach (var value in new[] { -1, int.MaxValue })
        {
            old.EncounterResetMode = (EncounterResetMode)123; old.EncounterResetSeconds = value; old.ApplyMigrations();
            Check(old.EncounterResetMode == EncounterResetMode.DactDefault && old.EncounterResetSeconds is >= 0 and <= 3600, "Invalid settings escaped normalization.");
        }
        RuntimeBoundaries();
        Console.WriteLine("Statistics reset: default preservation, editable delay, re-entry cancellation, zero, restore, independent ACT statistics and extension lifecycle passed.");
    }

    private static void RuntimeBoundaries()
    {
        var previous = ActGlobals.oFormActMain;
        ActGlobals.Init();
        var form = (FormActMain)RuntimeHelpers.GetUninitializedObject(typeof(FormActMain)); GC.SuppressFinalize(form);
        ActGlobals.oFormActMain = form;
        typeof(FormActMain).GetField("inCombat", Flags)!.SetValue(form, true);
        Advanced_Combat_Tracker.Resources.NotActMainFormatter.SetupEnvironment();
        var inCombat = true;
        var partyContinues = false;
        ActPlayerIdentity[] roster = [new("Self", "", "PLD", true, false)];
        try
        {
            using var runtime = new SelfHostedActRuntime(null!, DispatchProxy.Create<IPluginLog, MeterRuntimeLogProxy>(), null!,
                () => true, () => "Self", () => roster, () => null, null!, DispatchProxy.Create<IFramework, NoOpPluginLogProxy>(), null!,
                () => new(134, 0, EncounterMode.DutyAttempt, inCombat, false, false), null!, null!, null!, _ => null,
                () => partyContinues, _ => new(), () => new Dictionary<string, HtmlOverlayWindowSettings>(), () => { },
                () => false, () => false, (_, _) => false, getEncounterResetOptions: () => new(EncounterResetMode.AfterCombat, 5));
            var snapshots = new List<(ActEncounterSnapshot Data, bool Finished)>();
            var pluginEvents = new List<bool>(); var resets = 0;
            runtime.EncounterChanged += (data, finished) => snapshots.Add((data, finished));
            runtime.PluginCombatStateChanged += pluginEvents.Add;
            runtime.StatisticsReset += _ => resets++;
            runtime.UpdateFrameworkState(At);
            var original = new EncounterData("Self", "Boss room", null!);
            original.StartTimes.Add(At.LocalDateTime); original.Active = true;
            void Hit(int seconds, long damage, int swingType = 2)
            {
                var swing = new MasterSwing(swingType, false, damage, At.AddSeconds(seconds).LocalDateTime, seconds, "Hit", "Self", "damage", "Boss");
                original.AddCombatAction(swing);
                typeof(SelfHostedActRuntime).GetMethod("OnAfterCombatAction", Flags)!.Invoke(runtime, [false, new CombatActionEventArgs(swing)]);
                Check(ReferenceEquals(swing.ParentEncounter, original), "Statistics changed the shared ACT action's owner.");
            }
            Hit(0, 100); Hit(2, 200); runtime.UpdateFrameworkState(At.AddSeconds(2));
            var firstId = snapshots[^1].Data.Id;
            inCombat = false; runtime.UpdateFrameworkState(At.AddSeconds(3));
            partyContinues = true; runtime.UpdateFrameworkState(At.AddSeconds(10));
            Check(resets == 0, "Local death reset the surviving party's statistics.");
            partyContinues = false; runtime.UpdateFrameworkState(At.AddSeconds(11));
            var eventsBeforeReset = pluginEvents.Count;
            runtime.UpdateFrameworkState(At.AddSeconds(16));
            Check(resets == 1 && snapshots[^1].Finished && snapshots[^1].Data.Combatants.Single().TotalDamage == 300,
                "Timeout failed to finish the first statistics segment.");
            Check(pluginEvents.Count == eventsBeforeReset && form.InCombat && original.Active,
                "A statistics timeout changed shared ACT combat state or sent an extension combat event.");
            inCombat = true; runtime.UpdateFrameworkState(At.AddSeconds(17));
            Hit(18, 50); Hit(20, 25);
            var next = snapshots[^1].Data.Combatants.Single();
            Check(snapshots[^1].Data.Id != firstId && next.TotalDamage == 75 && next.HighestDamage == 50 && next.DamageHits == 2,
                $"The new segment reused prior totals, hit maxima or counts: {JsonConvert.SerializeObject(next)}.");
            Check(original.Items["SELF"].Damage == 375 && pluginEvents.All(finished => !finished), "Reset mutated original ACT data or ended an extension encounter.");
            var encounterLock = typeof(SelfHostedActRuntime).GetField("encounterSync", Flags)!.GetValue(runtime)!;
            Exception? endFailure = null;
            var ending = new Thread(() =>
            {
                try { typeof(SelfHostedActRuntime).GetMethod("OnAfterCombatEnd", Flags)!.Invoke(runtime, [original]); }
                catch (Exception ex) { endFailure = ex; }
            }) { IsBackground = true };
            bool lockOrderCorrect;
            lock (form.AfterCombatActionDataLock)
            {
                ending.Start();
                Check(SpinWait.SpinUntil(() => (ending.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2)),
                    "ACT end did not reach the held action-data lock.");
                // A snapshot already owns ActionDataLock; its next encounter lock
                // must stay available while the end callback waits on that snapshot.
                lockOrderCorrect = Monitor.TryEnter(encounterLock, TimeSpan.FromMilliseconds(200));
                if (lockOrderCorrect) Monitor.Exit(encounterLock);
            }
            Check(ending.Join(TimeSpan.FromSeconds(2)), "The real ACT end deadlocked with a snapshot reader.");
            if (endFailure is not null) throw endFailure;
            Check(lockOrderCorrect, "Private ACT finalization inverted the snapshot lock order.");
            Check(pluginEvents.Count(finished => finished) == 1 && snapshots[^1].Finished && snapshots[^1].Data.Combatants.Single().TotalDamage == 75,
                "The real ACT end was lost or duplicated after a statistics reset.");
        }
        finally { ActGlobals.oFormActMain = previous; }
    }

    internal static async Task AdapterAsync(string testRoot)
    {
        foreach (var mode in Enum.GetValues<EncounterMode>())
        {
            var log = DispatchProxy.Create<IPluginLog, MeterRuntimeLogProxy>();
            var framework = DispatchProxy.Create<IFramework, NoOpPluginLogProxy>();
            var state = new EncounterModeSnapshot(134, 0, mode, true, false, false);
            var config = new PluginConfiguration { EncounterResetMode = EncounterResetMode.AfterCombat, EncounterResetSeconds = 60 };
            var paths = new PluginPaths(Path.Combine(testRoot, "reset-adapter-" + mode, "DalamudActCompat"));
            var store = new EncounterStateStore(); var logger = new PluginLogger(log);
            await using var history = new EncounterService(new EncounterRepository(new JsonFileStore(), paths), store, config, logger, paths);
            var runtime = new SelfHostedActRuntime(null!, log, null!, () => true, () => "Self", () => [], () => null,
                null!, framework, null!, () => state, null!, null!, null!, _ => null, () => false, _ => new(),
                () => new Dictionary<string, HtmlOverlayWindowSettings>(), () => { }, () => false, () => false, (_, _) => false);
            await using var adapter = new IinactAdapter(runtime, logger, store, history, () => paths.CombatLogDirectory,
                framework, () => state, () => true, () => true, () => [], getEncounterResetOptions: () => new(config.EncounterResetMode, config.EncounterResetSeconds));
            ActEncounterSnapshot Snapshot(Guid id, int at, long damage, bool finished) => new(id, At.AddSeconds(at), finished ? At.AddSeconds(at + 5) : null,
                "Room", "Boss", [new("Self", "Self", "PLD", true, damage, 0, 0)])
                { EncounterMode = mode, CombatDuration = TimeSpan.FromSeconds(5), TerritoryId = 134 };
            void Publish(ActEncounterSnapshot snapshot, bool finished) => typeof(SelfHostedActRuntime).GetMethod("DispatchEncounter", Flags)!.Invoke(runtime, [snapshot, finished, false]);
            void Reset(int at) => ((Action<DateTimeOffset>)typeof(SelfHostedActRuntime).GetField("StatisticsReset", Flags)!.GetValue(runtime)!).Invoke(At.AddSeconds(at));
            var first = Guid.NewGuid(); var second = Guid.NewGuid();
            Publish(Snapshot(first, 0, 100, true), true); Publish(Snapshot(second, 20, 200, true), true);
            Check(store.GetDisplayEncounter()!.TotalDamage == 300, $"{mode}: a short combat gap reset before the selected timeout.");
            Reset(90);
            Check(store.GetDisplayEncounter() is null && store.GetDisplayEncounter(true) is null, $"{mode}: expired statistics remained visible.");
            Publish(Snapshot(Guid.NewGuid(), 100, 50, false), false);
            Publish(Snapshot(first, 0, 100, true), true);
            Check(store.GetDisplayEncounter()!.TotalDamage == 50, $"{mode}: a late old callback restored previous totals.");
            Reset(170);
            await history.DisposeAsync();
            var recent = store.GetSnapshot().Recent;
            Check(mode == EncounterMode.OpenWorld ? recent.Count == 2 && recent.Sum(item => item.TotalDamage) == 350
                : recent.Count == 1 && recent[0].SegmentRecords.Count == 2 && recent[0].SegmentRecords.Sum(item => item.TotalDamage) == 350,
                $"{mode}: reset discarded history or merged separate results.");
        }
        Console.WriteLine("Statistics adapter: all three encounter modes, short-gap accumulation, timeout clearing, late snapshots and per-reset history passed.");
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
