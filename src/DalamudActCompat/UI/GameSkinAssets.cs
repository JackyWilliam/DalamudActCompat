using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace DalamudActCompat.UI;

// Read the player's installed Light-theme assets through Dalamud. The package
// contains only paths and ULD slice coordinates, never extracted game artwork.
internal sealed class GameSkinAssets
{
    internal const string Root = "ui/uld/img01/";
    internal static readonly string[] Files = ["TabButtonA", "ButtonA", "WindowA_Button", "CircleButtons", "MainCommand_Icon", "CheckBoxA",
        "WindowA_BgSelected_Corner", "WindowA_BgSelected_H", "WindowA_BgSelected_HV", "WindowA_BgSelected_V"];
    private readonly Func<string, IDalamudTextureWrap?> resolve;

    public GameSkinAssets(ITextureProvider textures, IDataManager data)
    {
        var shared = new Dictionary<string, ISharedImmediateTexture>(StringComparer.Ordinal);
        resolve = name =>
        {
            if (!shared.TryGetValue(name, out var texture))
            {
                var path = PathFor(name, true);
                if (!data.FileExists(path)) path = PathFor(name, false);
                shared[name] = texture = textures.GetFromGame(path);
            }
            return texture.TryGetWrap(out var wrap, out _) ? wrap : null;
        };
    }

    internal GameSkinAssets(Func<string, IDalamudTextureWrap?> resolve) => this.resolve = resolve;

    internal static string PathFor(string name, bool highResolution)
        => (name == "MainCommand_Icon" ? "ui/uld/" : Root) + name + (highResolution ? "_hr1" : "") + ".tex";

    public bool HasWindowTextures => Files.Where(name => name.StartsWith("WindowA_Bg", StringComparison.Ordinal)).All(name => resolve(name) is not null);

    private static void Image(ImDrawListPtr draw, IDalamudTextureWrap texture, Vector2 atlasSize,
        Vector2 source, Vector2 sourceSize, Vector2 min, Vector2 max, Vector4 tint)
        => draw.AddImage(texture.Handle, min, max, source / atlasSize, (source + sourceSize) / atlasSize, ImGui.GetColorU32(tint));

    private bool Stretch(string name, Vector2 atlasSize, Vector2 source, Vector2 sourceSize,
        Vector2 min, Vector2 max, float cap, Vector4 tint)
    {
        var texture = resolve(name);
        if (texture is null) return false;
        var draw = ImGui.GetWindowDrawList();
        var scale = (max.Y - min.Y) / sourceSize.Y;
        var edge = Math.Min(cap * scale, (max.X - min.X) * .5f);
        // ULD NineGrid stretches only the middle; a wide DACT label must not
        // stretch the game's tab angle, selected gem, or rounded button ends.
        Image(draw, texture, atlasSize, source, new(cap, sourceSize.Y), min, new(min.X + edge, max.Y), tint);
        Image(draw, texture, atlasSize, source + new Vector2(cap, 0), new(sourceSize.X - 2 * cap, sourceSize.Y),
            new(min.X + edge, min.Y), new(max.X - edge, max.Y), tint);
        Image(draw, texture, atlasSize, source + new Vector2(sourceSize.X - cap, 0), new(cap, sourceSize.Y),
            new(max.X - edge, min.Y), max, tint);
        return true;
    }

    public bool Tab(Vector2 min, Vector2 max, bool selected, bool hovered)
        // social.uld / character.uld: 88x26 parts, 16px end caps, second row selected.
        => Stretch("TabButtonA", new(88, 52), new(0, selected ? 26 : 0), new(88, 26), min, max, 16,
            Vector4.One);

    public bool Button(Vector2 min, Vector2 max, bool active, bool hovered)
        => Stretch("ButtonA", new(100, 28), Vector2.Zero, new(100, 28), min, max, 16,
            active ? new(.75f, .75f, .75f, 1) : hovered ? Vector4.One : new(.94f, .94f, .94f, 1));

    public bool Icon(GameSkinIcon icon, Vector2 min, float size, bool active = false)
    {
        var window = icon == GameSkinIcon.Close;
        var friend = icon == GameSkinIcon.Friends;
        var texture = resolve(window ? "WindowA_Button" : friend ? "MainCommand_Icon" : "CircleButtons");
        if (texture is null) return false;
        var source = icon switch
        {
            GameSkinIcon.Help => new Vector2(84, 0),
            GameSkinIcon.Chat => new Vector2(140, 0),
            GameSkinIcon.Refresh => new Vector2(112, 0),
            GameSkinIcon.Search => new Vector2(0, 112),
            GameSkinIcon.Friends => new Vector2(160, 0),
            _ => Vector2.Zero,
        };
        var sourceSize = icon == GameSkinIcon.Search ? 24 : friend ? 32 : 28;
        Image(ImGui.GetWindowDrawList(), texture, window ? new(60, 28) : friend ? new(272, 32) : new(252, 160), source, new(sourceSize),
            min, min + new Vector2(size), active ? new(.7f, .7f, .7f, 1) : Vector4.One);
        return true;
    }

