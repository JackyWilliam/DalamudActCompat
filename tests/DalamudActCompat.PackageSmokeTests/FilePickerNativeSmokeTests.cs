using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

// DACT's file picker in real cimgui: the header drags the window (it once could not move: the
// centring position was set after the drag's and overrode it), only allowed extensions are
// listed, and closing answers the caller with 取消.
internal static class FilePickerNativeSmokeTests
{
    internal static unsafe void Run()
    {
        if (Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") is not { Length: > 0 } library) return;
        // The same path the managed bindings resolve, so one native ImGui is loaded.
        var nativePath = Path.Combine(AppContext.BaseDirectory, "cimgui.dll");
        try { File.Copy(library, nativePath, true); } catch (IOException) when (File.Exists(nativePath)) { }
        NativeLibrary.Load(nativePath);
        DrawFrames();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static unsafe void DrawFrames()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dact-picker-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Plugins"));
        File.WriteAllBytes(Path.Combine(folder, "PostNamazu.dll"), new byte[64]);
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "");
        File.WriteAllBytes(Path.Combine(folder, "Plugins", "Inner.dll"), new byte[64]);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DisplaySize = new(1400, 900); io.DeltaTime = 1f / 60;

            ushort* ranges = stackalloc ushort[] { 0x20, 0x024f, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            if (!io.Fonts.Build()) throw new InvalidOperationException("File picker font atlas failed.");
            using var logo = new SkinSmokeTests.EmptyTexture();
            DactTheme.SetCurrent(new(), false, 0);
            var picker = new DactFilePicker(new UiText(new PluginConfiguration()), logo);
            void Frame()
            {
                ImGui.NewFrame();
                using (DactTheme.PushFrame())
                {
                    if (picker.IsOpen)
                    {
                        // What Dalamud does from the window's Size / SizeCondition.
                        if (picker.Size is { } size) ImGui.SetNextWindowSize(size, picker.SizeCondition);
                        picker.PreDraw();
                        if (ImGui.Begin(picker.WindowName, picker.Flags)) picker.Draw();
                        ImGui.End();
                        picker.PostDraw();
                    }
                }
                ImGui.Render();
            }
            ImGuiWindowPtr Window()
            {
                var context = ImGui.GetCurrentContext();
                // The root window only: its child windows carry its name too ("…/##picker-folder").
                for (var i = 0; i < context.Windows.Size; i++)
                    if (Marshal.PtrToStringUTF8((nint)context.Windows[i].Name) is { } name &&
                        name.Contains("DalamudActCompatFilePicker", StringComparison.Ordinal) && !name.Contains('/'))
                        return context.Windows[i];
                throw new InvalidOperationException("File picker window was not drawn.");
            }

            bool? answered = null;
            picker.OpenFile("native", "选择 ACT 扩展 DLL 或 ZIP", [".dll", ".zip"], (ok, _) => answered = ok, startDirectory: folder);
            for (var i = 0; i < 20; i++) Frame();
            var entries = (System.Collections.IList)typeof(DactFilePicker).GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
            Check(entries.Count == 2, $"The picker listed {entries.Count} entries instead of the folder and the one .dll (readme.txt must be hidden).");

            // Drag by the header, past the title, in small steps like a real mouse.
            var start = Window().Pos;
            var grab = start + new Vector2(400, 24);
            io.AddMousePosEvent(grab.X, grab.Y); Frame();
            io.AddMouseButtonEvent(0, true); Frame();
            for (var step = 1; step <= 10; step++)
            {
                io.AddMousePosEvent(grab.X + step * 15, grab.Y + step * 9);
                Frame();
            }
            io.AddMouseButtonEvent(0, false); Frame(); Frame();
            var moved = Window().Pos - start;
            Check(Math.Abs(moved.X - 150) <= 3 && Math.Abs(moved.Y - 90) <= 3, $"Dragging the picker's header moved it by {moved} instead of (150, 90).");

            // Closing it (Dalamud's close hotkey or ×) answers the caller with 取消.
            picker.IsOpen = false;
            picker.OnClose();
            Check(answered == false, "Closing the picker did not answer the caller with a cancel.");
            Console.WriteLine("File picker native: header drag, extension filter and cancel answer passed.");
        }
        finally
        {
            ImGui.DestroyContext(context);
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
