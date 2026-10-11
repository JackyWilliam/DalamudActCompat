using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;

namespace DalamudActCompat.UI;

internal static partial class DactTheme
{
    // Drawing is confined to the UI thread; reuse the snapshot while the picker
    // is closed instead of allocating a dictionary on every game frame.
    private static readonly Dictionary<nint, int> FileDialogVertexStarts = new();
    public static void DrawFileDialog(FileDialogManager dialogs) => DrawFileDialogSurface(dialogs.Draw);

    internal static unsafe void DrawFileDialogSurface(Action draw)
    {
        var themed = CurrentSkin != SkinCatalog.Default;
        var light = Palette.Light;
        var scale = ImGui.GetIO().FontGlobalScale;
        var style = ImGui.GetStyle();
        var labelsWidth = ImGui.CalcTextSize("Ok").X + ImGui.CalcTextSize("Cancel").X;
        var compact = light || labelsWidth + style.FramePadding.X * 4 + style.ItemSpacing.X * 3 > 100 * scale;
        if (!themed && !compact) { draw(); return; }
        var context = ImGui.GetCurrentContext();
        var starts = FileDialogVertexStarts;
        starts.Clear();
        for (var i = 0; themed && i < context.Windows.Size; i++)
        {
            var window = context.Windows[i];
            // Begin resets an inactive window's previous-frame draw list.
            if (window.Active) starts[(nint)window.Handle] = window.DrawList.VtxBuffer.Size;
        }
        var button = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        if (compact)
        {
            // The upstream footer reserves a fixed 100 scaled pixels for Ok,
            // Cancel and three gaps. Its native controls need a smaller padding
            // budget than our game sprites or Cancel is clipped off the window.
            var gap = Math.Min(style.ItemSpacing.X, 4 * scale);
            var padding = Math.Clamp((100 * scale - labelsWidth - 3 * gap) / 4, 0, light ? 8 * scale : style.FramePadding.X);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, style.ItemSpacing.Y));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(padding, style.FramePadding.Y));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, light ? 5 * scale : style.FrameRounding);
        }
        try { draw(); }
        finally { if (compact) ImGui.PopStyleVar(3); }
        if (!themed) return;
        // Dalamud's picker hardcodes its breadcrumb fill and file-icon colors.
        // Recolor only geometry emitted by this draw, rather than altering its
        // shared static fields (which would also affect other plugins' dialogs).
        var solidUv = ImGui.GetFontTexUvWhitePixel();
        for (var i = 0; i < context.Windows.Size; i++)
        {
            var window = context.Windows[i];
            if (!window.Active) continue;
            var vertices = window.DrawList.VtxBuffer;
            for (var v = starts.GetValueOrDefault((nint)window.Handle); v < vertices.Size; v++)
            {
                var vertex = vertices[v];
                var rgb = vertex.Col & 0x00ffffff;
                Vector4? replacement = rgb == (ImGui.ColorConvertFloat4ToU32(new(.188f, .188f, .2f, 1)) & 0x00ffffff)
                    ? button : null;
                // Solid fills use the atlas white pixel too. Limit icon/text
                // recoloring to glyph UVs so glass's white scrims remain white.
                if (replacement is null && vertex.Uv != solidUv && (Palette.Light || Palette.Glass))
                {
                    foreach (var color in FileDialogIconColors)
                        if (rgb == (ImGui.ColorConvertFloat4ToU32(color) & 0x00ffffff))
                        {
                            replacement = color == Vector4.One ? Palette.Text : Vector4.Lerp(color, Palette.Text, .65f);
                            break;
                        }
                }
                if (replacement is not { } tint) continue;
                // Apply the theme opacity without losing disabled/fade alpha
                // or the anti-aliased fringe from the original geometry.
                tint.W *= (vertex.Col >> 24) / 255f;
                vertex.Col = ImGui.ColorConvertFloat4ToU32(tint);
                vertices[v] = vertex;
            }
        }
    }

    private static readonly Vector4[] FileDialogIconColors = [Vector4.One, new(1, 1f / 3, 1f / 3, 1),
        new(139f / 255, 233f / 255, 253f / 255, 1), new(241f / 255, 250f / 255, 140f / 255, 1),
        new(1, 121f / 255, 198f / 255, 1), new(80f / 255, 250f / 255, 123f / 255, 1)];
}
