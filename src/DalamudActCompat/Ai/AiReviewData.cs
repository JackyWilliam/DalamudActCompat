using System.Globalization;
using System.Text;

namespace DalamudActCompat.Ai;

// Turns the player's slice of the combat log into the numbers a log reviewer (xivanalysis, the
// "警察网") looks at first: GCD uptime and gaps, cooldown use against what was possible, the
// party's two-minute windows and what the player pressed in them, DoT/buff uptime, potions and
// deaths. Computing them here keeps the model from doing arithmetic over hundreds of lines.
internal static class AiReviewData
{
    // Raid buffs by action id (verified against XIVAPI); used to find the party's burst windows.
    private static readonly HashSet<uint> RaidBuffActions =
    [
        3557, // Battle Litany
        7396, // Brotherhood
        24405, // Arcane Circle
        36957, 2248, // Dokumori, Mug
        118, 25785, // Battle Voice, Radiant Finale
        16004, 16193, 16194, 16195, 16196, 33215, 33216, 33217, 33218, // Technical Finish (all step counts)
        25801, // Searing Light
        7520, // Embolden
        34675, // Starry Muse
        7436, // Chain Stratagem
        16552, // Divination
    ];

    private const uint MedicatedStatus = 49;

    public static string Describe(AiCombatLogWindow log, double fightSeconds, IReadOnlyDictionary<string, string> partyLabels,
        Func<uint, AiActionInfo?> actionInfo)
    {
        var player = log.PlayerId!;
        var text = new StringBuilder();
        var info = new Dictionary<uint, AiActionInfo?>();
        AiActionInfo? Info(uint id) => info.TryGetValue(id, out var known) ? known : info[id] = actionInfo(id);

        // Hard casts count from the start of their cast bar, so the GCD rhythm is right for casters.
        // Auto-attacks (melee Attack 7, ranged Shot 8) are not choices the player makes.
        var casts = log.Casts.Where(cast => cast.SourceId == player && cast.ActionId is not (7 or 8) && cast.At >= -1 && cast.At <= fightSeconds + 1)
            .Select(cast => cast with { At = CastStart(log, cast) })
            .OrderBy(cast => cast.At)
            .ToArray();
        text.AppendLine("[Your casts, from the combat log; times are m:ss from the pull; recasts and charges come from your game client]");
        if (casts.Length == 0)
        {
            text.AppendLine("No casts by the player were found in the log for this fight.");
            return text.ToString();
        }

        // GCD rhythm and the longest stretches without a GCD.
        var gcds = casts.Where(cast => Info(cast.ActionId)?.IsGcd == true).ToArray();
        if (gcds.Length > 1)
        {
            var intervals = gcds.Zip(gcds.Skip(1), (a, b) => (From: a.At, Gap: b.At - a.At)).ToArray();
            var typical = Median(intervals.Select(item => item.Gap).Where(gap => gap is > 1.2 and < 4).ToArray());
            var uptime = Math.Min(1, gcds.Length * typical / Math.Max(1, fightSeconds));
            text.AppendLine(Invariant($"GCD: {gcds.Length} casts, typical interval {typical:0.00} s, estimated GCD uptime {uptime * 100:0}% of the fight."));
            var gaps = intervals.Where(item => item.Gap > typical + 1.5).OrderByDescending(item => item.Gap).Take(8).OrderBy(item => item.From).ToArray();
            if (gaps.Length > 0)
                text.AppendLine("Longest GCD gaps (start, length): " + string.Join(", ", gaps.Select(item => Invariant($"{Clock(item.From)} {item.Gap:0.0} s"))));
        }
        if (log.Interrupts.Count > 0)
            text.AppendLine($"Interrupted casts: {log.Interrupts.Count} (at {string.Join(", ", log.Interrupts.Take(10).Select(Clock))})");

        // Cooldowns: uses against the most that fit, and how late each use came after it was ready.
        var byAction = casts.GroupBy(cast => cast.ActionId).ToArray();
        var cooldownLines = new List<(double Recast, string Line)>();
        var others = new List<string>();
        var weaponskills = new List<string>();
        foreach (var group in byAction)
        {
            var first = group.First();
            var action = Info(group.Key);
            var times = group.Select(cast => cast.At).ToArray();
            if (action is { RecastSeconds: >= 20 })
            {
                var charges = Math.Max(1, action.MaxCharges);
                var possible = charges + (int)Math.Floor(Math.Max(0, fightSeconds - 1) / action.RecastSeconds);
                var line = Invariant($"- {first.ActionName} #{group.Key} ({action.RecastSeconds:0} s{(charges > 1 ? $", {charges} charges" : string.Empty)}): {times.Length} of at most {possible} possible; at {string.Join(", ", times.Select(Clock))}");
                if (charges == 1 && times.Length > 1)
                {
                    var late = times.Zip(times.Skip(1), (a, b) => b - a - action.RecastSeconds).Where(delay => delay > 0).Sum();
                    line += Invariant($"; held {late:0} s in total after it was ready");
                }
                cooldownLines.Add((action.RecastSeconds, line));
            }
            else (action?.IsGcd == true ? weaponskills : others).Add($"{first.ActionName} ×{times.Length}");
        }
        if (cooldownLines.Count > 0)
        {
            text.AppendLine("Cooldowns (recast 20 s or more), uses vs. the most that fit if used whenever ready:");
            foreach (var (_, line) in cooldownLines.OrderByDescending(item => item.Recast)) text.AppendLine(line);
        }
        if (weaponskills.Count > 0) text.AppendLine("Weaponskills and spells: " + string.Join(", ", weaponskills));
        if (others.Count > 0) text.AppendLine("Other abilities: " + string.Join(", ", others));

        // Potions show up as the Medicated status on the player.
        var potions = log.Statuses.Where(status => status.Gained && status.StatusId == MedicatedStatus && status.TargetId == player)
            .Select(status => status.At).Distinct().ToArray();
        text.AppendLine(potions.Length > 0 ? $"Potions: {string.Join(", ", potions.Select(Clock))}" : "Potions: none found.");

        var deaths = log.Deaths.Where(death => death.TargetId == player && death.At >= 0).ToArray();
        text.AppendLine(deaths.Length > 0
            ? "Your deaths: " + string.Join(", ", deaths.Select(death => $"{Clock(death.At)} ({(death.SourceName.Length > 0 ? death.SourceName : "unknown source")})"))
            : "Your deaths: none.");

        AppendUptime(text, log, player, fightSeconds);

        // The party's two-minute windows: clusters of raid buff casts, then what the player pressed in them.
        var buffs = log.Casts.Where(cast => RaidBuffActions.Contains(cast.ActionId) && cast.At >= -1 && cast.At <= fightSeconds)
            .OrderBy(cast => cast.At).ToArray();
        if (buffs.Length > 0)
        {
            text.AppendLine("Party raid buffs cast:");
            foreach (var group in buffs.GroupBy(cast => (cast.SourceName, cast.ActionId)))
            {
                var who = group.First().SourceId == player ? "你" : partyLabels.GetValueOrDefault(group.Key.SourceName, "队友");
                text.AppendLine($"- {group.First().ActionName} ({who}): {string.Join(", ", group.Select(cast => Clock(cast.At)))}");
            }
            var windows = new List<double>();
            foreach (var buff in buffs)
                if (windows.Count == 0 || buff.At - windows[^1] > 25) windows.Add(buff.At);
            text.AppendLine("What you pressed in each burst window (from the first raid buff, 20 s):");
            foreach (var from in windows.Take(8))
            {
                var inside = casts.Where(cast => cast.At >= from - 2 && cast.At < from + 20).ToArray();
                var gcdCount = inside.Count(cast => Info(cast.ActionId)?.IsGcd == true);
                text.AppendLine($"- {Clock(from)}: {gcdCount} GCDs; " + string.Join(" → ", inside.Select(cast => cast.ActionName)));
            }
        }
        else text.AppendLine("Party raid buffs cast: none found (solo, or no raid buff jobs in the party).");

        var opener = casts.Where(cast => cast.At < 25).Take(30).ToArray();
        if (opener.Length > 0)
            text.AppendLine("Opener (first 25 s): " + string.Join(", ", opener.Select(cast => $"{Clock(cast.At)} {cast.ActionName}")));
        return text.ToString();
    }

