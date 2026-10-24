using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DalamudActCompat.Overlay;

// Expected values come from running Triggernometry 2.2.1.0's own XIVCoord, MathParser,
// DynamicCoordArg, ScaleArg and VfxManager.ResolveCoordArg on the same inputs and actor
// fixture, converted from ACT axes (x, y ground, z height) to Dalamud axes (x, height, y).
internal static class PictoTn221SyntaxSmokeTests
{
    private const uint Me = 0x10000001;
    private const uint Healer = 0x10000002;
    private const uint Caster = 0x10000003;
    private const uint OffTank = 0x10000004;
    private const uint DeadDps = 0x10000005;
    private const uint Boss = 0x40000010;
    private const uint Pet = 0x40000011;
    private const uint Add = 0x40000012;

    public static void Run()
    {
        ValidateAxisOverrides();
        ValidateEntitySelectors();
        ValidateSelectorDrawings();
        ValidateObjectTableCapture();
        ValidatePolarDirections();
        ValidateSpatialDistanceScale();
        ValidateModuloMinimum();
        Console.WriteLine(
            "PASS PictoACT TN 2.2.1.0 syntax: axis overrides, Target/Near/Far selectors, " +
            "polardir chains, _d3 and mod(x, y, min) match upstream; legacy forms unchanged");
    }

    private static PictoActEntity[] Fixture() =>
    [
        Entity(Me, new(100, 0, 100), kind: 1, job: 19, target: Boss),
        Entity(Healer, new(103, 0, 100), kind: 1, job: 24),
        // A transient duplicate object-table slot must not take a second distance rank.
        Entity(Healer, new(103, 0, 100), kind: 1, job: 24),
        Entity(Caster, new(100, 0, 110), kind: 1, job: 25),
        Entity(OffTank, new(95, 0, 100), kind: 1, job: 21),
        Entity(DeadDps, new(101, 0, 100), kind: 1, job: 22, hp: 0),
        // Dalamud reports "no owner" as 0xE0000000, ACT as 0; both are enemies.
        Entity(Boss, new(100, 0, 80), kind: 2, heading: MathF.PI / 2, owner: 0xE0000000),
        Entity(Pet, new(100, 0, 99), kind: 2, owner: Caster),
        Entity(Add, new(120, 2, 100), kind: 2, owner: 0),
        Entity(0xE0000000, new(100, 0, 100), kind: 7, hp: 0),
    ];

    private static PictoActEntity Entity(
        uint id,
        Vector3 position,
        byte kind,
        uint job = 0,
        uint hp = 1000,
        uint owner = 0xE0000000,
        uint target = 0xE0000000,
        float heading = 0)
        => new(id, position, heading, kind, job, hp, owner, target);

    private static Func<string, Vector3?> Positions(IReadOnlyList<PictoActEntity> entities)
        => raw => PictoActOverlayService.ResolveEntityState(raw, entities)?.Position;

    private static Func<string, float?> Headings(IReadOnlyList<PictoActEntity> entities)
        => raw => PictoActOverlayService.ResolveEntityState(raw, entities)?.Heading;

