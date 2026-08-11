using System.Collections.ObjectModel;
using System.Diagnostics;
using DalamudActCompat.Protocol;
using FFXIV_ACT_Plugin.Common;
using FFXIV_ACT_Plugin.Common.Models;

namespace DalamudActCompat.Host;

internal sealed class FfxivDataRepository : IDataRepository
{
    private readonly object syncRoot = new();
    private HostFfxivEntitySnapshot snapshot = new(0, 0, DateTimeOffset.MinValue, []);
    private ReadOnlyCollection<Combatant> combatants =
        Array.AsReadOnly(Array.Empty<Combatant>());
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

    public void Apply(HostFfxivEntitySnapshot next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var mapped = Array.AsReadOnly(next.Combatants
            .OrderByDescending(combatant => combatant.Id == next.CurrentPlayerId)
            .Select(MapCombatant)
            .ToArray());
        lock (syncRoot)
        {
            snapshot = next;
            combatants = mapped;
        }
    }

    public Language GetSelectedLanguageID() => Language.Chinese;

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
            return snapshot.TerritoryId;
        }
    }

    public uint GetCurrentPlayerID()
    {
        lock (syncRoot)
        {
            return snapshot.CurrentPlayerId;
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

    public string GetGameVersion() => string.Empty;

    public bool IsChatLogAvailable() => true;

    public string[] GetAntiVirusNames() => [];

    public byte GetGameRegion() => 2;

    private static Combatant MapCombatant(HostFfxivCombatant source)
        => new()
        {
            ID = source.Id,
            OwnerID = source.OwnerId,
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
            NetworkBuffs = source.Statuses.Select(status => new NetworkBuff
            {
                BuffID = status.Id,
                BuffExtra = status.Param,
                Timestamp = DateTime.Now,
                Duration = Math.Max(0, status.RemainingTime),
                ActorID = status.SourceId,
                ActorName = string.Empty,
                TargetID = source.Id,
                TargetName = source.Name,
            }).ToArray(),
        };
}
