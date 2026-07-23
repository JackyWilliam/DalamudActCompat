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

    public PluginLifecycle(
        IParserEngine parserEngine,
        EncounterService encounterService,
        PluginPaths paths,
        PluginConfiguration configuration,
        PluginLogger logger)
    {
        this.parserEngine = parserEngine;
        this.encounterService = encounterService;
        this.paths = paths;
        this.configuration = configuration;
        this.logger = logger;
    }

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), shutdown.Token).ConfigureAwait(false);
                paths.EnsureCreated();
                await encounterService.InitializeAsync(shutdown.Token).ConfigureAwait(false);
                if (configuration.EnableParsing && configuration.AutoStartParser)
                {
                    await parserEngine.StartAsync(shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                logger.Warning("Plugin startup was cancelled.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Plugin startup failed.");
            }
        }, shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();

        try
        {
            await parserEngine.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Parser stop failed during dispose.");
        }

        shutdown.Dispose();
    }
}
