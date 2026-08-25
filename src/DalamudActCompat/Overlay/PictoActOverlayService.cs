using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace DalamudActCompat.Overlay;

internal sealed partial class PictoActOverlayService : IDisposable
{
    private const int CurveSegments = 72;
    private static readonly Vector4 DefaultColor = new(1f, 0.82f, 0.18f, 0.85f);
    private readonly IGameGui gameGui;
    private readonly IObjectTable? objectTable;
    private readonly IPluginLog? log;
    private readonly PictoActNativeVfxBackend? nativeVfx;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, StoredPictoActShape> shapes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PictoActOverlayCommand> pendingCommands = [];
    private readonly List<nint> pendingNativeRemovals = [];
    private long shapeSequence;
    private DateTimeOffset nextDynamicRefreshAt;

    internal PictoActOverlayService(
        IGameGui gameGui,
        ISigScanner? sigScanner = null,
        IGameInteropProvider? gameInteropProvider = null,
        IPluginLog? log = null,
        IObjectTable? objectTable = null)
    {
        this.gameGui = gameGui;
        this.log = log;
        this.objectTable = objectTable;
        if (sigScanner is not null && gameInteropProvider is not null && log is not null)
        {
            nativeVfx = PictoActNativeVfxBackend.TryCreate(
                sigScanner,
                gameInteropProvider,
                log);
        }
    }

    internal int ShapeCount
    {
        get
        {
            lock (syncRoot)
            {
                return shapes.Count;
            }
        }
    }

    internal void Apply(string payload)
    {
        foreach (var command in Parse(payload, ResolveEntityPosition))
        {
            lock (syncRoot)
            {
                if (command.ExecuteAt > DateTimeOffset.UtcNow)
                {
                    pendingCommands.Add(command);
                    continue;
                }

                ExecuteCommand(command);
            }
        }
    }

    private void ExecuteCommand(PictoActOverlayCommand command)
    {
        if (command.Remove)
        {
            if (command.RemovalScope == PictoActRemovalScope.Actor)
            {
                // Actor VFX live in Triggernometry's upstream manager in the Host. The
                // rewritten callback forwards that half there and brokers only static VFX.
                return;
            }

            CancelPendingMatching(command.Tag, command.Regex);
            RemoveMatching(command.Tag, command.Regex);
            return;
        }

        if (command.Change)
        {
            ChangeMatching(command);
            return;
        }

        var semanticTag = string.IsNullOrWhiteSpace(command.Tag)
            ? "Auto"
            : command.Tag;
        // PictoACT tags are selectors, not unique identifiers: several VFX with
        // one tag must coexist so a later Change/Remove can update the whole group.
        var storageKey = $"Shape:{Interlocked.Increment(ref shapeSequence)}";
        shapes[storageKey] = new StoredPictoActShape(semanticTag, command.Shape!);
    }

    private void CancelPendingMatching(string? tag, Regex? regex)
    {
        pendingCommands.RemoveAll(command => MatchesSelector(
            string.IsNullOrWhiteSpace(command.Tag) ? "Auto" : command.Tag,
            tag,
            regex));
    }

    private static bool MatchesSelector(string semanticTag, string? tag, Regex? regex)
        => !string.IsNullOrWhiteSpace(tag)
            ? string.Equals(semanticTag, tag, StringComparison.OrdinalIgnoreCase)
            : regex is null || regex.IsMatch(semanticTag);

    private void RemoveMatching(string? tag, Regex? regex)
    {
        if (string.IsNullOrWhiteSpace(tag) && regex is null)
        {
            foreach (var stored in shapes.Values)
            {
                QueueNativeRemovals(stored.NativeHandles);
            }

            shapes.Clear();
            return;
        }

        foreach (var key in shapes
                     .Where(pair =>
                         !string.IsNullOrWhiteSpace(tag)
                            ? string.Equals(
                                pair.Value.SemanticTag,
                                tag,
                                StringComparison.OrdinalIgnoreCase)
                            : regex!.IsMatch(pair.Value.SemanticTag))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            QueueNativeRemovals(shapes[key].NativeHandles);
            shapes.Remove(key);
        }
    }

    private void ChangeMatching(PictoActOverlayCommand command)
    {
        foreach (var key in MatchingKeys(command.Tag, command.Regex))
        {
            var stored = shapes[key];
            stored.Shape = ApplyPatch(stored.Shape, command.Patch!);
            stored.NativeShapes = null;
            stored.NativeDirty = stored.NativeHandles.Count > 0;
        }
    }

    internal IReadOnlyList<PictoActShape> ShapeSnapshot
    {
        get
        {
            lock (syncRoot)
            {
                return shapes.Values.Select(value => value.Shape).ToArray();
            }
        }
    }

    internal void Clear()
    {
        lock (syncRoot)
        {
            ClearLocked();
            // Zone notifications can arrive on the parser thread. Draw drains these handles
            // on Dalamud's framework thread, where calling the client VFX remover is safe.
        }
    }

    private void ClearLocked()
    {
        foreach (var stored in shapes.Values)
        {
            QueueNativeRemovals(stored.NativeHandles);
        }

        shapes.Clear();
        // A delayed create from the previous territory must not resurrect stale VFX later.
        pendingCommands.Clear();
    }

    private string[] MatchingKeys(string? tag, Regex? regex)
        => shapes
            .Where(pair =>
                !string.IsNullOrWhiteSpace(tag)
                    ? string.Equals(
                        pair.Value.SemanticTag,
                        tag,
                        StringComparison.OrdinalIgnoreCase)
                    : regex is null || regex.IsMatch(pair.Value.SemanticTag))
            .Select(pair => pair.Key)
            .ToArray();