    public bool Checkbox(Vector2 min, float size, bool selected)
    {
        var texture = resolve("CheckBoxA");
        if (texture is null) return false;
        var draw = ImGui.GetWindowDrawList();
        Image(draw, texture, new(32, 16), Vector2.Zero, new(16), min, min + new Vector2(size), Vector4.One);
        if (selected) Image(draw, texture, new(32, 16), new(16, 0), new(16), min, min + new Vector2(size), Vector4.One);
        return true;
    }

    public bool Window()
        => Window(ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize(),
            Math.Max(.75f, ImGui.GetFontSize() / 17f), clipToContent: false);

    public void PopupFrame()
    {
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        var edge = 8 * scale;
        var draw = ImGui.GetWindowDrawList();
        // Popup scrollbars are emitted by Begin before caller content. Overlay
        // only the metal rim, so those existing controls remain visible. Trim
        // the source sprite's 10px transparent tail to the popup's actual bounds.
        void Strip(Vector2 a, Vector2 b)
        {
            draw.PushClipRect(a, b, false);
            Window(min, max + new Vector2(0, 10 * scale), scale);
            draw.PopClipRect();
        }
        Strip(min, new Vector2(max.X, min.Y + edge));
        Strip(new Vector2(min.X, max.Y - edge), max);
        Strip(new Vector2(min.X, min.Y + edge), new Vector2(min.X + edge, max.Y - edge));
        Strip(new Vector2(max.X - edge, min.Y + edge), new Vector2(max.X, max.Y - edge));
    }

    public bool Window(Vector2 min, Vector2 max, float scale, bool clipToContent = true)
    {
        var corner = resolve("WindowA_BgSelected_Corner");
        var horizontal = resolve("WindowA_BgSelected_H");
        var middle = resolve("WindowA_BgSelected_HV");
        var vertical = resolve("WindowA_BgSelected_V");
        if (corner is null || horizontal is null || middle is null || vertical is null) return false;
        var side = Math.Min(16 * scale, (max.X - min.X) / 2);
        var top = Math.Min(64 * scale, (max.Y - min.Y) * .6f);
        var bottom = Math.Min(32 * scale, (max.Y - min.Y) - top);
        var draw = ImGui.GetWindowDrawList();
        // Draw before content and outside the content padding. The corner alpha
        // and paper grain come from the same nine slices as the game window.
        draw.PushClipRect(min, max, clipToContent);
        float[] xs = [min.X, min.X + side, max.X - side, max.X];
        float[] ys = [min.Y, min.Y + top, max.Y - bottom, max.Y];
        for (var y = 0; y < 3; y++) for (var x = 0; x < 3; x++)
        {
            var texture = y == 1 ? x == 1 ? middle : vertical : x == 1 ? horizontal : corner;
            var atlas = y == 1 ? new Vector2(32, 32) : new Vector2(32, 96);
            var source = new Vector2(x == 2 ? 16 : 0, y == 2 ? 64 : 0);
            var sourceSize = new Vector2(x == 1 ? 32 : 16, y == 0 ? 64 : 32);
            // ULD uses tiled rendering for the window paper and edge strips.
            // Stretching a 32px paper swatch over the whole panel magnifies grain.
            var tileWidth = x == 1 ? sourceSize.X * scale : xs[x + 1] - xs[x];
            var tileHeight = y == 1 ? sourceSize.Y * scale : ys[y + 1] - ys[y];
            for (var ty = ys[y]; ty < ys[y + 1]; ty += tileHeight)
            for (var tx = xs[x]; tx < xs[x + 1]; tx += tileWidth)
            {
                var size = new Vector2(Math.Min(tileWidth, xs[x + 1] - tx), Math.Min(tileHeight, ys[y + 1] - ty));
                Image(draw, texture, atlas, source, sourceSize * size / new Vector2(tileWidth, tileHeight),
                    new(tx, ty), new Vector2(tx, ty) + size, Vector4.One);
            }
        }
        draw.PopClipRect();
        return true;
    }
}

internal enum GameSkinIcon { Close, Help, Settings, Chat, Refresh, Search, Friends }
