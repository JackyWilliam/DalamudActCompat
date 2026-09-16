using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace DalamudActCompat.Host;

public static partial class HostPluginBridge
{
    private static readonly ConditionalWeakTable<object, TriggernometryLogBinding> TriggerLogBindings = new();
    private static readonly Regex U7bInitialization = new(
        @"^(?<time>\[\d{2}:\d{2}:\d{2}\.\d{3}\]) \S+ 111:(?<id>4[0-9A-Fa-f]{7}):019D:[^:]*:2:",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex U7bInitializationConverted = new(
        @"^(?<time>\[\d{2}:\d{2}:\d{2}\.\d{3}\]) \S+ 1019D:(?<id>4[0-9A-Fa-f]{7}):[^:]*:[^:]*:2015156:(?:[^:]*:){5}2:",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static void CompleteTriggernometryInitializationAction(object action, object context)
    {
        // This observer runs outside TN's action exception handler. A changed upstream
        // context must fall back to the bounded wait, never terminate its action worker.
        try { ObserveTriggernometryInitializationAction(action, context); }
        catch (Exception ex) { ReportException("triggernometry", "ActorControl initialization observer", ex); }
    }

    private static void ObserveTriggernometryInitializationAction(object action, object context)
    {
        // TN's event queue and action queue are separate. Releasing all buffered logs at
        // once can evaluate the ice condition before the phase's variable actions finish.
        // Observe completion of the original initializer's final clear, without changing
        // its conditions, variables, ID or actions, and never wait on TN's worker thread.
        if (!TriggerLogBindings.Any(pair => pair.Value.AwaitingInitialization)) return;
        var actionType = action.GetType();
        if (actionType.GetField("ActionType")?.GetValue(action)?.ToString() != "Variable" ||
            actionType.GetProperty("VariableOp")?.GetValue(action)?.ToString() != "UnsetRegexUniversal" ||
            actionType.GetProperty("VariableName")?.GetValue(action) as string != "^U7b11a") return;
        const BindingFlags contextMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var text = context.GetType().GetField("triggeredText", contextMembers)?.GetValue(context) as string;
        var match = U7bInitializationConverted.Match(text ?? string.Empty);
        if (!match.Success) return;
        var plugin = context.GetType().GetProperty("Plugin", contextMembers)?.GetValue(context);
        if (plugin is not null && TriggerLogBindings.TryGetValue(plugin, out var binding))
            binding.CompleteInitialization(InitializationKey(match));
    }

    // The upstream converter formats hexadecimal IDs in uppercase even when raw input
    // used lowercase. Both sides must identify the same initialization event.
    private static string InitializationKey(Match match)
        => match.Groups["time"].Value + match.Groups["id"].Value.ToUpperInvariant();

    public static void ProcessTriggernometryLog(object plugin, bool isImport, string line, string zone)
    {
        if (!IsAllowed("triggernometry", "ReadCombatLogs")) return;
        TriggerLogBindings.GetValue(plugin, owner => new(owner)).Accept(isImport, line, zone);
    }

    private static void ResetTriggernometryLogWaits()
    {
        foreach (var binding in TriggerLogBindings) binding.Value.Reset();
    }

    private static void PumpTriggernometryLogWaits()
    {
        foreach (var binding in TriggerLogBindings) binding.Value.Pump();
    }

    private sealed class TriggernometryLogBinding : IDisposable
    {
        private readonly TriggernometryActorControlGate gate;
        private readonly System.Threading.Timer timer;
        private bool disposed;
        private readonly object sync = new();
        private string? awaitingInitialization;
        internal bool AwaitingInitialization => Volatile.Read(ref awaitingInitialization) is not null;

        internal TriggernometryLogBinding(object plugin)
        {
            var original = plugin.GetType().GetMethod(
                "OnLogLineRead__DalamudActCompatOrdered", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(plugin.GetType().FullName, "OnLogLineRead__DalamudActCompatOrdered");
            var callback = (Action<bool, string, string>)original.CreateDelegate(typeof(Action<bool, string, string>), plugin);
            gate = new(
                FfxivRepositoryInstance.GetCurrentTerritoryID,
                id => FfxivRepositoryInstance.GetCombatantList().Any(x => x.ID == id && x.BNpcID != 0),
                callback,
                message => Console.WriteLine("Triggernometry: " + message),
                beforeReplay: line =>
                {
                    var match = U7bInitialization.Match(line);
                    if (!match.Success) return;
                    var id = uint.Parse(match.Groups["id"].Value, System.Globalization.NumberStyles.HexNumber);
                    if (FfxivRepositoryInstance.GetCombatantList().Any(x => x.ID == id && x.BNpcID == 2015156))
                        Volatile.Write(ref awaitingInitialization, InitializationKey(match));
                },
                replayReady: () => !AwaitingInitialization,
                cancelReplay: () => Volatile.Write(ref awaitingInitialization, null));
            // A missing object must expire even if no more snapshots or logs arrive.
            timer = new(_ => Pump(), null, Timeout.Infinite, Timeout.Infinite);
        }

        internal void CompleteInitialization(string key)
        {
            var expected = Volatile.Read(ref awaitingInitialization);
            if (expected is not null && string.Equals(expected, key, StringComparison.Ordinal) &&
                ReferenceEquals(Interlocked.CompareExchange(ref awaitingInitialization, null, expected), expected))
                Console.WriteLine("Triggernometry: U7b P1 initialization completed after waiting for its scene entity.");
        }

        internal void Accept(bool isImport, string line, string zone)
        {
            lock (sync)
            {
                if (disposed) return;
                gate.Accept(isImport, line, zone);
                timer.Change(gate.HasPending ? 20 : Timeout.Infinite, Timeout.Infinite);
            }
        }

        internal void Pump()
        {
            lock (sync)
            {
                if (disposed) return;
                try
                {
                    if (IsAllowed("triggernometry", "ReadCombatLogs")) gate.Pump();
                    else gate.Reset();
                }
                catch (Exception ex) { ReportException("triggernometry", "ActorControl entity wait", ex); }
                timer.Change(gate.HasPending ? 20 : Timeout.Infinite, Timeout.Infinite);
            }
        }

        internal void Reset()
        {
            lock (sync)
            {
                gate.Reset();
                if (!disposed) timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                gate.Reset();
                timer.Dispose();
            }
        }
    }
}