    internal void Draw()
    {
        List<PictoActShape> fallbackShapes = [];
        var now = DateTimeOffset.UtcNow;
        lock (syncRoot)
        {
            ProcessPending(now);
            if (now >= nextDynamicRefreshAt)
            {
                RefreshDynamicShapes();
                // Upstream PictoACT samples moving entities at roughly 100 ms. Matching that
                // cadence avoids needless object-table scans without changing visible behavior.
                nextDynamicRefreshAt = now.AddMilliseconds(100);
            }

            DrainNativeRemovals();
            foreach (var expired in shapes
                         .Where(pair => pair.Value.Shape.ExpiresAt <= now)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                QueueNativeRemovals(shapes[expired].NativeHandles);
                shapes.Remove(expired);
            }

            DrainNativeRemovals();
            foreach (var stored in shapes.Values.Where(value => value.Shape.StartsAt <= now))
            {
                ActivateOrUpdateNative(stored);
                if (ShouldDrawScreenFallback(
                        stored.Shape.Kind,
                        nativeVfx is not null))
                {
                    fallbackShapes.Add(stored.Shape);
                }
            }
        }

        if (fallbackShapes.Count == 0)
        {
            return;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        foreach (var shape in fallbackShapes)
        {
            DrawWorldShape(drawList, shape);
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            ClearLocked();
            DrainNativeRemovals();
            nativeVfx?.Dispose();
        }
    }

    internal void ProcessPending(DateTimeOffset now)
    {
        lock (syncRoot)
        {
            var due = pendingCommands
                .Where(command => command.ExecuteAt <= now)
                .OrderBy(command => command.ExecuteAt)
                .ToArray();
            foreach (var command in due)
            {
                if (!pendingCommands.Remove(command))
                {
                    // A due Remove can cancel another command from the same snapshot.
                    continue;
                }

                ExecuteCommand(command);
            }
        }
    }

    private void ActivateOrUpdateNative(StoredPictoActShape stored)
    {
        if (nativeVfx is null || stored.NativeCreationFailed)
        {
            return;
        }

        try
        {
            // Polygon decomposition is deterministic for one shape revision but can be
            // expensive for large BBY safe-zone paths, so only rebuild it after Change.
            var nativeShapes = stored.NativeShapes ??= BuildNativeShapes(stored.Shape);
            while (stored.NativeHandles.Count > nativeShapes.Count)
            {
                var lastIndex = stored.NativeHandles.Count - 1;
                QueueNativeRemoval(stored.NativeHandles[lastIndex]);
                stored.NativeHandles.RemoveAt(lastIndex);
            }

            for (var index = 0; index < nativeShapes.Count; index++)
            {
                if (index == stored.NativeHandles.Count)
                {
                    stored.NativeHandles.Add(nint.Zero);
                }

                var handle = stored.NativeHandles[index];
                if (handle != nint.Zero && !nativeVfx.IsActive(handle))
                {
                    // The client can independently reclaim one member of a compound polygon.
                    // Recreate only that member and keep the remaining native VFX stable.
                    handle = nint.Zero;
                    stored.NativeHandles[index] = nint.Zero;
                }

                if (handle == nint.Zero)
                {
                    stored.NativeHandles[index] = nativeVfx.Create(nativeShapes[index]);
                    continue;
                }

                if (stored.NativeDirty && !nativeVfx.Update(handle, nativeShapes[index]))
                {
                    // The remove hook can run between IsActive and Update. Clear() owns
                    // territory invalidation, so a still-stored shape is safe to recreate.
                    stored.NativeHandles[index] = nativeVfx.Create(nativeShapes[index]);
                }
            }

            stored.NativeDirty = false;
        }
        catch (Exception ex)
        {
            QueueNativeRemovals(stored.NativeHandles);
            stored.NativeHandles.Clear();
            stored.NativeCreationFailed = true;
            log?.Warning(ex, $"PictoACT native VFX '{stored.Shape.VfxPath}' failed.");
        }
    }

    private void RefreshDynamicShapes()
    {
        if (objectTable is null)
        {
            return;
        }

        foreach (var stored in shapes.Values.Where(value => value.Shape.RequiresDynamicRefresh))
        {
            var refreshed = RefreshDynamicShape(
                stored.Shape,
                ResolveEntityPosition,
                ResolveEntityHeading);
            var renderStateChanged = !HasEquivalentRenderState(stored.Shape, refreshed);
            stored.Shape = refreshed;
            if (!renderStateChanged)
            {
                continue;
            }

            stored.NativeShapes = null;
            stored.NativeDirty = stored.NativeHandles.Count > 0;
        }
    }

    internal static IReadOnlyList<PictoActShape> BuildNativeShapes(PictoActShape shape)
    {
        if (shape.Kind != PictoActShapeKind.Polygon)
        {
            return [shape];
        }

        var polygon = shape.Polygon ??
                      throw new InvalidDataException("PictoACT polygon has no points.");
        var result = new List<PictoActShape>();
        foreach (var (first, second, third) in TriangulateWorldPath(shape, polygon))
        {
            AppendIsoscelesTriangleVfx(result, shape, first, second, third);
        }

        return result.Count > 0
            ? result
            : throw new InvalidDataException("PictoACT polygon could not be triangulated.");
    }

    private static void AppendIsoscelesTriangleVfx(
        List<PictoActShape> result,
        PictoActShape template,
        Vector3 first,
        Vector3 second,
        Vector3 third)
    {
        const float tolerance = 0.05f;
        var oppositeSides = new[]
        {
            (Length: GroundDistance(second, third), Vertex: first),
            (Length: GroundDistance(third, first), Vertex: second),
            (Length: GroundDistance(first, second), Vertex: third),
        }.OrderBy(pair => pair.Length).ToArray();
        var (a, vertexA) = oppositeSides[0];
        var (b, vertexB) = oppositeSides[1];
        var (c, vertexC) = oppositeSides[2];

        if (MathF.Abs(a - b) <= tolerance && c > tolerance)
        {
            AppendNativeIsosceles(result, template, vertexC, vertexA, vertexB);
            return;
        }

        if (MathF.Abs(b - c) <= tolerance && a > tolerance)
        {
            AppendNativeIsosceles(result, template, vertexA, vertexB, vertexC);
            return;
        }

        if (MathF.Abs(c - a) <= tolerance && b > tolerance)
        {
            AppendNativeIsosceles(result, template, vertexB, vertexC, vertexA);
            return;
        }

        var difference = a * a + b * b - c * c;
        var error = MathF.Sqrt(2) * tolerance * MathF.Max(a, b);
        if (difference > error)
        {
            var circumcenter = Circumcenter(vertexA, vertexB, vertexC);
            AppendNativeIsosceles(result, template, circumcenter, vertexA, vertexB);
            AppendNativeIsosceles(result, template, circumcenter, vertexB, vertexC);
            AppendNativeIsosceles(result, template, circumcenter, vertexC, vertexA);
            return;
        }

        if (difference >= -error)
        {
            var midpoint = (vertexA + vertexB) / 2;
            AppendNativeIsosceles(result, template, midpoint, vertexC, vertexA);
            AppendNativeIsosceles(result, template, midpoint, vertexC, vertexB);
            return;
        }

        var ab = GroundVector(vertexB, vertexA);
        var bc = GroundVector(vertexB, vertexC);
        var projection = Vector2.Dot(bc, ab) / ab.LengthSquared();
        var foot = new Vector3(
            vertexB.X + projection * ab.X,
            (vertexA.Y + vertexB.Y + vertexC.Y) / 3,
            vertexB.Z + projection * ab.Y);
        var midpointB = (vertexC + vertexA) / 2;
        var midpointA = (vertexC + vertexB) / 2;
        AppendNativeIsosceles(result, template, midpointA, foot, vertexB);
        AppendNativeIsosceles(result, template, midpointA, foot, vertexC);
        AppendNativeIsosceles(result, template, midpointB, foot, vertexC);
        AppendNativeIsosceles(result, template, midpointB, foot, vertexA);
    }

    private static void AppendNativeIsosceles(
        List<PictoActShape> result,
        PictoActShape template,
        Vector3 apex,
        Vector3 firstBaseVertex,
        Vector3 secondBaseVertex)
    {
        var baseMidpoint = (firstBaseVertex + secondBaseVertex) / 2;
        var halfBase = GroundDistance(firstBaseVertex, secondBaseVertex) / 2;
        var height = GroundDistance(apex, baseMidpoint);
        if (halfBase <= 0.00001f || height <= 0.00001f)
        {
            return;
        }

        // PictoACT's source implementation uses this right-isosceles omen. Its unit
        // triangle is scaled by sqrt(2), so arbitrary polygons retain their exact area.
        const string triangleVfxPath = "vfx/omen/eff/x6d3_b2_triangle90_p1.avfx";
        var angle = MathF.Atan2(baseMidpoint.X - apex.X, baseMidpoint.Z - apex.Z);
        result.Add(template with
        {
            VfxPath = triangleVfxPath,
            Kind = PictoActShapeKind.NativeOnly,
            Position = apex,
            PrimaryScale = halfBase * MathF.Sqrt(2),
            SecondaryScale = height * MathF.Sqrt(2),
            Angle = angle,
            Pitch = 0,
            Yaw = 0,
            TertiaryScale = 1,
            SourcePosition = apex,
            SourceTarget = null,
            SourceAngle = angle,
            TransformCenter = null,
            TransformRotation = null,
            KeepX = true,
            KeepY = true,
            SourcePolygon = null,
            Polygon = null,
        });
    }

    private static Vector3 Circumcenter(Vector3 first, Vector3 second, Vector3 third)
    {
        var denominator = 2 * (
            first.X * (second.Z - third.Z) +
            second.X * (third.Z - first.Z) +
            third.X * (first.Z - second.Z));
        if (MathF.Abs(denominator) <= 0.00001f)
        {
            return (first + second + third) / 3;
        }

        var firstSquared = first.X * first.X + first.Z * first.Z;
        var secondSquared = second.X * second.X + second.Z * second.Z;
        var thirdSquared = third.X * third.X + third.Z * third.Z;
        return new Vector3(
            (firstSquared * (second.Z - third.Z) +
             secondSquared * (third.Z - first.Z) +
             thirdSquared * (first.Z - second.Z)) / denominator,
            (first.Y + second.Y + third.Y) / 3,
            (firstSquared * (third.X - second.X) +
             secondSquared * (first.X - third.X) +
             thirdSquared * (second.X - first.X)) / denominator);
    }

    private static Vector2 GroundVector(Vector3 from, Vector3 to)
        => new(to.X - from.X, to.Z - from.Z);

    private static float GroundDistance(Vector3 first, Vector3 second)
        => GroundVector(first, second).Length();

    internal static bool ShouldDrawScreenFallback(
        PictoActShapeKind kind,
        bool nativeBackendAvailable)
    {
        if (kind == PictoActShapeKind.NativeOnly)
        {
            return false;
        }

        // A native-capable omen with no handle must not silently become a screen overlay:
        // ImGui has no access to the game's depth buffer and will draw the range over actors.
        return !nativeBackendAvailable;
    }

    private void QueueNativeRemovals(IEnumerable<nint> handles)
    {
        foreach (var handle in handles)
        {
            QueueNativeRemoval(handle);
        }
    }

    private void QueueNativeRemoval(nint handle)
    {
        if (handle != nint.Zero)
        {
            pendingNativeRemovals.Add(handle);
        }
    }

    private void DrainNativeRemovals()
    {
        if (nativeVfx is null || pendingNativeRemovals.Count == 0)
        {
            pendingNativeRemovals.Clear();
            return;
        }

        foreach (var handle in pendingNativeRemovals)
        {
            try
            {
                _ = nativeVfx.Remove(handle);
            }
            catch (Exception ex)
            {
                log?.Warning(ex, $"PictoACT native VFX 0x{handle:X} could not be removed.");
            }
        }

        pendingNativeRemovals.Clear();
    }

    private void DrawWorldShape(ImDrawListPtr drawList, PictoActShape shape)
    {
        var worldPath = BuildWorldPath(shape);
        var projected = new Vector2?[worldPath.Count];
        var fillPath = new Vector2[worldPath.Count];
        var fillProjectionValid = true;
        for (var index = 0; index < worldPath.Count; index++)
        {
            var inFront = gameGui.WorldToScreen(worldPath[index], out var screen, out var inView);
            if (!inFront || !float.IsFinite(screen.X) || !float.IsFinite(screen.Y))
            {
                fillProjectionValid = false;
                continue;
            }

            // The three-result projection distinguishes safe off-viewport coordinates from
            // points behind the camera, which would otherwise mirror across the viewport.
            fillPath[index] = screen;
            if (inView)
            {
                projected[index] = screen;
            }
        }

        var displayColor = NormalizeDisplayColor(
            shape.HasExplicitColor ? shape.Color : DefaultColor);
        var viewport = ImGui.GetMainViewport();
        var viewportMaximum = viewport.Pos + viewport.Size;
        if (viewport.Size.X > 0 && viewport.Size.Y > 0)
        {
            var fillColor = displayColor with
            {
                W = Math.Clamp(displayColor.W * 0.24f, 0.05f, 0.34f),
            };
            var fill = ImGui.ColorConvertFloat4ToU32(fillColor);
            if (fillProjectionValid)
            {
                var clippedFillPath = ClipPolygonToRectangle(
                    fillPath,
                    viewport.Pos,
                    viewportMaximum);
                if (shape.Kind is PictoActShapeKind.Polygon or PictoActShapeKind.Fan)
                {
                    foreach (var (first, second, third) in TriangulatePolygon(clippedFillPath))
                    {
                        drawList.AddTriangleFilled(first, second, third, fill);
                    }
                }
                else
                {
                    DrawRadialFill(drawList, clippedFillPath, fill);
                }
            }
            else
            {
                // A single vertex behind the camera used to reject the whole fill. Clip each
                // world-space triangle independently so concave user polygons cannot bridge
                // separate visible regions while crossing the camera plane.
                foreach (var (first, second, third) in TriangulateWorldPath(shape, worldPath))
                {
                    var nearClipped = ProjectTriangleAcrossNearPlane(
                        first,
                        second,
                        third,
                        point =>
                        {
                            var inFront = gameGui.WorldToScreen(point, out var screen, out _);
                            return (inFront, screen);
                        });
                    var viewportClipped = ClipPolygonToRectangle(
                        nearClipped,
                        viewport.Pos,
                        viewportMaximum);
                    DrawConvexFill(drawList, viewportClipped, fill);
                }
            }
        }

        var outline = ImGui.ColorConvertFloat4ToU32(displayColor);
        Vector2? previous = null;
        foreach (var point in projected)
        {
            if (point is { } current && previous is { } from)
            {
                drawList.AddLine(from, current, outline, 3f);
            }

            previous = point;
        }
    }

    private static void DrawRadialFill(
        ImDrawListPtr drawList,
        IReadOnlyList<Vector2> points,
        uint color)
    {
        if (points.Count < 3)
        {
            return;
        }

        var center = Vector2.Zero;
        foreach (var point in points)
        {
            center += point;
        }

        center /= points.Count;
        for (var index = 0; index < points.Count; index++)
        {
            drawList.AddTriangleFilled(
                center,
                points[index],
                points[(index + 1) % points.Count],
                color);
        }
    }

    private static void DrawConvexFill(
        ImDrawListPtr drawList,
        IReadOnlyList<Vector2> points,
        uint color)
    {
        for (var index = 1; index + 1 < points.Count; index++)
        {
            drawList.AddTriangleFilled(points[0], points[index], points[index + 1], color);
        }
    }

    internal static IReadOnlyList<Vector2> ProjectTriangleAcrossNearPlane(
        Vector3 first,
        Vector3 second,
        Vector3 third,
        Func<Vector3, (bool InFront, Vector2 Screen)> project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var vertices = new[]
        {
            Project(first, project),
            Project(second, project),
            Project(third, project),
        };
        var result = new List<Vector2>(4);
        var previous = vertices[^1];
        foreach (var current in vertices)
        {
            if (current.InFront != previous.InFront)
            {
                if (!TryProjectNearPlaneIntersection(previous, current, project, out var intersection))
                {
                    return [];
                }

                result.Add(intersection);
            }

            if (current.InFront)
            {
                if (!IsUsableProjection(current.Screen))
                {
                    return [];
                }

                result.Add(current.Screen);
            }

            previous = current;
        }

        return result;
    }

    private static ProjectedWorldVertex Project(
        Vector3 world,
        Func<Vector3, (bool InFront, Vector2 Screen)> project)
    {
        var (inFront, screen) = project(world);
        return new ProjectedWorldVertex(world, inFront, screen);
    }

    private static bool TryProjectNearPlaneIntersection(
        ProjectedWorldVertex first,
        ProjectedWorldVertex second,
        Func<Vector3, (bool InFront, Vector2 Screen)> project,
        out Vector2 screen)
    {
        var front = first.InFront ? first : second;
        var behind = first.InFront ? second : first;
        if (!IsUsableProjection(front.Screen))
        {
            screen = default;
            return false;
        }

        screen = front.Screen;
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var midpoint = Vector3.Lerp(front.World, behind.World, 0.5f);
            var projected = Project(midpoint, project);
            if (projected.InFront && IsUsableProjection(projected.Screen))
            {
                front = projected;
                screen = projected.Screen;
            }
            else
            {
                behind = projected;
            }
        }

        return true;
    }

