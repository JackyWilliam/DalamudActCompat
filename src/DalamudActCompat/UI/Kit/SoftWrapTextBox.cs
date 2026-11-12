using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI.Kit;

// A multi-line text box that wraps long lines (see SoftWrapText). ImGui owns a private copy of
// the text while the box is being typed in, so the wrapped copy is rebuilt from outside only while
// the box is idle; during typing every edit is re-wrapped inside ImGui's edit callback, which is
// the one place the box's own buffer and caret can be replaced.
internal static class SoftWrapTextBox
{
    private sealed class State
    {
        public SoftWrapText.Layout Layout = SoftWrapText.Layout.Unwrapped(string.Empty);
        public string? Text;
        public float Width;
        public bool Active;
        // The selection as real text (no soft breaks), refreshed every frame for Ctrl+C / Ctrl+X.
        public string? Selection;
    }

    private static readonly Dictionary<uint, State> States = new();

    internal static bool Draw(string id, ref string text, int maxBytes, Vector2 size)
    {
        var key = ImGui.GetID(id);
        if (!States.TryGetValue(key, out var state)) States[key] = state = new State();
        var style = ImGui.GetStyle();
        // The scrollbar's width is always left free so wrapped lines never run under it.
        var width = Math.Max(1, size.X - style.FramePadding.X * 2 - style.ScrollbarSize);
        if (!state.Active && (state.Text != text || Math.Abs(state.Width - width) > .5f))
        {
            state.Text = text;
            state.Width = width;
            state.Layout = WrapToFit(text, width, Capacity(maxBytes));
        }

        var shown = state.Layout.Display;
        // No undo: ImGui records each re-wrap as an edit of its own, so Ctrl+Z would hand back
        // a version whose wrap breaks could no longer be told from typed newlines.
        ImGui.InputTextMultiline(id, ref shown, Capacity(maxBytes), size,
            ImGuiInputTextFlags.CallbackEdit | ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.NoUndoRedo,
            data => OnCallback(data, state, maxBytes));
        state.Active = ImGui.IsItemActive();
        // ImGui copies what it shows, soft breaks included; put the real text on the clipboard.
        var io = ImGui.GetIO();
        if (state.Active && io.KeyCtrl && (ImGui.IsKeyPressed(ImGuiKey.C) || ImGui.IsKeyPressed(ImGuiKey.X)) &&
            state.Selection is { Length: > 0 } selected)
            ImGui.SetClipboardText(selected);

        var changed = !string.Equals(state.Text, text, StringComparison.Ordinal);
        text = state.Text ?? string.Empty;
        return changed;
    }

    // Soft breaks are one byte each; this headroom keeps them from eating into the player's limit.
    private static int Capacity(int maxBytes) => maxBytes + maxBytes / 4 + 64;

    private static SoftWrapText.Layout WrapToFit(string text, float width, int capacity)
    {
        var layout = SoftWrapText.Wrap(text, width, unit => ImGui.CalcTextSize(unit).X);
        // A box squeezed so narrow that the breaks no longer fit falls back to one long line
        // rather than losing text.
        return Encoding.UTF8.GetByteCount(layout.Display) < capacity ? layout : SoftWrapText.Layout.Unwrapped(text);
    }

    private static unsafe int OnCallback(ImGuiInputTextCallbackDataPtr data, State state, int maxBytes)
    {
        var bytes = data.BufTextSpan;
        var shown = Encoding.UTF8.GetString(bytes);

        if (data.EventFlag == ImGuiInputTextFlags.CallbackAlways)
        {
            var start = CharIndex(bytes, Math.Min(data.SelectionStart, data.SelectionEnd));
            var end = CharIndex(bytes, Math.Max(data.SelectionStart, data.SelectionEnd));
            state.Selection = start == end ? null : SoftWrapText.Strip(shown, state.Layout.SoftBreaks, start, end);
            return 0;
        }

        var (text, cursor) = SoftWrapText.ApplyEdit(state.Layout, shown, CharIndex(bytes, data.CursorPos),
            ImGui.IsKeyDown(ImGuiKey.Delete), maxBytes);
        var layout = WrapToFit(text, state.Width, Capacity(maxBytes));
        state.Text = text;
        state.Layout = layout;
        if (!string.Equals(layout.Display, shown, StringComparison.Ordinal))
        {
            data.DeleteChars(0, data.BufTextLen);
            data.InsertChars(0, layout.Display);
        }
        var caret = Encoding.UTF8.GetByteCount(layout.Display.AsSpan(0, SoftWrapText.ToDisplay(cursor, layout)));
        data.CursorPos = caret;
        data.SelectionStart = caret;
        data.SelectionEnd = caret;
        return 0;
    }

    // ImGui reports caret and selection as UTF-8 byte offsets.
    private static int CharIndex(ReadOnlySpan<byte> bytes, int byteOffset)
        => Encoding.UTF8.GetCharCount(bytes[..Math.Clamp(byteOffset, 0, bytes.Length)]);
}
