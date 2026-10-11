using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace DalamudActCompat.Host;

public static partial class HostPluginBridge
{
    private static string? combatLogDirectory;

    internal static void ConfigureCombatLogDirectory(string? directory)
        => Volatile.Write(ref combatLogDirectory,
            string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory));

    public static string GetUploaderConfigPath()
        // Never reuse another ACT installation's remembered FFLogs credentials or cleanup settings.
        => Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "ACTLogsUploader", "settings.xml");

    public static string GetUploaderParserCachePath()
        => Path.Combine(Path.GetDirectoryName(GetUploaderConfigPath())!, "parser");

    public static string? ResolveUploaderLogDirectory(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory) && Directory.Exists(configuredDirectory))
            return configuredDirectory;
        var directory = Volatile.Read(ref combatLogDirectory);
        // The Host's diagnostic log is not an uploadable FFXIV network log. An older
        // bridge without this path must let the user select a folder instead of guessing.
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    public static Process? OpenUploaderUrl(string url)
    {
        Demand("actlogsuploader", "LaunchExternalProcess");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host is not ("www.fflogs.com" or "cn.fflogs.com" or "fflogs.com"))
            throw new InvalidOperationException("ACTLogsUploader can only open an HTTPS FFLogs page.");
        return Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static IntPtr FindTitanGameWindow(string ignoredTitle)
    {
        DemandTitanKeys();
        var processId = FfxivRepositoryInstance.GetGameProcessId();
        if (processId <= 0) return IntPtr.Zero;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainWindowHandle;
        }
        catch (ArgumentException) { return IntPtr.Zero; }
        catch (InvalidOperationException) { return IntPtr.Zero; }
    }

    public static void SendTitanKey(IntPtr window, Keys key)
    {
        DemandTitanKeys();
        if (key < Keys.F5 || key > Keys.F12 || window == IntPtr.Zero) return;
        _ = GetWindowThreadProcessId(window, out var owner);
        // Window titles differ by region and can match an unrelated client. Recheck
        // both the exact handshake PID and grants for each delayed upstream key.
        if (owner != (uint)FfxivRepositoryInstance.GetGameProcessId() || owner == 0) return;
        _ = PostTitanMessage(window, 0x100, (IntPtr)(int)key, IntPtr.Zero);
        _ = PostTitanMessage(window, 0x101, (IntPtr)(int)key, IntPtr.Zero);
        Thread.Sleep(100);
    }

    public static void SendTitanTts(string text)
    {
        Demand("act.jsb_5", "TextToSpeech");
        SendGenericTts(text);
    }

    private static void DemandTitanKeys()
    {
        Demand("act.jsb_5", "GameCommand");
        Demand("act.jsb_5", "NativeSystemAccess");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostTitanMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
