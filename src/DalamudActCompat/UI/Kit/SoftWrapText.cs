using System.Text;

namespace DalamudActCompat.UI.Kit;

// Soft line wrapping for a multi-line text box. The ImGui build Dalamud ships has no word wrap
// for InputTextMultiline, so a long line ran off the right edge of the feedback box and players
// typed blind (反馈 from RaynorDing, 2026-11-12). The box is given a copy of the text with '\n'
// inserted at the wrap points; those breaks never reach the real text. Pure functions so the
// edit mapping can be tested without ImGui.
internal static class SoftWrapText
{
    // Display is the text shown in the box; SoftBreaks are the ascending indices in Display of
    // the inserted '\n'. A soft break always sits between two characters of one line, never next
    // to a typed newline, so an edit near it can be told apart from the player's own breaks.
    internal readonly record struct Layout(string Display, int[] SoftBreaks)
    {
        internal static Layout Unwrapped(string text) => new(text, []);
    }

    // Chinese breaks between any two characters; a Latin word moves to the next line whole. Spaces
    // and closing punctuation may hang past the edge so a line never starts with them.
    internal static Layout Wrap(string text, float width, Func<string, float> measure)
    {
        var display = new StringBuilder(text.Length + 16);
        var breaks = new List<int>();
        var lineStart = 0;
        var lineWidth = 0f;
        // Display index where the Latin word being typed began (-1 outside a word), and its width.
        var wordStart = -1;
        var wordWidth = 0f;
        for (var index = 0; index < text.Length;)
        {
            var length = UnitLength(text, index);
            var unit = text.Substring(index, length);
            index += length;
            if (unit == "\n")
            {
                display.Append('\n');
                lineStart = display.Length;
                lineWidth = 0;
                wordStart = -1;
                wordWidth = 0;
                continue;
            }

            var unitWidth = measure(unit);
            var word = IsWordUnit(unit);
            if (!Hangs(unit) && lineWidth + unitWidth > width && display.Length > lineStart)
            {
                if (word && wordStart > lineStart)
                {
                    breaks.Add(wordStart);
                    display.Insert(wordStart, '\n');
                    wordStart++;
                    lineStart = wordStart;
                    lineWidth = wordWidth;
                }
                else
                {
                    breaks.Add(display.Length);
                    display.Append('\n');
                    lineStart = display.Length;
                    lineWidth = 0;
                    // A word longer than a whole line carries on from here.
                    if (word) (wordStart, wordWidth) = (display.Length, 0);
                }
            }

            if (word)
            {
                if (wordStart < 0) (wordStart, wordWidth) = (display.Length, 0);
                wordWidth += unitWidth;
            }
            else
            {
                (wordStart, wordWidth) = (-1, 0);
            }
            display.Append(unit);
            lineWidth += unitWidth;
        }
        return new Layout(display.ToString(), breaks.ToArray());
    }

    // Letters and digits below the CJK blocks (accented Latin included) form words.
    private static bool IsWordUnit(string unit)
        => unit.Length == 1 && unit[0] < '⺀' && (char.IsLetterOrDigit(unit[0]) || unit[0] is '\'' or '’' or '-' or '_');

    private static bool Hangs(string unit)
        => unit.Length == 1 && " ，。、；：！？）」』》】〕〉…,.;:!?)]}".Contains(unit[0]);

    // The real text of display[start..end): the shown text without its soft breaks.
    internal static string Strip(string display, IReadOnlyList<int> softBreaks, int start = 0, int end = -1)
    {
        if (end < 0) end = display.Length;
        var result = new StringBuilder(end - start);
        var next = 0;
        while (next < softBreaks.Count && softBreaks[next] < start) next++;
        for (var index = start; index < end; index++)
        {
            if (next < softBreaks.Count && softBreaks[next] == index)
            {
                next++;
                continue;
            }
            result.Append(display[index]);
        }
        return result.ToString();
    }

    internal static int ToText(int displayIndex, IReadOnlyList<int> softBreaks)
    {
        var below = 0;
        while (below < softBreaks.Count && softBreaks[below] < displayIndex) below++;
        return displayIndex - below;
    }

