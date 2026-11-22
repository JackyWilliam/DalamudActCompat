using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Fflogs.Upload;

internal static class UploadLog
{
    internal static PluginLogger Logger { get; set; } = null!;
    internal static void Info(string message) => Logger?.Information(message);
    internal static void Warn(string message) => Logger?.Warning(message);
    internal static void Debug(string message) => Logger?.Debug(message, true);
    internal static void Error(string message, Exception exception) => Logger?.Error(exception, message);
    internal static void Error(string message) => Logger?.Warning(message);
}
