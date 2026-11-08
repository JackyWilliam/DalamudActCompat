using System.Globalization;
using System.Text.RegularExpressions;

namespace DalamudActCompat.Host;

/// <summary>
/// Keeps a short, ordered TN-only backlog while an actor referenced by a log line crosses
/// IPC. Network logs are produced as packets arrive, while the actor snapshot follows on a
/// later game frame; TN reads <c>_entity</c> synchronously and silently skips a missing one.
/// </summary>
internal sealed class TriggernometryActorControlGate(
    Func<uint> territory,
    Func<uint, bool> entityReady,
    Action<bool, string, string> deliver,
    Action<string> diagnostic,
    Func<long>? clock = null,
    Action<string>? beforeReplay = null,
    Func<bool>? replayReady = null,
    Action? cancelReplay = null,
    Func<uint, bool>? entitySeen = null)
{
    internal const int MaximumWaitMilliseconds = 500;
    // A snapshot normally follows within one or two frames. An actor that is still absent
    // after this bound is not coming, so it is remembered and never delays logs again.
    internal const int MaximumEntityWaitMilliseconds = 200;
    internal const int MaximumLines = 1024;
    internal const int MaximumCharacters = 1024 * 1024;
    private const uint U7bTerritory = 1363;
    private static readonly Regex SceneControl = new(
        @"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \S+ 111:(?<id>4[0-9A-Fa-f]{7}):019D:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));
    // Non-player actors (BNPCs, scene objects, pets) are 0x4xxxxxxx in ACT log fields, but so is
    // much that is not an actor: a status's remaining time is a float whose hex starts with 4 from
    // 2 s up (37/38 lines), and damage (21/22), gauge (31) and memory values (261) can be too.
    // None of those ever reaches an entity snapshot, so reading them as actors held each such line
    // for the full wait, several times a second in combat. Only the fields ACT uses for actor IDs
    // count, per line type (the hex code TN sees; field 1 is the first after it). Other types never wait.
    private static readonly Dictionary<string, int[]> ActorFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["03"] = [1, 5],      // AddCombatant: id, owner
        ["14"] = [1, 5],      // StartsCasting: source, target
        ["15"] = [1, 5, 46],  // ActionEffect: source, target, owner
        ["16"] = [1, 5, 46],  // AOEActionEffect: source, target, owner
        ["17"] = [1],         // CancelAction: source
        ["18"] = [1, 16],     // DoTHoT: target, source
        ["19"] = [1, 3],      // Death: target, source
        ["1A"] = [4, 6],      // StatusAdd: source, target
        ["1B"] = [1],         // TargetIcon (head marker): target
        ["1D"] = [3, 5],      // SignMarker: by, target
        ["1E"] = [4, 6],      // StatusRemove: source, target
        ["22"] = [1, 3],      // NameToggle: id, target
        ["23"] = [1, 3],      // Tether: source, target
        ["25"] = [1],         // EffectResult: target
        ["26"] = [1],         // StatusList: target
        ["27"] = [1],         // UpdateHp: id
        ["105"] = [2],        // CombatantMemory (261): change, id
        ["107"] = [1],        // StartsUsingExtra (263): source
        ["108"] = [1, 9],     // AbilityExtra (264): source, animation target
        ["10A"] = [1],        // NpcYell (266): npc
        ["10B"] = [1],        // BattleTalk2 (267): npc
        ["10E"] = [1],        // ActorMove (270): id
        ["10F"] = [1],        // ActorSetPos (271): id
        ["110"] = [1, 2],     // SpawnNpcExtra (272): id, parent
        ["111"] = [1],        // ActorControlExtra (273): id
        ["112"] = [1],        // ActorControlSelfExtra (274): id
    };
    private static readonly Regex LineType = new(
        @"^\[[^\]]*\] \S+ (?<type>[0-9A-Fa-f]{2,3}):",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));
    // Removal lines describe actors that may already be gone from the snapshot.
    private static readonly Regex RemovalLine = new(
        @"^\[[^\]]*\] \S+ (?:04:|105:Remove:)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));
    private readonly object sync = new();
    private readonly Queue<PendingLine> pending = new();
    private readonly HashSet<uint> expiredActors = [];
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private long started;
    private int waitLimit = MaximumWaitMilliseconds;
    private int characters;
    private bool delivering;
    private uint? lastTerritory;

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
            var actor = MissingActor(line, out var scene);
            if (pending.Count == 0 && ReplayReady && actor == 0)
            {
                Deliver(new(isImport, line, zone));
                return;
            }

            if (pending.Count == 0 && ReplayReady)
            {
                started = now();
                waitLimit = scene ? MaximumWaitMilliseconds : MaximumEntityWaitMilliseconds;
            }
            else if (scene)
            {
                waitLimit = MaximumWaitMilliseconds;
            }

            // Never let missing/invalid entities turn this compatibility wait into an
            // unbounded log queue. On expiry/overflow preserve upstream's original behavior.
            if (pending.Count >= MaximumLines || characters + (long)line.Length > MaximumCharacters)
            {
                diagnostic("Entity wait reached its queue limit; forwarding original logs.");
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
        // An actor ID may be reused in the next zone. Never complete an old wait against
        // that new object table, even if the explicit zone event trails its snapshot.
        var current = territory();
        if (lastTerritory != current)
        {
            lastTerritory = current;
            expiredActors.Clear();
            Reset();
        }
    }

    private uint MissingActor(string line, out bool scene)
    {
        var actor = MissingSceneActor(line);
        scene = actor != 0;
        return scene ? actor : MissingReferencedActor(line);
    }

    private uint MissingSceneActor(string line)
    {
        if (territory() != U7bTerritory || !line.Contains(" 111:", StringComparison.Ordinal)) return 0;
        var match = SceneControl.Match(line);
        if (!match.Success) return 0;
        var actor = uint.Parse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return entityReady(actor) ? 0 : actor;
    }

    private uint MissingReferencedActor(string line)
    {
        if (entitySeen is null || RemovalLine.IsMatch(line)) return 0;
        var type = LineType.Match(line);
        if (!type.Success || !ActorFields.TryGetValue(type.Groups["type"].Value, out var fields)) return 0;
        var start = type.Groups["type"].Index;
        // fields are in ascending order, so the scan stops after the last one this type uses.
        for (var index = 0; start <= line.Length && index <= fields[^1]; index++)
        {
            var end = line.IndexOf(':', start);
            if (end < 0) end = line.Length;
            if (Array.IndexOf(fields, index) >= 0 && end - start == 8 && line[start] == '4' &&
                uint.TryParse(line.AsSpan(start, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var actor) &&
                !expiredActors.Contains(actor) && !entitySeen(actor))
                return actor;
            start = end + 1;
        }

        return 0;
    }

    private void Drain(bool force = false)
    {
        if ((pending.Count == 0 && ReplayReady) || delivering) return;
        if (!force && now() - started >= waitLimit)
        {
            diagnostic(waitLimit >= MaximumWaitMilliseconds
                ? "U7b ActorControl prerequisites were not ready within 500 ms; forwarding original logs."
                : $"Referenced entities were not ready within {MaximumEntityWaitMilliseconds} ms; forwarding original logs.");
            ExpirePendingActors();
            force = true;
        }
        if (force) cancelReplay?.Invoke();
        while (pending.TryPeek(out var next))
        {
            if (!force && (!ReplayReady || MissingActor(next.Line, out _) != 0)) break;
            pending.Dequeue();
            characters -= next.Line.Length;
            if (!force) beforeReplay?.Invoke(next.Line);
            Deliver(next);
        }
    }

    private void ExpirePendingActors()
    {
        foreach (var line in pending)
        {
            var actor = MissingReferencedActor(line.Line);
            while (actor != 0)
            {
                expiredActors.Add(actor);
                actor = MissingReferencedActor(line.Line);
            }
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
