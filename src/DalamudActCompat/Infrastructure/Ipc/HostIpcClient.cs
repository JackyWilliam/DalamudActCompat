using System.IO.Pipes;
using System.Text.Json;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Infrastructure.Ipc;

public sealed class HostIpcClient : IAsyncDisposable
{
    private readonly EncounterStateStore stateStore;
    private readonly PluginLogger logger;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private CancellationTokenSource? readLoopCancellation;
    private Task? readLoop;

    public HostIpcClient(EncounterStateStore stateStore, PluginLogger logger)
    {
        this.stateStore = stateStore;
        this.logger = logger;
    }

    public async Task ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopUnlockedAsync(CancellationToken.None).ConfigureAwait(false);
            readLoopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readLoop = Task.Run(() => ReadLoopAsync(pipeName, readLoopCancellation.Token), CancellationToken.None);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public void ApplySnapshot(Encounter? current, IReadOnlyList<Encounter> recent)
        => stateStore.Replace(current, recent);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lifecycleLock.Dispose();
    }

    private async Task StopUnlockedAsync(CancellationToken cancellationToken)
    {
        if (readLoopCancellation is not null)
        {
            await readLoopCancellation.CancelAsync().ConfigureAwait(false);
            readLoopCancellation.Dispose();
            readLoopCancellation = null;
        }

        if (readLoop is not null)
        {
            try
            {
                await readLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                logger.Warning($"Host IPC read loop did not stop cleanly: {ex.Message}");
            }

            readLoop = null;
        }
    }

    private async Task ReadLoopAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    logger.Warning("Host IPC pipe closed.");
                    return;
                }

                ApplyMessage(line);
            }
        }
        catch (OperationCanceledException)
        {
            logger.Information("Host IPC read loop cancelled.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Host IPC read loop failed.");
        }
    }

    private void ApplyMessage(string line)
    {
        var message = JsonSerializer.Deserialize<HostIpcMessage>(line);
        if (message is null)
        {
            logger.Warning("Host IPC received an empty message.");
            return;
        }

        if (message.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase) && message.Current is not null)
        {
            var current = HostIpcMapper.ToEncounter(message.Current);
            var recent = message.Recent?.Select(HostIpcMapper.ToEncounter).ToArray() ?? Array.Empty<Encounter>();
            ApplySnapshot(current, recent);
        }
    }
}
