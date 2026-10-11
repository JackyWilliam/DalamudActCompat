using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Newtonsoft.Json;

internal static partial class SkinSmokeTests
{
    private static string[] UltimateIds => SkinCatalog.All.Where(skin => skin.Ultimate).Select(skin => skin.Id).ToArray();

    // Only the active account's permanent reward unlocks these skins. Ordinary
    // preferences and sponsorship cannot grant it; signing out preserves the choice.
    private static void UltimateUnlock()
    {
        var previous = SkinCatalog.UltimateUnlocked;
        try
        {
            var ids = UltimateIds;
            Check(ids.Length == 6 && ids.Distinct().Count() == 6, "Expected six distinct ultimate skins.");
            var settings = new UiSkinSettings();
            settings.UnlockedEasterEggs.UnionWith(SkinCatalog.All.Where(skin => skin.EasterEgg).Select(skin => skin.Id));
            SkinCatalog.UltimateUnlocked = () => false;
            foreach (var id in ids)
            {
                settings.SelectedSkin = id;
                Check(!SkinCatalog.IsAvailable(id, settings, true, 99) && SkinCatalog.Resolve(settings, true, 99) == SkinCatalog.Default &&
                      settings.SelectedSkin == id, $"{id}: sponsorship or discoveries unlocked an ultimate skin, or the locked choice was lost.");
            }
            var saved = JsonConvert.DeserializeObject<UiSkinSettings>("""{"SelectedSkin":"ultimate-top","UnlockedEasterEggs":["ultimate-ucob","ultimate-top"]}""")!;
            saved.Normalize();
            Check(saved.SelectedSkin == SkinCatalog.UltimateOmega && saved.UnlockedEasterEggs.Count == 0 &&
                  SkinCatalog.Resolve(saved, true, 99) == SkinCatalog.Default, "A saved configuration granted an ultimate skin or lost its selection.");
            SkinCatalog.UltimateUnlocked = () => true;
            Check(ids.All(id => SkinCatalog.Resolve(new() { SelectedSkin = id }, true, 0) == id), "An account grant did not unlock all six ultimate skins.");
            Check(ids.All(id => SkinCatalog.Resolve(new() { SelectedSkin = id }, false, 0) == SkinCatalog.Default), "Signed-out UI inherited an account grant.");
            var palettes = ids.Select(DactTheme.For).ToArray();
            Check(palettes.All(palette => palette.Ultimate is not null) && palettes.Select(palette => palette.Accent).Distinct().Count() == 6,
                "Ultimate skins lost their style or share one accent colour.");
            Check(palettes.Count(palette => palette.Ultimate!.BackingIcon != 0) == 5 && DactTheme.For(SkinCatalog.UltimateAlexander).Ultimate!.BackingIcon == 0,
                "Five plates should use their game backing and Alexander its drawn gears.");
        }
        finally { SkinCatalog.UltimateUnlocked = previous; }
    }

    // Plates are read from the local game install in production; tests read the same
    // `_hr1.tex` files through Lumina so geometry is checked against the real art.
    private static void LoadUltimateTextures(NativeUiRasterizer raster)
    {
        if (Environment.GetEnvironmentVariable("DACT_TEST_GAME_SQPACK") is not { Length: > 0 } sqpack) return;
        using var game = new Lumina.GameData(sqpack);
        var icons = new Dictionary<uint, IDalamudTextureWrap>();
        foreach (var icon in UltimateIds.Select(id => DactTheme.For(id).Ultimate!.BackingIcon).Where(icon => icon != 0))
        {
            var texture = game.GetFile<Lumina.Data.Files.TexFile>($"ui/icon/{icon / 1000 * 1000:000000}/{icon:000000}_hr1.tex");
            Check(texture is not null, $"Ultimate plate backing is missing: {icon}");
            var rgba = texture!.ImageData.ToArray();
            for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            var wrap = new EmptyTexture(3000 + icons.Count, texture.Header.Width, texture.Header.Height);
            icons.Add(icon, wrap);
            raster.AddTexture(wrap.Handle, rgba, wrap.Width, wrap.Height);
        }
        DactTheme.UltimateAssets = new UltimateSkinAssets(icon => icons.GetValueOrDefault(icon));
    }

