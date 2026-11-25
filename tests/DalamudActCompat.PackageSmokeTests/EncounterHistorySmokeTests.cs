using System.Reflection;
using Dalamud.Plugin.Services;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Plugin;

// History keeps duties and open-world fights apart: separate allowances in the list and on disk,
// a filter for the list, and runs of open-world fights folded into one row.
internal static class EncounterHistorySmokeTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 24, 20, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string testRoot)
    {
        SeparateAllowances();
        RowsAndFilters();
        await SnapshotsFollowHistoryAsync(testRoot);
        Console.WriteLine("Encounter history: separate duty/open-world allowances, snapshot files following their entries, open-world run folding and list filters passed.");
    }

    private static void SeparateAllowances()
    {
        // Newest first: duties at 9, 6, 3, 0 and open-world fights in between.
        var mixed = new List<Encounter>();
        for (var minute = 9; minute >= 0; minute--) mixed.Add(minute % 3 == 0 ? Duty(minute) : Fight(minute));
        var kept = EncounterHistoryRules.Retain(mixed, 2);
        Check(kept.SequenceEqual(mixed.Take(4)),
            "Two duties and two open-world fights should be kept, newest first and in their original order.");

        // The case the split exists for: one old duty under a pile of open-world fights.
        var flooded = Enumerable.Range(0, 30).Select(index => Fight(100 - index)).Append(Duty(0)).ToList();
        kept = EncounterHistoryRules.Retain(flooded, 2);
        Check(kept.Count == 3 && ReferenceEquals(kept[^1], flooded[^1]),
            "Open-world fights pushed the only duty record out of history.");
        Check(EncounterHistoryRules.Retain(flooded, 200).Count == flooded.Count,
            "Retention dropped records that fit their allowance.");
    }

    private static void RowsAndFilters()
    {
        var dummyC = Fight(50);
        var dummyB = Fight(40);
        var dummyA = Fight(30);
        var duty = Duty(20);
        // Same map as the dummy fights and within the gap of them, but a duty lies in between.
        var beforeDuty = Fight(10);
        var otherMap = Fight(5, territory: 135, zone: "Lower La Noscea");
        // Same map as otherMap, but more than the allowed pause earlier.
        var longAgo = Fight(-60, territory: 135, zone: "Lower La Noscea");
        IReadOnlyList<Encounter> history = [dummyC, dummyB, dummyA, duty, beforeDuty, otherMap, longAgo];

        var all = EncounterHistoryRules.BuildRows(history, HistoryFilter.All);
        Check(all.Count == 5, $"Expected a run, a duty and three single fights, got {all.Count} rows.");
        var run = all[0];
        Check(run.IsOpenWorldRun &&
              run.Entry.SegmentRecords.Select(static fight => fight.Id).SequenceEqual(new[] { dummyA.Id, dummyB.Id, dummyC.Id }),
            "Back-to-back open-world fights in one map were not folded oldest first.");
        Check(run.Entry.StartTime == dummyA.StartTime && run.Entry.EndTime == dummyC.EndTime &&
              run.Entry.TerritoryId == 134 && run.Entry.EnemyName == run.Entry.ZoneName && run.Entry.ZoneName == dummyA.ZoneName,
            "The folder of open-world fights does not span its fights or is not named after its map.");
        Check(run.Entry.Id != dummyA.Id && history.All(entry => entry.Id != run.Entry.Id),
            "The folder shares an id with a saved record, so its row would collide with that record's row.");
        Check(!all[1].OpenWorld && ReferenceEquals(all[1].Entry, duty), "A duty folder was not kept as its own row.");
        Check(all.Skip(2).All(static row => row.OpenWorld && !row.IsOpenWorldRun) &&
              ReferenceEquals(all[2].Entry, beforeDuty) && ReferenceEquals(all[3].Entry, otherMap) && ReferenceEquals(all[4].Entry, longAgo),
            "A duty in between, another map or a long pause must each end a run; a lone fight stays a plain row.");

        // The folder keeps its id while fights join it, so its row does not flicker.
        IReadOnlyList<Encounter> grown = [Fight(55), .. history];
        var grownRun = EncounterHistoryRules.BuildRows(grown, HistoryFilter.All)[0];
        Check(grownRun.Entry.Id == run.Entry.Id && grownRun.Entry.SegmentRecords.Count == 4,
            "A new fight joining a run changed the folder's id or was left out of it.");

        var duties = EncounterHistoryRules.BuildRows(history, HistoryFilter.Duty);
        Check(duties.Count == 1 && ReferenceEquals(duties[0].Entry, duty), "The duty filter did not show duties only.");
        var field = EncounterHistoryRules.BuildRows(history, HistoryFilter.OpenWorld);
        // Hiding the duty must not let the fights on either side of it merge.
        Check(field.Count == 4 && field[0].Entry.Id == run.Entry.Id && field[0].Entry.SegmentRecords.Count == 3 &&
              ReferenceEquals(field[1].Entry, beforeDuty),
            "The open-world filter changed which fights a folder holds.");
        Check(EncounterHistoryRules.BuildRows([], HistoryFilter.All).Count == 0, "An empty history produced rows.");
    }

    private static async Task SnapshotsFollowHistoryAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "encounter-history-kinds");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var paths = new PluginPaths(root);
        Directory.CreateDirectory(paths.EncounterLogDirectory);
        // Files history has never heard of keep the old newest-N rule instead of being wiped.
        var foreign = Path.Combine(paths.EncounterLogDirectory, "notes.json");
        var orphan = Path.Combine(paths.EncounterLogDirectory, $"20200101-000000-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(foreign, "{}");
        await File.WriteAllTextAsync(orphan, "{}");

        var repository = new EncounterRepository(new JsonFileStore(), paths);
        var configuration = new PluginConfiguration { HistoryLimit = 2 };
        var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
        var duties = new[] { Duty(0), Duty(1) };
        var service = new EncounterService(
            repository,
            new EncounterStateStore(),
            configuration,
            new PluginLogger(log),
            paths,
            () => Path.Combine(root, "network"));
        await service.InitializeAsync(CancellationToken.None);
        foreach (var duty in duties) service.QueueFinishedEncounter(duty);
        for (var index = 0; index < 6; index++) service.QueueFinishedEncounter(Fight(10 + index));
        await service.DisposeAsync();

        var history = await repository.LoadRecentAsync(CancellationToken.None);
        Check(history.Count == 4 && history.Count(EncounterHistoryRules.IsOpenWorld) == 2 &&
              duties.All(duty => history.Any(entry => entry.Id == duty.Id)),
            "Six open-world fights after two duties should leave both duties and the two newest fights.");
        var files = Directory.GetFiles(paths.EncounterLogDirectory, "*.json").Select(Path.GetFileName).ToArray();
        Check(history.All(entry => files.Any(name => name!.EndsWith($"-{entry.Id:N}.json", StringComparison.Ordinal))),
            "A record still in history lost its snapshot file.");
        Check(files.Length == 6 && File.Exists(foreign) && File.Exists(orphan),
            $"Expected four snapshots of kept records plus two unknown files, found {files.Length}.");
    }

    private static Encounter Fight(int minute, uint territory = 134, string zone = "Middle La Noscea")
    {
        var start = At.AddMinutes(minute);
        return new Encounter(Guid.NewGuid(), start, start.AddSeconds(30), zone, "Striking Dummy", [], [], [], [], [], [])
        {
            TerritoryId = territory,
        };
    }

    private static Encounter Duty(int minute)
    {
        const string name = "The Omega Protocol";
        var pull = Fight(minute, territory: 1122, zone: name);
        return new Encounter(Guid.NewGuid(), pull.StartTime, pull.EndTime, name, name, [], [], [], [], [], [])
        {
            TerritoryId = 1122,
            SegmentRecords = [pull],
        };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
