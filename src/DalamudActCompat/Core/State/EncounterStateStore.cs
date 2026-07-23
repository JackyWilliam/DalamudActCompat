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

    public void ResetCurrent()
    {
        lock (syncRoot)
        {
            snapshot = snapshot with { Current = null, CreatedAt = DateTimeOffset.UtcNow };
        }
    }
}
