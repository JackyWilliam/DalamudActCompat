using System.Runtime.InteropServices;

namespace DalamudActCompat.Infrastructure.Processes;

internal static class GameForegroundDetector
{
    public static bool IsCurrentProcessForeground()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == nint.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        // Dalamud runs in the game process, so process equality also covers every game window.
        return foregroundProcessId == Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