    private static void ValidateAxisOverrides()
    {
        PictoActEntity[] entities = [Entity(0x10ABCDEF, new(5, 4, 7), kind: 1, job: 24)];
        var overridden = PictoActOverlayService.Parse(
            "Omen: Circle\nt: 5\nPos: 10ABCDEF, y=100, z=0\nScale: 3",
            Positions(entities)).Single().Shape!;
        Check(
            overridden is { Position: { X: 5, Y: 0, Z: 100 }, SourcePositionEntity: "10ABCDEF, y=100, z=0" } &&
            overridden.RequiresDynamicRefresh,
            $"PictoACT 10ABCDEF, y=100, z=0 did not follow only the entity's X axis: {overridden.Position}.");

        PictoActEntity[] moved = [Entity(0x10ABCDEF, new(8, 6, 9), kind: 1, job: 24)];
        var refreshed = PictoActOverlayService.RefreshDynamicShape(
            overridden, Positions(moved), Headings(moved));
        Check(
            refreshed.Position is { X: 8, Y: 0, Z: 100 },
            $"PictoACT axis-override drawing did not keep fixed axes while following X: {refreshed.Position}.");

        Check(
            Resolve("0x10ABCDEF, z = 1.5", entities) is { X: 5, Y: 1.5f, Z: 7 } &&
            Resolve("10ABCDEF, X=1+2, y=3", entities) is { X: 3, Y: 4, Z: 3 },
            "PictoACT axis overrides lost case-insensitive axes or expression values.");

        // A plain ID still reaches the resolver verbatim, and fixed coordinates stay static.
        var plain = PictoActOverlayService.Parse(
            "Omen: Circle\nt: 5\nPos: 10ABCDEF\nScale: 3",
            raw => raw == "10ABCDEF" ? new Vector3(1, 2, 3) : null).Single().Shape!;
        var fixedCoordinate = PictoActOverlayService.Parse(
            "Omen: Circle\nt: 5\nPos: 100, 100, 0\nScale: 3").Single().Shape!;
        Check(
            plain is { Position: { X: 1, Y: 2, Z: 3 }, SourcePositionEntity: "10ABCDEF" } &&
            fixedCoordinate is { Position: { X: 100, Y: 0, Z: 100 }, RequiresDynamicRefresh: false },
            "PictoACT plain entity IDs or fixed coordinates changed after adding axis overrides.");
    }

    private static void ValidateEntitySelectors()
    {
        var entities = Fixture();
        var expected = new (string Spec, Vector3? Position)[]
        {
            ("10000001.Target", new(100, 0, 80)),
            ("10000001.NearPlayer1", new(103, 0, 100)),
            ("10000001.NearPlayer2", new(95, 0, 100)),
            ("10000001.FarPlayer1", new(100, 0, 110)),
            ("10000001.NearTank1", new(95, 0, 100)),
            ("10000001.NearTank2", null),
            ("10000001.NearHealer1", new(103, 0, 100)),
            ("10000001.NearDPS1", new(100, 0, 110)),
            ("10000001.NearNonTank1", new(103, 0, 100)),
            ("10000001.NearEnemy1", new(100, 0, 80)),
            ("10000001.NearEnemy2", new(120, 2, 100)),
            ("10000001.FarEnemy1", new(100, 0, 80)),
            ("120, 95, 0.NearEnemy1", new(120, 2, 100)),
            ("100, 100, 0.NearPlayer1", new(100, 0, 100)),
            ("10000001.Target, z=5", new(100, 5, 80)),
            ("10000003.NearEnemy1", new(120, 2, 100)),
            ("10000002.Target", null),
            ("10000001, y=50", new(100, 0, 50)),
            // DACT also accepts decimal entity IDs and loose spacing/case like upstream's regex.
            ("268435457.Target", new(100, 0, 80)),
            ("0x10000001. near healer 1", new(103, 0, 100)),
        };
        foreach (var (spec, position) in expected)
        {
            var actual = Resolve(spec, entities);
            Check(
                actual == position,
                $"PictoACT selector '{spec}' resolved to {actual?.ToString() ?? "none"}, " +
                $"upstream selects {position?.ToString() ?? "none"}.");
        }

        Check(
            PictoActOverlayService.ResolveEntityState("10000001.Target", entities)?.Heading == MathF.PI / 2,
            "PictoACT selector did not expose the selected entity's heading.");

        foreach (var invalid in new[] { "100, 100, 0.Target", "10000001.NearPlayer0" })
        {
            ExpectRejected(
                $"Omen: Circle\nt: 5\nPos: {invalid}\nScale: 3",
                $"PictoACT accepted the upstream-invalid selector '{invalid}'.");
        }
    }