    private static bool IsUsableProjection(Vector2 screen)
    {
        const float projectionLimit = 1_000_000f;
        // Near-plane projection approaches infinity. Keep the last finite point far outside
        // any practical viewport so the following rectangle clip remains numerically stable.
        return float.IsFinite(screen.X) &&
               float.IsFinite(screen.Y) &&
               Math.Abs(screen.X) <= projectionLimit &&
               Math.Abs(screen.Y) <= projectionLimit;
    }

    private readonly record struct ProjectedWorldVertex(
        Vector3 World,
        bool InFront,
        Vector2 Screen);

    private static Vector4 NormalizeDisplayColor(Vector4 color)
    {
        var maximum = Math.Max(1f, Math.Max(color.X, Math.Max(color.Y, color.Z)));
        return new Vector4(
            Math.Clamp(color.X / maximum, 0, 1),
            Math.Clamp(color.Y / maximum, 0, 1),
            Math.Clamp(color.Z / maximum, 0, 1),
            Math.Clamp(color.W, 0, 1));
    }

    internal static IReadOnlyList<Vector2> ClipPolygonToRectangle(
        IReadOnlyList<Vector2> points,
        Vector2 minimum,
        Vector2 maximum)
    {
        var count = points.Count > 1 && points[0] == points[^1]
            ? points.Count - 1
            : points.Count;
        if (count < 3 || maximum.X <= minimum.X || maximum.Y <= minimum.Y)
        {
            return [];
        }

        IReadOnlyList<Vector2> clipped = points.Take(count).ToArray();
        clipped = ClipPolygonAgainstEdge(
            clipped, vertical: true, boundary: minimum.X, keepGreater: true);
        clipped = ClipPolygonAgainstEdge(
            clipped, vertical: true, boundary: maximum.X, keepGreater: false);
        clipped = ClipPolygonAgainstEdge(
            clipped, vertical: false, boundary: minimum.Y, keepGreater: true);
        return ClipPolygonAgainstEdge(
            clipped, vertical: false, boundary: maximum.Y, keepGreater: false);
    }

    private static IReadOnlyList<Vector2> ClipPolygonAgainstEdge(
        IReadOnlyList<Vector2> points,
        bool vertical,
        float boundary,
        bool keepGreater)
    {
        if (points.Count == 0)
        {
            return [];
        }

        var result = new List<Vector2>(points.Count + 2);
        var previous = points[^1];
        var previousCoordinate = vertical ? previous.X : previous.Y;
        var previousInside = keepGreater
            ? previousCoordinate >= boundary
            : previousCoordinate <= boundary;
        foreach (var current in points)
        {
            var currentCoordinate = vertical ? current.X : current.Y;
            var currentInside = keepGreater
                ? currentCoordinate >= boundary
                : currentCoordinate <= boundary;
            if (currentInside != previousInside)
            {
                result.Add(vertical
                    ? IntersectVertical(previous, current, boundary)
                    : IntersectHorizontal(previous, current, boundary));
            }

            if (currentInside)
            {
                result.Add(current);
            }

            previous = current;
            previousInside = currentInside;
        }

        return result;
    }

    private static Vector2 IntersectVertical(Vector2 from, Vector2 to, float x)
    {
        var delta = to.X - from.X;
        var factor = Math.Abs(delta) < 0.00001f ? 0 : (x - from.X) / delta;
        return new Vector2(x, from.Y + (to.Y - from.Y) * Math.Clamp(factor, 0, 1));
    }

    private static Vector2 IntersectHorizontal(Vector2 from, Vector2 to, float y)
    {
        var delta = to.Y - from.Y;
        var factor = Math.Abs(delta) < 0.00001f ? 0 : (y - from.Y) / delta;
        return new Vector2(from.X + (to.X - from.X) * Math.Clamp(factor, 0, 1), y);
    }

