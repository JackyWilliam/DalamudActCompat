using System.Collections.ObjectModel;
using System.Diagnostics;
using DalamudActCompat.Protocol;
using FFXIV_ACT_Plugin.Common;
using FFXIV_ACT_Plugin.Common.Models;

namespace DalamudActCompat.Host;

internal sealed class FfxivDataRepository : IDataRepository
{
    private const uint InvalidEntityId = 0xE0000000;
    // Long open-world sessions keep spawning new actors; resetting the history is cheaper
    // than growing it forever and only costs one bounded log wait for an old actor.
    private const int MaximumSeenEntities = 65_536;
    private readonly object syncRoot = new();
    private readonly HashSet<uint> seenEntities = [];
    private uint seenTerritoryId;
    private uint territoryId;
    private uint currentPlayerId;
    // The last full snapshot, in its order, and the same actors as plugins see them. Deltas
    // arrive every 30 ms and each is relative to this; mapping only what a delta names keeps
    // their cost proportional to what changed instead of to everything in the zone.
    private HostFfxivCombatant[] baselineOrder = [];
    private Dictionary<uint, Combatant> baselineMapped = [];
    private DateTimeOffset baselineTimestamp = DateTimeOffset.MinValue;
    private DateTimeOffset lastEntityUpdateTimestamp = DateTimeOffset.MinValue;
    // Epoch of the newest applied game-side capture; see HostLogEvent.EntityEpoch.
    private long entityEpoch;
    private ReadOnlyCollection<Combatant> combatants =
        Array.AsReadOnly(Array.Empty<Combatant>());
    // A legacy core does not send HostGameContext. Preserve its historical CN contract
    // until a protocol-v7 handshake explicitly selects another region and language.
    private Language selectedLanguage = Language.Chinese;
    private byte gameRegion = (byte)HostGameRegion.Chinese;
    private int gameProcessId;
    private Process? gameProcess;

    public void SetGameProcessId(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        lock (syncRoot)
        {
            if (gameProcessId == processId)
            {
                return;
            }

            gameProcess?.Dispose();
            gameProcess = null;
            gameProcessId = processId;
        }
    }

    public void SetGameContext(HostGameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (syncRoot)
        {
            selectedLanguage = context.Language switch
            {
                HostClientLanguage.Japanese => Language.Japanese,
                HostClientLanguage.German => Language.German,
                HostClientLanguage.French => Language.French,
                HostClientLanguage.Chinese => Language.Chinese,
                HostClientLanguage.Korean => Language.Korean,
                _ => Language.English,
            };
            gameRegion = (byte)context.Region;
        }
    }

    public void Apply(HostFfxivEntitySnapshot next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var nextById = BuildCombatantMap(next.Combatants);
        lock (syncRoot)
        {
            if (next.Timestamp < lastEntityUpdateTimestamp)
            {
                return;
            }

            territoryId = next.TerritoryId;
            currentPlayerId = next.CurrentPlayerId;
            baselineOrder = nextById.Values.ToArray();
            var mappedAt = DateTime.Now;
            baselineMapped = new Dictionary<uint, Combatant>(baselineOrder.Length);
            foreach (var source in baselineOrder)
            {
                baselineMapped[source.Id] = MapCombatant(source, mappedAt);
            }
            baselineTimestamp = next.Timestamp;
            lastEntityUpdateTimestamp = next.Timestamp;
            // A full snapshot restarts the count, so a reloaded sender that begins again at a
            // low epoch is followed instead of being outranked by its predecessor forever.
            entityEpoch = next.Epoch;
            combatants = PlayerFirst(baselineOrder.Select(source => baselineMapped[source.Id]), baselineOrder.Length);
            RecordSeenLocked(next.TerritoryId, nextById.Keys);
        }
    }

    /// <summary>Whether a capture at or after <paramref name="epoch"/> has been applied.</summary>
    internal bool HasReachedEntityEpoch(long epoch)
    {
        lock (syncRoot)
        {
            return entityEpoch >= epoch;
        }
    }

    /// <summary>Whether the actor has appeared in any snapshot of the current territory.</summary>
    internal bool HasSeenEntity(uint id)
    {
        lock (syncRoot)
        {
            return seenEntities.Contains(id);
        }
    }

    private void RecordSeenLocked(uint territoryId, IEnumerable<uint> ids)
    {
        // Actor IDs are reused across territories, so history from another zone is meaningless.
        if (territoryId != seenTerritoryId || seenEntities.Count > MaximumSeenEntities)
        {
            seenEntities.Clear();
            seenTerritoryId = territoryId;
        }

        seenEntities.UnionWith(ids);
    }

