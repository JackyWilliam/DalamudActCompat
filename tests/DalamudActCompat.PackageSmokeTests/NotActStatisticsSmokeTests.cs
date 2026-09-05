using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Advanced_Combat_Tracker;

internal static class NotActStatisticsSmokeTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        ActGlobals.Init();
        Advanced_Combat_Tracker.Resources.NotActMainFormatter.SetupEnvironment();
        var failures = new List<Exception>();
        foreach (var test in new Action[] { DeathCacheDoesNotAccumulate, WriterHonorsSnapshotLock, StoppedWriterDoesNotMutate })
        {
            try { test(); Console.WriteLine($"PASS {test.Method.Name}"); }
            catch (Exception ex) { failures.Add(ex); Console.WriteLine($"FAIL {test.Method.Name}: {ex.Message}"); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void DeathCacheDoesNotAccumulate()
    {
        var combatant = new CombatantData("Player", new EncounterData("Player", "Offline", false, null!));
        combatant.AddReverseCombatAction(new MasterSwing(2, false, Dnum.Death, DateTime.UtcNow, 1,
            "Nonstandard death label", "Boss", "damage", "Player"));
        Check(combatant.Deaths == 1, "Initial death count must be one.");
        // The fallback is used when a parser/extension does not create the localized killing bucket.
        for (var i = 0; i < 10; i++)
        {
            combatant.InvalidateCachedValues();
            Check(combatant.Deaths == 1, "Recomputing a death snapshot added the same death again.");
        }
        combatant.AddReverseCombatAction(new MasterSwing(2, false, 100, DateTime.UtcNow, 2,
            "Ordinary hit", "Boss", "damage", "Player"));
        Check(combatant.Deaths == 1, "An ordinary incoming hit changed the death count.");
    }

    private static void WriterHonorsSnapshotLock()
        => AssertWriterSnapshotLock(stopBeforeRelease: false);

    private static void StoppedWriterDoesNotMutate()
        => AssertWriterSnapshotLock(stopBeforeRelease: true);

    private static void AssertWriterSnapshotLock(bool stopBeforeRelease)
    {
        // Avoid the Form constructor: only managed combat aggregation runs, never native UI or game parsing.
        var form = (FormActMain)RuntimeHelpers.GetUninitializedObject(typeof(FormActMain));
        GC.SuppressFinalize(form);
        var queue = new ConcurrentQueue<MasterSwing>();
        Set(form, "afterActionsQueue", queue);
        Set(form, "<PluginLog>k__BackingField", new TestLogger());
        var healthField = typeof(FormActMain).GetField("callbackHealth", Flags)!;
        healthField.SetValue(form, Activator.CreateInstance(healthField.FieldType));
        Set(form, "pluginActive", true);
        form.ActiveZone = new ZoneData(DateTime.UtcNow, "Offline", false, false, false);
        using var delivered = new ManualResetEventSlim();
        var callbackHeldLock = false;
        form.AfterCombatAction += (_, _) =>
        {
            callbackHeldLock = Monitor.IsEntered(form.AfterCombatActionDataLock);
            delivered.Set();
        };
        queue.Enqueue(new MasterSwing(2, false, 100, DateTime.UtcNow, 3, "Hit", "Player", "damage", "Boss"));
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { typeof(FormActMain).GetMethod("ThreadAfterCombatAction", Flags)!.Invoke(form, null); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        try
        {
            lock (form.AfterCombatActionDataLock)
            {
                worker.Start();
                Check(SpinWait.SpinUntil(() => queue.IsEmpty, TimeSpan.FromSeconds(2)), "Combat worker did not dequeue.");
                Check(!delivered.Wait(TimeSpan.FromMilliseconds(150)), "Combat mutation ignored the snapshot lock.");
                Check(form.ActiveZone.Items[^1].Items.Count == 0, "Combat collections changed during a locked snapshot.");
                if (stopBeforeRelease) typeof(FormActMain).GetMethod("Exit", Flags)!.Invoke(form, null);
            }
            if (stopBeforeRelease)
            {
                Check(worker.Join(TimeSpan.FromSeconds(2)), "Retired combat worker did not exit after lock release.");
                Check(!delivered.IsSet && form.ActiveZone.Items[^1].Items.Count == 0, "Retired worker mutated combat data or delivered a callback.");
            }
            else
            {
                Check(delivered.Wait(TimeSpan.FromSeconds(2)), "Combat did not resume after the snapshot released its lock.");
                Check(!callbackHeldLock, "Third-party callbacks must run outside the aggregation lock to avoid lock inversion.");
                Check(form.ActiveZone.Items[^1].Items.Count == 2, "Outgoing/incoming combatants were not aggregated.");
            }
        }
        finally
        {
            typeof(FormActMain).GetMethod("Exit", Flags)!.Invoke(form, null);
            Check(worker.Join(TimeSpan.FromSeconds(2)), "Combat worker failed to stop.");
        }
        if (failure is not null) throw failure;
    }

    private static void Set(FormActMain form, string field, object value)
        => typeof(FormActMain).GetField(field, Flags)!.SetValue(form, value);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class TestLogger : IActLogger
    {
        public void Error(string message) => Console.WriteLine(message);
        public void Error(Exception exception, string message) => Console.WriteLine($"{message}: {exception}");
        public void Warning(string message) => Console.WriteLine(message);
        public void Verbose(string message) { }
        public void Verbose(Exception exception, string message) { }
    }
}