    private static IReadOnlyList<Vector3> BuildWorldPath(PictoActShape shape)
        => shape.Kind switch
        {
            PictoActShapeKind.Circle => BuildCirclePath(shape),
            PictoActShapeKind.Rectangle => BuildRectanglePath(shape, bidirectional: false),
            PictoActShapeKind.BidirectionalRectangle =>
                BuildRectanglePath(shape, bidirectional: true),
            PictoActShapeKind.Fan => BuildFanPath(shape),
            PictoActShapeKind.Polygon => BuildPolygonPath(shape),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

    private static IReadOnlyList<Vector3> BuildPolygonPath(PictoActShape shape)
    {
        var points = shape.Polygon ??
                     throw new InvalidDataException("PictoACT polygon has no points.");
        return [.. points, points[0]];
    }

    private static IReadOnlyList<(Vector2 First, Vector2 Second, Vector2 Third)>
        TriangulatePolygon(IReadOnlyList<Vector2> closedPoints)
        => TriangulatePolygonIndices(closedPoints)
            .Select(indices => (
                closedPoints[indices.First],
                closedPoints[indices.Second],
                closedPoints[indices.Third]))
            .ToArray();

    private static IReadOnlyList<(Vector3 First, Vector3 Second, Vector3 Third)>
        TriangulateWorldPath(PictoActShape shape, IReadOnlyList<Vector3> closedPoints)
    {
        var count = closedPoints.Count > 1 && closedPoints[0] == closedPoints[^1]
            ? closedPoints.Count - 1
            : closedPoints.Count;
        if (count < 3)
        {
            return [];
        }

        if (shape.Kind == PictoActShapeKind.Circle)
        {
            return Enumerable.Range(0, count)
                .Select(index => (
                    shape.Position,
                    closedPoints[index],
                    closedPoints[(index + 1) % count]))
                .ToArray();
        }

        if (shape.Kind == PictoActShapeKind.Fan)
        {
            return Enumerable.Range(1, count - 2)
                .Select(index => (
                    closedPoints[0],
                    closedPoints[index],
                    closedPoints[index + 1]))
                .ToArray();
        }

        if (shape.Kind is PictoActShapeKind.Rectangle or PictoActShapeKind.BidirectionalRectangle)
        {
            return Enumerable.Range(1, count - 2)
                .Select(index => (
                    closedPoints[0],
                    closedPoints[index],
                    closedPoints[index + 1]))
                .ToArray();
        }

        var groundPoints = closedPoints
            .Take(count)
            .Select(static point => new Vector2(point.X, point.Z))
            .ToArray();
        return TriangulatePolygonIndices(groundPoints)
            .Select(indices => (
                closedPoints[indices.First],
                closedPoints[indices.Second],
                closedPoints[indices.Third]))
            .ToArray();
    }

    private static IReadOnlyList<(int First, int Second, int Third)>
        TriangulatePolygonIndices(IReadOnlyList<Vector2> closedPoints)
    {
        var count = closedPoints.Count > 1 && closedPoints[0] == closedPoints[^1]
            ? closedPoints.Count - 1
            : closedPoints.Count;
        if (count < 3)
        {
            return [];
        }

        var indices = Enumerable.Range(0, count).ToList();
        var signedArea = 0f;
        for (var index = 0; index < count; index++)
        {
            var current = closedPoints[index];
            var next = closedPoints[(index + 1) % count];
            signedArea += current.X * next.Y - next.X * current.Y;
        }

        if (signedArea < 0)
        {
            indices.Reverse();
        }

        var triangles = new List<(int, int, int)>(count - 2);
        while (indices.Count > 3)
        {
            var clipped = false;
            for (var index = 0; index < indices.Count; index++)
            {
                var previous = indices[(index - 1 + indices.Count) % indices.Count];
                var current = indices[index];
                var next = indices[(index + 1) % indices.Count];
                var a = closedPoints[previous];
                var b = closedPoints[current];
                var c = closedPoints[next];
                if (Cross(a, b, c) <= 0.00001f || indices.Any(candidate =>
                        candidate != previous && candidate != current && candidate != next &&
                        IsInsideTriangle(closedPoints[candidate], a, b, c)))
                {
                    continue;
                }

                triangles.Add((previous, current, next));
                indices.RemoveAt(index);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                // Degenerate user points should still show an outline instead of
                // spinning forever in the renderer.
                return triangles;
            }
        }

        if (indices.Count == 3)
        {
            triangles.Add((indices[0], indices[1], indices[2]));
        }

        return triangles;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool IsInsideTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        // A reflex vertex on an ear boundary must also block clipping; otherwise the
        // produced triangles overlap and a concave safe zone gains extra filled area.
        => Cross(a, b, point) >= -0.00001f &&
           Cross(b, c, point) >= -0.00001f &&
           Cross(c, a, point) >= -0.00001f;

    private static IReadOnlyList<Vector3> BuildCirclePath(PictoActShape shape)
    {
        var result = new Vector3[CurveSegments + 1];
        for (var index = 0; index <= CurveSegments; index++)
        {
            var angle = MathF.Tau * index / CurveSegments;
            result[index] = shape.Position + GroundOffset(angle, shape.PrimaryScale);
        }

        return result;
    }

    private static IReadOnlyList<Vector3> BuildRectanglePath(
        PictoActShape shape,
        bool bidirectional)
    {
        var forward = GroundOffset(shape.Angle, 1);
        var right = GroundOffset(shape.Angle + MathF.PI / 2, 1);
        var start = bidirectional
            ? shape.Position - forward * shape.SecondaryScale
            : shape.Position;
        var end = shape.Position + forward * shape.SecondaryScale;
        var halfWidth = shape.PrimaryScale;
        return
        [
            start - right * halfWidth,
            start + right * halfWidth,
            end + right * halfWidth,
            end - right * halfWidth,
            start - right * halfWidth,
        ];
    }

    private static IReadOnlyList<Vector3> BuildFanPath(PictoActShape shape)
    {
        var result = new List<Vector3>(CurveSegments + 3) { shape.Position };
        var segmentCount = Math.Max(
            8,
            (int)MathF.Ceiling(CurveSegments * shape.FanRadians / MathF.Tau));
        var start = shape.Angle - shape.FanRadians / 2;
        for (var index = 0; index <= segmentCount; index++)
        {
            var angle = start + shape.FanRadians * index / segmentCount;
            result.Add(shape.Position + GroundOffset(angle, shape.PrimaryScale));
        }

        result.Add(shape.Position);
        return result;
    }

    private static Vector3 GroundOffset(float angle, float distance)
        => new(
            MathF.Sin(angle) * distance,
            0,
            MathF.Cos(angle) * distance);

    internal static PictoActShape ApplyPatch(
        PictoActShape shape,
        PictoActShapePatch patch)
    {
        var positionSpecified = patch.Position.HasValue;
        var sourcePosition = patch.Position ?? shape.SourcePosition;
        var sourcePositionEntity = positionSpecified
            ? patch.PositionEntity
            : shape.SourcePositionEntity;
        var sourceTarget = patch.TargetSpecified
            ? patch.Target
            : patch.Angle.HasValue
                ? null
                : shape.SourceTarget;
        var sourceTargetEntity = patch.TargetSpecified
            ? patch.TargetEntity
            : patch.Angle.HasValue
                ? null
                : shape.SourceTargetEntity;
        var sourceAngle = patch.Angle ??
            (sourceTarget.HasValue && (positionSpecified || patch.TargetSpecified)
                ? DirectionTo(sourcePosition, sourceTarget.Value)
                : shape.SourceAngle);
        var transformCenter = patch.TransformCenterSpecified
            ? patch.TransformCenter
            : shape.TransformCenter;
        var transformCenterEntity = patch.TransformCenterSpecified
            ? patch.TransformCenterEntity
            : shape.TransformCenterEntity;
        var transformRotationMode = patch.TransformRotationMode !=
                                    PictoActTransformRotationMode.Unspecified
            ? patch.TransformRotationMode
            : shape.TransformRotationMode;
        var transformRotationTarget = patch.TransformRotationSpecified
            ? patch.TransformRotationTarget
            : shape.TransformRotationTarget;
        var transformRotationTargetEntity = patch.TransformRotationSpecified
            ? patch.TransformRotationTargetEntity
            : shape.TransformRotationTargetEntity;
        var transformRotation = transformRotationMode switch
        {
            PictoActTransformRotationMode.Fixed => patch.TransformRotationSpecified
                ? patch.TransformRotation
                : shape.TransformRotation,
            PictoActTransformRotationMode.Target when transformRotationTarget.HasValue =>
                DirectionTo(transformCenter ?? Vector3.Zero, transformRotationTarget.Value),
            PictoActTransformRotationMode.Default when transformCenterEntity is null => null,
            _ => shape.TransformRotation ?? MathF.PI,
        };
        var keepX = patch.KeepX ?? shape.KeepX;
        var keepY = patch.KeepY ?? shape.KeepY;
        var (position, angle) = ResolveTransform(
            sourcePosition,
            sourceAngle,
            transformCenter,
            transformRotation,
            keepX,
            keepY);
        var polygon = shape.SourcePolygon?
            .Select(point => ResolveTransform(
                point,
                MathF.PI,
                transformCenter,
                transformRotation,
                keepX,
                keepY).Position)
            .ToArray();
        var scaleExpression = patch.ScaleExpression ?? shape.ScaleExpression;
        var scaleIsCylindrical = patch.ScaleExpression is not null
            ? patch.ScaleIsCylindrical
            : shape.ScaleIsCylindrical;
        var scale = scaleExpression is not null
            ? ParseScale(
                scaleExpression,
                scaleIsCylindrical,
                DistanceTo(sourcePosition, sourceTarget))
            : (Vector3?)null;
        return shape with
        {
            SourcePosition = sourcePosition,
            SourcePositionEntity = sourcePositionEntity,
            SourceTarget = sourceTarget,
            SourceTargetEntity = sourceTargetEntity,
            Position = position,
            PrimaryScale = MathF.Abs(scale?.X ?? shape.PrimaryScale),
            SecondaryScale = MathF.Abs(scale?.Y ?? shape.SecondaryScale),
            TertiaryScale = MathF.Abs(scale?.Z ?? shape.TertiaryScale),
            SourceAngle = sourceAngle,
            Angle = angle,
            Pitch = patch.Pitch ?? shape.Pitch,
            Yaw = patch.Yaw ?? shape.Yaw,
            Color = patch.Color ?? shape.Color,
            HasExplicitColor = patch.Color.HasValue || shape.HasExplicitColor,
            TransformCenter = transformCenter,
            TransformCenterEntity = transformCenterEntity,
            TransformRotation = transformRotation,
            TransformRotationMode = transformRotationMode,
            TransformRotationTarget = transformRotationTarget,
            TransformRotationTargetEntity = transformRotationTargetEntity,
            KeepX = keepX,
            KeepY = keepY,
            ScaleExpression = scaleExpression,
            ScaleIsCylindrical = scaleIsCylindrical,
            Polygon = polygon,
        };
    }

    internal static PictoActShape RefreshDynamicShape(
        PictoActShape shape,
        Func<string, Vector3?> positionResolver,
        Func<string, float?> headingResolver)
    {
        if (!shape.RequiresDynamicRefresh)
        {
            return shape;
        }

        var sourcePosition = shape.SourcePositionEntity is not null
            ? ResolveDynamicValue(
                shape.SourcePositionEntity,
                shape.SourcePosition,
                positionResolver)
            : shape.SourcePosition;
        var sourceTarget = shape.SourceTargetEntity is not null
            ? ResolveDynamicValue(
                shape.SourceTargetEntity,
                shape.SourceTarget ?? Vector3.Zero,
                positionResolver)
            : shape.SourceTarget;
        var transformCenter = shape.TransformCenterEntity is not null
            ? ResolveDynamicValue(
                shape.TransformCenterEntity,
                shape.TransformCenter ?? Vector3.Zero,
                positionResolver)
            : shape.TransformCenter;
        var transformRotationTarget = shape.TransformRotationTargetEntity is not null
            ? ResolveDynamicValue(
                shape.TransformRotationTargetEntity,
                shape.TransformRotationTarget ?? Vector3.Zero,
                positionResolver)
            : shape.TransformRotationTarget;
        var transformRotation = shape.TransformRotationMode switch
        {
            PictoActTransformRotationMode.Default when shape.TransformCenterEntity is not null =>
                headingResolver(shape.TransformCenterEntity) ?? shape.TransformRotation ?? MathF.PI,
            PictoActTransformRotationMode.Default => null,
            PictoActTransformRotationMode.Target when transformRotationTarget.HasValue =>
                DirectionTo(transformCenter ?? Vector3.Zero, transformRotationTarget.Value),
            _ => shape.TransformRotation,
        };
        var sourceAngle = sourceTarget.HasValue
            ? DirectionTo(sourcePosition, sourceTarget.Value)
            : shape.SourceAngle;
        var (position, angle) = ResolveTransform(
            sourcePosition,
            sourceAngle,
            transformCenter,
            transformRotation,
            shape.KeepX,
            shape.KeepY);
        var scale = shape.ScaleExpression is not null
            ? ParseScale(
                shape.ScaleExpression,
                shape.ScaleIsCylindrical,
                DistanceTo(sourcePosition, sourceTarget))
            : new Vector3(shape.PrimaryScale, shape.SecondaryScale, shape.TertiaryScale);
        var polygon = shape.SourcePolygon?
            .Select(point => ResolveTransform(
                point,
                MathF.PI,
                transformCenter,
                transformRotation,
                shape.KeepX,
                shape.KeepY).Position)
            .ToArray();

        return shape with
        {
            SourcePosition = sourcePosition,
            SourceTarget = sourceTarget,
            SourceAngle = sourceAngle,
            TransformCenter = transformCenter,
            TransformRotation = transformRotation,
            TransformRotationTarget = transformRotationTarget,
            Position = position,
            Angle = angle,
            PrimaryScale = scale.X,
            SecondaryScale = scale.Y,
            TertiaryScale = scale.Z,
            Polygon = polygon,
        };
    }

    private static Vector3 ResolveDynamicValue(
        string entityReference,
        Vector3 previousValue,
        Func<string, Vector3?> resolver)
        => resolver(entityReference) ?? previousValue;

    private static bool HasEquivalentRenderState(PictoActShape first, PictoActShape second)
        => first.Position == second.Position &&
           first.PrimaryScale == second.PrimaryScale &&
           first.SecondaryScale == second.SecondaryScale &&
           first.TertiaryScale == second.TertiaryScale &&
           first.Angle == second.Angle &&
           first.Pitch == second.Pitch &&
           first.Yaw == second.Yaw &&
           PolygonEquals(first.Polygon, second.Polygon);

    private static bool PolygonEquals(
        IReadOnlyList<Vector3>? first,
        IReadOnlyList<Vector3>? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        return first is not null && second is not null && first.SequenceEqual(second);
    }

    private Vector3? ResolveEntityPosition(string value)
        => ResolveEntityState(value)?.Position;

    private float? ResolveEntityHeading(string value)
        => ResolveEntityState(value)?.Heading;

    private (Vector3 Position, float Heading)? ResolveEntityState(string value)
    {
        if (objectTable is null)
        {
            return null;
        }

        var candidates = new HashSet<uint>();
        var normalized = value.Trim();
        if (uint.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalId))
        {
            candidates.Add(decimalId);
        }

        var hexadecimal = normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? normalized[2..]
            : normalized;
        if (uint.TryParse(hexadecimal, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexId))
        {
            // Trigger resources use both decimal entity IDs and ACT-style hexadecimal IDs.
            candidates.Add(hexId);
        }

        var gameObject = objectTable
            .FirstOrDefault(candidate => candidates.Contains(candidate.EntityId));
        return gameObject is null
            ? null
            : (gameObject.Position, gameObject.Rotation);
    }

    private static (Vector3 Position, float Angle) ResolveTransform(
        Vector3 sourcePosition,
        float sourceAngle,
        Vector3? transformCenter,
        float? transformRotation,
        bool keepX,
        bool keepY)
    {
        if (transformCenter is null && transformRotation is null && keepX && keepY)
        {
            return (sourcePosition, sourceAngle);
        }

        var center = transformCenter ?? Vector3.Zero;
        var rotation = transformRotation ?? MathF.PI;
        var sourceX = sourcePosition.X * (keepX ? 1 : -1);
        var sourceY = sourcePosition.Z * (keepY ? 1 : -1);
        var sin = MathF.Sin(rotation);
        var cos = MathF.Cos(rotation);
        var transformed = new Vector3(
            -sourceX * cos - sourceY * sin + center.X,
            sourcePosition.Y + center.Y,
            sourceX * sin - sourceY * cos + center.Z);
        var angle = sourceAngle;
        if (!keepX)
        {
            angle *= -1;
        }

        if (!keepY)
        {
            angle = MathF.PI - angle;
        }

        return (transformed, angle + rotation - MathF.PI);
    }

    internal static IReadOnlyList<PictoActOverlayCommand> Parse(
        string payload,
        Func<string, Vector3?>? entityResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (payload.Length > 32_768)
        {
            throw new InvalidDataException("PictoACT payload exceeds 32768 characters.");
        }

        var commands = new List<PictoActOverlayCommand>();
        foreach (var segment in CommandSeparator().Split(payload))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var values = ParseFields(segment);
            var tag = Optional(values, "Tag");
            if (tag?.Length > 128)
            {
                throw new InvalidDataException("PictoACT Tag exceeds 128 characters.");
            }

            var regexText = Optional(values, "Regex");
            if (regexText?.Length > 512)
            {
                throw new InvalidDataException("PictoACT Regex exceeds 512 characters.");
            }

            var action = values.GetValueOrDefault("Action", "Create").Trim();
            var regex = ParseRegex(regexText);
            if (string.Equals(action, "Remove", StringComparison.OrdinalIgnoreCase))
            {
                commands.Add(new PictoActOverlayCommand(
                    tag,
                    regex,
                    PictoActOverlayAction.Remove,
                    null,
                    null)
                {
                    ExecuteAt = ParseExecuteAt(values),
                    RemovalScope = ParseRemovalScope(values),
                });
                continue;
            }

            if (string.Equals(action, "Change", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action, "Modify", StringComparison.OrdinalIgnoreCase))
            {
                commands.Add(new PictoActOverlayCommand(
                    tag,
                    regex,
                    PictoActOverlayAction.Change,
                    null,
                    ParsePatch(values, entityResolver))
                {
                    ExecuteAt = ParseExecuteAt(values),
                });
                continue;
            }

            if (string.Equals(action, "ExaFlare", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action, "地火", StringComparison.OrdinalIgnoreCase))
            {
                commands.AddRange(ParseExaFlare(values, tag, entityResolver));
                continue;
            }

            if (string.Equals(action, "Triangulate", StringComparison.OrdinalIgnoreCase) ||
                action is "△" or "Δ" or "∆")
            {
                commands.Add(ParsePolygon(values, tag, entityResolver));
                continue;
            }

            if (!string.Equals(action, "Create", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"PictoACT action '{action}' is unsupported.");
            }

            commands.Add(ParseCreate(values, tag, entityResolver));
        }

        return commands.Count > 0
            ? commands
            : throw new InvalidDataException("PictoACT payload contains no commands.");
    }

