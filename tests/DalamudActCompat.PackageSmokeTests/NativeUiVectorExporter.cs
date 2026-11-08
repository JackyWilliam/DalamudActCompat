using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;

// Test-only companion to NativeUiRasterizer: turns the same real cimgui draw commands into
// editable design primitives (rectangles, outlines, paths, text runs and images) so the shipped
// UI can be rebuilt layer by layer in a design tool. ImGui only records triangles, so shapes are
// recovered geometrically: every primitive call owns its vertices, which makes each connected
// triangle group one primitive, and glyph quads are mapped back to code points via the atlas.
internal sealed class NativeUiVectorExporter
{
    private readonly ImFontAtlasPtr atlas;
    private readonly Func<ulong, (byte[] Pixels, int Width, int Height)?> texture;
    private Dictionary<(ulong Texture, int U, int V), Glyph>? glyphs;
    private Vector2 whitePixel;
    private float fontSize, ascent, spaceAdvance, emSize;

    private sealed record Glyph(char Character, float X0, float Y0, float X1, float Y1, float Advance);

    private sealed class Run
    {
        public float X, Top, Scale, NextX; public uint Color; public string Layer = ""; public StringBuilder Text = new();
    }

    public NativeUiVectorExporter(ImFontAtlasPtr atlas, Func<ulong, (byte[] Pixels, int Width, int Height)?> texture)
    {
        this.atlas = atlas;
        this.texture = texture;
    }

    private unsafe void BuildGlyphMap()
    {
        glyphs = new();
        whitePixel = atlas.TexUvWhitePixel;
        var font = atlas.Fonts[0];
        fontSize = font.FontSize; ascent = font.Ascent; emSize = 0;
        for (var f = 0; f < atlas.Fonts.Size; f++)
        {
            var current = atlas.Fonts[f];
            for (var i = 0; i < current.Glyphs.Size; i++)
            {
                var glyph = current.Glyphs[i];
                if (glyph.Codepoint == ' ' && f == 0) spaceAdvance = glyph.AdvanceX;
                // ImGui's pixel size is the font's full ascent-to-descent height, while design
                // tools size text by its em box; a full-width CJK advance is exactly one em.
                if (glyph.Codepoint == 0x4E2D && f == 0) emSize = glyph.AdvanceX;
                if (glyph.Visible == 0 || glyph.Codepoint > 0xFFFF) continue;
                var handle = (ulong)glyph.TextureIndex + 1;
                if (texture(handle) is not { } tex) continue;
                glyphs.TryAdd(Key(handle, new(glyph.U0, glyph.V0), tex),
                    new((char)glyph.Codepoint, glyph.X0, glyph.Y0, glyph.X1, glyph.Y1, glyph.AdvanceX));
            }
        }

        if (emSize <= 0) emSize = fontSize / 1.32f;
    }

    private static (ulong, int, int) Key(ulong handle, Vector2 uv, (byte[] Pixels, int Width, int Height) tex)
        => (handle, (int)MathF.Round(uv.X * tex.Width * 4), (int)MathF.Round(uv.Y * tex.Height * 4));

