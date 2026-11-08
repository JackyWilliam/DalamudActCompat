using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DalamudActCompat.Infrastructure.Processes;

// Puts files on the Windows clipboard the way Explorer's "Copy" does (CF_HDROP plus
// "Preferred DropEffect"), so Ctrl+V in a chat client or folder pastes the file itself.
// ImGui only exposes text clipboard, hence the direct Win32 calls.
internal static class FileClipboard
{
    private const uint CfHdrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const uint DropEffectCopy = 1;
    private const int DropFilesHeaderSize = 20;

    public static bool TrySetFiles(IReadOnlyList<string> paths)
    {
        var dropFiles = BuildDropFiles(paths.Select(Path.GetFullPath).ToArray());
        // Owning the clipboard with the game window: with a null owner, EmptyClipboard leaves
        // no owner and SetClipboardData is documented to fail.
        if (!TryOpen(GetOwnerWindow()))
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard() || !TrySetData(CfHdrop, dropFiles))
            {
                return false;
            }

            // Without it some targets (Explorer) treat the paste as a move of the original file.
            var preferredEffect = RegisterClipboardFormat("Preferred DropEffect");
            if (preferredEffect != 0)
            {
                _ = TrySetData(preferredEffect, BitConverter.GetBytes(DropEffectCopy));
            }

            return true;
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    // DROPFILES: pFiles (offset of the list), POINT pt, BOOL fNC, BOOL fWide, followed by
    // NUL-terminated UTF-16 paths and a final NUL.
    internal static byte[] BuildDropFiles(IReadOnlyList<string> paths)
    {
        var list = Encoding.Unicode.GetBytes(string.Concat(paths.Select(path => path + '\0')) + '\0');
        var buffer = new byte[DropFilesHeaderSize + list.Length];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), DropFilesHeaderSize);
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 4), 1);
        list.CopyTo(buffer, DropFilesHeaderSize);
        return buffer;
    }

    private static nint GetOwnerWindow()
    {
        using var process = Process.GetCurrentProcess();
        return process.MainWindowHandle;
    }

    private static bool TryOpen(nint owner)
    {
        // Another program may hold the clipboard for a moment (clipboard managers, RDP).
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    private static bool TrySetData(uint format, byte[] data)
    {
        var memory = GlobalAlloc(GmemMoveable, (nuint)data.Length);
        if (memory == nint.Zero)
        {
            return false;
        }

        var target = GlobalLock(memory);
        if (target == nint.Zero)
        {
            _ = GlobalFree(memory);
            return false;
        }

        Marshal.Copy(data, 0, target, data.Length);
        _ = GlobalUnlock(memory);
        if (SetClipboardData(format, memory) == nint.Zero)
        {
            // Ownership only passes to the system when SetClipboardData succeeds.
            _ = GlobalFree(memory);
            return false;
        }

        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);
}
