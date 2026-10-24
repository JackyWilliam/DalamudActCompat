using System.Numerics;
using System.Text.Json;
using DalamudActCompat.Overlay;

internal static class PictoOnlineResourceSmokeTests
{
    public static void Run()
    {
        var expressions = Load<ExpressionCase[]>("PictoOnlineExpressions.json");
        foreach (var item in expressions)
        {
            var actual = PictoActOverlayService.EvaluateNumericExpression(item.Expression);
            Check(double.IsFinite(actual) && Math.Abs(actual - item.Expected) <= 1e-8 * Math.Max(1, Math.Abs(item.Expected)),
                $"{item.Source}/{item.Field}: {item.Expression} expected {item.Expected:R}, got {actual:R}.");
            if (item.Field is "+X" or "+Y" or "KeepX" or "KeepY")
            {
                var shape = PictoActOverlayService.Parse($"Omen: Circle\nScale: 1\n{item.Field}: {item.Expression}")[0].Shape!;
                var actualBool = item.Field is "+X" or "KeepX" ? shape.KeepX : shape.KeepY;
                Check(actualBool == (Math.Abs(item.Expected) >= 1e-9), "Boolean field lost upstream expression semantics.");
            }
        }

        var commandCases = Load<CommandCase[]>("PictoOnlineCommands.json");
        foreach (var item in commandCases)
        {
            // Keep the source payload intact until literal variable substitution.
            // In particular, never normalize !00/!01 to bool before DACT sees it.
            using var overlay = new PictoActOverlayService(null!);
            foreach (var original in item.Payloads)
            {
                var payload = original;
                foreach (var variable in item.Variables)
                    payload = payload.Replace(variable.Key, variable.Value, StringComparison.Ordinal);
                Check(!payload.Contains("${", StringComparison.Ordinal), $"{item.Name}: unresolved fixture variable.");
                overlay.Apply(payload);
            }
            // Some originals are delayed (U5a "delay: 2"); count them once they are due.
            overlay.ProcessPending(DateTimeOffset.UtcNow.AddHours(1));
            Check(overlay.ShapeCount == item.Shapes, $"{item.Name}: lost shapes in a batch or with a shared tag.");
            if (item.Asset is not null)
                Check(overlay.ShapeSnapshot.Count(s => s.VfxPath == item.Asset) == item.AssetCount,
                    $"{item.Name}: native asset changed.");
            foreach (var expected in item.Positions ?? [])
            {
                var position = new Vector3(expected[0], expected[1], expected[2]);
                Check(overlay.ShapeSnapshot.Any(s => Vector3.Distance(s.Position, position) < 1e-3f),
                    $"{item.Name}: no shape at upstream position {position}; got " +
                    string.Join("; ", overlay.ShapeSnapshot.Select(s => s.Position)));
            }
        }

        var snapshot = Load<SnapshotCase[]>("PictoOnline221Samples.json");
        foreach (var item in snapshot)
            CheckSnapshot(item);

        // Values from Triggernometry 2.2.1.0's MathParser: its implicit " *" before '(' and '√'
        // outranks '/', and blank text is 0 (vc.xml writes "-${l} / 2√2" and "delay: ").
        foreach (var (expression, expected) in new (string, double)[]
        {
            ("1/2√2", 0.35355339059327373), ("-100 / 2√2", -35.35533905932737), ("1/2 √2", 0.35355339059327373),
            ("6/2(1+2)", 1), ("(1)(2)/2(2)", 0.5), ("3/2√(2+2)", 0.75), ("sin(0)(2)+1", 1),
            ("-(1)(2)", -2), ("8/2*4", 16), ("", 0), ("  ", 0),
        })
        {
            var actual = PictoActOverlayService.EvaluateNumericExpression(expression);
            Check(Math.Abs(actual - expected) <= 1e-12 * Math.Max(1, Math.Abs(expected)),
                $"Implicit multiplication or blank input drifted from upstream: '{expression}' = {actual:R}, expected {expected:R}.");
        }

        foreach (var (expression, expected) in new (string, bool)[]
        {
            ("!00", true), ("!01", false), ("00", false), ("01", true),
            ("0x08 & 0x8", true), ("0x00 & 0x8", false),
            ("B == B", true), ("A == B", false), ("01 == 1", false),
            ("(3 % 2 = 0) = 0", true), ("!0 && 3 % 2 = 1", true),
            ("true", true), ("FALSE", false), ("0.0000000001", false),
            ("0.000000001", true),
        })
        {
            foreach (var alias in new[] { "+X", "+Y", "KeepX", "KeepY" })
            {
                using var overlay = new PictoActOverlayService(null!);
                overlay.Apply($"Omen: Circle\nTag: reflect\nPos: 2, 3\nScale: 1\n{alias}: {expression}");
                var shape = overlay.ShapeSnapshot.Single();
                Check((alias is "+X" or "KeepX" ? shape.KeepX : shape.KeepY) == expected,
                    $"Create: {alias}={expression}.");
                overlay.Apply($"Action: Change\nTag: reflect\n{alias}: !({expression})");
                shape = overlay.ShapeSnapshot.Single();
                Check((alias is "+X" or "KeepX" ? shape.KeepX : shape.KeepY) != expected,
                    $"Change: {alias}={expression}.");
            }
        }

        foreach (var position in new[] { "0, (!0 && 3 % 2 = 1 ? -11.35 : -4.63)", "(0, (!0 && 3 % 2 = 1 ? -11.35 : -4.63))", "[0, (-11.35)]", "<0, (-11.35)>" })
        {
            var shape = PictoActOverlayService.Parse($"Omen: Rect\nPos: {position}\nScale: 0.1, (!0 && 3 % 2 = 1 ? 11.35 : 4.63), 0.1")[0].Shape!;
            Check(Vector3.Distance(shape.Position, new(0, 0, -11.35f)) < 1e-5,
                "Coordinate wrapper removed parentheses from a component expression.");
        }

        // A malformed expression must still reject the whole batch without deleting
        // existing shapes, including NaN/Infinity hidden inside a bool comparison.
        foreach (var invalid in new[] { "!", "0x", "0xG1", "1 &&", "unknown", "1/0", "sqrt(-1)", "!sqrt(-1)", "!√(-1)", "(1/0) = 1", "0 && sqrt(-1)", "1 ? 2", "B = B" })
        {
            using var overlay = new PictoActOverlayService(null!);
            overlay.Apply("Omen: Circle\nTag: existing\nScale: 1");
            try
            {
                overlay.Apply($"Action: Remove\n---\nOmen: Circle\nScale: 1\nKeepX: {invalid}");
            }
            catch (InvalidDataException)
            {
                Check(overlay.ShapeCount == 1, "Rejected batch partially mutated overlay state.");
                continue;
            }
            throw new InvalidOperationException($"Invalid bool expression was accepted: {invalid}");
        }
        Console.WriteLine($"PASS online PictoACT: {expressions.Length} upstream expression samples, {commandCases.Length} original command cases, " +
            $"{snapshot.Length} TN 2.2.1.0 snapshot samples, aliases/change/vector/rejection checks");
    }

