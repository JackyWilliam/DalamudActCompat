using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Processes;

namespace DalamudActCompat.Parser;

public sealed class IinactAdapter : IParserEngine
{
    private readonly HostIpcClient ipcClient;
    private readonly CompatibilityHostProcess hostProcess;
    private readonly CompatibilityHostAssets hostAssets;
    private readonly PluginLogger logger;
    private readonly string pluginDirectory;
    private readonly string extractedHostDirectory;
    private readonly object syncRoot = new();
    private CancellationTokenSource? activeRun;
    private ParserStatus status = ParserStatus.Disabled;

    public IinactAdapter(
        HostIpcClient ipcClient,
        CompatibilityHostProcess hostProcess,
        CompatibilityHostAssets hostAssets,
        PluginLogger logger,
        string pluginDirectory,
        string extractedHostDirectory)
    {
        this.ipcClient = ipcClient;
        this.hostProcess = hostProcess;
        this.hostAssets = hostAssets;
        this.logger = logger;
        this.pluginDirectory = pluginDirectory;
        this.extractedHostDirectory = extractedHostDirectory;
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
            hostAssets.EnsureExtracted();
            var host = ResolveHostLaunchSpec();

            if (host is null)
            {
                SetStatus(
                    ParserState.MissingDependency,
                    "Compatibility host executable was not found.",
                    $"Expected embedded host assets or host files under {extractedHostDirectory}.");
                return;
            }

            await hostProcess.StartAsync(host, ["--pipe", pipeName, "--sample"], activeRun.Token).ConfigureAwait(false);
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

    private HostLaunchSpec? ResolveHostLaunchSpec()
    {
        var directories = new[]
        {
            extractedHostDirectory,
            Path.Combine(pluginDirectory, "host"),
            pluginDirectory,
        };

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exePath = Path.Combine(directory, "DalamudActCompat.Host.exe");
            if (File.Exists(exePath))
            {
                return HostLaunchSpec.ForExecutable(exePath);
            }

            var platformExePath = Path.Combine(directory, "DalamudActCompat.Host");
            if (File.Exists(platformExePath))
            {
                return HostLaunchSpec.ForExecutable(platformExePath);
            }

            var dllPath = Path.Combine(directory, "DalamudActCompat.Host.dll");
            if (File.Exists(dllPath))
            {
                return HostLaunchSpec.ForDotnet(dllPath);
            }
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
