using System.Text.Json;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Storage;

// 开荒进度 phases must match FFLogs where FflogsPhases knows the fight. Futures Rewritten used to
// read up to P7: its Ice Veil intermission and the bosses returning in P4 each counted as a phase.
internal static class ProgressionPhaseSmokeTests
{
    private const uint Fru = 1238;
    private const uint Fatebreaker = 9707, Usurper = 12809, Oracle = 9832, Pandora = 13561, IceVeil = 9358, CrystalOfLight = 13555;

    internal static async Task RunAsync(string testRoot)
    {
        // What a pull sees sample by sample (all targetable enemies) → FFLogs' phase after it.
        (uint[] Seen, int Phase)[] pull =
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
        ];
        var phase = 1;
        foreach (var (seen, expected) in pull)
        {
            foreach (var id in seen) phase = FflogsPhases.Next(Fru, phase, candidate => candidate == id);
            Check(phase == expected, $"Futures Rewritten after seeing {string.Join(",", seen)} was P{phase}, FFLogs says P{expected}.");
        }
        Check(FflogsPhases.Next(1363, 3, _ => true) == 3 && !FflogsPhases.Covers(1363), "A fight without FFLogs phase starts changed phase.");

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
        Check(FflogsPhases.Restore(saved with { TerritoryId = 1257 }, Name).Phase == 7, "A fight FFLogs phases do not cover was renumbered.");

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

        Console.WriteLine("Progression phases: Futures Rewritten matches FFLogs P1–P5 (intermission, returning bosses, old saves, other languages and fights) passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
