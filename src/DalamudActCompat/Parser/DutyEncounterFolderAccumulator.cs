using DalamudActCompat.Core.Models;

namespace DalamudActCompat.Parser;

internal sealed class DutyEncounterFolderAccumulator
{
    private readonly List<Encounter> pulls = [];
    private Guid folderId;
    private DateTimeOffset? startTime;
    private uint? territoryId;
    private string zoneName = string.Empty;

    public bool HasData => pulls.Count > 0;

    public Encounter Add(Encounter completedPull)
    {
        ArgumentNullException.ThrowIfNull(completedPull);
        if (folderId == Guid.Empty)
        {
            folderId = Guid.NewGuid();
            startTime = completedPull.StartTime;
        }
        else if (completedPull.StartTime < startTime)
        {
            startTime = completedPull.StartTime;
        }

        if (!string.IsNullOrWhiteSpace(completedPull.ZoneName))
        {
            zoneName = completedPull.ZoneName;
        }
        if (completedPull.TerritoryId is > 0)
        {
            territoryId = completedPull.TerritoryId;
        }

        // A child is a complete attempt, not the ACT fragments used to assemble it.
        // Keeping it as a leaf makes the history hierarchy match the player's mental model.
        var leaf = completedPull with
        {
            EndTime = completedPull.EndTime ?? DateTimeOffset.UtcNow,
            FflogsRankingEncounter = null,
            SegmentRecords = [],
        };
        var existingIndex = pulls.FindIndex(item => item.Id == leaf.Id);
        if (existingIndex >= 0)
        {
            pulls[existingIndex] = leaf;
        }
        else
        {
            pulls.Add(leaf);
        }

        return Build();
    }

    public Encounter? Complete()
    {
        if (!HasData)
        {
            Reset();
            return null;
        }

        var completed = Build();
        Reset();
        return completed;
    }

    public void Reset()
    {
        pulls.Clear();
        folderId = Guid.Empty;
        startTime = null;
        territoryId = null;
        zoneName = string.Empty;
    }

    private Encounter Build()
    {
        var records = pulls.ToArray();
        var folderStart = startTime ?? records[0].StartTime;
        var folderEnd = records
            .Select(static pull => pull.EndTime ?? pull.StartTime)
            .Max();
        var displayName = string.IsNullOrWhiteSpace(zoneName)
            ? records[^1].EnemyName
            : zoneName;
        return new Encounter(
            folderId,
            folderStart,
            folderEnd,
            zoneName,
            displayName,
            Array.Empty<Combatant>(),
            Array.Empty<DamageEvent>(),
            Array.Empty<HealEvent>(),
            Array.Empty<DeathEvent>(),
            Array.Empty<ActionSummary>(),
            Array.Empty<JobSummary>())
        {
            TerritoryId = territoryId,
            SegmentRecords = records,
            CombatDuration = TimeSpan.FromTicks(
                records.Sum(static pull => pull.EffectiveDuration.Ticks)),
        };
    }
}
