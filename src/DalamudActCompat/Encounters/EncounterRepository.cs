using DalamudActCompat.Core.Models;
using DalamudActCompat.Infrastructure.Storage;

namespace DalamudActCompat.Encounters;

public sealed class EncounterRepository
{
    private readonly JsonFileStore jsonStore;
    private readonly PluginPaths paths;

    public EncounterRepository(JsonFileStore jsonStore, PluginPaths paths)
    {
        this.jsonStore = jsonStore;
        this.paths = paths;
    }

    public async Task<IReadOnlyList<Encounter>> LoadRecentAsync(CancellationToken cancellationToken)
    {
        var encounters = await jsonStore.ReadAsync<List<Encounter>>(paths.HistoryFile, cancellationToken).ConfigureAwait(false);
        if (encounters is null)
        {
            return Array.Empty<Encounter>();
        }

        return encounters;
    }

    public async Task SaveRecentAsync(IReadOnlyList<Encounter> encounters, CancellationToken cancellationToken)
        => await jsonStore.WriteAsync(paths.HistoryFile, encounters, cancellationToken).ConfigureAwait(false);
}