    private static unsafe void UltimateNative(NativeUiRasterizer raster, string? output, EmptyTexture logo)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var config = new PluginConfiguration();
        config.Meter.IsVisible = true;
        var window = (ControlCenterWindow)RuntimeHelpers.GetUninitializedObject(typeof(ControlCenterWindow));
        void Set(string name, object value) => typeof(ControlCenterWindow).GetField(name, flags)!.SetValue(window, value);
        Set("configuration", config); Set("text", new UiText(config));
        Set("parserStatus", new ParserStatus(ParserState.Running, "解析器运行正常", DateTimeOffset.UnixEpoch));
        Set("isStatusVisible", (Func<bool>)(() => true));
        Set("setStatusVisible", (Action<bool>)(_ => { }));
        Set("setMeterVisible", (Action<bool>)(_ => { }));
        Set("getGameRegionSelection", (Func<GameRegionSelection>)(() => GameRegionResolver.Resolve(GameRegionMode.Auto, "Chinese", 4)));
        Set("setGameRegionMode", (Action<GameRegionMode>)(_ => { }));
        var overview = typeof(ControlCenterWindow).GetMethod("DrawOverview", flags)!;
        var io = ImGui.GetIO(); var context = ImGui.GetCurrentContext();
        var previousSize = io.DisplaySize; var previousUnlock = SkinCatalog.UltimateUnlocked;
        // Room around the window shows how far the plate art reaches past its edges.
        io.DisplaySize = new(1560, 1000);
        var drag = new WindowDragController(); var text = new UiText(config);
        var windowMin = new Vector2(250, 110); var windowSize = new Vector2(1060, 780);
        var check = true; var buttonCenter = Vector2.Zero; var clicks = 0;
        void Frame(bool controls)
        {
            ImGui.NewFrame(); DactTheme.SetCurrent(config.Appearance, true, 3);
            using (DactTheme.PushFrame())
            {
                ControlCenterWindow.PushTheme();
                ImGui.SetNextWindowPos(windowMin); ImGui.SetNextWindowSize(windowSize);
                ImGui.PushStyleColor(ImGuiCol.WindowBg, DactTheme.Palette.Surface);
                ImGui.Begin("ultimate-control-center", DactTheme.WindowFlags(ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings));
                // Same call as ControlCenterWindow.Draw so the test exercises the production anchor.
                if (DactTheme.Palette.Ultimate is { } ultimate)
                    UltimateSkins.DrawOrnaments(ImGui.GetBackgroundDrawList(), ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize(),
                        ultimate, Math.Max(.75f, ImGui.GetFontSize() / 17f), 12.5, DactTheme.UltimateAssets);
                BrandedWindowChrome.Draw(drag, logo, text.Get("概览", "Overview"), text.Get("运行中", "Running"), DactTheme.Palette.Accent, "0.4.5.9", "ultimate",
                    helpAction: () => { }, friendsAction: () => { }, onlineFriends: 2, centerRunning: true);
                BrandedWindowChrome.DrawNavigationRail("ultimate-tabs", [text.Get("概览", "Overview"), text.Get("战斗统计", "Combat Meter"),
                    text.Get("悬浮窗", "Overlays"), text.Get("扩展", "Extensions"), text.Get("云同步", "Cloud Sync"), text.Get("设置&账号", "Settings & Account")], controls ? 3 : 0);
                ImGui.BeginChild("control-center-page-content", new Vector2(-1, ControlCenterWindow.PageContentHeight()), true);
                if (!controls) overview.Invoke(window, null);
                else
                {
                    BrandedWindowChrome.BeginGoldCard("ultimate-controls", 250);
                    DactTheme.TextColored(DactTheme.Palette.Gold, "按钮与控件");
                    if (DactTheme.Button("安装 DLL / ZIP", new(160, 36))) clicks++;
                    buttonCenter = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
                    ImGui.SameLine(); DactTheme.PushStyleColor(ImGuiCol.Button, new(.48f, .10f, .12f, 1));
                    DactTheme.Button("删除", new(100, 36)); ImGui.PopStyleColor();
                    ImGui.SameLine(); ImGui.BeginDisabled(); DactTheme.Button("不可用", new(120, 36)); ImGui.EndDisabled();
                    ImGui.SameLine(); DactTheme.SmallButton("小按钮");
                    DactTheme.Checkbox("开启时自动启动", ref check);
                    var off = false; DactTheme.Checkbox("仅在副本中显示", ref off);
                    var selected = 1; ImGui.SetNextItemWidth(300);
                    DactTheme.Combo("模板", ref selected, ["横向", "纵向", "紧凑"], 3);
                    var input = "D:/ACT/Plugins"; ImGui.SetNextItemWidth(300);
                    ImGui.InputText("路径", ref input, 260);
                    BrandedWindowChrome.EndGoldCard();
                }
                ImGui.EndChild();
                ImGui.End(); ImGui.PopStyleColor(); ControlCenterWindow.PopTheme();
            }
            ImGui.Render();
            Check(context.ColorStack.Size == 0 && context.StyleVarStack.Size == 0, $"{DactTheme.CurrentSkin}: ultimate drawing leaked style state.");
            Check(DactTheme.OpenButtonDepth == 0 && DactTheme.PlainButtonDepth == 0, $"{DactTheme.CurrentSkin}: button look counters leaked.");
        }
        // The background list only exists inside a frame; after Render inspect the
        // submitted draw data, where it is the first list ImGui emits.
        bool BackgroundUses(EmptyTexture texture)
        {
            var data = ImGui.GetDrawData();
            var list = new ImDrawListPtr(data.CmdLists[0]);
            for (var c = 0; c < list.CmdBuffer.Size; c++)
                if (list.CmdBuffer[c].TextureId.Handle == texture.Handle.Handle && list.CmdBuffer[c].ElemCount > 0) return true;
            return false;
        }
        try
        {
            SkinCatalog.UltimateUnlocked = () => true;
            foreach (var id in UltimateIds)
            {
                config.Appearance.SelectedSkin = id;
                io.AddMousePosEvent(-100, -100); Frame(false); Frame(false);
                Check(DactTheme.CurrentSkin == id, $"{id}: cleared ultimate fell back to another skin.");
                var backing = DactTheme.Palette.Ultimate!.BackingIcon;
                if (backing != 0 && DactTheme.UltimateAssets?.Icon(backing) is EmptyTexture plate)
                    Check(BackgroundUses(plate), $"{id}: plate backing was not drawn behind the control centre.");
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"ultimate-{id}-overview.png"));
                Frame(true); Frame(true);
                var before = clicks;
                io.AddMousePosEvent(buttonCenter.X, buttonCenter.Y); Frame(true);
                io.AddMouseButtonEvent(0, true); Frame(true);
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"ultimate-{id}-controls-pressed.png"));
                io.AddMouseButtonEvent(0, false); Frame(true);
                Check(clicks == before + 1, $"{id}: redrawn button no longer activates exactly once.");
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"ultimate-{id}-controls.png"));
            }
            // Without a clear the same saved choice renders the default skin and keeps the choice.
            SkinCatalog.UltimateUnlocked = () => false;
            Frame(false);
            Check(DactTheme.CurrentSkin == SkinCatalog.Default && config.Appearance.SelectedSkin == UltimateIds[^1],
                "Losing the clear did not fall back to the default skin, or erased the choice.");
            Console.WriteLine("PASS ultimate skins: six cleared skins render plates/gears, chrome, homepage, redrawn controls and balanced style scopes.");
        }
        finally
        {
            io.DisplaySize = previousSize; SkinCatalog.UltimateUnlocked = previousUnlock;
            DactTheme.SetCurrent(new(), false, 0);
        }
    }
}
