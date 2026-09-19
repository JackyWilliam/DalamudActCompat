using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

// Test-only software rendering of real cimgui draw commands and its actual font
// atlas. This supplies inspectable evidence without a mock HTML UI or game injection.
internal sealed class NativeUiRasterizer
{
    internal Action<ImDrawCmdPtr, byte[], int, int>? RenderCallback { get; set; }
    internal Action<byte[], int, int>? PaintBackdrop { get; set; }
    private readonly Dictionary<ulong, (byte[] Pixels, int Width, int Height)> textures = new();
    public void AddTexture(ImTextureID id, byte[] pixels, int width, int height)
        => textures.Add(id.Handle, (pixels, width, height));
    public unsafe NativeUiRasterizer(ImFontAtlasPtr atlas)
    {
        for (var i = 0; i < atlas.Textures.Size; i++)
        {
            byte* source = null; var width = 0; var height = 0;
            atlas.GetTexDataAsRGBA32(i, ref source, ref width, ref height);
            var pixels = new byte[width * height * 4]; Marshal.Copy((nint)source, pixels, 0, pixels.Length);
            var id = (ulong)i + 1; atlas.SetTexID(i, new ImTextureID(id));
            textures.Add(id, (pixels, width, height));
        }
    }

    public unsafe void Save(ImDrawDataPtr data, string path)
    {
        var width = (int)data.DisplaySize.X; var height = (int)data.DisplaySize.Y;
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < pixels.Length; i += 3) { pixels[i] = 23; pixels[i + 1] = 30; pixels[i + 2] = 40; }
        PaintBackdrop?.Invoke(pixels, width, height);
        for (var n = 0; n < data.CmdListsCount; n++)
        {
            var list = new ImDrawListPtr(data.CmdLists[n]);
            for (var c = 0; c < list.CmdBuffer.Size; c++)
            {
                var command = list.CmdBuffer[c];
                if (command.UserCallback != null)
                {
                    // Optional real D3D11 pass over the pixels drawn so far: this
                    // preserves native draw order and does not fake the glass shader.
                    RenderCallback?.Invoke(new ImDrawCmdPtr(&command), pixels, width, height);
                    continue;
                }
                if (!textures.TryGetValue(command.TextureId.Handle, out var texture)) continue;
                var clip = command.ClipRect;
                for (var i = 0; i < command.ElemCount; i += 3)
                {
                    var first = (int)command.IdxOffset + i;
                    var a = list.VtxBuffer[(int)command.VtxOffset + list.IdxBuffer[first]];
                    var b = list.VtxBuffer[(int)command.VtxOffset + list.IdxBuffer[first + 1]];
                    var d = list.VtxBuffer[(int)command.VtxOffset + list.IdxBuffer[first + 2]];
                    Triangle(a, b, d, clip, texture, pixels, width, height);
                }
            }
        }
        WritePng(path, pixels, width, height);
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static Vector4 Color(uint c) => new(c & 255, (c >> 8) & 255, (c >> 16) & 255, c >> 24);
    private static void Triangle(ImDrawVert a, ImDrawVert b, ImDrawVert c, Vector4 clip,
        (byte[] Pixels, int Width, int Height) texture, byte[] pixels, int width, int height)
    {
        var area = Cross(b.Pos - a.Pos, c.Pos - a.Pos); if (Math.Abs(area) < .0001f) return;
        if (area < 0) { (b, c) = (c, b); area = -area; }
        var min = Vector2.Max(Vector2.Max(Vector2.Min(a.Pos, Vector2.Min(b.Pos, c.Pos)), Vector2.Zero), new(clip.X, clip.Y));
        var max = Vector2.Min(Vector2.Min(Vector2.Max(a.Pos, Vector2.Max(b.Pos, c.Pos)), new(width, height)), new(clip.Z, clip.W));
        var ca = Color(a.Col); var cb = Color(b.Col); var cc = Color(c.Col);
        // The top-left edge rule prevents shared triangle edges from receiving alpha twice.
        static bool Inside(float edge, Vector2 direction) => edge > 0 || (edge == 0 && (direction.Y < 0 || direction.Y == 0 && direction.X > 0));
        for (var y = (int)Math.Ceiling(min.Y - .5f); y < Math.Ceiling(max.Y - .5f); y++)
        for (var x = (int)Math.Ceiling(min.X - .5f); x < Math.Ceiling(max.X - .5f); x++)
        {
            var point = new Vector2(x + .5f, y + .5f);
            var wa = Cross(c.Pos - b.Pos, point - b.Pos);
            var wb = Cross(a.Pos - c.Pos, point - c.Pos);
            var wc = Cross(b.Pos - a.Pos, point - a.Pos);
            if (!Inside(wa, c.Pos - b.Pos) || !Inside(wb, a.Pos - c.Pos) || !Inside(wc, b.Pos - a.Pos)) continue;
            wa /= area; wb /= area; wc /= area;
            var uv = a.Uv * wa + b.Uv * wb + c.Uv * wc;
            var color = ca * wa + cb * wb + cc * wc;
            var sample = Sample(texture, uv);
            var alpha = color.W * sample.W / (255f * 255f);
            var index = (y * width + x) * 3;
            pixels[index] = (byte)Math.Clamp(color.X * sample.X / 255f * alpha + pixels[index] * (1 - alpha), 0, 255);
            pixels[index + 1] = (byte)Math.Clamp(color.Y * sample.Y / 255f * alpha + pixels[index + 1] * (1 - alpha), 0, 255);
            pixels[index + 2] = (byte)Math.Clamp(color.Z * sample.Z / 255f * alpha + pixels[index + 2] * (1 - alpha), 0, 255);
        }
    }
    private static Vector4 Sample((byte[] Pixels, int Width, int Height) tex, Vector2 uv)
    {
        var p = uv * new Vector2(tex.Width, tex.Height) - new Vector2(.5f);
        var x = (int)MathF.Floor(p.X); var y = (int)MathF.Floor(p.Y); var fx = p.X - x; var fy = p.Y - y;
        Vector4 At(int xx, int yy)
        {
            var i = (Math.Clamp(yy, 0, tex.Height - 1) * tex.Width + Math.Clamp(xx, 0, tex.Width - 1)) * 4;
            return new(tex.Pixels[i], tex.Pixels[i + 1], tex.Pixels[i + 2], tex.Pixels[i + 3]);
        }
        return Vector4.Lerp(Vector4.Lerp(At(x, y), At(x + 1, y), fx), Vector4.Lerp(At(x, y + 1), At(x + 1, y + 1), fx), fy);
    }
    private static void WritePng(string path, byte[] pixels, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] bytes)
        {
            Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); file.Write(number);
            var name = System.Text.Encoding.ASCII.GetBytes(type); file.Write(name); file.Write(bytes);
            uint crc = 0xffffffff;
            foreach (var value in name.Concat(bytes))
            {
                crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); file.Write(number);
        }
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            for (var y = 0; y < height; y++) { zip.WriteByte(0); zip.Write(pixels, y * width * 3, width * 3); }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
    }
}
