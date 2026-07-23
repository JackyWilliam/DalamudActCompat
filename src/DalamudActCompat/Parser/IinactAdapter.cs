using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Processes;

namespace DalamudActCompat.Parser;

public sealed class IinactAdapter : IParserEngine
{
    private readonly HostIpcClient ipcClient;
    private readonly CompatibilityHostProcess hostProcess;
    private readonly PluginLogger logger;
    private readonly string pluginDirectory;
    private readonly object syncRoot = new();
    private CancellationTokenSource? activeRun;
    private ParserStatus status = ParserStatus.Disabled;

    public IinactAdapter(
        HostIpcClient ipcClient,
        CompatibilityHostProcess hostProcess,
        PluginLogger logger,
        string pluginDirectory)
    {
        this.ipcClient = ipcClient;
        this.hostProcess = hostProcess;
        this.logger = logger;
        this.pluginDirectory = pluginDirectory;
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
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            activeRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pipeName = $"DalamudActCompat-{Guid.NewGuid():N}";
            var hostPath = ResolveHostExecutable();

            if (hostPath is null)
            {
                SetStatus(
                    ParserState.MissingDependency,
                    "Compatibility host executable was not found.",
                    $"Expected DalamudActCompat.Host.exe under {Path.Combine(pluginDirectory, "host")}.");
                return;
            }

            await hostProcess.StartAsync(hostPath, $"--pipe {pipeName} --sample", activeRun.Token).ConfigureAwait(false);
            await ipcClient.ConnectAsync(pipeName, activeRun.Token).ConfigureAwait(false);
            SetStatus(ParserState.Running, "Compatibility host IPC bridge is running with sample snapshots.");
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        activeRun?.Cancel();
        activeRun?.Dispose();
        activeRun = null;
        await ipcClient.StopAsync(cancellationToken).ConfigureAwait(false);
        await hostProcess.StopAsync(cancellationToken).ConfigureAwait(false);
        SetStatus(ParserState.Stopped, "Parser stopped.");
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
        await hostProcess.DisposeAsync().ConfigureAwait(false);
    }

    private string? ResolveHostExecutable()
    {
        var hostDirectory = Path.Combine(pluginDirectory, "host");
        var exePath = Path.Combine(hostDirectory, "DalamudActCompat.Host.exe");
        if (File.Exists(exePath))
        {
            return exePath;
        }

        var platformExePath = Path.Combine(hostDirectory, "DalamudActCompat.Host");
        if (File.Exists(platformExePath))
        {
            return platformExePath;
        }

        var rootExePath = Path.Combine(pluginDirectory, "DalamudActCompat.Host.exe");
        if (File.Exists(rootExePath))
        {
            return rootExePath;
        }

        var rootPlatformExePath = Path.Combine(pluginDirectory, "DalamudActCompat.Host");
        if (File.Exists(rootPlatformExePath))
        {
            return rootPlatformExePath;
        }

        return null;
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