    private static PictoActOverlayCommand ParsePolygon(
        IReadOnlyDictionary<string, string> values,
        string? tag,
        Func<string, Vector3?>? entityResolver)
    {
        var rawPoints = Required(values, "Points")
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (rawPoints.Length is < 3 or > 256)
        {
            throw new InvalidDataException("PictoACT polygon requires 3-256 points.");
        }

        var sourcePoints = rawPoints
            .Select(point => ParseVector3(point, "Points", entityResolver))
            .ToList();
        for (var index = 2; index < sourcePoints.Count - 1; index++)
        {
            if (Vector3.DistanceSquared(sourcePoints[0], sourcePoints[index]) > 0.0001f)
            {
                continue;
            }

            // Some resources append one Delaunay seed after explicitly closing the
            // boundary. The renderer needs only the ordered boundary vertices.
            sourcePoints.RemoveRange(index, sourcePoints.Count - index);
            break;
        }

        if (sourcePoints.Count < 3)
        {
            throw new InvalidDataException("PictoACT polygon boundary has fewer than three points.");
        }

        var transform = ParseTransform(values, entityResolver);
        var transformed = sourcePoints
            .Select(point => ResolveTransform(
                point,
                MathF.PI,
                transform.Center,
                transform.Rotation,
                transform.KeepX ?? true,
                transform.KeepY ?? true).Position)
            .ToArray();
        var colorText = Optional(values, "Color");
        var color = ParseColor(colorText);
        var delay = ParseSingle(values.GetValueOrDefault("Delay", "0"), "Delay");
        if (!float.IsFinite(delay) || delay > 3600)
        {
            throw new InvalidDataException("PictoACT delay is outside 0-3600 seconds.");
        }

        delay = Math.Max(0, delay);
        float? duration = null;
        if (TryGetAny(values, out var durationText, "Time", "t"))
        {
            duration = ParseSingle(durationText, "t");
            if (!float.IsFinite(duration.Value) || duration.Value is < 0 or > 3600)
            {
                throw new InvalidDataException("PictoACT duration is outside 0-3600 seconds.");
            }
        }

        var startsAt = DateTimeOffset.UtcNow.AddSeconds(delay);
        return new PictoActOverlayCommand(
            tag,
            null,
            PictoActOverlayAction.Create,
            new PictoActShape(
                string.Empty,
                PictoActShapeKind.Polygon,
                transformed[0],
                1,
                1,
                0,
                0,
                0,
                0,
                color,
                colorText is not null,
                startsAt,
                // Upstream Triangulate treats an omitted or zero duration as persistent,
                // unlike ordinary Create where an explicit t: 0 removes immediately.
                duration is > 0 ? startsAt.AddSeconds(duration.Value) : DateTimeOffset.MaxValue,
                1,
                sourcePoints[0],
                null,
                0,
                transform.Center,
                transform.Rotation,
                transform.KeepX ?? true,
                transform.KeepY ?? true,
                sourcePoints,
                transformed)
            {
                TransformCenterEntity = transform.CenterEntity,
                TransformRotationMode = transform.RotationMode ==
                                        PictoActTransformRotationMode.Unspecified
                    ? PictoActTransformRotationMode.Default
                    : transform.RotationMode,
                TransformRotationTarget = transform.RotationTarget,
                TransformRotationTargetEntity = transform.RotationTargetEntity,
            },
            null)
        {
            ExecuteAt = startsAt,
        };
    }

    private static PictoActOverlayCommand ParseCreate(
        IReadOnlyDictionary<string, string> values,
        string? tag,
        Func<string, Vector3?>? entityResolver)
    {
        var (vfxPath, kind, fanRadians) = ParseVfx(values);
        var sourcePositionArg = ParseCoordinateArg(
            values.GetValueOrDefault("Pos", "0, 0, 0"),
            "Pos",
            entityResolver);
        var sourceTargetArg = Optional(values, "Target") is { } target
            ? ParseCoordinateArg(target, "Target", entityResolver)
            : null;
        var sourcePosition = sourcePositionArg.Value;
        var sourceTarget = sourceTargetArg?.Value;
        if (sourceTargetArg is not null &&
            (Optional(values, "Angle") is not null || Optional(values, "Angle3D") is not null))
        {
            throw new InvalidDataException(
                "PictoACT Target and Angle/Angle3D cannot both be set.");
        }
        var (scaleExpression, scaleIsCylindrical) = GetScaleExpression(values, required: false);
        var scale = ParseScale(
            scaleExpression ?? "1",
            scaleIsCylindrical,
            DistanceTo(sourcePosition, sourceTarget));
        var colorText = Optional(values, "Color");
        var color = ParseColor(colorText);
        float? duration = null;
        if (TryGetAny(values, out var durationText, "Time", "t"))
        {
            duration = ParseSingle(durationText, "t");
            if (!float.IsFinite(duration.Value) || duration.Value is < 0 or > 3600)
            {
                throw new InvalidDataException("PictoACT duration is outside 0-3600 seconds.");
            }
        }

        var delay = ParseSingle(values.GetValueOrDefault("Delay", "0"), "Delay");
        if (!float.IsFinite(delay) || delay > 3600)
        {
            throw new InvalidDataException("PictoACT delay is outside 0-3600 seconds.");
        }

        delay = Math.Max(0, delay);
        var sourceAngles = ParseAngles(values, sourceTargetArg is not null
            ? DirectionTo(sourcePosition, sourceTargetArg.Value)
            : MathF.PI);
        var transform = ParseTransform(values, entityResolver);
        var transformRotationMode = transform.RotationMode ==
                                    PictoActTransformRotationMode.Unspecified
            ? PictoActTransformRotationMode.Default
            : transform.RotationMode;
        if (transformRotationMode == PictoActTransformRotationMode.Target &&
            transform.Center is null)
        {
            throw new InvalidDataException(
                "PictoACT θ as a coordinate or entity requires O/Center.");
        }

        var (position, transformedAngle) = ResolveTransform(
            sourcePosition,
            sourceAngles.X,
            transform.Center,
            transform.Rotation,
            transform.KeepX ?? true,
            transform.KeepY ?? true);
        var startsAt = DateTimeOffset.UtcNow.AddSeconds(delay);
        return new PictoActOverlayCommand(
            tag,
            null,
            PictoActOverlayAction.Create,
            new PictoActShape(
                vfxPath,
                kind,
                position,
                scale.X,
                scale.Y,
                transformedAngle,
                sourceAngles.Y,
                sourceAngles.Z,
                fanRadians,
                color,
                colorText is not null,
                startsAt,
                duration.HasValue ? startsAt.AddSeconds(duration.Value) : DateTimeOffset.MaxValue,
                scale.Z,
                sourcePosition,
                sourceTarget,
                sourceAngles.X,
                transform.Center,
                transform.Rotation,
                transform.KeepX ?? true,
                transform.KeepY ?? true,
                null,
                null)
            {
                SourcePositionEntity = sourcePositionArg.EntityReference,
                SourceTargetEntity = sourceTargetArg?.EntityReference,
                TransformCenterEntity = transform.CenterEntity,
                TransformRotationMode = transformRotationMode,
                TransformRotationTarget = transform.RotationTarget,
                TransformRotationTargetEntity = transform.RotationTargetEntity,
                ScaleExpression = scaleExpression ?? "1",
                ScaleIsCylindrical = scaleIsCylindrical,
            },
            null)
        {
            ExecuteAt = startsAt,
        };
    }

    private static IReadOnlyList<PictoActOverlayCommand> ParseExaFlare(
        IReadOnlyDictionary<string, string> values,
        string? tag,
        Func<string, Vector3?>? entityResolver)
    {
        var countValue = ParseSingle(
            TryGetAny(values, out var rawCount, "n", "count")
                ? rawCount
                : throw new InvalidDataException("PictoACT ExaFlare requires 'n'."),
            "n");
        var count = checked((int)countValue);
        if (countValue != count || count is <= 0 or > 128)
        {
            throw new InvalidDataException("PictoACT ExaFlare n must be an integer from 1-128.");
        }

        var deltaTime = ParseSingle(Required(values, "dt"), "dt");
        var initialIndex = Optional(values, "n0") is { } rawInitialIndex
            ? ParseSingle(rawInitialIndex, "n0")
            : 0;
        var initialDelay = TryGetAny(values, out var rawInitialDelay, "Delay0")
            ? ParseSingle(rawInitialDelay, "Delay0")
            : 0;
        var outerDelay = Optional(values, "Delay") is { } rawOuterDelay
            ? ParseSingle(rawOuterDelay, "Delay")
            : 0;
        var basePosition = Optional(values, "Pos") is { } rawPosition
            ? ParseVector3(rawPosition, "Pos", entityResolver)
            : Vector3.Zero;
        var deltaPosition = TryGetAny(values, out var rawDeltaPosition, "dPos")
            ? ParseVector3(rawDeltaPosition, "dPos", entityResolver)
            : Vector3.Zero;
        var deltaRotation = TryGetAny(values, out var rawDeltaRotation, "dθ", "dTheta")
            ? ParseSingle(rawDeltaRotation, "dTheta")
            : (float?)null;
        var baseRotation = TryGetAny(values, out var rawBaseRotation, "θ", "Theta")
            ? ParseSingle(rawBaseRotation, "Theta")
            : -MathF.PI;
        var duration = ParseSingle(
            TryGetAny(values, out var rawDuration, "Time", "t")
                ? rawDuration
                : throw new InvalidDataException("PictoACT ExaFlare requires 't'."),
            "t");

        var commands = new List<PictoActOverlayCommand>(count);
        for (var step = 0; step < count; step++)
        {
            var index = initialIndex + step;
            var stepDelay = outerDelay + initialDelay + step * deltaTime;
            var stepDuration = duration;
            if (stepDelay < 0)
            {
                stepDuration += stepDelay;
                stepDelay = 0;
            }

            if (stepDuration < 0)
            {
                continue;
            }

            var expanded = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
            {
                ["Action"] = "Create",
                ["Delay"] = stepDelay.ToString("R", CultureInfo.InvariantCulture),
                ["t"] = stepDuration.ToString("R", CultureInfo.InvariantCulture),
                ["Pos"] = FormatVector3(basePosition + deltaPosition * index),
            };
            if (deltaRotation.HasValue)
            {
                expanded["θ"] = (baseRotation + deltaRotation.Value * index)
                    .ToString("R", CultureInfo.InvariantCulture);
            }

            commands.Add(ParseCreate(expanded, tag, entityResolver));
        }

        return commands;
    }

