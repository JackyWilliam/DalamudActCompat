namespace DalamudActCompat.Encounters;

// Phase numbers as FFLogs gives them, for the fights where counting "the main boss changed" (what
// ProgressionTracker does elsewhere) disagrees with it: an intermission's adds, two bosses sharing
// the arena and a boss coming back for a later phase each looked like one more phase, so Futures
// Rewritten read up to P7 and The Epic of Alexander up to P8. Here a phase starts only when that
// phase's own boss shows up.
//
// Why not the weather, which the game does change at some of these transitions: it does not
// change at all of them, nothing lists which weather belongs to which phase, and FFLogs cannot be
// using it either (the ACT log it reads has no weather line), so its numbers would not follow.
internal static class FflogsPhases
{
    // A phase's bosses, by BNpcName id. Refilled marks a phase that brings no new name: it starts
    // when a boss of the phase before comes back with its HP bar full again.
    private readonly record struct Phase(uint[] Bosses, bool Refilled = false);

    private static Phase Boss(params uint[] ids) => new(ids);

    private static Phase Refill(params uint[] ids) => new(ids, true);

    // How much of its bar a boss has to regain. Nothing heals these bosses mid-phase, so half a
    // bar back means the bar started over. A boss that left its phase with more than half of it
    // goes unnoticed, and the pull then reads the phase before.
    private const double RefillRise = .5;

    // TerritoryType → its phases in order, P1 first. The game has several name rows for some
    // bosses, so every row with the name is listed. Intermissions have no number on FFLogs; they
    // stay in the phase before until the next phase's boss can be targeted.
    private static readonly Dictionary<uint, Phase[]> Fights = new()
    {
        // The Unending Coil of Bahamut: P1 Twintania, P2 Nael deus Darnus, P3 Bahamut Prime,
        // P4 Twintania and Nael together, P5 Bahamut Prime again (golden).
        [733] = [Boss(1482), Boss(2612), Boss(3210), Boss(1482, 2612), Boss(3210)],
        // The Weapon's Refrain: P1 Garuda, P2 Ifrit, P3 Titan, P4 Lahabrea, P5 The Ultima Weapon.
        // Counting main-boss changes read P5 by Titan: Garuda's sisters and plumes and Ifrit's
        // nails each took over as the largest target while the primal was away.
        [777] = [Boss(1644, 7893, 9287), Boss(1185, 9288), Boss(1801, 8350), Boss(2143, 11444), Boss(2137)],
        // The Epic of Alexander: P1 Living Liquid, P2 Brute Justice and Cruise Chaser, P3
        // Alexander Prime, P4 Perfect Alexander. The pair return as adds late in P3 and stay P3.
        [887] = [Boss(3765, 9211), Boss(4708, 9216, 5186, 9218), Boss(5193, 9220), Boss(9042)],
        // Dragonsong's Reprise: P1 Adelphel, Grinnaux and Charibert, P2 King Thordan, P3 Nidhogg,
        // P4 the Eyes, P5 King Thordan again, P6 Nidhogg and Hraesvelgr, P7 Dragon-king Thordan.
        // Charibert in the intermission after P4 belongs to no later phase and moves nothing.
        [968] =
        [
            Boss(3634, 3639, 3642), Boss(3632), Boss(3458), Boss(11317, 11318), Boss(3632), Boss(3458, 4954), Boss(11319),
        ],
        // The Omega Protocol: P1 Omega, P2 Omega-M and Omega-F, P3 Omega, P4 the same Omega
        // rebooted with full HP (Blue Screen), P5 Omega-M, P6 Alpha Omega.
        [1122] =
        [
            Boss(7635, 7636, 7666, 7695), Boss(7633, 7634, 12257, 12258), Boss(7635, 7636, 7666, 7695),
            Refill(7635, 7636, 7666, 7695), Boss(7633, 7634, 12257, 12258), Boss(12256),
        ],
        // Futures Rewritten: P1 Fatebreaker … P5 Pandora. The Ice Veil / crystals intermission
        // stays in P2. P4 brings back Usurper of Frost (12809) beside Oracle of Darkness (9832);
        // 13700 is the pair's shared name.
        [1238] = [Boss(9707), Boss(12809), Boss(9832), Boss(12809, 13700), Boss(13561)],
        // Dancing Mad: every phase but P3 (Chaos and Exdeath) is Kefka under the one name, so
        // P2 (god form) and P5 are only told apart by his HP bar starting over.
        [1363] = [Boss(7131), Refill(7131), Boss(7691, 6052), Boss(7131), Refill(7131)],
    };

    internal static bool Covers(uint territory) => Fights.ContainsKey(territory);

    // The phase to be in after seeing a boss for which `isBoss` holds, starting from `current`.
    // A boss of the current phase never moves it on, so Usurper of Frost during P2 stays P2 even
    // though it also starts P4; any other boss moves to the first later phase it starts.
    internal static int Next(uint territory, int current, Func<uint, bool> isBoss)
    {
        if (!Fights.TryGetValue(territory, out var phases) || current < 1 || current > phases.Length) return current;
        if (phases[current - 1].Bosses.Any(isBoss)) return current;
        for (var index = current; index < phases.Length; index++)
            if (!phases[index].Refilled && phases[index].Bosses.Any(isBoss)) return index + 1;
        return current;
    }

    // Next for a live pull, which also knows the boss's HP and so can tell a Refilled phase.
    // `lowestHp` is the caller's state for the pull (1 at the start): the least HP, as a fraction,
    // the current phase's bosses have shown.
    internal static int Next(uint territory, int current, uint nameId, double hpFraction, ref double lowestHp)
    {
        var next = Next(territory, current, id => id == nameId);
        if (next != current)
        {
            lowestHp = hpFraction;
            return next;
        }

        if (!Fights.TryGetValue(territory, out var phases) || current < 1 || current > phases.Length ||
            !phases[current - 1].Bosses.Contains(nameId)) return current;
        if (current < phases.Length && phases[current].Refilled && phases[current].Bosses.Contains(nameId) &&
            hpFraction - lowestHp >= RefillRise)
        {
            lowestHp = hpFraction;
            return current + 1;
        }

        lowestHp = Math.Min(lowestHp, hpFraction);
        return current;
    }

    // Pulls recorded before a fight had its table only kept the main boss names in order.
    // Replaying them through the same rule, with the names this client gives those ids, recovers
    // FFLogs' phase, except that names cannot show a Refilled phase (such a pull reads the phase
    // before it). A pull none of whose names match (recorded in another client language) keeps
    // its old count.
    internal static ProgressionPull Restore(ProgressionPull pull, Func<uint, string?> bossName)
    {
        if (pull.FflogsPhase > 0 || !Fights.TryGetValue(pull.TerritoryId, out var phases)) return pull;
        var phase = 1;
        var matched = false;
        foreach (var boss in pull.Bosses)
        {
            bool IsBoss(uint id) => string.Equals(bossName(id)?.Trim(), boss.Trim(), StringComparison.OrdinalIgnoreCase);
            // A P1 boss counts as a match too: a P1 wipe whose adds were each counted as a phase
            // goes back to P1 instead of keeping that count.
            matched = matched || phases.Any(candidate => candidate.Bosses.Any(IsBoss));
            phase = Next(pull.TerritoryId, phase, IsBoss);
        }
        return matched || pull.Bosses.Count <= 1 ? pull with { FflogsPhase = phase } : pull;
    }
}
