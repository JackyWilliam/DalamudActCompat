using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompatRepair;

internal static class NativePreview
{
    internal static unsafe void Save(string path)
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI");
        if (string.IsNullOrEmpty(library)) return;
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true);
        NativeLibrary.Load(library);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null;
            io.DisplaySize = new(760, 450); io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0xff, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            if (!io.Fonts.Build()) throw new Exception("Repair preview font failed");
            var raster = new NativeUiRasterizer(io.Fonts);
            var repaired = 0; var opened = 0;
            var window = new RepairWindow(() => opened++, () => repaired++)
            {
                IsOpen = true, Loaded = true, WrongVersionPresent = true,
                Detection = "检测到错误版本：4.4.0.0 → 0.4.4.0",
            };
            void Frame()
            {
                ImGui.NewFrame(); ImGui.SetNextWindowPos(new(25, 25)); ImGui.SetNextWindowSize(new(710, 390));
                ImGui.Begin(window.WindowName); window.Draw(); ImGui.End(); ImGui.Render();
                if (context.ColorStack.Size != 0 || context.StyleVarStack.Size != 0) throw new Exception("Repair UI leaked style");
            }
            Frame(); Frame();
            raster.Save(ImGui.GetDrawData(), path);
            // Click the actual disabled/enabled repair button at the same location.
            void Click() { io.AddMousePosEvent(210, 180); Frame(); io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame(); }
            Click(); if (repaired != 0) throw new Exception("Loaded DACT accepted repair");
            window.Loaded = false; Frame(); Click();
            if (repaired != 1) throw new Exception("Stopped DACT repair button is unreachable");
            window.Busy = true; Frame(); Click(); if (repaired != 1) throw new Exception("Concurrent repair accepted");
            window.Busy = false; window.WrongVersionPresent = false; Frame(); Click();
            if (repaired != 1) throw new Exception("Unaffected installation accepted repair");
            Console.WriteLine("PASS native Dalamud repair window and actual loaded/busy/unaffected click guards");
        }
        finally { ImGui.DestroyContext(context); }
    }
}