    private static Regex? ParseRegex(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : new Regex(
                value,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100));

    private static DateTimeOffset ParseExecuteAt(
        IReadOnlyDictionary<string, string> values)
    {
        var delay = ParseSingle(values.GetValueOrDefault("Delay", "0"), "Delay");
        if (!float.IsFinite(delay) || delay > 3600)
        {
            throw new InvalidDataException("PictoACT delay is outside 0-3600 seconds.");
        }

        return DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, delay));
    }

    private static PictoActRemovalScope ParseRemovalScope(
        IReadOnlyDictionary<string, string> values)
        => values.GetValueOrDefault("Type", "All").Trim().ToLowerInvariant() switch
        {
            "" or "all" => PictoActRemovalScope.All,
            "static" or "staticvfx" => PictoActRemovalScope.Static,
            "actor" or "actorvfx" => PictoActRemovalScope.Actor,
            var value => throw new InvalidDataException(
                $"PictoACT Remove VFX type '{value}' is invalid."),
        };

    private static PictoActShapePatch ParsePatch(
        IReadOnlyDictionary<string, string> values,
        Func<string, Vector3?>? entityResolver)
    {
        var transform = ParseTransform(values, entityResolver);
        var (scaleExpression, scaleIsCylindrical) = GetScaleExpression(values, required: false);
        var angles = Optional(values, "Angle3D") is { } angle3D
            ? ParseAngle3D(angle3D)
            : (Vector3?)null;
        var targetSpecified = values.ContainsKey("Target");
        var targetArg = Optional(values, "Target") is { } targetText &&
                        !string.Equals(targetText, "clear", StringComparison.OrdinalIgnoreCase)
            ? ParseCoordinateArg(targetText, "Target", entityResolver)
            : null;
        if (targetSpecified && angles.HasValue)
        {
            throw new InvalidDataException(
                "PictoACT Target and Angle/Angle3D cannot both be set.");
        }

        var positionArg = Optional(values, "Pos") is { } position
            ? ParseCoordinateArg(position, "Pos", entityResolver)
            : null;
        return new PictoActShapePatch(
            positionArg?.Value,
            targetArg?.Value,
            targetSpecified,
            scaleExpression,
            scaleIsCylindrical,
            angles?.X ?? (Optional(values, "Angle") is { } angle
                ? ParseSingle(angle, "Angle")
                : null),
            angles?.Y,
            angles?.Z,
            Optional(values, "Color") is { } color
                ? ParseColor(color)
                : null,
            transform.Center,
            transform.CenterSpecified,
            transform.Rotation,
            transform.RotationSpecified,
            transform.RotationTarget,
            transform.KeepX,
            transform.KeepY)
        {
            PositionEntity = positionArg?.EntityReference,
            TargetEntity = targetArg?.EntityReference,
            TransformCenterEntity = transform.CenterEntity,
            TransformRotationMode = transform.RotationMode,
            TransformRotationTargetEntity = transform.RotationTargetEntity,
        };
    }

    private static PictoActTransform ParseTransform(
        IReadOnlyDictionary<string, string> values,
        Func<string, Vector3?>? entityResolver)
    {
        var centerSpecified = TryGetAny(values, out var centerText, "O", "Center");
        var rotationSpecified = TryGetAny(values, out var rotationText, "θ", "Theta");
        var centerArg = centerSpecified &&
                        !string.Equals(centerText, "clear", StringComparison.OrdinalIgnoreCase)
            ? ParseCoordinateArg(centerText, "Center", entityResolver)
            : null;
        var center = centerArg?.Value;
        var directionFields = values
            .Select(pair => (Pair: pair, Match: DirectionField().Match(pair.Key)))
            .Where(candidate => candidate.Match.Success)
            .ToArray();
        if (directionFields.Length > 1)
        {
            throw new InvalidDataException("PictoACT accepts only one Dir field per command.");
        }

        var directionField = directionFields.FirstOrDefault();
        if (rotationSpecified && directionFields.Length == 1)
        {
            throw new InvalidDataException("PictoACT θ and Dir fields cannot both be set.");
        }

        float? rotation = null;
        Vector3? rotationTarget = null;
        string? rotationTargetEntity = null;
        var rotationMode = PictoActTransformRotationMode.Unspecified;
        if (rotationSpecified &&
            !string.Equals(rotationText.Trim(), "clear", StringComparison.OrdinalIgnoreCase))
        {
            (rotation, rotationTarget, rotationTargetEntity, rotationMode) = ParseRotation(
                rotationText,
                center ?? Vector3.Zero,
                entityResolver);
        }
        else if (rotationSpecified)
        {
            rotationMode = PictoActTransformRotationMode.Default;
        }
        else if (directionFields.Length == 1)
        {
            var divisions = int.Parse(
                directionField.Match.Groups["divisions"].Value,
                CultureInfo.InvariantCulture);
            if (divisions is 0 or > 360)
            {
                throw new InvalidDataException("PictoACT Dir divisions must be from 1-360.");
            }

            var directionIndex = ParseSingle(
                directionField.Pair.Value,
                directionField.Pair.Key);
            if (directionField.Match.Groups["negative"].Success)
            {
                // PictoACT's DirN form addresses the half-step between two normal
                // direction indices; it is not a negative division count.
                directionIndex += 0.5f;
            }

            directionIndex = ((directionIndex % divisions) + divisions) % divisions;
            rotation = directionIndex / divisions * MathF.Tau - MathF.PI;
            rotationSpecified = true;
            rotationMode = PictoActTransformRotationMode.Fixed;
        }

        return new PictoActTransform(
            center,
            centerSpecified,
            rotation,
            rotationSpecified,
            rotationTarget,
            TryGetAny(values, out var keepX, "+X", "KeepX")
                ? ParseBoolean(keepX, "KeepX")
                : null,
            TryGetAny(values, out var keepY, "+Y", "KeepY")
                ? ParseBoolean(keepY, "KeepY")
                : null)
        {
            CenterEntity = centerArg?.EntityReference,
            RotationMode = rotationMode,
            RotationTargetEntity = rotationTargetEntity,
        };
    }

    private static (
        float Rotation,
        Vector3? Target,
        string? TargetEntity,
        PictoActTransformRotationMode Mode) ParseRotation(
        string value,
        Vector3 center,
        Func<string, Vector3?>? entityResolver)
    {
        var normalized = value.Trim();
        if (TryParseEntityReference(normalized, out var entityReference))
        {
            // Legacy PictoACT overloads θ with an entity ID to mean "face this entity".
            // Resolve it before numeric parsing so all-digit ACT IDs remain compatible too.
            var target = entityResolver?.Invoke(entityReference) ?? Vector3.Zero;
            return (
                DirectionTo(center, target),
                target,
                entityReference,
                PictoActTransformRotationMode.Target);
        }

        try
        {
            return (
                ParseSingle(value, "Theta"),
                null,
                null,
                PictoActTransformRotationMode.Fixed);
        }
        catch (InvalidDataException)
        {
            // Current PictoACT accepts a world coordinate here in addition to an angle
            // expression. Angle parsing runs first because expressions can contain commas.
            var target = ParseVector3(value, "Theta", entityResolver);
            return (
                DirectionTo(center, target),
                target,
                null,
                PictoActTransformRotationMode.Target);
        }
    }

    private static bool TryGetAny(
        IReadOnlyDictionary<string, string> values,
        out string value,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out value!) && !string.IsNullOrWhiteSpace(value))
            {
                value = value.Trim();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool ParseBoolean(string value, string name)
        => value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => throw new InvalidDataException($"PictoACT {name} is not a boolean."),
        };

    private static (string VfxPath, PictoActShapeKind Kind, float FanRadians) ParseVfx(
        IReadOnlyDictionary<string, string> values)
    {
        var omen = Optional(values, "Omen");
        var isOmen = omen is not null;
        var rawPath = omen ?? Required(values, "StaticVfx");
        var normalizedName = isOmen && OmenAbbreviations.TryGetValue(rawPath, out var expanded)
            ? expanded
            : rawPath;
        var vfxPath = normalizedName.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)
            ? normalizedName
            : isOmen
                ? $"vfx/omen/eff/{normalizedName}.avfx"
                : throw new InvalidDataException("PictoACT StaticVfx path must end with .avfx.");
        var (kind, fanRadians) = ParseFallbackShape(rawPath);
        return (vfxPath, kind, fanRadians);
    }

    private static (PictoActShapeKind Kind, float FanRadians) ParseFallbackShape(string omen)
    {
        if (string.Equals(omen, "Circle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(omen, "general_1bf", StringComparison.OrdinalIgnoreCase))
        {
            return (PictoActShapeKind.Circle, 0);
        }

        if (string.Equals(omen, "Rect", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(omen, "general02f", StringComparison.OrdinalIgnoreCase))
        {
            return (PictoActShapeKind.Rectangle, 0);
        }

        if (string.Equals(omen, "Rect2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(omen, "general_x02f", StringComparison.OrdinalIgnoreCase))
        {
            return (PictoActShapeKind.BidirectionalRectangle, 0);
        }

        var fanMatch = FanOmen().Match(omen);
        if (fanMatch.Success &&
            int.TryParse(
                fanMatch.Groups["degrees"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var fanDegrees) &&
            fanDegrees is > 0 and <= 360)
        {
            return (PictoActShapeKind.Fan, fanDegrees * MathF.PI / 180);
        }

        return (PictoActShapeKind.NativeOnly, 0);
    }

    private static readonly IReadOnlyDictionary<string, string> OmenAbbreviations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Rect"] = "general02f",
            ["Rect2"] = "general_x02f",
            ["Circle"] = "general_1bf",
            ["Cross"] = "n4fg_betaest_o0p",
            ["Fan15"] = "gl_fan015_0x",
            ["Fan20"] = "gl_fan020_0f",
            ["Fan30"] = "gl_fan030_1bf",
            ["Fan40"] = "z5fc_fan40_o0g",
            ["Fan45"] = "gl_fan045_1bf",
            ["Fan60"] = "gl_fan060_1bf",
            ["Fan80"] = "gl_fan80_o0g",
            ["Fan90"] = "gl_fan090_1bf",
            ["Fan100"] = "er_gl_fan100_o0v",
            ["Fan120"] = "gl_fan120_1bf",
            ["Fan130"] = "gl_fan130_0x",
            ["Fan135"] = "gl_fan135_c0g",
            ["Fan145"] = "m0501_fan145_d1",
            ["Fan150"] = "gl_fan150_1bf",
            ["Fan180"] = "gl_fan180_1bf",
            ["Fan210"] = "gl_fan210_1bf",
            ["Fan225"] = "gl_fan225_c0k1",
            ["Fan240"] = "x6d3_b1_fan240_p1",
            ["Fan270"] = "gl_fan270_0100af",
        };

    private static Dictionary<string, string> ParseFields(string segment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in segment.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                // Upstream PictoACT treats free-form lines as annotations, so old trigger
                // packs can keep their inline descriptions without invalidating a command.
                continue;
            }

            var key = line[..separator].Trim();
            if (!result.TryAdd(key, line[(separator + 1)..].Trim()))
            {
                throw new InvalidDataException($"PictoACT key '{key}' is duplicated.");
            }
        }

        return result;
    }

    private static string? Optional(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static string Required(IReadOnlyDictionary<string, string> values, string name)
        => Optional(values, name)
           ?? throw new InvalidDataException($"PictoACT requires '{name}'.");

    private static PictoActCoordinateArg ParseCoordinateArg(
        string value,
        string name,
        Func<string, Vector3?>? entityResolver)
    {
        var normalized = value.Trim();
        if (TryParseEntityReference(normalized, out var entityReference))
        {
            // Keep the ID as source state even when the actor is temporarily absent. The
            // framework refresh can then acquire it later, matching upstream PictoACT.
            return new PictoActCoordinateArg(
                entityResolver?.Invoke(entityReference) ?? Vector3.Zero,
                entityReference);
        }

        return new PictoActCoordinateArg(
            ParseVector3(value, name, entityResolver),
            null);
    }

    private static bool TryParseEntityReference(string value, out string entityReference)
    {
        entityReference = value.Trim();
        var raw = entityReference.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? entityReference[2..]
            : entityReference;
        if (uint.TryParse(
                raw,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var hexadecimalId) &&
            IsPictoActEntityId(hexadecimalId))
        {
            return true;
        }

        // Keep accepting decimal IDs used by older local resources even though current
        // upstream exports normally format these actor IDs as hexadecimal text.
        return uint.TryParse(
                   raw,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out var decimalId) &&
               IsPictoActEntityId(decimalId);
    }

    private static bool IsPictoActEntityId(uint entityId)
        => entityId is >= 0x10000000 and <= 0x10FFFFFF or
           >= 0x40000000 and <= 0x40FFFFFF;

    private static Vector3 ParseVector3(
        string value,
        string name,
        Func<string, Vector3?>? entityResolver = null)
    {
        var normalized = value.Trim().Trim('<', '>', '(', ')', '[', ']');
        if (!normalized.Contains(',') && entityResolver?.Invoke(normalized) is { } entityPosition)
        {
            return entityPosition;
        }

        var polarIndex = normalized.IndexOf("polar", StringComparison.OrdinalIgnoreCase);
        if (polarIndex >= 0)
        {
            var baseText = normalized[..polarIndex].Trim().TrimEnd(',');
            var basePosition = string.IsNullOrWhiteSpace(baseText)
                ? Vector3.Zero
                : ParseVector3(baseText, name, entityResolver);
            var polar = ParseNumbers(normalized[(polarIndex + "polar".Length)..], 2, 3, name);
            var radius = polar[0];
            var angle = polar[1];
            var height = polar.Length > 2 ? polar[2] : 0;
            return ValidateCoordinate(
                basePosition + new Vector3(
                    MathF.Sin(angle) * radius,
                    height,
                    MathF.Cos(angle) * radius),
                name);
        }

        var numbers = ParseNumbers(normalized, 2, 3, name);
        // PictoACT follows ACT/FFXIV log coordinates: X/Y are the ground plane and Z is height.
        // Dalamud's world vector follows the client layout: X/Z are the ground plane and Y is height.
        var result = new Vector3(numbers[0], numbers.Length > 2 ? numbers[2] : 0, numbers[1]);
        return ValidateCoordinate(result, name);
    }

    private static Vector3 ValidateCoordinate(Vector3 result, string name)
    {
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) ||
            !float.IsFinite(result.Z) || result.LengthSquared() > 30_000_000_000f)
        {
            throw new InvalidDataException($"PictoACT {name} is outside the safe coordinate range.");
        }

        return result;
    }

    private static string FormatVector3(Vector3 value)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{value.X:R}, {value.Z:R}, {value.Y:R}");

    private static (string? Expression, bool Cylindrical) GetScaleExpression(
        IReadOnlyDictionary<string, string> values,
        bool required)
    {
        var scale = Optional(values, "Scale");
        var cylindrical = Optional(values, "ScaleCyl");
        if (scale is not null && cylindrical is not null)
        {
            throw new InvalidDataException("PictoACT Scale and ScaleCyl cannot both be set.");
        }

        var expression = scale ?? cylindrical;
        if (required && expression is null)
        {
            throw new InvalidDataException("PictoACT requires 'Scale' or 'ScaleCyl'.");
        }

        return (expression, cylindrical is not null);
    }

    private static Vector3 ParseScale(
        string value,
        bool cylindrical = false,
        float? targetDistance = null)
    {
        if (value.Contains("_d", StringComparison.OrdinalIgnoreCase))
        {
            if (!targetDistance.HasValue)
            {
                throw new InvalidDataException("PictoACT Scale uses _d without a Target.");
            }

            value = DistanceToken().Replace(
                value,
                targetDistance.Value.ToString("R", CultureInfo.InvariantCulture));
        }

        var components = ParseNumbers(value, 1, cylindrical ? 2 : 3, "Scale");
        var scale = cylindrical
            ? new Vector3(
                MathF.Abs(components[0]),
                MathF.Abs(components[0]),
                MathF.Abs(components.Length > 1 ? components[1] : components[0]))
            : new Vector3(
                MathF.Abs(components[0]),
                MathF.Abs(components.Length > 1 ? components[1] : components[0]),
                MathF.Abs(components.Length > 2 ? components[2] : 1));
        if (!float.IsFinite(scale.X) || scale.X is < 0 or > 1000 ||
            !float.IsFinite(scale.Y) || scale.Y is < 0 or > 1000 ||
            !float.IsFinite(scale.Z) || scale.Z is < 0 or > 1000 ||
            scale == Vector3.Zero)
        {
            throw new InvalidDataException("PictoACT Scale is outside 0-1000.");
        }

        return scale;
    }

    private static float? DistanceTo(Vector3 source, Vector3? target)
        => target.HasValue
            ? Vector2.Distance(
                new Vector2(source.X, source.Z),
                new Vector2(target.Value.X, target.Value.Z))
            : null;

    private static float DirectionTo(Vector3 source, Vector3 target)
        => MathF.Atan2(target.X - source.X, target.Z - source.Z);

    private static Vector3 ParseAngles(
        IReadOnlyDictionary<string, string> values,
        float defaultAngle)
    {
        if (Optional(values, "Angle3D") is { } angle3D)
        {
            if (Optional(values, "Angle") is not null)
            {
                throw new InvalidDataException("PictoACT Angle and Angle3D cannot both be set.");
            }

            return ParseAngle3D(angle3D);
        }

        var angle = Optional(values, "Angle") is { } angleText
            ? ParseSingle(angleText, "Angle")
            : defaultAngle;
        if (!float.IsFinite(angle))
        {
            throw new InvalidDataException("PictoACT Angle is not finite.");
        }

        return new Vector3(angle, 0, 0);
    }

    private static Vector3 ParseAngle3D(string value)
    {
        var components = ParseNumbers(value, 1, 3, "Angle3D");
        var result = new Vector3(
            components[0],
            components.Length > 1 ? components[1] : 0,
            components.Length > 2 ? components[2] : 0);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z))
        {
            throw new InvalidDataException("PictoACT Angle3D is not finite.");
        }

        return result;
    }

    private static Vector4 ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultColor;
        }

        var colorValues = ParseNumbers(value, 3, 4, "Color");
        var color = new Vector4(
            colorValues[0],
            colorValues[1],
            colorValues[2],
            colorValues.Length > 3 ? colorValues[3] : 1f);
        if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) ||
            !float.IsFinite(color.Z) || !float.IsFinite(color.W) ||
            color.X is < 0 or > 32 || color.Y is < 0 or > 32 ||
            color.Z is < 0 or > 32 || color.W is < 0 or > 32)
        {
            throw new InvalidDataException("PictoACT Color is outside 0-32.");
        }

        // Values above one encode relative VFX intensity. Preserve their hue here and
        // normalize only while drawing the SDR ImGui fallback.
        return color;
    }

    private static float[] ParseNumbers(
        string value,
        int minimum,
        int maximum,
        string name)
    {
        var parts = SplitTopLevel(value);
        if (parts.Length < minimum || parts.Length > maximum)
        {
            throw new InvalidDataException(
                $"PictoACT {name} requires {minimum}-{maximum} comma-separated numbers.");
        }

        return parts.Select(part => ParseSingle(part, name)).ToArray();
    }

    private static string[] SplitTopLevel(string value)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            depth += value[index] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };
            if (depth < 0)
            {
                throw new InvalidDataException("PictoACT number expression has unmatched parentheses.");
            }

            if (value[index] != ',' || depth != 0)
            {
                continue;
            }

            parts.Add(value[start..index].Trim());
            start = index + 1;
        }

        if (depth != 0)
        {
            throw new InvalidDataException("PictoACT number expression has unmatched parentheses.");
        }

        parts.Add(value[start..].Trim());
        return parts.Where(part => part.Length > 0).ToArray();
    }

    private static float ParseSingle(string value, string name)
    {
        try
        {
            return checked((float)new NumericExpressionParser(value).Parse());
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new InvalidDataException(
                $"PictoACT {name} contains an invalid number expression.",
                ex);
        }
    }

    private sealed class NumericExpressionParser(string text)
    {
        private int position;

        internal double Parse()
        {
            var result = ParseConditional();
            SkipWhiteSpace();
            if (position != text.Length)
            {
                throw new FormatException($"Unexpected token at position {position}.");
            }

            return result;
        }

        private double ParseConditional()
        {
            var condition = ParseComparison();
            SkipWhiteSpace();
            if (!Take('?'))
            {
                return condition;
            }

            var whenTrue = ParseConditional();
            SkipWhiteSpace();
            if (!Take(':'))
            {
                throw new FormatException("Conditional expression is missing ':'.");
            }

            var whenFalse = ParseConditional();
            return condition != 0 ? whenTrue : whenFalse;
        }

        private double ParseComparison()
        {
            var result = ParseExpression();
            while (true)
            {
                SkipWhiteSpace();
                if (TakeString("<="))
                {
                    result = result <= ParseExpression() ? 1 : 0;
                }
                else if (TakeString(">="))
                {
                    result = result >= ParseExpression() ? 1 : 0;
                }
                else if (TakeString("==") || Take('='))
                {
                    result = Math.Abs(result - ParseExpression()) < 1e-9 ? 1 : 0;
                }
                else if (TakeString("!="))
                {
                    result = Math.Abs(result - ParseExpression()) >= 1e-9 ? 1 : 0;
                }
                else if (Take('<'))
                {
                    result = result < ParseExpression() ? 1 : 0;
                }
                else if (Take('>'))
                {
                    result = result > ParseExpression() ? 1 : 0;
                }
                else
                {
                    return result;
                }
            }
        }

        private double ParseExpression()
        {
            var result = ParseTerm();
            while (true)
            {
                SkipWhiteSpace();
                if (Take('+'))
                {
                    result += ParseTerm();
                }
                else if (Take('-'))
                {
                    result -= ParseTerm();
                }
                else
                {
                    return result;
                }
            }
        }

        private double ParseTerm()
        {
            var result = ParseUnary();
            while (true)
            {
                SkipWhiteSpace();
                if (Take('*'))
                {
                    result *= ParseUnary();
                }
                else if (Take('/'))
                {
                    result /= ParseUnary();
                }
                else if (TakeString("%%"))
                {
                    var divisor = ParseUnary();
                    result = ((result % divisor) + divisor) % divisor;
                }
                else if (Take('%'))
                {
                    result %= ParseUnary();
                }
                else if (CanStartPrimary())
                {
                    // PictoACT resources commonly write 5√2 and 2π without '*'.
                    result *= ParseUnary();
                }
                else
                {
                    return result;
                }
            }
        }

        private double ParseUnary()
        {
            SkipWhiteSpace();
            if (Take('+'))
            {
                return ParseUnary();
            }

            if (Take('-'))
            {
                return -ParseUnary();
            }

            if (Take('!'))
            {
                return ParseUnary() == 0 ? 1 : 0;
            }

            if (Take('√'))
            {
                return Math.Sqrt(ParseUnary());
            }

            return ParsePower();
        }

        private double ParsePower()
        {
            var result = ParsePrimary();
            SkipWhiteSpace();
            return Take('^')
                ? Math.Pow(result, ParseUnary())
                : result;
        }

        private double ParsePrimary()
        {
            SkipWhiteSpace();
            double result;
            if (Take('('))
            {
                result = ParseExpression();
                SkipWhiteSpace();
                if (!Take(')'))
                {
                    throw new FormatException("Missing closing parenthesis.");
                }
            }
            else if (char.IsLetter(Current))
            {
                var identifier = ParseIdentifier();
                if (string.Equals(identifier, "pi", StringComparison.OrdinalIgnoreCase) ||
                    identifier == "π")
                {
                    result = Math.PI;
                }
                else if (string.Equals(identifier, "true", StringComparison.OrdinalIgnoreCase))
                {
                    result = 1;
                }
                else if (string.Equals(identifier, "false", StringComparison.OrdinalIgnoreCase))
                {
                    result = 0;
                }
                else
                {
                    result = ParseFunction(identifier);
                }
            }
            else
            {
                result = ParseNumber();
            }

            SkipWhiteSpace();
            if (Take('°'))
            {
                result *= Math.PI / 180;
            }

            return result;
        }

        private double ParseFunction(string name)
        {
            SkipWhiteSpace();
            if (!Take('('))
            {
                throw new FormatException($"Unknown number name '{name}'.");
            }

            var arguments = new List<double>();
            SkipWhiteSpace();
            if (!Take(')'))
            {
                while (true)
                {
                    arguments.Add(ParseConditional());
                    SkipWhiteSpace();
                    if (Take(')'))
                    {
                        break;
                    }

                    if (!Take(','))
                    {
                        throw new FormatException($"Function '{name}' has an invalid argument list.");
                    }
                }
            }

            var normalized = name.ToLowerInvariant();
            return (normalized, arguments.Count) switch
            {
                ("sqrt", 1) => Math.Sqrt(arguments[0]),
                ("abs", 1) => Math.Abs(arguments[0]),
                ("sin", 1) => Math.Sin(arguments[0]),
                ("cos", 1) => Math.Cos(arguments[0]),
                ("tan", 1) => Math.Tan(arguments[0]),
                ("atan", 1) or ("arctan", 1) => Math.Atan(arguments[0]),
                ("atan2", 2) or ("arctan2", 2) => Math.Atan2(arguments[0], arguments[1]),
                ("min", 2) => Math.Min(arguments[0], arguments[1]),
                ("max", 2) => Math.Max(arguments[0], arguments[1]),
                ("floor", 1) => Math.Floor(arguments[0]),
                ("ceil", 1) or ("ceiling", 1) => Math.Ceiling(arguments[0]),
                ("round", 1) => Math.Round(arguments[0]),
                ("round", 2) => Math.Round(arguments[0], checked((int)arguments[1])),
                ("dir2rad", 2) or ("dirtorad", 2) => DirectionToRadians(
                    arguments[0],
                    arguments[1]),
                ("deg2rad", 1) or ("degtorad", 1) => arguments[0] * Math.PI / 180,
                ("rad2deg", 1) or ("radtodeg", 1) => arguments[0] * 180 / Math.PI,
                ("d", 4) => Math.Sqrt(
                    Math.Pow(arguments[2] - arguments[0], 2) +
                    Math.Pow(arguments[3] - arguments[1], 2)),
                ("θ", 4) => Math.Atan2(
                    arguments[2] - arguments[0],
                    arguments[3] - arguments[1]),
                ("roundir", 2) => PositiveModulo(
                    Math.Round(arguments[0] / Math.Tau * arguments[1]),
                    arguments[1]),
                _ => throw new FormatException(
                    $"Unknown function '{name}' with {arguments.Count} arguments."),
            };
        }

        private static double PositiveModulo(double value, double divisor)
            => ((value % divisor) + divisor) % divisor;

        private static double DirectionToRadians(double direction, double divisions)
        {
            if (divisions < 0)
            {
                // Triggernometry uses a negative division count for the half-step
                // between normal directions, so the sign cannot be treated as arithmetic only.
                divisions = -divisions;
                direction += 0.5;
            }

            return -Math.PI + Math.Tau * PositiveModulo(direction / divisions, 1);
        }

        private double ParseNumber()
        {
            SkipWhiteSpace();
            var start = position;
            var hasExponent = false;
            while (position < text.Length)
            {
                var character = text[position];
                if (char.IsDigit(character) || character == '.')
                {
                    position++;
                    continue;
                }

                if (!hasExponent && (character == 'e' || character == 'E'))
                {
                    hasExponent = true;
                    position++;
                    if (position < text.Length && text[position] is '+' or '-')
                    {
                        position++;
                    }
                    continue;
                }

                break;
            }

            if (start == position ||
                !double.TryParse(
                    text.AsSpan(start, position - start),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var result))
            {
                throw new FormatException($"Expected number at position {start}.");
            }

            return result;
        }

        private string ParseIdentifier()
        {
            var start = position;
            while (position < text.Length &&
                   (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
            {
                position++;
            }

            return text[start..position];
        }

        private bool CanStartPrimary()
        {
            SkipWhiteSpace();
            return char.IsDigit(Current) ||
                   Current is '.' or '(' or '√' or 'π' ||
                   char.IsLetter(Current);
        }

        private bool TakeString(string expected)
        {
            if (position + expected.Length > text.Length ||
                !text.AsSpan(position, expected.Length).SequenceEqual(expected))
            {
                return false;
            }

            position += expected.Length;
            return true;
        }

        private bool Take(char expected)
        {
            if (position >= text.Length || text[position] != expected)
            {
                return false;
            }

            position++;
            return true;
        }

        private void SkipWhiteSpace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
        }

        private char Current => position < text.Length ? text[position] : '\0';
    }

    [GeneratedRegex(@"(?:\r\n|\n|\r)\s*---\s*(?:\r\n|\n|\r)", RegexOptions.CultureInvariant)]
    private static partial Regex CommandSeparator();

    [GeneratedRegex(@"^Fan(?<degrees>\d{1,3})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FanOmen();

    [GeneratedRegex(@"^Dir(?<negative>N)?(?<divisions>\d{1,3})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DirectionField();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])_d(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DistanceToken();
}

internal sealed record PictoActOverlayCommand(
    string? Tag,
    Regex? Regex,
    PictoActOverlayAction Action,
    PictoActShape? Shape,
    PictoActShapePatch? Patch)
{
    internal DateTimeOffset ExecuteAt { get; init; } = DateTimeOffset.UtcNow;

    internal PictoActRemovalScope RemovalScope { get; init; } = PictoActRemovalScope.All;

    internal bool Remove => Action == PictoActOverlayAction.Remove;

    internal bool Change => Action == PictoActOverlayAction.Change;
}

internal enum PictoActRemovalScope
{
    Static,
    Actor,
    All,
}

internal enum PictoActOverlayAction
{
    Create,
    Change,
    Remove,
}

internal sealed record PictoActShapePatch(
    Vector3? Position,
    Vector3? Target,
    bool TargetSpecified,
    string? ScaleExpression,
    bool ScaleIsCylindrical,
    float? Angle,
    float? Pitch,
    float? Yaw,
    Vector4? Color,
    Vector3? TransformCenter,
    bool TransformCenterSpecified,
    float? TransformRotation,
    bool TransformRotationSpecified,
    Vector3? TransformRotationTarget,
    bool? KeepX,
    bool? KeepY)
{
    internal string? PositionEntity { get; init; }

    internal string? TargetEntity { get; init; }

    internal string? TransformCenterEntity { get; init; }

    internal PictoActTransformRotationMode TransformRotationMode { get; init; }

    internal string? TransformRotationTargetEntity { get; init; }
}

internal sealed record PictoActTransform(
    Vector3? Center,
    bool CenterSpecified,
    float? Rotation,
    bool RotationSpecified,
    Vector3? RotationTarget,
    bool? KeepX,
    bool? KeepY)
{
    internal string? CenterEntity { get; init; }

    internal PictoActTransformRotationMode RotationMode { get; init; }

    internal string? RotationTargetEntity { get; init; }
}

internal sealed record PictoActShape(
    string VfxPath,
    PictoActShapeKind Kind,
    Vector3 Position,
    float PrimaryScale,
    float SecondaryScale,
    float Angle,
    float Pitch,
    float Yaw,
    float FanRadians,
    Vector4 Color,
    bool HasExplicitColor,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    float TertiaryScale,
    Vector3 SourcePosition,
    Vector3? SourceTarget,
    float SourceAngle,
    Vector3? TransformCenter,
    float? TransformRotation,
    bool KeepX,
    bool KeepY,
    IReadOnlyList<Vector3>? SourcePolygon,
    IReadOnlyList<Vector3>? Polygon)
{
    internal string? SourcePositionEntity { get; init; }

    internal string? SourceTargetEntity { get; init; }

    internal string? TransformCenterEntity { get; init; }

    internal PictoActTransformRotationMode TransformRotationMode { get; init; }

    internal Vector3? TransformRotationTarget { get; init; }

    internal string? TransformRotationTargetEntity { get; init; }

    internal string? ScaleExpression { get; init; }

    internal bool ScaleIsCylindrical { get; init; }

    internal bool RequiresDynamicRefresh =>
        SourcePositionEntity is not null ||
        SourceTargetEntity is not null ||
        TransformCenterEntity is not null ||
        TransformRotationTargetEntity is not null;
}

internal sealed record PictoActCoordinateArg(Vector3 Value, string? EntityReference);

internal enum PictoActTransformRotationMode
{
    Unspecified,
    Default,
    Fixed,
    Target,
}

internal enum PictoActShapeKind
{
    Circle,
    Rectangle,
    BidirectionalRectangle,
    Fan,
    Polygon,
    NativeOnly,
}

internal sealed class StoredPictoActShape(string semanticTag, PictoActShape shape)
{
    internal string SemanticTag { get; } = semanticTag;

    internal PictoActShape Shape { get; set; } = shape;

    internal List<nint> NativeHandles { get; } = [];

    internal IReadOnlyList<PictoActShape>? NativeShapes { get; set; }

    internal bool NativeDirty { get; set; }

    internal bool NativeCreationFailed { get; set; }
}
