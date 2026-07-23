using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.Encounters;

public sealed class EncounterService : IAsyncDisposable
{
    private readonly EncounterRepository repository;
    private readonly EncounterStateStore stateStore;
    private readonly PluginConfiguration configuration;
    private readonly PluginLogger logger;
    private readonly SemaphoreSlim saveLock = new(1, 1);
    private IReadOnlyList<Encounter> recent = Array.Empty<Encounter>();

    public EncounterService(
        EncounterRepository repository,
        EncounterStateStore stateStore,
        PluginConfiguration configuration,
        PluginLogger logger)
    {
        this.repository = repository;
        this.stateStore = stateStore;
        this.configuration = configuration;
        this.logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            recent = await repository.LoadRecentAsync(cancellationToken).ConfigureAwait(false);
            stateStore.Replace(stateStore.GetSnapshot().Current, recent);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to load encounter history.");
        }
    }

    public async Task AddFinishedEncounterAsync(Encounter encounter, CancellationToken cancellationToken)
    {
        await saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            recent = recent.Prepend(encounter)
                .Take(Math.Max(1, configuration.HistoryLimit))
                .ToArray();
            stateStore.Replace(stateStore.GetSnapshot().Current, recent);
            await repository.SaveRecentAsync(recent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to save encounter history.");
        }
        finally
        {
            saveLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        saveLock.Dispose();
        await ValueTask.CompletedTask;
    }
}
