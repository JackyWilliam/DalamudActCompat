using DalamudActCompat.Core.Models;

namespace DalamudActCompat.Core.State;

public sealed class EncounterStateStore
{
    private readonly object syncRoot = new();
    private EncounterSnapshot snapshot = EncounterSnapshot.Empty;
    private Encounter? latestDisplayableEncounter;
    private Encounter? latestParserDisplayableEncounter;

    public EncounterSnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            return snapshot;
        }
    }

    public Encounter? GetDisplayEncounter(bool parserView = false)
    {
        lock (syncRoot)
        {
            var current = snapshot.Current;
            if (HasDisplayData(current, parserView))
            {
                return current;
            }

            return (parserView ? latestParserDisplayableEncounter : latestDisplayableEncounter) ?? current;
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
            latestParserDisplayableEncounter = null;
            snapshot = snapshot with
            {
                Current = null,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private void RememberDisplayableEncounter(Encounter? encounter)
    {
        Remember(ref latestDisplayableEncounter, encounter, false);
        Remember(ref latestParserDisplayableEncounter, encounter, true);
    }

    private static void Remember(ref Encounter? previous, Encounter? encounter, bool parserView)
    {
        if (HasDisplayData(encounter, parserView))
        {
            previous = encounter;
            return;
        }

        if (previous is not { IsActive: true } retained)
        {
            return;
        }

        var endTime = encounter?.Id == retained.Id && encounter.EndTime is { } completedAt
            ? completedAt
            : DateTimeOffset.UtcNow;
        previous = retained with
        {
            EndTime = endTime < retained.StartTime ? retained.StartTime : endTime,
        };
    }

    private static bool HasDisplayData(Encounter? encounter, bool parserView = false)
        => encounter is not null &&
           (parserView ? (encounter.ParsedPlayers ?? encounter.Combatants).Any(player => player.TotalDamage > 0 || player.TotalHealing > 0)
               : encounter.TotalDamage > 0 || encounter.TotalHealing > 0);
}
