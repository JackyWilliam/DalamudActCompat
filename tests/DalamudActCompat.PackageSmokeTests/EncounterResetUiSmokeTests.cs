using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static class EncounterResetUiSmokeTests
{
    internal static unsafe void Run()
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI");
        if (string.IsNullOrEmpty(library)) return;
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true); NativeLibrary.Load(library);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DisplaySize = new(800, 500); io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0xff, 0x3000, 0x303f, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            if (!io.Fonts.Build()) throw new Exception("Reset UI font failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            var config = new PluginConfiguration(); var text = new UiText(config);
            Vector2 combo = default, input = default; float optionHeight = 0;
            void Frame()
            {
                ImGui.NewFrame();
                using (DactTheme.PushFrame())
                {
                    ControlCenterWindow.PushTheme();
                    ImGui.SetNextWindowPos(new(30, 30)); ImGui.SetNextWindowSize(new(730, 360));
                    ImGui.Begin("战斗统计", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
                    var start = ImGui.GetCursorScreenPos();
                    optionHeight = ImGui.GetTextLineHeightWithSpacing();
                    combo = start + new Vector2(100, ImGui.GetFrameHeight() / 2);
                    input = start + new Vector2(30, ImGui.GetFrameHeightWithSpacing() + ImGui.GetFrameHeight() / 2);
                    EncounterResetSelector.Draw(text, config);
                    ImGui.End(); ControlCenterWindow.PopTheme();
                }
                ImGui.Render();
            }
            void Click(Vector2 point)
            {
                io.AddMousePosEvent(point.X, point.Y); Frame(); io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();
            }
            void Select(int index)
            {
                Click(combo); Frame(); ImGuiWindowPtr popup = default;
                for (var i = 0; i < context.Windows.Size; i++)
                    if (context.Windows[i].Active && (context.Windows[i].Flags & ImGuiWindowFlags.Popup) != 0) popup = context.Windows[i];
                if (popup.Handle == null) throw new Exception("Reset mode dropdown did not open.");
                Click(popup.Pos + popup.WindowPadding + new Vector2(45, (index + .5f) * optionHeight));
            }
            foreach (var skin in new[] { SkinCatalog.Default, SkinCatalog.Eorzea })
            foreach (var scale in new[] { 1f, 1.4f })
            {
                config.Appearance.SelectedSkin = skin; DactTheme.SetCurrent(config.Appearance, true, 3); io.FontGlobalScale = scale;
                config.EncounterResetMode = EncounterResetMode.DactDefault; Frame(); Frame(); Select(1);
                if (config.EncounterResetMode != EncounterResetMode.AfterCombat) throw new Exception("Could not select the timed reset option.");
                Click(input);
                io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Frame();
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                io.AddInputCharacters("37"); Frame(); io.AddKeyEvent(ImGuiKey.Enter, true); Frame(); io.AddKeyEvent(ImGuiKey.Enter, false); Frame();
                if (config.EncounterResetSeconds != 37) throw new Exception($"Typed reset delay was not saved: {config.EncounterResetSeconds}.");
                if (Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT") is { } output)
                {
                    Directory.CreateDirectory(output); raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"reset-{skin}-{scale * 100:0}.png"));
                }
                Select(0);
                if (config.EncounterResetMode != EncounterResetMode.DactDefault || config.EncounterResetSeconds != 37)
                    throw new Exception("Returning to default lost the typed value or changed the selected mode.");
            }
            Console.WriteLine("Reset native UI: default/Eorzea at 100%/140%, mode clicks, typed custom seconds and return to default passed.");
        }
        finally { DactTheme.SetCurrent(new(), false, 0); ImGui.DestroyContext(context); }
    }
}
