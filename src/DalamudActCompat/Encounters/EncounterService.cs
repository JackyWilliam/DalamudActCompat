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
        PluginPaths paths)
    {
        this.repository = repository;
        this.stateStore = stateStore;
        this.configuration = configuration;
        this.logger = logger;
        this.paths = paths;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            recent = await repository.LoadRecentAsync(cancellationToken).ConfigureAwait(false);
            stateStore.UpdateRecent(recent);
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

    private async Task SaveAfterAsync(Task previousSave, Encounter encounter)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
            recent = recent.Prepend(encounter)
                .Take(Math.Max(1, configuration.HistoryLimit))
                .ToArray();
            stateStore.UpdateRecent(recent);
            await repository.SaveRecentAsync(recent, CancellationToken.None).ConfigureAwait(false);
            Directory.CreateDirectory(paths.EncounterLogDirectory);
            var fileName = $"{encounter.StartTime.LocalDateTime:yyyyMMdd-HHmmss}-{encounter.Id:N}.json";
            await File.WriteAllTextAsync(
                Path.Combine(paths.EncounterLogDirectory, fileName),
                JsonSerializer.Serialize(encounter, new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to save encounter history.");
        }
    }

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
            await pending.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.Warning(
                "Encounter save flush exceeded five seconds; the tracked save will continue without disposing its resources.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Encounter save flush failed during shutdown.");
        }
    }
}
