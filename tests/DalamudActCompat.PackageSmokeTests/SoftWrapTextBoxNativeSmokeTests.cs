using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.UI.Kit;

// The feedback box's wrapping in real cimgui: text typed through ImGui's own input queue comes
// back without wrap breaks, Backspace at the start of a wrapped line removes the character
// before the break, Enter adds a real newline, and the byte limit holds. SoftWrapTextSmokeTests
// covers the mapping itself; this covers the callback glue (UTF-8 offsets, buffer swap, caret).
internal static class SoftWrapTextBoxNativeSmokeTests
{
    internal static unsafe void Run()
    {
        if (Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") is not { Length: > 0 } library) return;
        var nativePath = Path.Combine(AppContext.BaseDirectory, "cimgui.dll");
        try { File.Copy(library, nativePath, true); } catch (IOException) when (File.Exists(nativePath)) { }
        NativeLibrary.Load(nativePath);
        DrawFrames();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static unsafe void DrawFrames()
    {
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DisplaySize = new(600, 400); io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0x024f, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            if (!io.Fonts.Build()) throw new InvalidOperationException("Soft-wrap font atlas failed.");

            var text = string.Empty;
            var maxBytes = 4000;
            var box = Vector2.Zero;
            void Frame()
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new(10)); ImGui.SetNextWindowSize(new(300, 200));
                ImGui.Begin("soft-wrap", ImGuiWindowFlags.NoDecoration);
                SoftWrapTextBox.Draw("##box", ref text, maxBytes, new Vector2(200, 120));
                box = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * .5f;
                ImGui.End();
                ImGui.Render();
            }
            void Key(ImGuiKey key) { io.AddKeyEvent(key, true); Frame(); io.AddKeyEvent(key, false); Frame(); }
            // What Dalamud forwards from the game's text input: one character event each.
            void Type(string value) { foreach (var character in value) io.AddInputCharacter(character); }
            SoftWrapText.Layout Shown()
            {
                var states = (IDictionary)typeof(SoftWrapTextBox).GetField("States", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                var state = states.Values.Cast<object>().Single();
                return (SoftWrapText.Layout)state.GetType().GetField("Layout")!.GetValue(state)!;
            }

            Frame();
            io.AddMousePosEvent(box.X, box.Y); Frame();
            io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();

            const string chinese = "触发器不播报邮差标点没反应共享重启后恢复运行但每次报找不到语音引擎";
            Type(chinese); Frame(); Frame();
            Check(text == chinese, $"Typed Chinese came back changed: {text}");
            var layout = Shown();
            Check(layout.SoftBreaks.Length >= 2 && SoftWrapText.Strip(layout.Display, layout.SoftBreaks) == chinese,
                "The box did not wrap the typed Chinese.");

            // Home goes to the start of the last shown line, right after a wrap break; Backspace
            // there removes the real character before the break.
            Key(ImGuiKey.Home);
            Key(ImGuiKey.Backspace);
            var removed = SoftWrapText.ToText(layout.SoftBreaks[^1], layout.SoftBreaks) - 1;
            Check(text == chinese.Remove(removed, 1), $"Backspace at a wrap point: {text}");

            // The text reflowed up, so the caret (where the removed character was) is now on the
            // line above the last one: Down, then End, reaches the end of the text.
            Key(ImGuiKey.DownArrow);
            Key(ImGuiKey.End);
            Key(ImGuiKey.Enter);
            Type("Then a sentence wraps by whole words"); Frame(); Frame();
            Check(text == chinese.Remove(removed, 1) + "\nThen a sentence wraps by whole words", $"Enter or English typing: {text}");
            layout = Shown();
            Check(layout.SoftBreaks.All(at => layout.Display[at - 1] != '\n' && layout.Display[at + 1] != '\n'),
                "A wrap break was placed next to a typed newline.");

            // The UTF-8 limit is on the real text, not on the shown copy with its breaks.
            maxBytes = Encoding.UTF8.GetByteCount(text) + 6;
            Type("一二三四五"); Frame(); Frame();
            Check(Encoding.UTF8.GetByteCount(text) <= maxBytes && text.EndsWith("一二", StringComparison.Ordinal),
                $"Byte limit: {Encoding.UTF8.GetByteCount(text)} > {maxBytes}");
            Console.WriteLine("Soft-wrapped text box (native): typed Chinese/English, Backspace at a wrap point, Enter and the byte limit passed.");
        }
        finally
        {
            ImGui.DestroyContext(context);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
