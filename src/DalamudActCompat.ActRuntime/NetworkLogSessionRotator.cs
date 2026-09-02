using System.Diagnostics;
using System.Globalization;

namespace DalamudActCompat.ActRuntime;

internal static class NetworkLogSessionRotator
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public static string BuildActiveLogPath(
        string logDirectory,
        Version logfileVersion,
        DateTime localDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentNullException.ThrowIfNull(logfileVersion);

        // FFXIV_ACT_Plugin.Logfile derives its daily filename from its own assembly
        // version. Keeping that exact rule here lets us archive the old session before
        // the upstream writer has a chance to reopen it with FileMode.Append.
        var fileName = string.Format(
            CultureInfo.InvariantCulture,
            "Network_{0}{1}{2}0{3}_{4:yyyyMMdd}.log",
            logfileVersion.Major,
            logfileVersion.Minor,
            logfileVersion.Build,
            logfileVersion.Revision,
            localDate);
        return Path.Combine(logDirectory, fileName);
    }

    public static string? RotateExisting(
        string logDirectory,
        Version logfileVersion,
        DateTime sessionStartedAt,
        TimeSpan retryTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryTimeout, TimeSpan.Zero);

        var activeLogPath = BuildActiveLogPath(logDirectory, logfileVersion, sessionStartedAt);
        if (!File.Exists(activeLogPath))
        {
            return null;
        }

        var archiveStem = string.Format(
            CultureInfo.InvariantCulture,
            "{0}_session-{1:yyyyMMdd-HHmmssfff}",
            Path.GetFileNameWithoutExtension(activeLogPath),
            sessionStartedAt);
        var startedAt = Stopwatch.GetTimestamp();
        var collisionIndex = 0;

        while (true)
        {
            var collisionSuffix = collisionIndex == 0 ? string.Empty : $"-{collisionIndex + 1}";
            var archivePath = Path.Combine(
                logDirectory,
                $"{archiveStem}{collisionSuffix}.log");
            if (File.Exists(archivePath))
            {
                collisionIndex++;
                continue;
            }

            try
            {
                File.Move(activeLogPath, archivePath);
                return archivePath;
            }
            catch (IOException) when (File.Exists(archivePath))
            {
                collisionIndex++;
            }
            catch (IOException) when (
                File.Exists(activeLogPath) &&
                Stopwatch.GetElapsedTime(startedAt) < retryTimeout)
            {
                // The previous writer exits asynchronously and can retain the handle
                // briefly. Wait only for that bounded hand-off; a persistent lock must
                // fail parser startup instead of falling back to appending.
                Thread.Sleep(RetryDelay);
            }
            catch (FileNotFoundException)
            {
                // Another owner may already have completed the same atomic rotation.
                return null;
            }
        }
    }
}
