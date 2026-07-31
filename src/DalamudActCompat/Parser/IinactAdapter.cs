using DalamudActCompat.ActRuntime;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Parser;

public sealed class IinactAdapter : IParserEngine
{
    private readonly SelfHostedActRuntime actRuntime;
    private readonly PluginLogger logger;
    private readonly EncounterStateStore stateStore;
    private readonly EncounterService encounterService;
    private readonly string logDirectory;
    private readonly Func<bool> parserEnabled;
    private readonly Func<bool> overlayEnabled;
    private readonly Func<IReadOnlyList<RuntimePluginSpec>> customPlugins;
    private readonly object syncRoot = new();
    private CancellationTokenSource? activeRun;
    private ParserStatus status = ParserStatus.Disabled;

    public IinactAdapter(
        SelfHostedActRuntime actRuntime,
        PluginLogger logger,
        EncounterStateStore stateStore,
        EncounterService encounterService,
        string logDirectory,
        Func<bool> parserEnabled,
        Func<bool> overlayEnabled,
        Func<IReadOnlyList<RuntimePluginSpec>> customPlugins)
    {
        this.actRuntime = actRuntime;
        this.logger = logger;
        this.stateStore = stateStore;
        this.encounterService = encounterService;
        this.logDirectory = logDirectory;
        this.parserEnabled = parserEnabled;
        this.overlayEnabled = overlayEnabled;
        this.customPlugins = customPlugins;
        actRuntime.EncounterChanged += OnEncounterChanged;
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
            if (!parserEnabled())
            {
                SetStatus(ParserState.Disabled, "FFXIV_ACT_Plugin is disabled in the embedded plugin manager.");
                return;
            }

            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            activeRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            actRuntime.StartParser(logDirectory);

            var runtimePlugins = customPlugins();
            LoadCustomPlugins(runtimePlugins.Where(MustLoadBeforeOverlay));
            if (overlayEnabled())
            {
                actRuntime.StartOverlay();
            }

            LoadCustomPlugins(runtimePlugins.Where(plugin => !MustLoadBeforeOverlay(plugin)));
            var pluginDetail = string.Join(
                Environment.NewLine,
                actRuntime.CustomPluginStatuses.Select(plugin =>
                {
                    var stages = plugin.Stages.Count == 0
                        ? string.Empty
                        : $" [{string.Join(", ", plugin.Stages.Select(stage => $"{stage.Stage}={stage.State}"))}]";
                    return $"{plugin.Id}: {plugin.Status}{stages}";
                }));
            SetStatus(
                ParserState.Running,
                actRuntime.IsOverlayRunning
                    ? "FFXIV_ACT_Plugin and OverlayPlugin are running in DalamudActCompat."
                    : "FFXIV_ACT_Plugin is running in DalamudActCompat.",
                string.IsNullOrWhiteSpace(pluginDetail) ? null : pluginDetail);
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

    private void LoadCustomPlugins(IEnumerable<RuntimePluginSpec> plugins)
    {
        foreach (var failure in actRuntime.LoadCustomPlugins(plugins))
        {
            logger.Error(failure.Error, $"ACT plugin '{failure.Id}' failed to load.");
        }
    }

    private static bool MustLoadBeforeOverlay(RuntimePluginSpec plugin)
        => string.Equals(plugin.Id, "act.foxtts", StringComparison.OrdinalIgnoreCase);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        activeRun?.Cancel();
        activeRun?.Dispose();
        activeRun = null;
        actRuntime.StopParser();
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
        actRuntime.EncounterChanged -= OnEncounterChanged;
        actRuntime.Dispose();
        await Task.CompletedTask;
    }

    private void OnEncounterChanged(ActEncounterSnapshot snapshot, bool finished)
    {
        var encounter = ActEncounterMapper.Map(snapshot);
        if (!finished)
        {
            stateStore.Replace(encounter, stateStore.GetSnapshot().Recent);
            return;
        }

        stateStore.Replace(encounter, stateStore.GetSnapshot().Recent);
        _ = encounterService.AddFinishedEncounterAsync(encounter, CancellationToken.None);
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