    // Uptime of the statuses the player keeps up: DoTs and debuffs on enemies, buffs on themselves.
    private static void AppendUptime(StringBuilder text, AiCombatLogWindow log, string player, double fightSeconds)
    {
        var mine = log.Statuses.Where(status => status.SourceId == player).ToArray();
        var lines = new List<(bool Self, double Uptime, string Line)>();
        foreach (var group in mine.GroupBy(status => status.StatusId))
        {
            // Pets share enemies' 4xxxxxxx ids, so a status counts as a DoT only when it never lands
            // on a player; party buffs, shields and heals reach players and are left out.
            var self = group.All(status => status.TargetId == player);
            var onEnemies = group.All(status => status.TargetId.StartsWith('4'));
            if (!self && !onEnemies) continue;
            var gains = group.Where(status => status.Gained && (self ? status.TargetId == player : status.TargetId.StartsWith('4'))).ToArray();
            if (gains.Length == 0 || gains.Max(status => status.Duration) < 10) continue;
            // Per target: a status lasts until it is removed, refreshed or its duration runs out.
            var intervals = new List<(double From, double To)>();
            foreach (var target in group.GroupBy(status => status.TargetId))
            {
                double? since = null, until = null;
                foreach (var status in target.OrderBy(status => status.At))
                {
                    if (status.Gained)
                    {
                        if (since is not null && until is not null && status.At > until) intervals.Add((since.Value, until.Value));
                        if (since is null || until is null || status.At > until) since = status.At;
                        until = status.At + (status.Duration > 0 ? status.Duration : 30);
                    }
                    else if (since is not null)
                    {
                        intervals.Add((since.Value, Math.Min(status.At, until ?? status.At)));
                        since = until = null;
                    }
                }
                if (since is not null) intervals.Add((since.Value, Math.Min(until ?? fightSeconds, fightSeconds)));
            }
            var covered = Union(intervals, fightSeconds);
            var uptime = covered / Math.Max(1, fightSeconds);
            lines.Add((self, uptime, Invariant($"{gains[0].StatusName} {uptime * 100:0}% (applied {gains.Length}×)")));
        }
        var onEnemy = lines.Where(line => !line.Self).OrderByDescending(line => line.Uptime).Take(10).Select(line => line.Line).ToArray();
        var onSelf = lines.Where(line => line.Self).OrderByDescending(line => line.Uptime).Take(12).Select(line => line.Line).ToArray();
        if (onEnemy.Length > 0) text.AppendLine("Uptime of your DoTs/debuffs on enemies: " + string.Join(", ", onEnemy));
        if (onSelf.Length > 0) text.AppendLine("Uptime of your buffs on yourself: " + string.Join(", ", onSelf));
    }

    private static double Union(List<(double From, double To)> intervals, double fightSeconds)
    {
        var total = 0d;
        var end = double.MinValue;
        foreach (var (from, to) in intervals.Select(item => (Math.Max(0, item.From), Math.Min(fightSeconds, item.To))).Where(item => item.Item2 > item.Item1).OrderBy(item => item.Item1))
        {
            if (to <= end) continue;
            total += to - Math.Max(from, end);
            end = to;
        }
        return total;
    }

    private static double CastStart(AiCombatLogWindow log, AiLogCast cast)
    {
        var start = log.CastStarts.LastOrDefault(started => started.ActionId == cast.ActionId && started.At <= cast.At && cast.At - started.At < 6);
        return start?.At ?? cast.At;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 2.5;
        Array.Sort(values);
        return values[values.Length / 2];
    }

    public static string Clock(double seconds)
    {
        var whole = (int)Math.Floor(Math.Max(0, seconds));
        return $"{whole / 60}:{whole % 60:00}";
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
