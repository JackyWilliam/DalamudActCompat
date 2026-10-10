using System.Collections;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DalamudActCompat.Overlay;

internal static class DrawingLifetimeSmokeTests
{
    private static readonly FieldInfo RefreshAt = typeof(PictoActOverlayService)
        .GetField("nextDynamicRefreshAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Stored = typeof(PictoActOverlayService)
        .GetField("shapes", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Pending = typeof(PictoActOverlayService)
        .GetField("pendingCommands", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run(bool faultOnly = false)
    {
        DynamicFailureMustNotFreezeOtherShapes("_d", new(2000, 0, 0));
        DynamicFailureMustNotFreezeOtherShapes("10 / _d", Vector3.Zero);
        ExpiredFailureMustNotBlockExpiry();
        if (faultOnly) return;
        RepeatedPulls();
        M11Positions();
    }

    private static void DynamicFailureMustNotFreezeOtherShapes(string scale, Vector3 invalidTarget)
    {
        var state = new Dictionary<string, object?> { ["EntityId"] = 0x40000001u, ["Position"] = new Vector3(10, 0, 0) };
        var healthy = new Dictionary<string, object?> { ["EntityId"] = 0x40000002u, ["Position"] = new Vector3(20, 0, 0) };
        var objects = new List<IGameObject> { EntityServiceProxy.Create<IGameObject>(state), EntityServiceProxy.Create<IGameObject>(healthy) };
        using var overlay = new PictoActOverlayService(null!, objectTable: EntityServiceProxy.Create<IObjectTable>([], objects));
        // NativeOnly prevents a test from touching ImGui or a game VFX function. The
        // production Draw, dynamic resolver, expiry and storage paths still execute.
        overlay.Apply($"Omen: vfx/omen/eff/soak_test.avfx\nTag: bad\nPos: 0, 0\nTarget: 40000001\nScale: 1, {scale}, 1\nt: 60");
        overlay.Apply("Omen: vfx/omen/eff/soak_test.avfx\nTag: healthy\nPos: 40000002\nScale: 1\nt: 60");
        Frame(overlay);
        state["Position"] = invalidTarget;
        healthy["Position"] = new Vector3(33, 2, 44);
        Frame(overlay);
        Require(Shapes(overlay).Values.Single(x => x.SemanticTag == "healthy").Shape.Position == new Vector3(33, 2, 44),
            "One invalid dynamic shape froze a healthy drawing.");
        Require(Shapes(overlay).Values.Single(x => x.SemanticTag == "bad").DynamicRefreshFailed,
            "An invalid drawing remained visible at its stale position.");
        for (var frame = 0; frame < 100; frame++) Frame(overlay);
        // A failed source can recover without reloading the Host or discarding the
        // user's still-valid shape; do not convert a transient condition into a latch.
        state["Position"] = new Vector3(5, 0, 0);
        Frame(overlay);
        var recovered = Shapes(overlay).Values.Single(x => x.SemanticTag == "bad").Shape;
        Require(Math.Abs(recovered.SecondaryScale - (scale == "_d" ? 5 : 2)) < 0.0001f,
            "A dynamic shape did not recover when valid entity data returned.");
        Require(!Shapes(overlay).Values.Single(x => x.SemanticTag == "bad").DynamicRefreshFailed,
            "A recovered drawing remained hidden.");
        state["Position"] = invalidTarget;
        Frame(overlay);
        overlay.Apply("Action: Change\nTag: bad\nPos: 4, 5\nAngle: 0\nScale: 1");
        Frame(overlay);
        Require(!Shapes(overlay).Values.Single(x => x.SemanticTag == "bad").DynamicRefreshFailed,
            "Replacing a failed entity binding with static coordinates left the drawing hidden.");
        Console.WriteLine($"PASS dynamic failure isolation and recovery: {scale}");
    }

    private static void ExpiredFailureMustNotBlockExpiry()
    {
        var state = new Dictionary<string, object?> { ["EntityId"] = 0x40000001u, ["Position"] = new Vector3(10, 0, 0) };
        var objects = new List<IGameObject> { EntityServiceProxy.Create<IGameObject>(state) };
        using var overlay = new PictoActOverlayService(null!, objectTable: EntityServiceProxy.Create<IObjectTable>([], objects));
        overlay.Apply("Omen: vfx/omen/eff/soak_test.avfx\nPos: 0, 0\nTarget: 40000001\nScale: 1, _d, 1\nt: 60");
        state["Position"] = new Vector3(2000, 0, 0);
        foreach (var stored in Shapes(overlay).Values) stored.Shape = stored.Shape with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        Frame(overlay);
        Require(overlay.ShapeCount == 0, "An expired invalid drawing blocked its own cleanup.");
        Console.WriteLine("PASS expiry before invalid entity refresh");
    }

    private static void RepeatedPulls()
    {
        var timer = Stopwatch.StartNew();
        var state = new Dictionary<string, object?> { ["EntityId"] = 0x40000001u, ["Position"] = new Vector3(100, 0, 100) };
        var objects = new List<IGameObject> { EntityServiceProxy.Create<IGameObject>(state) };
        using var overlay = new PictoActOverlayService(null!, objectTable: EntityServiceProxy.Create<IObjectTable>([], objects));
        var peak = 0;
        for (var pull = 0; pull < 1000; pull++)
        {
            overlay.Apply("Omen: vfx/omen/eff/soak_test.avfx\nTag: follow\nPos: 40000001\nScale: 1\nt: 60");
            for (var frame = 0; frame < 300; frame++)
            {
                var position = new Vector3(pull % 50, frame % 5, frame % 73);
                state["Position"] = position;
                Frame(overlay);
                Require(overlay.ShapeSnapshot.Single().Position == position, $"Coordinate froze at pull {pull}, frame {frame}.");
            }
            overlay.Apply("Omen: vfx/omen/eff/soak_test.avfx\nTag: delayed\nDelay: 30\nScale: 1\nt: 5");
            peak = Math.Max(peak, overlay.ShapeCount);
            if (pull % 2 == 0)
            {
                // Same-territory wipe cleanup uses the trigger's Remove semantics.
                overlay.Apply("Action: Remove");
            }
            else overlay.Clear();
            overlay.ProcessPending(DateTimeOffset.UtcNow.AddHours(12));
            Frame(overlay);
            Require(overlay.ShapeCount == 0 && ((ICollection)Pending.GetValue(overlay)!).Count == 0,
                $"Old shapes or delayed actions survived pull {pull}.");
        }
        Console.WriteLine($"PASS 1000 pulls / 300000 dynamic frames / 1000 cancelled delayed creates; peak shapes={peak}; wall={timer.Elapsed.TotalSeconds:F2}s (accelerated, not a 12-hour soak)");
    }

    private static void M11Positions()
    {
        var cases = JsonSerializer.Deserialize<CommandCase[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PictoOnlineCommands.json")))!
            .Where(x => x.Name.StartsWith("M11 ", StringComparison.Ordinal)).ToArray();
        using var overlay = new PictoActOverlayService(null!);
        foreach (var item in cases)
        for (var pull = 0; pull < 100; pull++)
        {
            var variables = new Dictionary<string, string>(item.Variables) { ["${x}"] = (80 + pull % 40).ToString(), ["${y}"] = (85 + pull % 30).ToString() };
            foreach (var original in item.Payloads)
            {
                var payload = original;
                foreach (var variable in variables) payload = payload.Replace(variable.Key, variable.Value, StringComparison.Ordinal);
                overlay.Apply(payload);
            }
            Require(overlay.ShapeCount == item.Shapes, item.Name + " lost shared-tag shapes.");
            var center = new Vector3(80 + pull % 40, 0, 85 + pull % 30);
            Require(overlay.ShapeSnapshot.All(s => Vector3.Distance(s.Position, center) <= 4.501f), item.Name + " retained a previous pull's position.");
            if (item.Name.Contains("sword", StringComparison.Ordinal))
            {
                var heading = float.Parse(item.Variables["${h}"], System.Globalization.CultureInfo.InvariantCulture);
                // Dictionary slots may be reused in either order after a wipe.
                // The invariant is the two perpendicular headings, not enumeration order.
                var actual = overlay.ShapeSnapshot.Select(s => s.Angle).Order().ToArray();
                var expected = new[] { heading, heading + MathF.PI / 2 }.Order().ToArray();
                Require(actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 0.00001),
                    $"{item.Name} changed cross orientation: expected {string.Join('/', expected)}, actual {string.Join('/', actual)}.");
            }
            else
            {
                // Fixed arena offsets independently catch mirrored/rotated safe spots;
                // checking only their distance from the center would miss those errors.
                const float diagonal = 3.1819805f;
                Vector2[][] offsets =
                [
                    [new(0, -4.5f), new(-4.5f, 0), new(-diagonal, -diagonal), new(diagonal, -diagonal)],
                    [new(-4.5f, 0), new(0, 4.5f), new(-diagonal, diagonal), new(-diagonal, -diagonal)],
                    [new(0, 4.5f), new(4.5f, 0), new(diagonal, diagonal), new(-diagonal, diagonal)],
                    [new(4.5f, 0), new(0, -4.5f), new(diagonal, -diagonal), new(diagonal, diagonal)],
                ];
                var expected = offsets[int.Parse(item.Name[^1..])]
                    .Select(p => center + new Vector3(p.X, 0, p.Y)).Append(center);
                foreach (var position in expected)
                    Require(overlay.ShapeSnapshot.Any(s => Vector3.Distance(s.Position, position) < 0.0001f),
                        $"{item.Name} lost or mirrored a guide at {position}.");
            }
            overlay.Apply("Action: Remove\nRegex: ^MBs_");
            Require(overlay.ShapeCount == 0, "M11 wipe left old weapon shapes.");
        }
        Console.WriteLine($"PASS {cases.Length * 100} M11 original weapon payload replays, moving positions / four directions / same-tag cleanup");
    }

    private static Dictionary<string, StoredPictoActShape> Shapes(PictoActOverlayService overlay)
        => (Dictionary<string, StoredPictoActShape>)Stored.GetValue(overlay)!;

    private static void Frame(PictoActOverlayService overlay)
    {
        // Skip the real 100 ms polling wait without changing the implementation
        // under test. No wall-clock longevity claim is made for this stress loop.
        RefreshAt.SetValue(overlay, DateTimeOffset.MinValue);
        overlay.Draw();
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed record CommandCase(string Name, string[] Payloads, Dictionary<string, string> Variables, int Shapes);
}