    public unsafe void Save(ImDrawDataPtr data, string jsonPath, string imageDirectory)
    {
        if (glyphs is null) BuildGlyphMap();
        var nodes = new List<object>();
        Run? run = null;
        void Flush()
        {
            if (run is { Text.Length: > 0 } && run.Text.ToString().Trim().Length > 0)
                nodes.Add(new
                {
                    k = "text", layer = run.Layer, x = R(run.X), y = R(run.Top), w = R(run.NextX - run.X),
                    size = R(emSize * run.Scale), lh = R(fontSize * run.Scale), baseline = R(ascent * run.Scale),
                    color = Color(run.Color), text = run.Text.ToString().TrimEnd(),
                });
            run = null;
        }

        for (var n = 0; n < data.CmdListsCount; n++)
        {
            var list = new ImDrawListPtr(data.CmdLists[n]);
            var layer = list.OwnerName is null ? $"list-{n}" : Encoding.UTF8.GetString(MemoryMarshal(list.OwnerName));
            for (var c = 0; c < list.CmdBuffer.Size; c++)
            {
                var command = list.CmdBuffer[c];
                if (command.UserCallback != null || command.ElemCount == 0) continue;
                var handle = command.TextureId.Handle;
                if (texture(handle) is not { } tex) continue;
                var clip = command.ClipRect;
                foreach (var component in Components(list, command))
                {
                    var vertices = component.Vertices;
                    var distinctUv = vertices.Select(v => v.Uv).Distinct().Count();
                    var fontTexture = handle <= (ulong)atlas.Textures.Size;
                    if (fontTexture && distinctUv >= 4 && vertices.Count == 4 &&
                        glyphs!.TryGetValue(Key(handle, Vector2.Min(Vector2.Min(vertices[0].Uv, vertices[1].Uv), Vector2.Min(vertices[2].Uv, vertices[3].Uv)), tex), out var glyph))
                    {
                        var min = Vector2.Min(Vector2.Min(vertices[0].Pos, vertices[1].Pos), Vector2.Min(vertices[2].Pos, vertices[3].Pos));
                        var max = Vector2.Max(Vector2.Max(vertices[0].Pos, vertices[1].Pos), Vector2.Max(vertices[2].Pos, vertices[3].Pos));
                        var center = (min + max) / 2;
                        if (center.X < clip.X || center.X > clip.Z || center.Y < clip.Y || center.Y > clip.W) continue;
                        var scale = glyph.Y1 > glyph.Y0 ? (max.Y - min.Y) / (glyph.Y1 - glyph.Y0) : 1;
                        var penX = min.X - glyph.X0 * scale; var top = min.Y - glyph.Y0 * scale;
                        var color = vertices[0].Col;
                        var space = spaceAdvance * scale;
                        var gap = run is null ? 0 : penX - run.NextX;
                        var spaces = space > 0 ? (int)MathF.Round(gap / space) : 0;
                        if (run is null || run.Color != color || run.Layer != layer || MathF.Abs(run.Top - top) > .6f ||
                            MathF.Abs(run.Scale - scale) > .01f || gap < -.75f || spaces > 6 || MathF.Abs(gap - spaces * space) > .75f)
                        {
                            Flush();
                            run = new() { X = penX, Top = top, Scale = scale, NextX = penX, Color = color, Layer = layer };
                        }
                        else run.Text.Append(' ', spaces);
                        run.Text.Append(glyph.Character);
                        run.NextX = penX + glyph.Advance * scale;
                        continue;
                    }

                    Flush();
                    // A partially clipped glyph has adjusted UVs and can no longer be identified.
                    if (fontTexture && distinctUv >= 4 && vertices.Count == 4) continue;
                    var whiteUv = vertices.All(v => Vector2.DistanceSquared(v.Uv, whitePixel) < 1e-8f);
                    if (!fontTexture) { AddImage(nodes, layer, vertices, tex, clip, imageDirectory); continue; }
                    AddShape(nodes, layer, component, clip, texturedLine: !whiteUv);
                }
            }

            Flush();
        }

        var document = new
        {
            width = (int)data.DisplaySize.X, height = (int)data.DisplaySize.Y,
            background = new[] { 23 / 255f, 30 / 255f, 40 / 255f, 1f },
            font = new { size = fontSize, em = emSize, ascent },
            nodes,
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(document));
    }

    private static unsafe ReadOnlySpan<byte> MemoryMarshal(byte* text)
    {
        var length = 0; while (text[length] != 0) length++;
        return new ReadOnlySpan<byte>(text, length);
    }

    private sealed record Component(List<ImDrawVert> Vertices, List<(int A, int B, int C)> Triangles);