    private static void ValidateSelectorDrawings()
    {
        var entities = Fixture();
        var targetLine = PictoActOverlayService.Parse(
            "Omen: Rect\nTag: TARGET_LINE\nt: 5\nPos: 10000001\nTarget: 10000001.Target\nScale: 1, _d, 1",
            Positions(entities)).Single().Shape!;
        Check(
            targetLine.SourceTargetEntity == "10000001.Target" &&
            Near(targetLine.SecondaryScale, 20) &&
            Near(MathF.Abs(targetLine.Angle), MathF.PI),
            $"PictoACT Target: <id>.Target did not aim at the current target: " +
            $"length={targetLine.SecondaryScale}, angle={targetLine.Angle}.");

        // Selectors are re-ranked on refresh: the boss moves and the nearest player changes.
        var moved = Fixture()
            .Select(entity => entity.EntityId switch
            {
                Boss => entity with { Position = new(110, 0, 100) },
                Caster => entity with { Position = new(100, 0, 101) },
                _ => entity,
            })
            .ToArray();
        var retargeted = PictoActOverlayService.RefreshDynamicShape(
            targetLine, Positions(moved), Headings(moved));
        var nearest = PictoActOverlayService.Parse(
            "Omen: Circle\nt: 5\nPos: 10000001.NearPlayer1\nScale: 2",
            Positions(entities)).Single().Shape!;
        var reranked = PictoActOverlayService.RefreshDynamicShape(
            nearest, Positions(moved), Headings(moved));
        Check(
            Near(retargeted.SecondaryScale, 10) &&
            Near(retargeted.Angle, MathF.PI / 2) &&
            nearest.Position == new Vector3(103, 0, 100) &&
            reranked.Position == new Vector3(100, 0, 101),
            "PictoACT .Target/.NearPlayer selectors did not re-resolve moving entities on refresh.");

        var thetaSelector = PictoActOverlayService.Parse(
            "Omen: Circle\nt: 5\nO: 100, 100\nθ: 10000001.NearHealer1\nPos: 0, -5\nScale: 1",
            Positions(entities)).Single().Shape!;
        Check(
            thetaSelector.TransformRotationMode == PictoActTransformRotationMode.Target &&
            thetaSelector.TransformRotationTargetEntity == "10000001.NearHealer1" &&
            thetaSelector.TransformRotation is { } thetaRotation && Near(thetaRotation, MathF.PI / 2) &&
            Near(thetaSelector.Position.X, 105) && Near(thetaSelector.Position.Z, 100),
            $"PictoACT θ selector did not face the selected entity: {thetaSelector.Position}.");

        // Without θ, a dynamic O inherits the facing of whichever entity it selected.
        var centerSelector = PictoActOverlayService.Parse(
            "Omen: Circle\nt: 5\nO: 10000001.Target\nPos: 0, -5\nScale: 1",
            Positions(entities)).Single().Shape!;
        var centered = PictoActOverlayService.RefreshDynamicShape(
            centerSelector, Positions(entities), Headings(entities));
        Check(
            centerSelector.TransformCenterEntity == "10000001.Target" &&
            centered.TransformRotation is { } centerRotation && Near(centerRotation, MathF.PI / 2) &&
            Near(centered.Position.X, 105) && Near(centered.Position.Z, 80),
            $"PictoACT O selector did not inherit the selected entity's heading: {centered.Position}.");

        var changed = PictoActOverlayService.ApplyPatch(
            nearest,
            PictoActOverlayService.Parse(
                "Action: Change\nPos: 10000001.FarPlayer1",
                Positions(entities)).Single().Patch!);
        Check(
            changed is { Position: { X: 100, Z: 110 }, SourcePositionEntity: "10000001.FarPlayer1" },
            "PictoACT Change did not accept an entity selector for Pos.");
    }

