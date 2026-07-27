using RainbowMage.OverlayPlugin.MemoryProcessors;

namespace DalamudActCompat.ActRuntime;

internal sealed class CachedDalamudGameStateProvider : IDalamudGameStateProvider
{
    private DalamudGameStateSnapshot snapshot = DalamudGameStateSnapshot.Empty;
    private IReadOnlyList<ActPlayerIdentity> identities = [];

    public DalamudGameStateSnapshot Snapshot
        => Volatile.Read(ref snapshot);

    public IReadOnlyList<ActPlayerIdentity> Identities
        => Volatile.Read(ref identities);

    public void Update(IReadOnlyList<ActPlayerIdentity> identities, bool inCombat)
    {
        var cachedIdentities = identities.ToArray();
        Volatile.Write(ref this.identities, cachedIdentities);
        var player = cachedIdentities.FirstOrDefault(static identity => identity.IsLocalPlayer);
        if (player is null)
        {
            Volatile.Write(ref snapshot, DalamudGameStateSnapshot.Empty);
            return;
        }

        var party = cachedIdentities.Select(ToPartyMember).ToArray();
        Volatile.Write(
            ref snapshot,
            new DalamudGameStateSnapshot(
                true,
                true,
                inCombat,
                ToPartyMember(player),
                party));
    }

    public void Clear()
    {
        Volatile.Write(ref identities, []);
        Volatile.Write(ref snapshot, DalamudGameStateSnapshot.Empty);
    }

    private static DalamudPartyMember ToPartyMember(ActPlayerIdentity identity)
    {
        return new DalamudPartyMember
        {
            Name = identity.Name,
            EntityId = identity.EntityId,
            ContentId = identity.ContentId,
            WorldId = identity.WorldId,
            JobId = identity.JobId,
            Level = identity.Level,
            CurrentHp = identity.CurrentHp,
            MaxHp = identity.MaxHp,
            CurrentMp = identity.CurrentMp,
            MaxMp = identity.MaxMp,
            TerritoryId = identity.TerritoryId,
            PositionX = identity.PositionX,
            PositionY = identity.PositionY,
            PositionZ = identity.PositionZ,
        };
    }
}
