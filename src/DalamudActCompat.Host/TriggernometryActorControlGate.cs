using System.Globalization;
using System.Text.RegularExpressions;

namespace DalamudActCompat.Host;

/// <summary>Keeps a short, ordered TN-only backlog while a U7b scene object crosses IPC.</summary>
internal sealed class TriggernometryActorControlGate(
    Func<uint> territory,
    Func<uint, bool> entityReady,
    Action<bool, string, string> deliver,
    Action<string> diagnostic,
    Func<long>? clock = null,
    Action<string>? beforeReplay = null,
    Func<bool>? replayReady = null,
    Action? cancelReplay = null)
{
    internal const int MaximumWaitMilliseconds = 500;
    internal const int MaximumLines = 1024;
    internal const int MaximumCharacters = 1024 * 1024;
    private static readonly Regex SceneControl = new(
        @"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \S+ 111:(?<id>4[0-9A-Fa-f]{7}):019D:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));
    private readonly object sync = new();
    private readonly Queue<PendingLine> pending = new();
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private long started;
    private int characters;
    private bool delivering;

    private bool ReplayReady => replayReady?.Invoke() ?? true;
    internal bool HasPending { get { lock (sync) return pending.Count != 0 || !ReplayReady; } }

    internal void Accept(bool isImport, string line, string zone)
    {
        lock (sync)
        {
            // Preserve synchronous, log-derived callbacks inside upstream dispatch. Other
            // threads still serialize here; only reentrant calls take this path.
            if (delivering || isImport || string.IsNullOrEmpty(line))
            {
                deliver(isImport, line, zone);
                return;
            }

            DiscardAfterTerritoryChange();
            Drain();
            var actor = MissingSceneActor(line);
            if (pending.Count == 0 && ReplayReady && actor == 0)
            {
                Deliver(new(isImport, line, zone));
                return;
            }

            if (pending.Count == 0 && ReplayReady) started = now();
            // Never let missing/invalid entities turn this compatibility wait into an
            // unbounded log queue. On expiry/overflow preserve upstream's original behavior.
            if (pending.Count >= MaximumLines || characters + (long)line.Length > MaximumCharacters)
            {
                diagnostic("U7b ActorControl wait reached its queue limit; forwarding original logs.");
                Drain(force: true);
                Deliver(new(isImport, line, zone));
                return;
            }
            pending.Enqueue(new(isImport, line, zone));
            characters += line.Length;
        }
    }

    internal void Pump()
    {
        lock (sync)
        {
            DiscardAfterTerritoryChange();
            Drain();
        }
    }

    internal void Reset()
    {
        lock (sync)
        {
            pending.Clear();
            characters = 0;
            cancelReplay?.Invoke();
        }
    }

    private void DiscardAfterTerritoryChange()
    {
        // An actor ID may be reused in the next zone. Never complete an old initialization
        // against that new object table, even if the explicit zone event trails its snapshot.
        if (territory() != 1363) Reset();
    }

    private uint MissingSceneActor(string line)
    {
        if (territory() != 1363 || !line.Contains(" 111:", StringComparison.Ordinal)) return 0;
        var match = SceneControl.Match(line);
        if (!match.Success) return 0;
        var actor = uint.Parse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return entityReady(actor) ? 0 : actor;
    }

    private void Drain(bool force = false)
    {
        if ((pending.Count == 0 && ReplayReady) || delivering) return;
        if (!force && now() - started >= MaximumWaitMilliseconds)
        {
            diagnostic("U7b ActorControl prerequisites were not ready within 500 ms; forwarding original logs.");
            force = true;
        }
        if (force) cancelReplay?.Invoke();
        while (pending.TryPeek(out var next))
        {
            if (!force && (!ReplayReady || MissingSceneActor(next.Line) != 0)) break;
            pending.Dequeue();
            characters -= next.Line.Length;
            if (!force) beforeReplay?.Invoke(next.Line);
            Deliver(next);
        }
    }

    private void Deliver(PendingLine item)
    {
        var previous = delivering;
        delivering = true;
        try { deliver(item.IsImport, item.Line, item.Zone); }
        finally { delivering = previous; }
    }

    private sealed record PendingLine(bool IsImport, string Line, string Zone);
}
