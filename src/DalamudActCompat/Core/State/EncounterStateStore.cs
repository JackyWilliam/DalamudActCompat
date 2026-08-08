using DalamudActCompat.Core.Models;

namespace DalamudActCompat.Core.State;

public sealed class EncounterStateStore
{
    private readonly object syncRoot = new();
    private EncounterSnapshot snapshot = EncounterSnapshot.Empty;
    private Encounter? latestDisplayableEncounter;

    public EncounterSnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            return snapshot;
        }
    }

    public Encounter? GetDisplayEncounter()
    {
        lock (syncRoot)
        {
            var current = snapshot.Current;
            if (HasDisplayData(current))
            {
                return current;
            }

            return latestDisplayableEncounter ?? current;
        }
    }

    public void Replace(Encounter? current, IReadOnlyList<Encounter> recent)
    {
        lock (syncRoot)
        {
            RememberDisplayableEncounter(current);
            snapshot = new EncounterSnapshot(current, recent.ToArray(), DateTimeOffset.UtcNow);
        }
    }

    public void UpdateCurrent(Encounter? current)
    {
        lock (syncRoot)
        {
            RememberDisplayableEncounter(current);
            snapshot = snapshot with
            {
                Current = current,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    public void UpdateRecent(IReadOnlyList<Encounter> recent)
    {
        lock (syncRoot)
        {
            snapshot = snapshot with
            {
                Recent = recent.ToArray(),
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    public void ResetCurrent()
    {
        lock (syncRoot)
        {
            latestDisplayableEncounter = null;
            snapshot = snapshot with
            {
                Current = null,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private void RememberDisplayableEncounter(Encounter? encounter)
    {
        if (HasDisplayData(encounter))
        {
            latestDisplayableEncounter = encounter;
            return;
        }

        if (latestDisplayableEncounter is not { IsActive: true } retained)
        {
            return;
        }

        var endTime = encounter?.Id == retained.Id && encounter.EndTime is { } completedAt
            ? completedAt
            : DateTimeOffset.UtcNow;
        latestDisplayableEncounter = retained with
        {
            EndTime = endTime < retained.StartTime ? retained.StartTime : endTime,
        };
    }

    private static bool HasDisplayData(Encounter? encounter)
        => encounter is not null &&
           (encounter.TotalDamage > 0 || encounter.TotalHealing > 0);
}
