using System.IO;

namespace DalamudActCompat.Host.FflogsUpload;

// The helper has no logger of its own. It writes one line per message to standard output with a
// level prefix, and the game side forwards those lines into the plugin log (FflogsUploadHelper).
// Nothing written here may contain the account e-mail, password, user name or a server response
// body: players attach that log to feedback.
internal static class UploadLog
{
    private static readonly object Sync = new();

    internal static void Info(string message) => Write('I', message);

    internal static void Warn(string message) => Write('W', message);

    internal static void Debug(string message) => Write('D', message);

    internal static void Error(string message) => Write('E', message);

    internal static void Error(string message, Exception exception)
        => Write('E', $"{message}: {exception.GetType().Name}: {exception.GetBaseException().Message}");

    private static void Write(char level, string message)
    {
        // A line break inside a message would be read as a second, unprefixed line.
        var line = $"{level}|{message.Replace('\r', ' ').Replace('\n', ' ')}";
        lock (Sync)
        {
            try
            {
                Console.Out.WriteLine(line);
            }
            catch (IOException)
            {
                // The game closed its end; there is nobody left to read diagnostics.
            }
        }
    }
}
