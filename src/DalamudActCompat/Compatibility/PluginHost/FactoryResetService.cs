using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using System.Runtime.ExceptionServices;

namespace DalamudActCompat.Compatibility.PluginHost;

public sealed class FactoryResetService
{
    private enum ResetStage
    {
        PreStaging,
        Staging,
        Staged,
        ApplyingDefaults,
        Committed,
    }

    private const string BackupDirectoryName = "factory-reset-backups";
    private readonly IParserEngine parserEngine;
    private readonly PluginPaths paths;
    private readonly PluginConfiguration configuration;
    private readonly PluginLogger logger;
    private readonly Action saveConfiguration;

    internal Action<string, string> StageEntry { get; set; } = Move;

    public FactoryResetService(
        IParserEngine parserEngine,
        PluginPaths paths,
        PluginConfiguration configuration,
        PluginLogger logger,
        Action saveConfiguration)
    {
        this.parserEngine = parserEngine;
        this.paths = paths;
        this.configuration = configuration;
        this.logger = logger;
        this.saveConfiguration = saveConfiguration;
    }

    public async Task<string> ResetAsync(
        CancellationToken cancellationToken,
        CancellationToken pluginShutdownToken = default)
    {
        var configurationSnapshot = configuration.CreateSnapshot();
        var parserWasRunning = parserEngine.Status.State == ParserState.Running;
        var stopAttempted = false;
        string? backupRoot = null;
        string? backupDirectory = null;
        var movedEntries = new List<(string Original, string Backup)>();
        var stage = ResetStage.PreStaging;
        var configurationWriteAttempted = false;
        try
        {
            stopAttempted = true;
            await parserEngine.StopAsync(cancellationToken).ConfigureAwait(false);
            paths.EnsureCreated();

            backupRoot = Path.Combine(paths.ConfigDirectory, BackupDirectoryName);
            backupDirectory = Path.Combine(
                backupRoot,
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(backupDirectory);
            stage = ResetStage.Staging;

            foreach (var entry in Directory.EnumerateFileSystemEntries(paths.ConfigDirectory).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(entry).Equals(BackupDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Path.Combine(backupDirectory, Path.GetFileName(entry));
                StageEntry(entry, destination);
                movedEntries.Add((entry, destination));
            }

            stage = ResetStage.Staged;
            stage = ResetStage.ApplyingDefaults;
            configuration.ResetToDefaults(paths.CombatLogDirectory);
            paths.EnsureCreated();
            cancellationToken.ThrowIfCancellationRequested();
            configurationWriteAttempted = true;
            saveConfiguration();
            stage = ResetStage.Committed;
            logger.Information($"Factory settings restored. Backup: {backupDirectory}");
            return backupDirectory;
        }
        catch (Exception failure)
        {
            var recoveryFailures = new List<Exception>();
            var configurationRestored = false;
            try
            {
                configuration.RestoreFrom(configurationSnapshot);
                configurationRestored = true;
            }
            catch (Exception ex)
            {
                recoveryFailures.Add(ex);
            }

            if (backupRoot is not null && backupDirectory is not null)
            {
                try
                {
                    RollbackFileSystem(
                        backupRoot,
                        backupDirectory,
                        movedEntries,
                        removeAppliedDefaults: stage >= ResetStage.ApplyingDefaults);
                }
                catch (Exception ex)
                {
                    recoveryFailures.Add(ex);
                }
            }

            if (configurationWriteAttempted && configurationRestored)
            {
                try
                {
                    saveConfiguration();
                }
                catch (Exception ex)
                {
                    logger.Warning(
                        "Factory-reset rollback restored the in-memory configuration, " +
                        "but the persisted Dalamud configuration may still be inconsistent.");
                    recoveryFailures.Add(new InvalidOperationException(
                        "Factory-reset rollback could not persist the restored configuration snapshot.",
                        ex));
                }
            }

            if (stopAttempted && parserWasRunning && !pluginShutdownToken.IsCancellationRequested)
            {
                try
                {
                    using var recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                        pluginShutdownToken);
                    recoveryTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await parserEngine.StartAsync(recoveryTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    recoveryFailures.Add(ex);
                }
            }

            if (recoveryFailures.Count > 0)
            {
                throw new AggregateException(
                    "Factory reset failed and one or more recovery steps also failed.",
                    [failure, .. recoveryFailures]);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private void RollbackFileSystem(
        string backupRoot,
        string backupDirectory,
        IReadOnlyList<(string Original, string Backup)> movedEntries,
        bool removeAppliedDefaults)
    {
        var failures = new List<Exception>();
        if (removeAppliedDefaults)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(paths.ConfigDirectory).ToArray())
            {
                if (Path.GetFullPath(entry).Equals(Path.GetFullPath(backupRoot), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Delete(entry);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        }

        foreach (var (original, backup) in movedEntries.Reverse())
        {
            try
            {
                if (Directory.Exists(original) || File.Exists(original))
                {
                    throw new IOException(
                        $"Factory-reset rollback refused to overwrite an existing original path: {original}");
                }
                if (Directory.Exists(backup) || File.Exists(backup))
                {
                    Move(backup, original);
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        try
        {
            if (Directory.Exists(backupDirectory) &&
                !Directory.EnumerateFileSystemEntries(backupDirectory).Any())
            {
                Directory.Delete(backupDirectory);
            }
            if (Directory.Exists(backupRoot) &&
                !Directory.EnumerateFileSystemEntries(backupRoot).Any())
            {
                Directory.Delete(backupRoot);
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Factory-reset rollback did not fully restore the original files.", failures);
        }
    }

    private static void Move(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

internal sealed class FactoryResetOperationCoordinator(
    Func<CancellationToken, Task<string>> operation)
{
    private readonly object syncRoot = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task<string>? currentTask;
    private bool shutdownStarted;

    public bool IsShutdownStarted
    {
        get
        {
            lock (syncRoot)
            {
                return shutdownStarted;
            }
        }
    }

    public CancellationToken ShutdownToken => shutdown.Token;

    public Task<string> Start()
    {
        lock (syncRoot)
        {
            if (shutdownStarted)
            {
                return Task.FromCanceled<string>(new CancellationToken(canceled: true));
            }

            if (currentTask is { IsCompleted: false })
            {
                return currentTask;
            }

            currentTask = Task.Run(
                () => operation(shutdown.Token),
                CancellationToken.None);
            return currentTask;
        }
    }

    public void BeginShutdown()
    {
        var shouldCancel = false;
        lock (syncRoot)
        {
            if (!shutdownStarted)
            {
                shutdownStarted = true;
                shouldCancel = true;
            }
        }

        if (shouldCancel)
        {
            shutdown.Cancel();
        }
    }

    public async Task<bool> WaitForShutdownAsync(TimeSpan timeout)
    {
        BeginShutdown();
        var task = GetCurrentTask();
        if (task is null)
        {
            return true;
        }

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    public async Task WaitForCompletionAsync()
    {
        var task = GetCurrentTask();
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The UI or shutdown caller reports the operation failure.
        }
    }

    private Task<string>? GetCurrentTask()
    {
        lock (syncRoot)
        {
            return currentTask;
        }
    }
}
