using RainbowMage.OverlayPlugin.MemoryProcessors;

namespace DalamudActCompat.ActRuntime;

internal sealed class CachedDalamudGameStateProvider : IDalamudGameStateProvider
{
    private DalamudGameStateSnapshot snapshot = DalamudGameStateSnapshot.Empty;
    private IReadOnlyList<ActPlayerIdentity> identities = [];
    private IReadOnlyList<ActPlayerIdentity>? lastSourceIdentities;
    private ActPlayerIdentity[] cachedIdentities = [];
    private ActPlayerPose? lastLocalPlayerPose;
    private bool lastInCombat;
    private bool hasSnapshot;

    public DalamudGameStateSnapshot Snapshot
        => Volatile.Read(ref snapshot);

    public IReadOnlyList<ActPlayerIdentity> Identities
        => Volatile.Read(ref identities);

    public void Update(
        IReadOnlyList<ActPlayerIdentity> identities,
        ActPlayerPose? localPlayerPose,
        bool inCombat)
    {
        var sameIdentities = hasSnapshot && ReferenceEquals(identities, lastSourceIdentities);
        if (sameIdentities &&
            inCombat == lastInCombat &&
            Equals(localPlayerPose, lastLocalPlayerPose))
        {
            return;
        }

        if (!sameIdentities)
        {
            cachedIdentities = identities.Count == 0 ? [] : identities.ToArray();
            lastSourceIdentities = identities;
            Volatile.Write(ref this.identities, cachedIdentities);
        }

        lastLocalPlayerPose = localPlayerPose;
        lastInCombat = inCombat;
        hasSnapshot = true;

        ActPlayerIdentity? player = null;
        for (var i = 0; i < cachedIdentities.Length; i++)
        {
            if (cachedIdentities[i].IsLocalPlayer)
            {
                player = cachedIdentities[i];
                break;
            }
        }

        if (player is null)
        {
            Volatile.Write(ref snapshot, DalamudGameStateSnapshot.Empty);
            return;
        }

        var party = new DalamudPartyMember[cachedIdentities.Length];
        DalamudPartyMember? localMember = null;
        for (var i = 0; i < cachedIdentities.Length; i++)
        {
            var member = ToPartyMember(cachedIdentities[i], localPlayerPose);
            party[i] = member;
            if (ReferenceEquals(cachedIdentities[i], player))
            {
                localMember = member;
            }
        }

        Volatile.Write(
            ref snapshot,
            new DalamudGameStateSnapshot(
                true,
                true,
                inCombat,
                localMember ?? ToPartyMember(player, localPlayerPose),
                party));
    }

    public void Clear()
    {
        hasSnapshot = false;
        lastSourceIdentities = null;
        lastLocalPlayerPose = null;
        lastInCombat = false;
        cachedIdentities = [];
        Volatile.Write(ref identities, []);
        Volatile.Write(ref snapshot, DalamudGameStateSnapshot.Empty);
    }

    private static DalamudPartyMember ToPartyMember(
        ActPlayerIdentity identity,
        ActPlayerPose? localPlayerPose)
    {
        var useLivePose = identity.IsLocalPlayer &&
                          localPlayerPose is not null &&
                          (identity.EntityId == 0 || identity.EntityId == localPlayerPose.EntityId);
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
            PositionX = useLivePose ? localPlayerPose!.PositionX : identity.PositionX,
            PositionY = useLivePose ? localPlayerPose!.PositionY : identity.PositionY,
            PositionZ = useLivePose ? localPlayerPose!.PositionZ : identity.PositionZ,
            Rotation = useLivePose ? localPlayerPose!.Rotation : identity.Rotation,
        };
    }
}