    private static void ValidateObjectTableCapture()
    {
        var objects = Fixture()
            .Select(entity => (IGameObject)EntityServiceProxy.Create<ICharacter>(new()
            {
                ["EntityId"] = entity.EntityId,
                ["Position"] = entity.Position,
                ["Rotation"] = entity.Heading,
                ["ObjectKind"] = (ObjectKind)entity.ObjectKind,
                ["ClassJob"] = ClassJobRow(entity.ClassJob),
                ["CurrentHp"] = entity.CurrentHp,
                ["OwnerId"] = entity.OwnerId,
                ["TargetObjectId"] = (ulong)entity.TargetId,
            }))
            .ToArray();
        using var overlay = new PictoActOverlayService(
            null!,
            objectTable: EntityServiceProxy.Create<IObjectTable>(new(), objects));
        overlay.Apply(
            "Omen: Circle\nTag: SELECTED\nt: 5\nPos: 10000001.NearHealer1\nScale: 2\n---\n" +
            "Omen: Circle\nTag: SELECTED\nt: 5\nPos: 10000001.NearEnemy2, z=0\nScale: 2\n---\n" +
            "Omen: Circle\nTag: SELECTED\nt: 5\nPos: 10000001.Target\nScale: 2");
        var positions = overlay.ShapeSnapshot.Select(shape => shape.Position).ToHashSet();
        Check(
            positions.SetEquals([new Vector3(103, 0, 100), new Vector3(120, 0, 100), new Vector3(100, 0, 80)]),
            "PictoACT selectors did not read job, owner, HP or target from the game object table: " +
            string.Join("; ", positions));
    }

    private static void ValidatePolarDirections()
    {
        var expected = new (string Pos, Vector3 Position)[]
        {
            ("polardir8 10, 1", new(-7.0710678f, 0, -7.0710678f)),
            ("100, 100, 0 polardirN4 10, 2", new(107.0710678f, 0, 107.0710678f)),
            ("polardir4 5, 1, 2", new(-5, 2, 0)),
            ("polardir8 10, -1", new(7.0710678f, 0, -7.0710678f)),
            ("polardirN8 4, 7.5", new(0, 0, -4)),
            ("100, 100 plus 1, 2, 3 minus 0.5, 0.5 polar 2, pi/2 minuspolar 1, pi/2, 4", new(101.5f, 7, 101.5f)),
            ("100, 100, 0 polardirN8 3, 0, 1 polar 2, pi/2", new(100.85195f, 1, 97.22836f)),
            // Legacy forms must keep their previous results.
            ("10, 20 polar 5, 0", new(10, 0, 25)),
            ("polar 6, 90°, 0.1", new(6, 0.1f, 0)),
            ("1, 2, 0polar 5, 0", new(1, 0, 7)),
            ("40000001 polar 2, 0", new(10, 1, 22)),
        };
        foreach (var (pos, position) in expected)
        {
            var actual = PictoActOverlayService.Parse(
                $"Omen: Circle\nt: 5\nPos: {pos}\nScale: 1",
                raw => raw == "40000001" ? new Vector3(10, 1, 20) : null).Single().Shape!.Position;
            Check(
                Vector3.Distance(actual, position) < 0.0001f,
                $"PictoACT coordinate '{pos}' parsed to {actual}, upstream gives {position}.");
        }

        var exaFlare = PictoActOverlayService.Parse(
            "Action: ExaFlare\nOmen: Circle\nn: 2\ndt: 1\nt: 1\nPos: 100, 100\n" +
            "dPos: polardir4 5, 1\nScale: 3");
        Check(
            exaFlare.Count == 2 &&
            Vector3.Distance(exaFlare[1].Shape!.Position, new Vector3(95, 0, 100)) < 0.0001f,
            "PictoACT ExaFlare dPos did not accept polardir.");
        ExpectRejected(
            "Omen: Circle\nt: 5\nPos: polardir0 1, 1\nScale: 1",
            "PictoACT accepted polardir with zero segments.");
    }

