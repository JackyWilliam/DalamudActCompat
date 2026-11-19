using System.Text.Json;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Storage;

// 开荒进度 phases must match FFLogs where FflogsPhases knows the fight. Futures Rewritten used to
// read up to P7: its Ice Veil intermission and the bosses returning in P4 each counted as a phase.
internal static class ProgressionPhaseSmokeTests
{
    private const uint Fru = 1238;
    private const uint Fatebreaker = 9707, Usurper = 12809, Oracle = 9832, Pandora = 13561, IceVeil = 9358, CrystalOfLight = 13555;
    private const uint Uwu = 777;
    private const uint Garuda = 1644, Chirada = 1645, Suparna = 1646, Ifrit = 1185, InfernalNail = 1186, Titan = 1801,
        Lahabrea = 2143, UltimaWeapon = 2137;
    private const uint Tea = 887;
    private const uint LivingLiquid = 9211, LiquidHand = 9212, JagdDoll = 9214, BruteJustice = 9216, CruiseChaser = 9218,
        AlexanderPrime = 9220, PerfectAlexander = 9042;
    private const uint Ucob = 733;
    private const uint Twintania = 1482, Nael = 2612, BahamutPrime = 3210;
    private const uint Dsr = 968;
    private const uint Adelphel = 3634, Grinnaux = 3639, Charibert = 3642, KingThordan = 3632, Nidhogg = 3458, LeftEye = 11317,
        RightEye = 11318, Hraesvelgr = 4954, DragonKing = 11319;
    private const uint Top = 1122;
    private const uint Omega = 7695, OmegaM = 7633, OmegaF = 7634, FinalOmega = 7636, DynamisOmegaM = 12257, AlphaOmega = 12256;
    private const uint DancingMad = 1363;
    private const uint Kefka = 7131, Chaos = 7691, Exdeath = 6052, NeoExdeath = 6055;
    // A duty FflogsPhases has no table for.
    private const uint Uncovered = 1257;