    internal bool ApplyDelta(HostFfxivEntityDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        lock (syncRoot)
        {
            if (delta.BaseTimestamp != baselineTimestamp ||
                delta.Timestamp < lastEntityUpdateTimestamp ||
                delta.TerritoryId != territoryId ||
                delta.CurrentPlayerId != currentPlayerId)
            {
                return false;
            }

            // Every delta is relative to the last full snapshot because state-priority IPC may
            // coalesce intermediate frames. Rebuilding from that baseline makes the newest delta
            // complete even when one or more earlier deltas never crossed the process boundary.
            var keepStatuses = delta.KeepStatusIds is { Count: > 0 } keepIds ? new HashSet<uint>(keepIds) : null;
            var mappedAt = DateTime.Now;
            var upserts = new Dictionary<uint, Combatant>(delta.Upserts.Count);
            foreach (var upsert in delta.Upserts)
            {
                if (!IsValidEntityId(upsert.Id))
                {
                    continue;
                }

                // The sender left out statuses that equal the baseline's, so the ones mapped
                // from the baseline are reused. An actor the baseline never had cannot be in
                // that list; if it is anyway, it keeps the (empty) statuses it was sent with.
                upserts[upsert.Id] = keepStatuses?.Contains(upsert.Id) == true &&
                                     baselineMapped.TryGetValue(upsert.Id, out var kept)
                    ? MapCombatant(upsert, mappedAt, kept.NetworkBuffs)
                    : MapCombatant(upsert, mappedAt);
            }

            var removed = delta.RemovedIds.Count == 0 ? null : new HashSet<uint>(delta.RemovedIds);
            // Baseline order with the changed actors in place, then the actors new since it.
            // Unchanged actors are the very objects mapped from the baseline.
            var merged = new List<Combatant>(baselineOrder.Length + upserts.Count);
            foreach (var source in baselineOrder)
            {
                if (upserts.Remove(source.Id, out var changed))
                {
                    merged.Add(changed);
                }
                else if (removed?.Contains(source.Id) != true)
                {
                    merged.Add(baselineMapped[source.Id]);
                }
            }
            merged.AddRange(upserts.Values);

            lastEntityUpdateTimestamp = delta.Timestamp;
            entityEpoch = Math.Max(entityEpoch, delta.Epoch);
            combatants = PlayerFirst(merged, merged.Count);
            RecordSeenLocked(delta.TerritoryId, delta.Upserts.Select(upsert => upsert.Id).Where(IsValidEntityId));
            return true;
        }
    }

    public Language GetSelectedLanguageID()
    {
        lock (syncRoot)
        {
            return selectedLanguage;
        }
    }