    private static void ValidateSpatialDistanceScale()
    {
        var created = PictoActOverlayService.Parse(
            "Omen: Rect\nt: 5\nPos: 0, 0, 0\nTarget: 3, 4, 12\nScale: 1, _d, _d3\n---\n" +
            "Omen: Circle\nt: 5\nPos: 0, 0, 0\nTarget: 3, 4, 12\nScaleCyl: _d3, 2\n---\n" +
            "Omen: Circle\nt: 5\nPos: 0, 0, 0\nTarget: 3, 4, 12\nScale: 2*_D3");
        Check(
            created[0].Shape is { PrimaryScale: 1, SecondaryScale: 5, TertiaryScale: 13 } &&
            created[1].Shape is { PrimaryScale: 13, SecondaryScale: 13, TertiaryScale: 2 } &&
            created[2].Shape is { PrimaryScale: 26, SecondaryScale: 26, TertiaryScale: 1 },
            "PictoACT _d3 did not use the 3D Pos-Target distance while _d stayed 2D.");

        // Height difference must change _d3 but not _d after an entity refresh.
        var entities = Fixture();
        var dynamic = PictoActOverlayService.Parse(
            "Omen: Rect\nt: 5\nPos: 10000001\nTarget: 10000001.Target\nScale: 1, _d, _d3",
            Positions(entities)).Single().Shape!;
        var raised = Fixture()
            .Select(entity => entity.EntityId == Boss ? entity with { Position = new(100, 15, 80) } : entity)
            .ToArray();
        var refreshed = PictoActOverlayService.RefreshDynamicShape(
            dynamic, Positions(raised), Headings(raised));
        var patched = PictoActOverlayService.ApplyPatch(
            created[0].Shape!,
            PictoActOverlayService.Parse("Action: Change\nScale: 1, 1, _d3 + 1").Single().Patch!);
        Check(
            Near(refreshed.SecondaryScale, 20) && Near(refreshed.TertiaryScale, 25) &&
            Near(patched.TertiaryScale, 14),
            $"PictoACT _d/_d3 refresh or Change drifted: d={refreshed.SecondaryScale}, " +
            $"d3={refreshed.TertiaryScale}, patched={patched.TertiaryScale}.");

        ExpectRejected(
            "Omen: Circle\nt: 5\nPos: 0, 0\nScale: _d3",
            "PictoACT accepted _d3 without a Target.");
        ExpectRejected(
            "Omen: Circle\nt: 5\nPos: 0, 0\nTarget: 3, 4\nScale: _d4",
            "PictoACT treated _d4 as a distance token, unlike upstream.");
    }

    private static void ValidateModuloMinimum()
    {
        foreach (var (expression, value) in new[]
                 {
                     ("mod(9, 8, -4)", 1.0), ("mod(-5, 8, -4)", 3.0), ("mod(4, 8, -4)", -4.0),
                     ("mod(-1, 8)", 7.0), ("mod(10, 8)", 2.0), ("mod(7.5, 2, 1)", 1.5),
                 })
        {
            var actual = PictoActOverlayService.EvaluateNumericExpression(expression);
            Check(
                Math.Abs(actual - value) < 1e-9,
                $"PictoACT {expression} evaluated to {actual}, upstream gives {value}.");
        }

        var angled = PictoActOverlayService.Parse(
            "Omen: Rect\nt: 5\nPos: 0, 0\nAngle: mod(5, 4, -2) * pi/2\nScale: 1").Single().Shape!;
        Check(
            Near(angled.Angle, MathF.PI / 2),
            "PictoACT field expressions did not evaluate mod(x, y, min).");
    }

    private static Vector3? Resolve(string spec, IReadOnlyList<PictoActEntity> entities)
        => PictoActOverlayService.ResolveEntityState(spec, entities)?.Position;

    private static object ClassJobRow(uint rowId)
    {
        // RowRef<ClassJob> lives in Lumina.Excel, which this project does not reference at
        // compile time; only its RowId matters to the selector.
        var type = typeof(ICharacter).GetProperty("ClassJob")!.PropertyType;
        var constructor = type.GetConstructors()
            .First(candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(uint)));
        return constructor.Invoke(constructor.GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(uint) ? (object?)rowId : null)
            .ToArray());
    }

    private static void ExpectRejected(string payload, string message)
    {
        try
        {
            _ = PictoActOverlayService.Parse(payload);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static bool Near(float actual, float expected) => MathF.Abs(actual - expected) < 0.0001f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
