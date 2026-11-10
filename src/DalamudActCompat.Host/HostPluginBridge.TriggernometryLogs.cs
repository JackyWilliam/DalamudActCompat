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
        // TN 2.4 runs the same XML action as ActionVariableScalar with Operation/Name.
        var typed = actionType.FullName == "Triggernometry.Core.Actions.ActionVariableScalar";
        if (!typed && actionType.GetField("ActionType")?.GetValue(action)?.ToString() != "Variable" ||
            actionType.GetProperty(typed ? "Operation" : "VariableOp")?.GetValue(action)?.ToString() != "UnsetRegexUniversal" ||
            actionType.GetProperty(typed ? "Name" : "VariableName")?.GetValue(action) as string != "^U7b11a") return;
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

    // ACT hands a forwarded line to its handlers synchronously, so the entity epoch that came
    // with the line is carried on the dispatching thread rather than through ACT's signatures.
    [ThreadStatic] private static long currentLogEntityEpoch;

    internal static void SetCurrentLogEntityEpoch(long epoch) => currentLogEntityEpoch = epoch;

    public static void ProcessTriggernometryLog(object plugin, bool isImport, string line, string zone)
    {
        if (!IsAllowed("triggernometry", "ReadCombatLogs")) return;
        ObserveDrawingLog(plugin, line, "received", isImport ? "import" : "live");
        TriggerLogBindings.GetValue(plugin, owner => new(owner)).Accept(isImport, line, zone, currentLogEntityEpoch);
    }

    private static void ResetTriggernometryLogWaits()
    {
        foreach (var binding in TriggerLogBindings) binding.Value.Reset();
    }

    internal static void FlushTriggernometryLogOrder()
    {
        foreach (var binding in TriggerLogBindings) binding.Value.FlushOrder();
    }

    private static void PumpTriggernometryLogWaits()
    {
        foreach (var binding in TriggerLogBindings) binding.Value.Pump();
    }

    private sealed class TriggernometryLogBinding : IDisposable
    {
        private readonly TriggernometryActorControlGate gate;
        private readonly TriggernometryEntityFreshnessBarrier freshness;
        // Non-zero while TN is handling a delivered line; only read or written under sync.
        private int dispatchDepth;
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
            var handler = (Action<bool, string, string>)original.CreateDelegate(typeof(Action<bool, string, string>), plugin);
            Action<bool, string, string> callback = (isImport, line, zone) =>
            {
                dispatchDepth++;
                try { handler(isImport, line, zone); }
                finally { dispatchDepth--; }
            };
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
                cancelReplay: () => Volatile.Write(ref awaitingInitialization, null),
                entitySeen: FfxivRepositoryInstance.HasSeenEntity);
            // Freshness first, then the actor wait: a line reaches the gate in arrival order
            // once the capture taken after it is applied, and the gate still holds it if an
            // actor it names has never appeared.
            freshness = new(
                FfxivRepositoryInstance.HasReachedEntityEpoch,
                gate.Accept,
                message => Console.WriteLine("Triggernometry: " + message));
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

        internal void Accept(bool isImport, string line, string zone, long entityEpoch)
        {
            lock (sync)
            {
                if (disposed) return;
                // A line TN raises while handling a delivered one belongs to that delivery;
                // queueing it behind lines that still wait would reorder upstream callbacks.
                if (dispatchDepth != 0) gate.Accept(isImport, line, zone);
                else freshness.Accept(isImport, line, zone, entityEpoch);
                timer.Change(HasPending ? 20 : Timeout.Infinite, Timeout.Infinite);
            }
        }

        private bool HasPending => gate.HasPending || freshness.HasPending;

        internal void FlushOrder()
        {
            lock (sync)
            {
                if (disposed) return;
                try { freshness.Flush(); }
                catch (Exception ex) { ReportException("triggernometry", "Entity freshness flush", ex); }
                timer.Change(HasPending ? 20 : Timeout.Infinite, Timeout.Infinite);
            }
        }

        internal void Pump()
        {
            lock (sync)
            {
                if (disposed) return;
                try
                {
                    if (IsAllowed("triggernometry", "ReadCombatLogs"))
                    {
                        freshness.Pump();
                        gate.Pump();
                    }
                    else
                    {
                        freshness.Reset();
                        gate.Reset();
                    }
                }
                catch (Exception ex) { ReportException("triggernometry", "ActorControl entity wait", ex); }
                timer.Change(HasPending ? 20 : Timeout.Infinite, Timeout.Infinite);
            }
        }

        internal void Reset()
        {
            lock (sync)
            {
                freshness.Reset();
                gate.Reset();
                if (!disposed) timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                freshness.Reset();
                gate.Reset();
                timer.Dispose();
            }
        }
    }
}
