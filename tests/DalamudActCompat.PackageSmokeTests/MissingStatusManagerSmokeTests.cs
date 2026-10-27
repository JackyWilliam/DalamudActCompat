using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using DalamudActCompat.Plugin;

internal static class MissingStatusManagerSmokeTests
{
    public static void Run()
    {
        // Dalamud builds IBattleChara.StatusList from GetStatusManager() without a null check.
        // A battle object that is still spawning or already despawning reports address 0;
        // enumerating it is the StatusList.Enumerator NullReferenceException in user logs.
        var missingManager = (StatusList)Activator.CreateInstance(
            typeof(StatusList),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [(nint)0],
            culture: null)!;
        var transitional = EntityServiceProxy.Create<IBattleChara>(new()
        {
            ["EntityId"] = 0x40001234u,
            ["BaseId"] = 18000u,
            ["Name"] = new SeString(new TextPayload("Spawning add")),
            ["ObjectKind"] = ObjectKind.BattleNpc,
            ["Position"] = new Vector3(10, 0, 20),
            ["CurrentHp"] = 900u,
            ["MaxHp"] = 1000u,
            ["StatusList"] = missingManager,
        });
        var stable = EntityServiceProxy.Create<IGameObject>(new()
        {
            ["EntityId"] = 0x40005678u,
            ["BaseId"] = 2015156u,
            ["Name"] = new SeString(new TextPayload("Stable object")),
            ["ObjectKind"] = ObjectKind.EventObj,
            ["Position"] = new Vector3(1, 2, 3),
        });

        var snapshot = FfxivEntitySnapshotBuilder.Build(
            EntityServiceProxy.Create<IObjectTable>(new(), new IGameObject[] { transitional, stable }),
            EntityServiceProxy.Create<IPartyList>(new()),
            EntityServiceProxy.Create<IClientState>(new() { ["TerritoryType"] = 1363u }),
            EntityServiceProxy.Create<IPlayerState>(new()),
            DateTimeOffset.UtcNow);
        if (snapshot.Combatants.Count != 2 ||
            snapshot.Combatants.SingleOrDefault(x => x.Id == 0x40001234u) is not
                { CurrentHp: 900, MaxHp: 1000, Statuses.Count: 0, PosX: 10, PosZ: 20 } ||
            snapshot.Combatants.SingleOrDefault(x => x.Id == 0x40005678u) is not { BNpcId: 2015156 })
        {
            throw new InvalidOperationException(
                "A battle object without a status manager was dropped or corrupted the whole entity frame.");
        }

        Console.WriteLine("Entity snapshot: battle object without a status manager kept its own row and the rest of the frame.");
    }
}