    internal static async Task RunAsync(string testRoot)
    {
        // What a pull sees sample by sample (all targetable enemies) → FFLogs' phase after it.
        Replay("Futures Rewritten", Fru,
        [
            ([Fatebreaker], 1),
            ([Usurper], 2),
            ([IceVeil, CrystalOfLight], 2),
            // Usurper of Frost also starts P4, but while it is P2's boss it must not skip ahead.
            ([Usurper], 2),
            ([Oracle], 3),
            ([Oracle], 3),
            ([Oracle, Usurper], 4),
            ([Oracle], 4),
            ([Pandora], 5),
        ]);
        Check(FflogsPhases.Next(Uncovered, 3, _ => true) == 3 && !FflogsPhases.Covers(Uncovered), "A fight without FFLogs phase starts changed phase.");

        // The Weapon's Refrain: a player reported P5 on first reaching Titan. Garuda's sisters and
        // Ifrit's nails become the largest target while the primal is away; they are not phases.
        Replay("The Weapon's Refrain", Uwu,
        [
            ([Garuda], 1),
            ([Chirada, Suparna], 1),
            ([Garuda], 1),
            ([Ifrit], 2),
            ([InfernalNail], 2),
            ([Ifrit], 2),
            ([Titan], 3),
            ([Lahabrea], 4),
            ([UltimaWeapon], 5),
            // The primals Ultima calls back never return the pull to an earlier phase.
            ([UltimaWeapon, Garuda, Ifrit, Titan], 5),
        ]);
        var uwuNames = new Dictionary<uint, string>
        {
            [Garuda] = "迦楼罗", [Ifrit] = "伊弗利特", [Titan] = "泰坦", [Lahabrea] = "拉哈布雷亚", [UltimaWeapon] = "究极神兵",
        };
        var reported = new ProgressionPull(Uwu, "究极神兵绝境战", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(6), false,
            ["迦楼罗", "妙翅", "迦楼罗", "伊弗利特", "泰坦"], .7, null, 300);
        Check(reported.Phase == 5, "The reported Weapon's Refrain pull no longer reproduces the old P5.");
        Check(FflogsPhases.Restore(reported, id => uwuNames.GetValueOrDefault(id)).Phase == 3,
            "A saved Weapon's Refrain pull that reached Titan did not read P3.");
        Check(FflogsPhases.Restore(reported with { Bosses = ["迦楼罗", "妙翅", "迦楼罗"] }, id => uwuNames.GetValueOrDefault(id)).Phase == 1,
            "A saved P1 wipe kept the phases its adds were counted as.");

        // The Epic of Alexander: a player reported "最远 P8" where FFLogs has four phases. Brute
        // Justice and Cruise Chaser share the arena, take turns as the largest target, and come
        // back as adds late in P3; the hand and the dolls of P1 are not phases either.
        Replay("The Epic of Alexander", Tea,
        [
            ([LivingLiquid], 1),
            ([LivingLiquid, LiquidHand], 1),
            ([JagdDoll], 1),
            ([BruteJustice, CruiseChaser], 2),
            ([CruiseChaser], 2),
            ([BruteJustice], 2),
            ([AlexanderPrime], 3),
            ([BruteJustice, CruiseChaser], 3),
            ([AlexanderPrime], 3),
            ([PerfectAlexander], 4),
        ]);
        var teaNames = new Dictionary<uint, string>
        {
            [LivingLiquid] = "有生命活水", [LiquidHand] = "活水之手", [BruteJustice] = "残暴正义号", [CruiseChaser] = "巡航驱逐者",
            [AlexanderPrime] = "至尊亚历山大", [PerfectAlexander] = "完美亚历山大",
        };
        var teaReported = new ProgressionPull(Tea, "亚历山大绝境战", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(12), false,
            ["有生命活水", "活水之手", "残暴正义号", "巡航驱逐者", "残暴正义号", "至尊亚历山大", "巡航驱逐者", "至尊亚历山大"], .85, null, 700);
        Check(teaReported.Phase == 8, "The reported Epic of Alexander pull no longer reproduces the old P8.");
        Check(FflogsPhases.Restore(teaReported, id => teaNames.GetValueOrDefault(id)).Phase == 3,
            "A saved Epic of Alexander pull that reached Alexander Prime did not read P3.");

        // The Unending Coil: Twintania opens the fight and returns beside Nael for P4, and the
        // last phase is Bahamut Prime again.
        Replay("The Unending Coil of Bahamut", Ucob,
        [
            ([Twintania], 1),
            ([Nael], 2),
            ([BahamutPrime], 3),
            ([BahamutPrime], 3),
            ([Twintania, Nael], 4),
            ([Twintania], 4),
            ([BahamutPrime], 5),
        ]);

        // Dragonsong's Reprise: King Thordan and Nidhogg each come back for a later phase, and
        // Charibert in the intermission after the Eyes is nobody's phase.
        Replay("Dragonsong's Reprise", Dsr,
        [
            ([Adelphel, Grinnaux], 1),
            ([Charibert], 1),
            ([KingThordan], 2),
            ([Nidhogg], 3),
            ([LeftEye, RightEye], 4),
            ([Charibert], 4),
            ([KingThordan], 5),
            ([Nidhogg, Hraesvelgr], 6),
            ([Hraesvelgr], 6),
            ([DragonKing], 7),
        ]);

        // The Omega Protocol: P4 is P3's Omega rebooted, so only its HP bar starting over shows it.
        ReplayWithHp("The Omega Protocol", Top,
        [
            (Omega, 1, 1),
            (Omega, .03, 1),
            (OmegaM, 1, 2),
            (OmegaF, 1, 2),
            (OmegaM, .1, 2),
            // Omega-F still near full beside a low Omega-M is not a refill: P3 starts with Omega.
            (OmegaF, .9, 2),
            (FinalOmega, 1, 3),
            (FinalOmega, .6, 3),
            (FinalOmega, .02, 3),
            (FinalOmega, 1, 4),
            (FinalOmega, .3, 4),
            (DynamisOmegaM, 1, 5),
            (AlphaOmega, 1, 6),
        ]);

        // Dancing Mad: Kefka keeps his name through P1, P2, P4 and P5.
        ReplayWithHp("Dancing Mad", DancingMad,
        [
            (Kefka, 1, 1),
            (Kefka, .04, 1),
            (Kefka, 1, 2),
            (Kefka, .2, 2),
            (Chaos, 1, 3),
            (Exdeath, 1, 3),
            (Chaos, .1, 3),
            (Kefka, 1, 4),
            (Chaos, 1, 4),
            (NeoExdeath, 1, 4),
            (Kefka, .05, 4),
            (Kefka, 1, 5),
            (Kefka, .5, 5),
        ]);
        // A small heal or a sample jitter is not a new bar.
        var lowest = 1d;
        var steady = 1;
        foreach (var hp in new[] { 1, .7, .75, .4, .45, .8 })
            steady = FflogsPhases.Next(DancingMad, steady, Kefka, hp, ref lowest);
        Check(steady == 1, "Dancing Mad left P1 without Kefka's HP bar starting over.");
        // Names alone cannot show a refilled phase: an old pull that reached P5 reads P4.
        var kefkaNames = new Dictionary<uint, string> { [Kefka] = "凯夫卡", [Chaos] = "卡奥斯", [Exdeath] = "艾克斯迪司" };
        var dancingMad = new ProgressionPull(DancingMad, "妖星乱舞绝境战", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(15), false,
            ["凯夫卡", "卡奥斯", "艾克斯迪司", "卡奥斯", "凯夫卡"], .5, null, 800);
        Check(FflogsPhases.Restore(dancingMad, id => kefkaNames.GetValueOrDefault(id)).Phase == 4,
            "A saved Dancing Mad pull that met Kefka again after Chaos and Exdeath did not read P4.");

        // Pulls saved before the table: the old main-boss list replays to FFLogs' phase in the client's
        // language; one recorded in another language keeps its old count rather than a wrong guess.
        var names = new Dictionary<uint, string>
        {
            [Fatebreaker] = "绝命战士", [Usurper] = "希瓦·米特隆", [Oracle] = "暗之巫女", [Pandora] = "潘多拉·米特隆", [IceVeil] = "永久冰晶",
        };
        string? Name(uint id) => names.GetValueOrDefault(id);
        var saved = new ProgressionPull(Fru, "光暗未来绝境战", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(17), false,
            ["绝命战士", "希瓦·米特隆", "永久冰晶", "暗之巫女", "希瓦·米特隆", "暗之巫女", "潘多拉·米特隆"], .2, null, 600);
        Check(saved.Phase == 7, "The old main-boss count changed for pulls without an FFLogs phase.");
        Check(FflogsPhases.Restore(saved, Name).Phase == 5, "A saved Futures Rewritten pull that reached Pandora did not read P5.");
        Check(FflogsPhases.Restore(saved with { Bosses = ["绝命战士", "希瓦·米特隆", "永久冰晶"] }, Name).Phase == 2,
            "A saved wipe in the Ice Veil intermission did not stay in P2.");
        Check(FflogsPhases.Restore(saved with { Bosses = ["Fatebreaker", "Usurper of Frost", "Ice Veil", "Oracle of Darkness"] }, Name).Phase == 4,
            "A pull saved in another client language was renumbered from names that do not match.");
        Check(FflogsPhases.Restore(saved with { TerritoryId = Uncovered }, Name).Phase == 7, "A fight FFLogs phases do not cover was renumbered.");

        // The store upgrades old files as they load, keeps the phase once saved, and an old file's
        // computed "Phase" value is never read back as FFLogs' phase.
        var directory = Path.Combine(testRoot, "progression-phases");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "progression.json");
        var json = new JsonFileStore();
        await json.WriteAsync(path, new[] { saved }, CancellationToken.None);
        Check(File.ReadAllText(path).Contains("\"Phase\": 7", StringComparison.Ordinal), "Fixture no longer looks like a file written by 1.0.0.1.");
        var store = new ProgressionStore(json, path, pull => FflogsPhases.Restore(pull, Name));
        await store.LoadAsync(CancellationToken.None);
        Check(store.Pulls.Single().Phase == 5 && DutyProgress.Summarize(store.Pulls).Single().MaxPhase == 5,
            "Loading saved Futures Rewritten pulls did not renumber them to FFLogs' phases.");
        store.Add(saved with { StartTime = saved.StartTime.AddHours(1), FflogsPhase = 3 });
        await store.FlushAsync();
        var reloaded = new ProgressionStore(json, path);
        await reloaded.LoadAsync(CancellationToken.None);
        Check(reloaded.Pulls.Select(pull => pull.Phase).SequenceEqual([5, 3]), "FFLogs phases were not kept in the saved file.");
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            Check(document.RootElement.EnumerateArray().All(item => item.GetProperty("FflogsPhase").GetInt32() > 0), "FFLogs phase was not written.");