    // A text position falls on the far side of any soft break there, so the caret follows a word
    // that wrapped onto the next line, as in other editors.
    internal static int ToDisplay(int textIndex, Layout layout)
    {
        var display = textIndex;
        for (var index = 0; index < layout.SoftBreaks.Length; index++)
        {
            if (layout.SoftBreaks[index] - index > textIndex) break;
            display++;
        }
        return Math.Min(display, layout.Display.Length);
    }

    // Turns an edit ImGui made to the shown text into the new real text and caret. edited is the
    // box content after the edit, cursor its caret (characters). Removing only soft breaks means
    // Backspace (or Delete) hit a wrap point: that removes the real character beyond it, or the
    // break would just come back and the key would appear dead. maxBytes keeps the UTF-8 limit the
    // box had before wrapping; typed text that would exceed it is cut from its end.
    internal static (string Text, int Cursor) ApplyEdit(Layout before, string edited, int cursor, bool deleteForward, int maxBytes)
    {
        var shown = before.Display;
        cursor = Math.Clamp(cursor, 0, edited.Length);
        // Typing, pasting and deleting leave the caret at the end of the change, so everything
        // after the caret is the unchanged tail when it matches. That settles ties in favour of
        // the caret, e.g. Enter typed just before a wrap break shows the same text as Enter
        // typed just after it.
        var suffix = 0;
        var afterCaret = edited.Length - cursor;
        if (afterCaret <= shown.Length && edited.AsSpan(cursor).SequenceEqual(shown.AsSpan(shown.Length - afterCaret)))
        {
            suffix = afterCaret;
        }
        else
        {
            while (suffix < shown.Length && suffix < edited.Length &&
                   shown[shown.Length - 1 - suffix] == edited[edited.Length - 1 - suffix])
                suffix++;
        }
        var prefix = 0;
        var shortest = Math.Min(shown.Length, edited.Length) - suffix;
        while (prefix < shortest && shown[prefix] == edited[prefix]) prefix++;

        var oldText = Strip(shown, before.SoftBreaks);
        var removedStart = ToText(prefix, before.SoftBreaks);
        var removedEnd = ToText(shown.Length - suffix, before.SoftBreaks);
        var inserted = edited.Substring(prefix, edited.Length - suffix - prefix);
        if (removedStart == removedEnd && inserted.Length == 0 && shown.Length - suffix > prefix)
        {
            if (deleteForward)
            {
                if (removedEnd < oldText.Length) removedEnd += UnitLength(oldText, removedEnd);
            }
            else if (removedStart > 0)
            {
                removedStart -= UnitLengthBefore(oldText, removedStart);
            }
            return (oldText[..removedStart] + oldText[removedEnd..], removedStart);
        }

        var head = oldText[..removedStart];
        var tail = oldText[removedEnd..];
        var room = maxBytes - Encoding.UTF8.GetByteCount(head) - Encoding.UTF8.GetByteCount(tail);
        var kept = inserted;
        while (kept.Length > 0 && Encoding.UTF8.GetByteCount(kept) > room) kept = kept[..^UnitLengthBefore(kept, kept.Length)];

        int textCursor;
        if (cursor <= prefix) textCursor = ToText(cursor, before.SoftBreaks);
        else if (cursor <= prefix + inserted.Length) textCursor = removedStart + Math.Min(cursor - prefix, kept.Length);
        else textCursor = removedStart + kept.Length + ToText(cursor - inserted.Length + (shown.Length - suffix - prefix), before.SoftBreaks) - removedEnd;
        var text = head + kept + tail;
        return (text, Math.Clamp(textCursor, 0, text.Length));
    }

    // Characters are measured and removed whole: a surrogate pair is one unit.
    private static int UnitLength(string text, int index)
        => index + 1 < text.Length && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;

    private static int UnitLengthBefore(string text, int index)
        => index >= 2 && char.IsLowSurrogate(text[index - 1]) && char.IsHighSurrogate(text[index - 2]) ? 2 : 1;
}
