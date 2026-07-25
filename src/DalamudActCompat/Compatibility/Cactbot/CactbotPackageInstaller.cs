using System.IO.Compression;
using DalamudActCompat.Infrastructure.Storage;

namespace DalamudActCompat.Compatibility.Cactbot;

public sealed class CactbotPackageInstaller(PluginPaths paths)
{
    private const int MaximumEntryCount = 12000;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;

    public bool IsInstalled =>
        File.Exists(Path.Combine(paths.CactbotDirectory, "CactbotOverlay.dll")) &&
        File.Exists(Path.Combine(paths.CactbotDirectory, "ui", "raidboss", "raidboss.html"));

    public async Task InstallAsync(string zipPath, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(zipPath);
        if (!File.Exists(source) || !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("请选择 OverlayPlugin/cactbot 官方 Release ZIP。");
        }

        paths.EnsureCreated();
        var staging = Path.Combine(paths.PluginStagingDirectory, $"cactbot-{Guid.NewGuid():N}");
        var backup = $"{paths.CactbotDirectory}.backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
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

            if (Directory.Exists(paths.CactbotDirectory))
            {
                Directory.Move(paths.CactbotDirectory, backup);
            }

            Directory.Move(staging, paths.CactbotDirectory);
            if (Directory.Exists(backup))
            {
                await Task.Run(() => Directory.Delete(backup, true), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }

            if (!Directory.Exists(paths.CactbotDirectory) && Directory.Exists(backup))
            {
                Directory.Move(backup, paths.CactbotDirectory);
            }

            throw;
        }
    }
}
