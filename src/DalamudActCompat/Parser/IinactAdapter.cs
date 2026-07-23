using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Parser;

public sealed class IinactAdapter : IParserEngine
{
    private readonly HostIpcClient ipcClient;
    private readonly PluginLogger logger;
    private readonly object syncRoot = new();
    private ParserStatus status = ParserStatus.Disabled;

    public IinactAdapter(HostIpcClient ipcClient, PluginLogger logger)
    {
        this.ipcClient = ipcClient;
        this.logger = logger;
    }

    public event EventHandler<ParserStatus>? StatusChanged;

    public ParserStatus Status
    {
        get
        {
            lock (syncRoot)
            {
                return status;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SetStatus(ParserState.Initializing, "Initializing parser host bridge.");

        try
        {
            await ipcClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(
                ParserState.MissingDependency,
                "IINACT/FFXIV_ACT_Plugin runtime is not installed yet.",
                "This build provides the safe adapter boundary and UI/storage layers. The actual parser host must be integrated next.");
        }
        catch (OperationCanceledException)
        {
            SetStatus(ParserState.Stopped, "Parser initialization cancelled.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Parser initialization failed.");
            SetStatus(ParserState.Faulted, "Parser initialization failed.", ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SetStatus(ParserState.Stopped, "Parser stopped.");
        return Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        SetStatus(ParserState.Stopped, "Parser disposed.");
        await ipcClient.DisposeAsync().ConfigureAwait(false);
    }

    private void SetStatus(ParserState state, string message, string? detail = null)
    {
        var next = new ParserStatus(state, message, DateTimeOffset.UtcNow, detail);
        lock (syncRoot)
        {
            status = next;
        }

        StatusChanged?.Invoke(this, next);
    }
}
