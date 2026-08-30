using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Plugin;
using DalamudActCompat.Infrastructure.Storage;
using System.Text.Json;

namespace DalamudActCompat.Encounters;

public sealed class EncounterService : IAsyncDisposable
{
    private readonly EncounterRepository repository;
    private readonly EncounterStateStore stateStore;
    private readonly PluginConfiguration configuration;
    private readonly PluginLogger logger;
    private readonly PluginPaths paths;
    private readonly Func<string> getCombatLogDirectory;
    private readonly object saveQueueLock = new();
    private IReadOnlyList<Encounter> recent = Array.Empty<Encounter>();
    private Task pendingSaves = Task.CompletedTask;
    private Task? disposeTask;
    private bool acceptingSaves = true;

    public EncounterService(
        EncounterRepository repository,
        EncounterStateStore stateStore,
        PluginConfiguration configuration,
        PluginLogger logger,
        PluginPaths paths,
        Func<string>? getCombatLogDirectory = null)
    {
        this.repository = repository;
        this.stateStore = stateStore;
        this.configuration = configuration;
        this.logger = logger;
        this.paths = paths;
        this.getCombatLogDirectory = getCombatLogDirectory ?? (() => paths.CombatLogDirectory);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await repository.LoadRecentAsync(cancellationToken).ConfigureAwait(false);
            recent = loaded.Take(GetRetentionLimit()).ToArray();
            stateStore.UpdateRecent(recent);
            if (recent.Count != loaded.Count)
            {
                await repository.SaveRecentAsync(recent, cancellationToken).ConfigureAwait(false);
            }

            ApplyFileRetention(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to load encounter history.");
        }
    }

    public void QueueFinishedEncounter(Encounter encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        lock (saveQueueLock)
        {
            if (!acceptingSaves)
            {
                logger.Warning("Encounter save was submitted after shutdown began and was ignored.");
                return;
            }

            pendingSaves = SaveAfterAsync(pendingSaves, encounter);
        }
    }

    public void QueueRetentionCleanup()
    {
        lock (saveQueueLock)
        {
            if (!acceptingSaves)
            {
                return;
            }

            // Retention shares the save queue so it cannot delete a snapshot while that
            // snapshot is being committed or race a history rewrite after confirmation.
            pendingSaves = ApplyRetentionAfterAsync(pendingSaves);
        }
    }

    private async Task SaveAfterAsync(Task previousSave, Encounter encounter)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
            // A duty folder is saved after every pull with one stable ID. Replacing its
            // previous snapshot keeps all attempts under one history entry.
            recent = recent
                .Where(item => item.Id != encounter.Id)
                .Prepend(encounter)
                .Take(GetRetentionLimit())
                .ToArray();
            stateStore.UpdateRecent(recent);
            await repository.SaveRecentAsync(recent, CancellationToken.None).ConfigureAwait(false);
            Directory.CreateDirectory(paths.EncounterLogDirectory);
            var fileName = $"{encounter.StartTime.LocalDateTime:yyyyMMdd-HHmmss}-{encounter.Id:N}.json";
            await File.WriteAllTextAsync(
                Path.Combine(paths.EncounterLogDirectory, fileName),
                JsonSerializer.Serialize(encounter, new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None).ConfigureAwait(false);
            ApplyFileRetention(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to save encounter history.");
        }
    }

    private async Task ApplyRetentionAfterAsync(Task previousSave)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
            var retained = recent.Take(GetRetentionLimit()).ToArray();
            if (retained.Length != recent.Count)
            {
                recent = retained;
                stateStore.UpdateRecent(recent);
                await repository.SaveRecentAsync(recent, CancellationToken.None).ConfigureAwait(false);
            }

            ApplyFileRetention(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to apply encounter and network log retention.");
        }
    }

    private void ApplyFileRetention(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = GetRetentionLimit();
        DeleteOldFiles(paths.EncounterLogDirectory, "*.json", limit, "encounter snapshot");
        DeleteOldFiles(getCombatLogDirectory(), "Network_*.log", limit, "network log");
    }

    private void DeleteOldFiles(
        string directory,
        string searchPattern,
        int limit,
        string category)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(directory)
                .EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .ThenByDescending(static file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning($"Could not enumerate {category} files for retention: {ex.Message}");
            return;
        }

        foreach (var file in files.Skip(limit))
        {
            try
            {
                // The active Network log must not disappear underneath FFXIV_ACT_Plugin.
                // Exclusive access turns a shared writer into a safe skip instead.
                using (file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }

                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warning($"Could not delete old {category} '{file.FullName}': {ex.Message}");
            }
        }
    }

    private int GetRetentionLimit()
        => Math.Clamp(configuration.HistoryLimit, 1, 200);

    public ValueTask DisposeAsync()
    {
        lock (saveQueueLock)
        {
            if (disposeTask is null)
            {
                acceptingSaves = false;
                disposeTask = FlushPendingSavesAsync(pendingSaves);
            }

            return new ValueTask(disposeTask);
        }
    }

    private async Task FlushPendingSavesAsync(Task pending)
    {
        try
        {
            // Encounter writes are serialized and owned by this service, so shutdown joins
            // the final write instead of leaving plugin code running after unload.
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Encounter save flush failed during shutdown.");
        }
    }
}