    // Union-find over the command's index buffer: every ImDrawList primitive call writes its
    // own vertices, so each connected set of triangles is exactly one primitive.
    private static List<Component> Components(ImDrawListPtr list, ImDrawCmd command)
    {
        var count = (int)command.ElemCount;
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
        var triangles = new List<(int, int, int)>(count / 3);
        for (var i = 0; i < count; i += 3)
        {
            var first = (int)command.IdxOffset + i;
            var a = (int)command.VtxOffset + list.IdxBuffer[first];
            var b = (int)command.VtxOffset + list.IdxBuffer[first + 1];
            var c = (int)command.VtxOffset + list.IdxBuffer[first + 2];
            parent.TryAdd(a, a); parent.TryAdd(b, b); parent.TryAdd(c, c);
            Union(a, b); Union(b, c);
            triangles.Add((a, b, c));
        }

        // Preserve draw order: components are emitted by their first triangle.
        var groups = new Dictionary<int, (List<int> Vertices, List<(int, int, int)> Triangles)>();
        var order = new List<int>();
        foreach (var triangle in triangles)
        {
            var root = Find(triangle.Item1);
            if (!groups.TryGetValue(root, out var group)) { group = (new(), new()); groups[root] = group; order.Add(root); }
            group.Triangles.Add(triangle);
        }

        var components = new List<Component>(order.Count);
        foreach (var root in order)
        {
            var group = groups[root];
            var indices = group.Triangles.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).Distinct().OrderBy(x => x).ToList();
            var map = indices.Select((index, local) => (index, local)).ToDictionary(x => x.index, x => x.local);
            var vertices = new List<ImDrawVert>(indices.Count);
            foreach (var index in indices) vertices.Add(list.VtxBuffer[index]);
            components.Add(new Component(vertices, group.Triangles.Select(t => (map[t.Item1], map[t.Item2], map[t.Item3])).ToList()));
        }

