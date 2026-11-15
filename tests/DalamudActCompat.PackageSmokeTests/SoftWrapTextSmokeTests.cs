using DalamudActCompat.UI.Kit;

// The feedback box wraps long lines by showing the text with inserted breaks (SoftWrapText).
// These checks pin the two halves: where lines break, and how an edit ImGui made to the shown
// text maps back to the real text and caret. Every character is one unit wide here.
internal static class SoftWrapTextSmokeTests
{
    private static float One(string unit) => 1;

    internal static void Run()
    {
        // Chinese breaks between characters; the real text never contains the inserted breaks.
        var chinese = SoftWrapText.Wrap("一二三四五六七", 3, One);
        Check(chinese.Display == "一二三\n四五六\n七" && chinese.SoftBreaks.SequenceEqual([3, 7]), "Chinese text did not wrap per character.");
        Check(SoftWrapText.Strip(chinese.Display, chinese.SoftBreaks) == "一二三四五六七", "Soft breaks leaked into the real text.");

        // A Latin word moves down whole; Chinese before it stays on its line; spaces hang.
        var mixed = SoftWrapText.Wrap("共享 Host重启", 6, One);
        Check(mixed.Display == "共享 \nHost重启", $"A Latin word was split or pulled Chinese with it: {mixed.Display}");
        var english = SoftWrapText.Wrap("hello world again", 8, One);
        Check(english.Display == "hello \nworld \nagain", $"English words did not wrap whole: {english.Display}");
        var longWord = SoftWrapText.Wrap("abcdefghij", 4, One);
        Check(longWord.Display == "abcd\nefgh\nij", "A word longer than the line was not cut.");

        // Closing punctuation hangs instead of starting a line; typed newlines stay untouched.
        var punctuation = SoftWrapText.Wrap("一二三。四", 3, One);
        Check(punctuation.Display == "一二三。\n四", $"A line started with punctuation: {punctuation.Display}");
        var hard = SoftWrapText.Wrap("一二\n三四五六", 3, One);
        Check(hard.Display == "一二\n三四五\n六" && hard.SoftBreaks.SequenceEqual([6]), "A typed newline was treated as a wrap point.");

        // Typing at the end of a full line: the character wraps and the caret follows it.
        var full = SoftWrapText.Wrap("一二三", 3, One);
        var (typed, typedCursor) = SoftWrapText.ApplyEdit(full, "一二三四", 4, false, 4000);
        var typedLayout = SoftWrapText.Wrap(typed, 3, One);
        Check(typed == "一二三四" && typedCursor == 4 && SoftWrapText.ToDisplay(typedCursor, typedLayout) == 5,
            "The caret did not follow a character that wrapped.");

        // Backspace right after a wrap point removes the character before it, not the break,
        // which would only come back and make the key look dead. Delete removes the one after.
        var wrapped = SoftWrapText.Wrap("一二三四", 3, One);
        var (backspaced, backCursor) = SoftWrapText.ApplyEdit(wrapped, "一二三四", 3, false, 4000);
        Check(backspaced == "一二四" && backCursor == 2, $"Backspace at a wrap point: {backspaced} / {backCursor}");
        var (deleted, deleteCursor) = SoftWrapText.ApplyEdit(wrapped, "一二三四", 3, true, 4000);
        Check(deleted == "一二三" && deleteCursor == 3, $"Delete at a wrap point: {deleted} / {deleteCursor}");

        // Typed newlines next to a wrap point are real; a selection across one is replaced whole.
        var (newline, newlineCursor) = SoftWrapText.ApplyEdit(wrapped, "一二三\n\n四", 4, false, 4000);
        Check(newline == "一二三\n四" && newlineCursor == 4, "A typed newline beside a wrap point was lost.");
        var five = SoftWrapText.Wrap("一二三四五", 3, One);
        var (replaced, replacedCursor) = SoftWrapText.ApplyEdit(five, "一二X五", 3, false, 4000);
        Check(replaced == "一二X五" && replacedCursor == 3, $"Replacing a selection across a wrap point: {replaced}");

        // An edit after the caret (Ctrl+Delete style) keeps the caret where it was.
        var (cut, cutCursor) = SoftWrapText.ApplyEdit(five, "一\n四五", 1, false, 4000);
        Check(cut == "一四五" && cutCursor == 1, $"Deleting after the caret moved it: {cut} / {cutCursor}");

        // The UTF-8 limit applies to the real text and trims what was just typed.
        var (limited, limitedCursor) = SoftWrapText.ApplyEdit(full, "一二三四五", 5, false, 9);
        Check(limited == "一二三" && limitedCursor == 3, "Typing past the byte limit was kept.");

        // Selection text copied out of the box comes without wrap breaks.
        Check(SoftWrapText.Strip(five.Display, five.SoftBreaks, 1, 5) == "二三四", "Copied text kept a wrap break.");
        Console.WriteLine("Soft-wrapped text box: Chinese/English/punctuation wrapping, typed newlines, caret, Backspace/Delete at wrap points, selections, byte limit and copy passed.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
