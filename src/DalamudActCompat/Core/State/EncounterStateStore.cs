using DalamudActCompat.Core.Models;

namespace DalamudActCompat.Core.State;

public sealed class EncounterStateStore
{
    private readonly object syncRoot = new();
    private EncounterSnapshot snapshot = EncounterSnapshot.Empty;

    public EncounterSnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            return snapshot;
        }
    }

    public void Replace(Encounter? current, IReadOnlyList<Encounter> recent)
    {
        lock (syncRoot)
        {
            snapshot = new EncounterSnapshot(current, recent.ToArray(), DateTimeOffset.UtcNow);
        }
    }

    public void UpdateCurrent(Encounter? current)
    {
        lock (syncRoot)
        {
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
        => UpdateCurrent(null);
}
