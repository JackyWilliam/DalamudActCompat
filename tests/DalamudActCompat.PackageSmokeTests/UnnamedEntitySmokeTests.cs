using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using DalamudActCompat.Plugin;
using DalamudActCompat.Overlay;

internal static class UnnamedEntitySmokeTests
{
    public static void Run()
    {
        IGameObject Entity(uint id, uint baseId, string name, Vector3 position)
            => EntityServiceProxy.Create<IGameObject>(new()
            {
                ["EntityId"] = id,
                ["BaseId"] = baseId,
                ["Name"] = new SeString(new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(name)),
                ["ObjectKind"] = ObjectKind.EventObj,
                ["Position"] = position,
                ["Address"] = (nint)0x12345678,
            });

        // U7b's own event samples use an empty name for these scene objects. A name is
        // display text, not an identity requirement for the Utils ActorControl lookup.
        var statue = Entity(0x4001895C, 2015156, "", new(100, 0, 100));
        var left = Entity(0x40018959, 2015164, "", new(92, 15, 27));
        var right = Entity(0x4001895A, 2015165, "", new(108, 15, 27));
        var named = Entity(0x40000001, 100, "Named control", new(1, 2, 3));
        var whitespace = Entity(0x40000002, 101, " ", new(4, 5, 6));
        var objects = new[]
        {
            named, statue, left, right, whitespace,
            Entity(0, 2015156, "Invalid zero", default),
            Entity(0xE0000000, 2015156, "Invalid sentinel", default),
        };
        var objectTable = EntityServiceProxy.Create<IObjectTable>(new(), objects);
        var party = EntityServiceProxy.Create<IPartyList>(new());
        var client = EntityServiceProxy.Create<IClientState>(new() { ["TerritoryType"] = 1363u });
        var player = EntityServiceProxy.Create<IPlayerState>(new());
        var at = DateTimeOffset.UtcNow;
        var snapshot = FfxivEntitySnapshotBuilder.Build(objectTable, party, client, player, at);
        if (snapshot.Combatants.Count != 5 ||
            snapshot.Combatants.SingleOrDefault(x => x.Id == statue.EntityId) is not
                { Name: "", BNpcId: 2015156, PosX: 100, PosY: 0, PosZ: 100 } ||
            snapshot.Combatants.SingleOrDefault(x => x.Id == left.EntityId)?.BNpcId != 2015164 ||
            snapshot.Combatants.SingleOrDefault(x => x.Id == right.EntityId)?.BNpcId != 2015165)
        {
            throw new InvalidOperationException(
                $"Object-table snapshot lost unnamed U7b scene objects: expected 5 valid entities, got {snapshot.Combatants.Count}.");
        }

        var nextObjects = new[] { named, statue, right, Entity(right.EntityId, 2015165, "", new(109, 15, 27)) };
        var next = FfxivEntitySnapshotBuilder.Build(
            EntityServiceProxy.Create<IObjectTable>(new(), nextObjects), party, client, player, at.AddMilliseconds(30));
        var delta = FfxivEntitySnapshotBuilder.BuildDelta(snapshot, next);
        if (next.Combatants.Count != 3 || delta.Upserts.Count != 1 ||
            delta.Upserts[0] is not { Id: 0x4001895A, Name: "", PosX: 109 } ||
            !delta.RemovedIds.ToHashSet().SetEquals([left.EntityId, whitespace.EntityId]))
        {
            throw new InvalidOperationException("Unnamed entity movement, duplicate identity, or removal regressed.");
        }
        Console.WriteLine("Unnamed entity snapshot: P1 statue, both half-room objects, named/invalid controls and delta lifecycle passed.");
        ValidateU7bDrawings();
    }

    private static void ValidateU7bDrawings()
    {
        var captured = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "U7bP1Drawings.json")))!;
        foreach (var (scenario, payloads) in captured)
        {
            // Captured from the real Host running the unmodified Utils/U7b action chain,
            // these are separate callbacks, as in a fight, not a synthetic atomic batch.
            using var overlay = new PictoActOverlayService(null!);
            foreach (var payload in payloads) overlay.Apply(payload);
            var expected = scenario.StartsWith("filtered-", StringComparison.Ordinal) ? 0 :
                scenario.EndsWith("spread", StringComparison.Ordinal) ? 9 :
                scenario.EndsWith("stack", StringComparison.Ordinal) ? 3 : 1;
            if (overlay.ShapeCount != expected)
                throw new InvalidOperationException($"{scenario}: expected {expected} stored shapes, got {overlay.ShapeCount}.");
            var circles = overlay.ShapeSnapshot.Where(x => x.VfxPath == "vfx/omen/eff/m0532om_don01x.avfx").ToArray();
            var expectedCircles = scenario == "retained-spread" ? 8 : scenario == "retained-stack" ? 2 : 0;
            if (circles.Length != expectedCircles ||
                circles.Select(x => x.Position).Distinct().Count() != expectedCircles)
                throw new InvalidOperationException($"{scenario}: circles lost their distinct positions or native resource.");
            if (scenario is "retained-40018959" or "retained-4001895A")
            {
                var angle = scenario.EndsWith("59", StringComparison.Ordinal) ? -MathF.PI / 2 : MathF.PI / 2;
                if (MathF.Abs(overlay.ShapeSnapshot.Single().Angle - angle) > 0.00001f)
                    throw new InvalidOperationException($"{scenario}: half-room facing is incorrect.");
            }
        }
        Console.WriteLine("Real U7b callback payloads: 8/2 distinct circles, personal arrows and both half-room angles passed managed drawing.");
    }
}

public class EntityServiceProxy : DispatchProxy
{
    private Dictionary<string, object?> values = [];
    private IEnumerable? items;

    public static T Create<T>(Dictionary<string, object?> values, IEnumerable? items = null) where T : class
    {
        var result = Create<T, EntityServiceProxy>();
        var proxy = (EntityServiceProxy)(object)result;
        proxy.values = values;
        proxy.items = items;
        return result;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.Name == "GetEnumerator")
        {
            var element = method.ReturnType.IsGenericType ? method.ReturnType.GenericTypeArguments[0] : typeof(object);
            if (items is not null)
            {
                return method.ReturnType.IsGenericType
                    ? typeof(IEnumerable<>).MakeGenericType(element).GetMethod("GetEnumerator")!.Invoke(items, null)
                    : items.GetEnumerator();
            }
            return ((IEnumerable)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!).GetEnumerator();
        }
        if (values.TryGetValue(method.Name.Replace("get_", "", StringComparison.Ordinal), out var value))
        {
            return value is not null && !method.ReturnType.IsInstanceOfType(value)
                ? Convert.ChangeType(value, method.ReturnType) : value;
        }
        return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
