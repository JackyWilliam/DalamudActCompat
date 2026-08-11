using System.Runtime.InteropServices;

namespace DalamudActCompat.Host;

internal static class GameForegroundDetector
{
    public static bool IsGameForeground(int gameProcessId)
    {
        if (gameProcessId <= 0)
        {
            return false;
        }

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == nint.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        return IsForegroundProcess(gameProcessId, checked((int)foregroundProcessId));
    }

    internal static bool IsForegroundProcess(int gameProcessId, int foregroundProcessId)
        => gameProcessId > 0 && gameProcessId == foregroundProcessId;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);
}