        MainBossPhaseChecks();

        Console.WriteLine("Progression phases: all seven ultimates follow FFLogs' phases (intermissions, adds, shared arenas, returning and rebooted bosses, old saves, other languages), and fights without a table count enemies that share the arena as one phase, passed.");
    }

    // Fights without a table count main bosses in order (MainBossPhases).
    private static void MainBossPhaseChecks()
    {
        const uint A = 1, B = 2;
        var phases = new MainBossPhases();
        var now = DateTimeOffset.UnixEpoch;
        var counted = false;
        // Quarter-second samples for `seconds`: the main boss, then everyone targetable.
        void Feed(double seconds, uint main, params uint[] present)
        {
            var targetable = present.ToHashSet();
            for (var elapsed = 0d; elapsed < seconds; elapsed += .25)
            {
                now = now.AddSeconds(.25);
                counted = phases.See(now, main, $"enemy {main}", targetable);
            }
        }
        void Restart()
        {
            now = now.AddHours(1);
            phases.Reset(now);
        }

        // Two bosses fought together: each time the larger one cannot be targeted the other is
        // the main boss. Players saw one phase per turn; it is one phase, shown under whichever
        // of the two was up last.
        Restart();
        Feed(40, A, A, B);
        Feed(20, B, B);
        Feed(40, A, A, B);
        Feed(20, B, B);
        Check(phases.Bosses.SequenceEqual(["enemy 2"]) && counted, "Two bosses sharing the arena were counted as a phase each time they swapped.");

        // Adds that arrive after the boss left are a phase, and so is the boss coming back.
        Restart();
        Feed(60, A, A);
        Feed(40, B, B);
        Feed(40, A, A);
        Check(phases.Bosses.SequenceEqual(["enemy 1", "enemy 2", "enemy 1"]), "A boss leaving for an add phase and returning no longer counts three phases.");

        // A boss lingering a few seconds beside its successor does not swallow the next phase.
        Restart();
        Feed(60, A, A);
        Feed(3, A, A, B);
        Feed(40, B, B);
        Check(phases.Bosses.SequenceEqual(["enemy 1", "enemy 2"]), "A short overlap at a transition hid the next phase.");

        // A hitch between two samples is not time spent together.
        Restart();
        Feed(1, A, A, B);
        now = now.AddSeconds(120);
        Feed(1, A, A, B);
        Feed(40, B, B);
        Check(phases.Bosses.Count == 2, "A long gap between samples was credited as time two enemies shared the arena.");

        // The older guards still hold: a name must settle, and a phase must have lasted a while.
        Restart();
        Feed(60, A, A);
        Feed(1, B, B);
        Feed(10, A, A);
        Check(phases.Bosses.Count == 1 && counted, "An enemy that was the largest target for a second started a phase.");
        Restart();
        Feed(10, A, A);
        Feed(10, B, B);
        Check(phases.Bosses.Count == 1 && !counted, "A phase changed before the first one had lasted long enough.");
        Feed(15, B, B);
        Check(phases.Bosses.Count == 2 && counted, "The next boss was never counted once the first phase had lasted long enough.");
    }

    // A pull as the tracker samples it: every targetable enemy of a sample → the phase after it.
    private static void Replay(string fight, uint territory, (uint[] Seen, int Phase)[] pull)
    {
        var phase = 1;
        foreach (var (seen, expected) in pull)
        {
            foreach (var id in seen) phase = FflogsPhases.Next(territory, phase, candidate => candidate == id);
            Check(phase == expected, $"{fight} after seeing {string.Join(",", seen)} was P{phase}, FFLogs says P{expected}.");
        }
    }

    // The same with each enemy's HP, for the fights where a phase is the same boss with a new bar.
    private static void ReplayWithHp(string fight, uint territory, (uint Seen, double Hp, int Phase)[] pull)
    {
        var phase = 1;
        var lowestHp = 1d;
        foreach (var (seen, hp, expected) in pull)
        {
            phase = FflogsPhases.Next(territory, phase, seen, hp, ref lowestHp);
            Check(phase == expected, $"{fight} after seeing {seen} at {hp:P0} was P{phase}, FFLogs says P{expected}.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