    public Process GetCurrentFFXIVProcess()
    {
        if (!HostPluginBridge.IsPostNamazuNativeRuntimeAllowed())
        {
            return null!;
        }

        lock (syncRoot)
        {
            if (gameProcessId <= 0)
            {
                return null!;
            }

            if (gameProcess is not null)
            {
                try
                {
                    if (!gameProcess.HasExited)
                    {
                        return gameProcess;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Recreate the Process facade below.
                }

                gameProcess.Dispose();
                gameProcess = null;
            }

            try
            {
                gameProcess = Process.GetProcessById(gameProcessId);
                return gameProcess;
            }
            catch (ArgumentException)
            {
                return null!;
            }
        }
    }

    internal Process? GetGameProcessForAuthorizedBridge()
    {
        int processId;
        lock (syncRoot)
        {
            processId = gameProcessId;
        }

        if (processId <= 0)
        {
            return null;
        }

        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal int GetGameProcessId()
    {
        lock (syncRoot)
        {
            return gameProcessId;
        }
    }

    public IDictionary<uint, string> GetResourceDictionary(ResourceType resourceType)
        => new Dictionary<uint, string>();

    public uint GetCurrentTerritoryID()
    {
        lock (syncRoot)
        {
            return territoryId;
        }
    }

    public uint GetCurrentPlayerID()
    {
        lock (syncRoot)
        {
            return currentPlayerId;
        }
    }

    public ReadOnlyCollection<Combatant> GetCombatantList()
    {
        lock (syncRoot)
        {
            return combatants;
        }
    }

    public Player GetPlayer() => new();

    public DateTime GetServerTimestamp() => DateTime.Now;

    internal object DrawingDiagnosticSnapshot(uint id)
    {
        lock (syncRoot)
        {
            var entity = combatants.FirstOrDefault(value => value.ID == id);
            return new
            {
                actor = id.ToString("X8"), present = entity is not null,
                bnpcId = entity?.BNpcID, address = entity?.Address.ToInt64().ToString("X"),
                ageMs = lastEntityUpdateTimestamp == DateTimeOffset.MinValue ? (double?)null :
                    Math.Round((DateTimeOffset.UtcNow - lastEntityUpdateTimestamp).TotalMilliseconds),
            };
        }
    }

    internal string DrawingDiagnosticEntityState(uint id)
    {
        lock (syncRoot)
        {
            var entity = combatants.FirstOrDefault(value => value.ID == id);
            return entity is null ? "missing" : $"bnpc={entity.BNpcID},address={entity.Address.ToInt64():X}";
        }
    }

    public string GetGameVersion() => string.Empty;

    public bool IsChatLogAvailable() => true;

    public string[] GetAntiVirusNames() => [];

    public byte GetGameRegion()
    {
        lock (syncRoot)
        {
            return gameRegion;
        }
    }

    // FFXIV_ACT_Plugin lists the local player first and everything else keeps its order. This
    // used to be a full sort of every actor on every update.
    private ReadOnlyCollection<Combatant> PlayerFirst(IEnumerable<Combatant> ordered, int count)
    {
        var result = new List<Combatant>(count);
        Combatant? player = null;
        foreach (var combatant in ordered)
        {
            if (player is null && combatant.ID == currentPlayerId)
            {
                player = combatant;
            }
            else
            {
                result.Add(combatant);
            }
        }

        if (player is not null)
        {
            result.Insert(0, player);
        }
        return result.AsReadOnly();
    }

    private static Dictionary<uint, HostFfxivCombatant> BuildCombatantMap(
        IEnumerable<HostFfxivCombatant> sources)
    {
        var result = new Dictionary<uint, HostFfxivCombatant>();
        foreach (var source in sources)
        {
            if (IsValidEntityId(source.Id))
            {
                // A malformed or transient duplicate snapshot must degrade to one actor value;
                // crashing this process removes every extension assigned to the shared Host.
                result[source.Id] = source;
            }
        }

        return result;
    }

    private static bool IsValidEntityId(uint entityId)
        => entityId is not 0 and not InvalidEntityId;

    private static Combatant MapCombatant(
        HostFfxivCombatant source,
        DateTime mappedAt,
        NetworkBuff[]? keptBuffs = null)
        => new()
        {
            ID = source.Id,
            // Dalamud reports "no owner" as 0xE0000000; FFXIV_ACT_Plugin and OverlayPlugin
            // expose it as 0, and triggers test OwnerID == 0 to tell players from pets.
            OwnerID = IsValidEntityId(source.OwnerId) ? source.OwnerId : 0,
            type = source.Type,
            Job = source.Job,
            Level = source.Level,
            Name = source.Name,
            CurrentHP = source.CurrentHp,
            MaxHP = source.MaxHp,
            CurrentMP = source.CurrentMp,
            MaxMP = source.MaxMp,
            CurrentCP = source.CurrentCp,
            MaxCP = source.MaxCp,
            CurrentGP = source.CurrentGp,
            MaxGP = source.MaxGp,
            IsCasting = source.IsCasting,
            CastBuffID = source.CastId,
            CastTargetID = source.CastTargetId,
            CastDurationCurrent = source.CastTime,
            CastDurationMax = source.MaxCastTime,
            PosX = source.PosX,
            // FFXIV_ACT_Plugin exposes X/Y as the ground plane and Z as height,
            // while Dalamud's Vector3 uses X/Z as the ground plane and Y as height.
            PosY = source.PosZ,
            PosZ = source.PosY,
            Heading = source.Heading,
            CurrentWorldID = source.CurrentWorldId,
            WorldID = source.WorldId,
            WorldName = source.WorldName,
            BNpcNameID = source.BNpcNameId,
            BNpcID = source.BNpcId,
            TargetID = source.TargetId,
            EffectiveDistance = source.EffectiveDistance,
            PartyType = source.PartyType switch
            {
                1 => PartyType.Party,
                2 => PartyType.Alliance,
                _ => PartyType.None,
            },
            Address = new IntPtr(source.Address),
            NetworkBuffs = keptBuffs ?? source.Statuses.Select(status => new NetworkBuff
            {
                BuffID = status.Id,
                BuffExtra = status.Param,
                Timestamp = mappedAt,
                Duration = Math.Max(0, status.RemainingTime),
                ActorID = status.SourceId,
                ActorName = string.Empty,
                TargetID = source.Id,
                TargetName = source.Name,
            }).ToArray(),
        };
}
