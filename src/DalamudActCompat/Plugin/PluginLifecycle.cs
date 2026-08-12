using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;

namespace DalamudActCompat.Plugin;

internal sealed class PluginLifecycle : IAsyncDisposable
{
    private readonly IParserEngine parserEngine;
    private readonly EncounterService encounterService;
    private readonly PluginPaths paths;
    private readonly PluginConfiguration configuration;
    private readonly PluginLogger logger;
    private readonly CancellationTokenSource shutdown = new();
    private readonly TimeSpan startupDelay;
    private readonly object syncRoot = new();
    private Task startupTask = Task.CompletedTask;
    private Task? disposeTask;
    private bool started;
    private bool stopping;

    public PluginLifecycle(
        IParserEngine parserEngine,
        EncounterService encounterService,
        PluginPaths paths,
        PluginConfiguration configuration,
        PluginLogger logger,
        TimeSpan? startupDelay = null)
    {
        this.parserEngine = parserEngine;
        this.encounterService = encounterService;
        this.paths = paths;
        this.configuration = configuration;
        this.logger = logger;
        this.startupDelay = startupDelay ?? TimeSpan.FromMilliseconds(1500);
    }

    public void Start()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (started)
            {
                return;
            }

            started = true;
            // The task is owned by this lifetime so Dalamud cannot unload its assembly
            // context while startup code is still executing inside it.
            startupTask = Task.Run(() => RunStartupAsync(shutdown.Token), CancellationToken.None);
        }
    }

    public void BeginShutdown()
    {
        var shouldCancel = false;
        lock (syncRoot)
        {
            if (!stopping)
            {
                stopping = true;
                shouldCancel = true;
            }
        }

        if (shouldCancel)
        {
            shutdown.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        BeginShutdown();
        lock (syncRoot)
        {
            disposeTask ??= DisposeCoreAsync(startupTask);
            return new ValueTask(disposeTask);
        }
    }

    private async Task RunStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(startupDelay, cancellationToken).ConfigureAwait(false);
            paths.EnsureCreated();
            await encounterService.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (configuration.EnableParsing && configuration.AutoStartParser)
            {
                await parserEngine.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.Warning("Plugin startup was cancelled.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Plugin startup failed.");
        }
    }

    private async Task DisposeCoreAsync(Task trackedStartup)
    {
        try
        {
            await trackedStartup.ConfigureAwait(false);
            try
            {
                await parserEngine.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Parser stop failed during dispose.");
            }
        }
        finally
        {
            shutdown.Dispose();
        }
    }
}