    private static void CheckSnapshot(SnapshotCase item)
    {
        var where = $"{item.Source}/{item.Field} '{item.Expression}' (template '{item.Template}')";
        switch (item.Kind)
        {
            case "scalar":
                var actual = PictoActOverlayService.EvaluateNumericExpression(item.Expression);
                Check(Math.Abs(actual - item.Expected!.Value) <= 1e-8 * Math.Max(1, Math.Abs(item.Expected.Value)),
                    $"{where}: expected {item.Expected:R}, got {actual:R}.");
                break;
            case "coord":
                var shape = PictoActOverlayService.Parse($"Omen: Circle\nScale: 1\nPos: {item.Expression}")[0].Shape!;
                if (item.Dynamic)
                {
                    Check(shape.SourcePositionEntity is not null, $"{where}: upstream follows an entity, DACT drew a fixed point.");
                }
                else
                {
                    var position = new Vector3(item.Position![0], item.Position[1], item.Position[2]);
                    Check(shape.SourcePositionEntity is null &&
                          Vector3.Distance(shape.Position, position) <= 1e-4f * Math.Max(1, position.Length()),
                        $"{where}: expected {position}, got {shape.Position}.");
                }

                break;
            default:
                var theta = PictoActOverlayService.Parse($"Omen: Circle\nScale: 1\nO: 100, 100\nθ: {item.Expression}")[0].Shape!;
                if (item.Angle is { } angle)
                {
                    Check(theta.TransformRotationMode == PictoActTransformRotationMode.Fixed &&
                          theta.TransformRotation is { } rotation &&
                          Math.Abs(rotation - angle) <= 1e-5 * Math.Max(1, Math.Abs(angle)),
                        $"{where}: expected angle {angle:R}, got {theta.TransformRotation:R}.");
                }
                else if (item.Dynamic)
                {
                    Check(theta.TransformRotationMode == PictoActTransformRotationMode.Target &&
                          theta.TransformRotationTargetEntity is not null,
                        $"{where}: upstream faces an entity, DACT did not.");
                }
                else
                {
                    var target = new Vector3(item.Target![0], item.Target[1], item.Target[2]);
                    Check(theta.TransformRotationMode == PictoActTransformRotationMode.Target &&
                          theta.TransformRotationTargetEntity is null &&
                          theta.TransformRotationTarget is { } actualTarget &&
                          Vector3.Distance(actualTarget, target) <= 1e-3f,
                        $"{where}: expected to face {target}, got {theta.TransformRotationTarget}.");
                }

                break;
        }
    }

    private static T Load<T>(string name)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record ExpressionCase(string Expression, double Expected, string Source, string Template, string Field);
    private sealed record CommandCase(string Name, string[] Payloads, Dictionary<string, string> Variables,
        int Shapes, string? Asset, int AssetCount, float[][]? Positions);
    // Positions and targets use Dalamud axes (x, height, ground y), converted by the audit harness.
    private sealed record SnapshotCase(string Kind, string Expression, double? Expected, bool Dynamic,
        float[]? Position, double? Angle, float[]? Target, string Source, string Field, string Template);
}
