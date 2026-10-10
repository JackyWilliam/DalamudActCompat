using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static partial class SkinSmokeTests
{
    private static unsafe void SolidButtons(NativeUiRasterizer raster, string? output)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var config = new PluginConfiguration();
        config.Appearance.UnlockedEasterEggs.UnionWith(SkinCatalog.All.Where(s => s.EasterEgg).Select(s => s.Id));
        var window = (ControlCenterWindow)RuntimeHelpers.GetUninitializedObject(typeof(ControlCenterWindow));
        void Set(string name, object value) => typeof(ControlCenterWindow).GetField(name, flags)!.SetValue(window, value);
        Set("configuration", config); Set("text", new UiText(config));
        Set("parserStatus", new ParserStatus(ParserState.Running, "解析器运行正常", DateTimeOffset.UnixEpoch));
        Set("isStatusVisible", (Func<bool>)(() => true));
        Set("getGameRegionSelection", (Func<GameRegionSelection>)(() => GameRegionResolver.Resolve(GameRegionMode.Auto, "Chinese", 4)));
        Set("setGameRegionMode", (Action<GameRegionMode>)(_ => { }));
        var overview = typeof(ControlCenterWindow).GetMethod("DrawOverview", flags)!;
        var selectedStyle = typeof(ControlCenterWindow).GetMethod("PushOpenWindowButtonStyle", BindingFlags.Static | BindingFlags.NonPublic)!;
        var io = ImGui.GetIO(); var context = ImGui.GetCurrentContext();
        var previousSize = io.DisplaySize; io.DisplaySize = new(1100, 900);
        var normal = Vector4.Zero; var hovered = Vector4.Zero; var active = Vector4.Zero;
        var buttonCenter = Vector2.Zero; var disabledCenter = Vector2.Zero; var clicks = 0;
        static double Contrast(Vector4 a, Vector4 b)
        {
            static double Channel(float c) => c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
            static double L(Vector4 c) => .2126 * Channel(c.X) + .7152 * Channel(c.Y) + .0722 * Channel(c.Z);
            var x = L(a); var y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
        }
        void Frame(bool home)
        {
            ImGui.NewFrame(); DactTheme.SetCurrent(config.Appearance, true, 3);
            using (DactTheme.PushFrame())
            {
                ControlCenterWindow.PushTheme();
                ImGui.SetNextWindowPos(new(20)); ImGui.SetNextWindowSize(new(1060, 860));
                ImGui.Begin("Production skin controls", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
                if (home) overview.Invoke(window, null);
                else
                {
                    ImGui.TextUnformatted("公共按钮：普通 / 已打开 / 删除 / 小按钮 / 禁用");
                    BrandedWindowChrome.BeginGoldCard("button-contrast", 220);
                    var colors = ImGui.GetStyle().Colors;
                    normal = colors[(int)ImGuiCol.Button]; hovered = colors[(int)ImGuiCol.ButtonHovered]; active = colors[(int)ImGuiCol.ButtonActive];
                    if (DactTheme.Button("普通按钮", new(160, 36))) clicks++;
                    buttonCenter = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
                    ImGui.SameLine(); selectedStyle.Invoke(null, null); DactTheme.Button("关闭已打开窗口", new(160, 36)); ImGui.PopStyleColor(4);
                    ImGui.SameLine();
                    DactTheme.PushStyleColor(ImGuiCol.Button, new(.48f, .10f, .12f, 1));
                    DactTheme.Button("删除", new(100, 36)); ImGui.PopStyleColor();
                    DactTheme.SmallButton("小按钮"); ImGui.SameLine();
                    ImGui.BeginDisabled(); if (DactTheme.Button("不可用", new(140, 36))) clicks++;
                    disabledCenter = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2; ImGui.EndDisabled();
                    ImGui.SameLine(); DactTheme.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                    Check(ImGui.GetStyle().Colors[(int)ImGuiCol.Button].W == 0, "Transparent icon control gained a filled box.");
                    DactTheme.Button("×", new(36)); ImGui.PopStyleColor();
                    BrandedWindowChrome.EndGoldCard();
                }
                ImGui.End(); ControlCenterWindow.PopTheme();
            }
            ImGui.Render();
            Check(context.ColorStack.Size == 0 && context.StyleVarStack.Size == 0, "Button styling escaped its scope.");
        }
        try
        {
            foreach (var skin in new[] { SkinCatalog.Default, SkinCatalog.Jade, SkinCatalog.Amethyst, SkinCatalog.Amber, SkinCatalog.NeonPink, SkinCatalog.Obsidian })
            {
                config.Appearance.SelectedSkin = skin; io.AddMousePosEvent(-100, -100); Frame(false); Frame(false);
                var palette = DactTheme.Palette;
                Check(normal != palette.Raised && hovered != normal && active != hovered,
                    $"{skin}: button disappeared into its card or lost distinct hover/pressed states.");
                foreach (var fill in new[] { normal, hovered, active })
                    Check(Contrast(palette.Text, fill) >= 4.5, $"{skin}: button text contrast fell below 4.5:1.");
                var before = clicks;
                io.AddMousePosEvent(buttonCenter.X, buttonCenter.Y); Frame(false);
                io.AddMouseButtonEvent(0, true); Frame(false); io.AddMouseButtonEvent(0, false); Frame(false);
                Check(clicks == before + 1, $"{skin}: normal button no longer activates exactly once.");
                io.AddMousePosEvent(disabledCenter.X, disabledCenter.Y); Frame(false);
                io.AddMouseButtonEvent(0, true); Frame(false); io.AddMouseButtonEvent(0, false); Frame(false);
                Check(clicks == before + 1, $"{skin}: disabled button became clickable.");
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"DACT-按钮-{skin}.png"));
                io.AddMousePosEvent(-100, -100); Frame(true); Frame(true);
                // Inspect the real homepage child, not a duplicate mock: at least
                // one ordinary quick action must submit the visible button fill.
                var hasFill = false;
                for (var i = 0; i < context.Windows.Size; i++)
                {
                    var child = context.Windows[i];
                    if (!(Marshal.PtrToStringUTF8((nint)child.Name) ?? "").Contains("overview-quick-actions-card")) continue;
                    for (var v = 0; v < child.DrawList.VtxBuffer.Size; v++)
                        hasFill |= child.DrawList.VtxBuffer[v].Col == ImGui.ColorConvertFloat4ToU32(normal);
                }
                Check(hasFill, $"{skin}: real homepage quick buttons did not render their distinct fill.");
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"DACT-首页-{skin}.png"));
            }
            Console.WriteLine("PASS solid skins: six actual homepages, distinct button states, text contrast, native clicks/disabled controls and balanced style scopes.");
        }
        finally { io.DisplaySize = previousSize; DactTheme.SetCurrent(new(), false, 0); }
    }
}