        return components;
    }

    private static void AddShape(List<object> nodes, string layer, Component component, Vector4 clip, bool texturedLine)
    {
        var vertices = component.Vertices;
        var visible = vertices.Where(v => (v.Col >> 24) > 0).ToList();
        if (visible.Count == 0) return;
        var fringe = visible.Count < vertices.Count;
        var hull = Hull(vertices.Select(v => v.Pos).ToList());
        var hullArea = Area(hull);
        if (hullArea < .05f) return;
        var area = component.Triangles.Sum(t => MathF.Abs(Cross(vertices[t.B].Pos - vertices[t.A].Pos, vertices[t.C].Pos - vertices[t.A].Pos)) / 2);
        var paint = Paint(visible);
        var min = new Vector2(hull.Min(p => p.X), hull.Min(p => p.Y));
        var max = new Vector2(hull.Max(p => p.X), hull.Max(p => p.Y));
        // Anti-aliased fills sit half a pixel inside their alpha-zero fringe; textured lines
        // carry one extra pixel of coverage on each side.
        var inset = texturedLine ? 1f : fringe ? .5f : 0f;

        if (area >= hullArea * .95f)
        {
            if (texturedLine)
            {
                // A single textured segment: draw it as a thin bar of the real line thickness.
                var size = max - min;
                if (size.X >= size.Y) { min.Y += 1; max.Y -= 1; } else { min.X += 1; max.X -= 1; }
                AddRectangle(nodes, layer, min, max, [0, 0, 0, 0], paint, null, 0, clip);
                return;
            }

            var shrunk = inset > 0 ? Offset(hull, -inset) : hull;
            min = new(shrunk.Min(p => p.X), shrunk.Min(p => p.Y)); max = new(shrunk.Max(p => p.X), shrunk.Max(p => p.Y));
            if (RoundedRectangle(shrunk, min, max) is { } radii) AddRectangle(nodes, layer, min, max, radii, paint, null, 0, clip);
            else AddPath(nodes, layer, [shrunk], paint, clip);
            return;
        }

        // Ring-shaped geometry around a (rounded) rectangle is an outline drawn with AddRect.
        min += new Vector2(inset); max -= new Vector2(inset);
        if (max.X > min.X && max.Y > min.Y && RoundedRectangle(Offset(hull, -inset), min, max) is { } ringRadii)
        {
            var w = max.X - min.X + 2 * inset; var h = max.Y - min.Y + 2 * inset;
            // Band width b of a rectangular ring: area = 2b(w+h) - 4b^2.
            var disc = MathF.Max(0, 4 * (w + h) * (w + h) - 16 * area);
            var band = (2 * (w + h) - MathF.Sqrt(disc)) / 8;
            var weight = MathF.Max(1, MathF.Round(texturedLine ? band - 2 : fringe ? band - 1 : band));
            if (weight < Math.Min(max.X - min.X, max.Y - min.Y) / 2)
            {
                AddRectangle(nodes, layer, min, max, ringRadii, null, paint, weight, clip);
                return;
            }
        }

        // Anything else (check marks, arrows, curved strokes) keeps its exact triangle coverage.
        var polygons = component.Triangles
            .Where(t => !fringe || ((vertices[t.A].Col >> 24) > 0 && (vertices[t.B].Col >> 24) > 0 && (vertices[t.C].Col >> 24) > 0))
            .Select(t => new List<Vector2> { vertices[t.A].Pos, vertices[t.B].Pos, vertices[t.C].Pos })
            .ToList();
        if (polygons.Count > 0) AddPath(nodes, layer, polygons, paint, clip);
    }

    private static float[]? RoundedRectangle(List<Vector2> hull, Vector2 min, Vector2 max)
    {
        var size = max - min;
        if (size.X <= 0 || size.Y <= 0) return null;
        var limit = Math.Min(size.X, size.Y) / 2;
        float Corner(Vector2 corner)
        {
            var distance = hull.Min(p => Vector2.Distance(p, corner));
            // The nearest point of an arc of radius r to its box corner is r(sqrt2 - 1) away.
            var radius = distance / (MathF.Sqrt(2) - 1);
            return radius < .75f ? 0 : MathF.Min(limit, MathF.Round(radius * 2) / 2);
        }

        float[] radii = [Corner(min), Corner(new(max.X, min.Y)), Corner(max), Corner(new(min.X, max.Y))];
        var expected = size.X * size.Y - (4 - MathF.PI) / 4 * radii.Sum(r => r * r);
        var actual = Area(hull);
        return MathF.Abs(actual - expected) <= MathF.Max(1.5f, expected * .03f) ? radii : null;
    }

    private static void AddRectangle(List<object> nodes, string layer, Vector2 min, Vector2 max, float[] radii,
        object? fill, object? stroke, float weight, Vector4 clip)
    {
        var clippedMin = Vector2.Max(min, new(clip.X, clip.Y)); var clippedMax = Vector2.Min(max, new(clip.Z, clip.W));
        if (clippedMax.X - clippedMin.X < .25f || clippedMax.Y - clippedMin.Y < .25f) return;
        // Clipping by a scroll region keeps the visible part; rounding on cut edges is dropped.
        if (clippedMin != min || clippedMax != max)
        {
            if (clippedMin.Y > min.Y) { radii[0] = 0; radii[1] = 0; }
            if (clippedMax.X < max.X) { radii[1] = 0; radii[2] = 0; }
            if (clippedMax.Y < max.Y) { radii[2] = 0; radii[3] = 0; }
            if (clippedMin.X > min.X) { radii[0] = 0; radii[3] = 0; }
        }

        nodes.Add(new
        {
            k = "rect", layer, x = R(clippedMin.X), y = R(clippedMin.Y), w = R(clippedMax.X - clippedMin.X), h = R(clippedMax.Y - clippedMin.Y),
            r = radii.Select(R).ToArray(), fill, stroke, sw = weight,
        });
    }

    private static void AddPath(List<object> nodes, string layer, List<List<Vector2>> polygons, object paint, Vector4 clip)
    {
        var data = new StringBuilder();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var polygon in polygons)
        {
            var clipped = ClipPolygon(polygon, clip);
            if (clipped.Count < 3 || MathF.Abs(Area(clipped)) < .01f) continue;
            for (var i = 0; i < clipped.Count; i++)
            {
                var p = clipped[i];
                data.Append(i == 0 ? "M " : " L ").Append(R(p.X)).Append(' ').Append(R(p.Y));
                minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y); maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y);
            }

            data.Append(" Z ");
        }

        if (data.Length == 0) return;
        nodes.Add(new { k = "path", layer, d = data.ToString().Trim(), x = R(minX), y = R(minY), w = R(maxX - minX), h = R(maxY - minY), fill = paint });
    }

    private void AddImage(List<object> nodes, string layer, List<ImDrawVert> vertices, (byte[] Pixels, int Width, int Height) tex, Vector4 clip, string directory)
    {
        var min = new Vector2(vertices.Min(v => v.Pos.X), vertices.Min(v => v.Pos.Y));
        var max = new Vector2(vertices.Max(v => v.Pos.X), vertices.Max(v => v.Pos.Y));
        var uvMin = new Vector2(vertices.Min(v => v.Uv.X), vertices.Min(v => v.Uv.Y));
        var uvMax = new Vector2(vertices.Max(v => v.Uv.X), vertices.Max(v => v.Uv.Y));
        var clippedMin = Vector2.Max(min, new(clip.X, clip.Y)); var clippedMax = Vector2.Min(max, new(clip.Z, clip.W));
        if (clippedMax.X - clippedMin.X < 1 || clippedMax.Y - clippedMin.Y < 1 || max.X - min.X < .5f || max.Y - min.Y < .5f) return;
        // Map the visible screen rectangle back into texture space before cropping.
        Vector2 Uv(Vector2 p) => uvMin + (uvMax - uvMin) * ((p - min) / (max - min));
        var a = Uv(clippedMin) * new Vector2(tex.Width, tex.Height); var b = Uv(clippedMax) * new Vector2(tex.Width, tex.Height);
        var x0 = Math.Clamp((int)MathF.Floor(a.X), 0, tex.Width - 1); var y0 = Math.Clamp((int)MathF.Floor(a.Y), 0, tex.Height - 1);
        var x1 = Math.Clamp((int)MathF.Ceiling(b.X), x0 + 1, tex.Width); var y1 = Math.Clamp((int)MathF.Ceiling(b.Y), y0 + 1, tex.Height);
        var width = x1 - x0; var height = y1 - y0;
        var tint = Color(vertices[0].Col);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var source = ((y0 + y) * tex.Width + x0 + x) * 4; var target = (y * width + x) * 4;
            for (var channel = 0; channel < 4; channel++)
                pixels[target + channel] = (byte)Math.Clamp(tex.Pixels[source + channel] * tint[channel], 0, 255);
        }

        var name = Convert.ToHexString(SHA1.HashData(pixels.Concat(BitConverter.GetBytes(width)).ToArray()))[..16].ToLowerInvariant() + ".png";
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) WriteRgbaPng(path, pixels, width, height);
        nodes.Add(new
        {
            k = "image", layer, x = R(clippedMin.X), y = R(clippedMin.Y), w = R(clippedMax.X - clippedMin.X), h = R(clippedMax.Y - clippedMin.Y),
            src = name,
        });
    }

    private static object Paint(List<ImDrawVert> vertices)
    {
        var colors = vertices.Select(v => Color(v.Col)).ToList();
        var first = colors[0];
        if (colors.All(c => Close(c, first))) return new { c = first };
        // Multi-colour fills (AddRectFilledMultiColor, shaded bars) become a linear gradient
        // along whichever axis explains the colour change best.
        var minY = vertices.Min(v => v.Pos.Y); var maxY = vertices.Max(v => v.Pos.Y);
        var minX = vertices.Min(v => v.Pos.X); var maxX = vertices.Max(v => v.Pos.X);
        float[] Average(IEnumerable<int> indices)
        {
            var list = indices.ToList();
            return list.Count == 0 ? first : Enumerable.Range(0, 4).Select(i => list.Average(index => colors[index][i])).ToArray();
        }

        var indices = Enumerable.Range(0, vertices.Count).ToList();
        var top = Average(indices.Where(i => vertices[i].Pos.Y < (minY + maxY) / 2));
        var bottom = Average(indices.Where(i => vertices[i].Pos.Y >= (minY + maxY) / 2));
        var left = Average(indices.Where(i => vertices[i].Pos.X < (minX + maxX) / 2));
        var right = Average(indices.Where(i => vertices[i].Pos.X >= (minX + maxX) / 2));
        static float Difference(float[] a, float[] b) => a.Zip(b, (x, y) => MathF.Abs(x - y)).Sum();
        return Difference(top, bottom) >= Difference(left, right)
            ? new { g = "v", a = top, b = bottom }
            : new { g = "h", a = left, b = right };
    }

    private static bool Close(float[] a, float[] b) => a.Zip(b, (x, y) => MathF.Abs(x - y)).All(d => d < .02f);
    private static float[] Color(uint c) => [R3((c & 255) / 255f), R3(((c >> 8) & 255) / 255f), R3(((c >> 16) & 255) / 255f), R3((c >> 24) / 255f)];
    private static float R(float value) => MathF.Round(value, 2);
    private static float R3(float value) => MathF.Round(value, 3);
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static float Area(List<Vector2> polygon)
    {
        var sum = 0f;
        for (var i = 0; i < polygon.Count; i++) sum += Cross(polygon[i], polygon[(i + 1) % polygon.Count]);
        return MathF.Abs(sum) / 2;
    }

    private static List<Vector2> Hull(List<Vector2> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted;
        var hull = new List<Vector2>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^1] - hull[^2], p - hull[^2]) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    // Moves every edge of a convex polygon outward (positive) or inward by a fixed distance,
    // keeping straight edges straight so rectangles stay pixel-aligned.
    private static List<Vector2> Offset(List<Vector2> polygon, float distance)
    {
        if (polygon.Count < 3) return polygon;
        var signed = 0f;
        for (var i = 0; i < polygon.Count; i++) signed += Cross(polygon[i], polygon[(i + 1) % polygon.Count]);
        // In y-down screen space a positive shoelace sum means (edge.Y, -edge.X) faces outward.
        var orientation = signed >= 0 ? 1f : -1f;
        Vector2 Normal(Vector2 a, Vector2 b)
        {
            var edge = b - a; var length = edge.Length();
            return length < 1e-4f ? Vector2.Zero : new Vector2(edge.Y, -edge.X) / length * orientation;
        }

        var result = new List<Vector2>(polygon.Count);
        for (var i = 0; i < polygon.Count; i++)
        {
            var previous = polygon[(i + polygon.Count - 1) % polygon.Count]; var current = polygon[i]; var next = polygon[(i + 1) % polygon.Count];
            var n1 = Normal(previous, current); var n2 = Normal(current, next);
            var denominator = 1 + Vector2.Dot(n1, n2);
            result.Add(denominator < 1e-3f ? current + n2 * distance : current + (n1 + n2) / denominator * distance);
        }

        return result;
    }

    private static List<Vector2> ClipPolygon(List<Vector2> polygon, Vector4 clip)
    {
        var result = polygon;
        foreach (var (inside, intersect) in new (Func<Vector2, bool>, Func<Vector2, Vector2, Vector2>)[]
                 {
                     (p => p.X >= clip.X, (a, b) => Vector2.Lerp(a, b, (clip.X - a.X) / (b.X - a.X))),
                     (p => p.X <= clip.Z, (a, b) => Vector2.Lerp(a, b, (clip.Z - a.X) / (b.X - a.X))),
                     (p => p.Y >= clip.Y, (a, b) => Vector2.Lerp(a, b, (clip.Y - a.Y) / (b.Y - a.Y))),
                     (p => p.Y <= clip.W, (a, b) => Vector2.Lerp(a, b, (clip.W - a.Y) / (b.Y - a.Y))),
                 })
        {
            var input = result; result = new();
            for (var i = 0; i < input.Count; i++)
            {
                var current = input[i]; var previous = input[(i + input.Count - 1) % input.Count];
                if (inside(current))
                {
                    if (!inside(previous)) result.Add(intersect(previous, current));
                    result.Add(current);
                }
                else if (inside(previous)) result.Add(intersect(previous, current));
            }

            if (result.Count == 0) break;
        }

        return result;
    }

    private static void WriteRgbaPng(string path, byte[] pixels, int width, int height)
    {
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] bytes)
        {
            Span<byte> number = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); file.Write(number);
            var name = Encoding.ASCII.GetBytes(type); file.Write(name); file.Write(bytes);
            uint crc = 0xffffffff;
            foreach (var value in name.Concat(bytes))
            {
                crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); file.Write(number);
        }

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zip = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < height; y++) { zip.WriteByte(0); zip.Write(pixels, y * width * 4, width * 4); }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
    }
}
