using System.Diagnostics;
using System.IO.Compression;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;

namespace DalamudActCompat.Compatibility.Cactbot;

public sealed class CactbotPackageInstaller(
    PluginPaths paths,
    PluginLogger? logger = null,
    Action<string>? warningSink = null)
{
    private const int MaximumEntryCount = 12000;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const string CommittedBackupMarkerSuffix = ".committed-cleanup";
    private readonly SemaphoreSlim installGate = new(1, 1);

    internal Func<string, Task> DeleteCommittedBackupAsync { get; set; } =
        backup => Task.Run(() => Directory.Delete(backup, recursive: true));

    internal Action<string, string> MoveDirectory { get; set; } = Directory.Move;

    public bool IsInstalled =>
        File.Exists(Path.Combine(paths.CactbotDirectory, "CactbotOverlay.dll")) &&
        File.Exists(Path.Combine(paths.CactbotDirectory, "ui", "raidboss", "raidboss.html"));

    public Version? InstalledVersion
    {
        get
        {
            var assembly = Path.Combine(paths.CactbotDirectory, "CactbotOverlay.dll");
            if (!File.Exists(assembly))
            {
                return null;
            }

            var version = FileVersionInfo.GetVersionInfo(assembly).FileVersion;
            return Version.TryParse(version, out var parsed) ? parsed : null;
        }
    }

    public async Task InstallAsync(string zipPath, CancellationToken cancellationToken)
    {
        await installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InstallCoreAsync(zipPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            installGate.Release();
        }
    }

    private async Task InstallCoreAsync(string zipPath, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(zipPath);
        if (!File.Exists(source) || !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("请选择 OverlayPlugin/cactbot 官方 Release ZIP。");
        }

        Directory.CreateDirectory(paths.ConfigDirectory);
        RecoverMissingInstallationFromBackup();
        Directory.CreateDirectory(paths.PluginStagingDirectory);
        CleanupCommittedBackups();
        var staging = Path.Combine(paths.PluginStagingDirectory, $"cactbot-{Guid.NewGuid():N}");
        var backup = $"{paths.CactbotDirectory}.backup-{Guid.NewGuid():N}";
        var committed = false;
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(source);
            if (archive.Entries.Count > MaximumEntryCount ||
                archive.Entries.Sum(entry => entry.Length) > MaximumExpandedBytes)
            {
                throw new InvalidDataException("Cactbot 安装包超过安全限制。");
            }

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = entry.FullName.Replace('\\', '/');
                const string marker = "cactbot/cactbot/";
                var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                var relative = normalized[(markerIndex + marker.Length)..];
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(staging, relative));
                var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Cactbot 安装包包含不安全路径：{entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination);
                }
            }

            if (!File.Exists(Path.Combine(staging, "CactbotOverlay.dll")) ||
                !File.Exists(Path.Combine(staging, "ui", "raidboss", "raidboss.html")))
            {
                throw new InvalidDataException("这不是有效的 OverlayPlugin/cactbot Release ZIP。");
            }

            PreserveUserFiles(staging);
            if (Directory.Exists(paths.CactbotDirectory))
            {
                MoveDirectory(paths.CactbotDirectory, backup);
            }

            MoveDirectory(staging, paths.CactbotDirectory);
            committed = true;
            if (Directory.Exists(backup))
            {
                try
                {
                    File.WriteAllText(
                        GetCommittedBackupMarkerPath(backup),
                        DateTimeOffset.UtcNow.ToString("O"));
                    await DeleteCommittedBackupAsync(backup).ConfigureAwait(false);
                    File.Delete(GetCommittedBackupMarkerPath(backup));
                }
                catch (Exception ex)
                {
                    Warn(
                        $"Cactbot was installed successfully, but its backup could not be removed. " +
                        $"It will be retried on a later install: {backup}. {ex.Message}");
                }
            }
        }
        catch (Exception failure) when (!committed)
        {
            var recoveryFailures = new List<Exception>();
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
            catch (Exception ex)
            {
                recoveryFailures.Add(ex);
            }

            try
            {
                if (!Directory.Exists(paths.CactbotDirectory) && Directory.Exists(backup))
                {
                    MoveDirectory(backup, paths.CactbotDirectory);
                }
            }
            catch (Exception ex)
            {
                recoveryFailures.Add(ex);
            }

            if (recoveryFailures.Count > 0)
            {
                throw new AggregateException(
                    "Cactbot installation failed and the previous installation could not be fully restored.",
                    [failure, .. recoveryFailures]);
            }

            throw;
        }
    }

    private void RecoverMissingInstallationFromBackup()
    {
        if (Directory.Exists(paths.CactbotDirectory))
        {
            return;
        }

        foreach (var backup in EnumerateBackupsNewestFirst())
        {
            if (!IsValidInstallation(backup))
            {
                continue;
            }

            try
            {
                MoveDirectory(backup, paths.CactbotDirectory);
                File.Delete(GetCommittedBackupMarkerPath(backup));
                Warn($"Recovered Cactbot from backup after an incomplete previous replacement: {backup}.");
                return;
            }
            catch (Exception ex)
            {
                Warn($"A recoverable Cactbot backup could not be restored and was preserved: {backup}. {ex.Message}");
            }
        }
    }

    private void CleanupCommittedBackups()
    {
        if (!IsValidInstallation(paths.CactbotDirectory))
        {
            return;
        }

        foreach (var backup in EnumerateBackupsNewestFirst()
                     .Where(backup => File.Exists(GetCommittedBackupMarkerPath(backup))))
        {
            try
            {
                Directory.Delete(backup, recursive: true);
                File.Delete(GetCommittedBackupMarkerPath(backup));
            }
            catch (Exception ex)
            {
                Warn($"A stale Cactbot backup could not be removed: {backup}. {ex.Message}");
            }
        }
    }

    private static string GetCommittedBackupMarkerPath(string backup)
        => backup + CommittedBackupMarkerSuffix;

    private IEnumerable<string> EnumerateBackupsNewestFirst()
    {
        var parent = Path.GetDirectoryName(paths.CactbotDirectory)!;
        var prefix = $"{Path.GetFileName(paths.CactbotDirectory)}.backup-";
        return Directory
            .EnumerateDirectories(parent, $"{prefix}*")
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ThenByDescending(static backup => backup, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsValidInstallation(string directory)
        => File.Exists(Path.Combine(directory, "CactbotOverlay.dll")) &&
           File.Exists(Path.Combine(directory, "ui", "raidboss", "raidboss.html"));

    private void Warn(string message)
    {
        if (warningSink is not null)
        {
            warningSink(message);
            return;
        }

        if (logger is not null)
        {
            logger.Warning(message);
            return;
        }

        Trace.TraceWarning(message);
    }

    private void PreserveUserFiles(string staging)
    {
        var source = Path.Combine(paths.CactbotDirectory, "user");
        if (!Directory.Exists(source))
        {
            return;
        }

        var destinationRoot = Path.Combine(staging, "user");
        foreach (var sourceFile in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, sourceFile);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination, overwrite: true);
        }
    }
}
