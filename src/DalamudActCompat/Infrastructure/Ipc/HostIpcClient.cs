using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Infrastructure.Ipc;

public sealed class HostIpcClient : IAsyncDisposable
{
    private readonly EncounterStateStore stateStore;
    private readonly PluginLogger logger;

    public HostIpcClient(EncounterStateStore stateStore, PluginLogger logger)
    {
        this.stateStore = stateStore;
        this.logger = logger;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        logger.Information("Host IPC client initialized. Named pipe protocol is reserved for the IINACT/ACT compatibility host.");
        return Task.CompletedTask;
    }

    public void ApplySnapshot(Encounter? current, IReadOnlyList<Encounter> recent)
        => stateStore.Replace(current, recent);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
