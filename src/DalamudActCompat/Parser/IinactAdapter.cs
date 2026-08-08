using DalamudActCompat.ActRuntime;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Logging;
using Dalamud.Plugin.Services;

namespace DalamudActCompat.Parser;

public sealed class IinactAdapter : IParserEngine
{
    private readonly SelfHostedActRuntime actRuntime;
    private readonly PluginLogger logger;
    private readonly EncounterStateStore stateStore;
    private readonly EncounterService encounterService;
    private readonly string logDirectory;
    private readonly IFramework framework;
    private readonly Func<uint> getTerritoryId;
    private readonly Func<bool> isBoundByDuty;
    private readonly Func<bool> parserEnabled;
    private readonly Func<bool> overlayEnabled;
    private readonly Func<IReadOnlyList<RuntimePluginSpec>> customPlugins;
    private readonly Func<Encounter, Encounter> captureFflogsEstimates;
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly object dutySessionLock = new();
    private readonly DutyEncounterAccumulator dutySession = new();
    private readonly HashSet<Guid> finalizedDutySegmentIds = [];
    private readonly Queue<Guid> finalizedDutySegmentOrder = [];
    private CancellationTokenSource? activeRun;
    private ParserStatus status = ParserStatus.Disabled;
    private volatile bool wasBoundByDuty;
    private bool disposed;

    public IinactAdapter(
        SelfHostedActRuntime actRuntime,
        PluginLogger logger,
        EncounterStateStore stateStore,
        EncounterService encounterService,
        string logDirectory,
        IFramework framework,
        Func<uint> getTerritoryId,
        Func<bool> isBoundByDuty,
        Func<bool> parserEnabled,
        Func<bool> overlayEnabled,
        Func<IReadOnlyList<RuntimePluginSpec>> customPlugins,
        Func<Encounter, Encounter>? captureFflogsEstimates = null)
    {
        this.actRuntime = actRuntime;
        this.logger = logger;
        this.stateStore = stateStore;
        this.encounterService = encounterService;
        this.logDirectory = logDirectory;
        this.framework = framework;
        this.getTerritoryId = getTerritoryId;
        this.isBoundByDuty = isBoundByDuty;
        this.parserEnabled = parserEnabled;
        this.overlayEnabled = overlayEnabled;
        this.customPlugins = customPlugins;
        this.captureFflogsEstimates = captureFflogsEstimates ?? (static encounter => encounter);
        actRuntime.EncounterChanged += OnEncounterChanged;
        framework.Update += OnFrameworkUpdate;
        wasBoundByDuty = isBoundByDuty();
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
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            StartCore(cancellationToken);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    private void StartCore(CancellationToken cancellationToken)
    {
        SetStatus(ParserState.Initializing, "Initializing parser host bridge.");

        try
        {
            StopCore(updateStatus: false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!parserEnabled())
            {
                SetStatus(ParserState.Disabled, "FFXIV_ACT_Plugin is disabled in the embedded plugin manager.");
                return;
            }

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
            CleanupFailedStart();
            SetStatus(ParserState.Stopped, "Parser initialization cancelled.");
        }
        catch (Exception ex)
        {
            CleanupFailedStart();
            logger.Error(ex, "Parser initialization failed.");
            SetStatus(ParserState.Faulted, "Parser initialization failed.", ex.Message);
        }
    }

    private void CleanupFailedStart()
    {
        try
        {
            StopCore(updateStatus: false);
        }
        catch (Exception cleanupError)
        {
            logger.Error(cleanupError, "Parser cleanup after failed initialization also failed.");
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
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!disposed)
            {
                StopCore(updateStatus: true);
            }
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    private void StopCore(bool updateStatus)
    {
        FinalizeDutySession(DateTimeOffset.UtcNow);
        activeRun?.Cancel();
        activeRun?.Dispose();
        activeRun = null;
        actRuntime.StopParser();
        if (updateStatus)
        {
            SetStatus(ParserState.Stopped, "Parser stopped.");
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            StartCore(cancellationToken);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                StopCore(updateStatus: false);
            }
            catch (Exception stopError)
            {
                logger.Error(stopError, "Parser stop during disposal failed.");
            }
            finally
            {
                SetStatus(ParserState.Stopped, "Parser disposed.");
                actRuntime.EncounterChanged -= OnEncounterChanged;
                framework.Update -= OnFrameworkUpdate;
                actRuntime.Dispose();
            }
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    private void OnEncounterChanged(ActEncounterSnapshot snapshot, bool finished)
    {
        var encounter = ActEncounterMapper.Map(snapshot) with
        {
            TerritoryId = getTerritoryId(),
        };
        if (finished)
        {
            encounter = CaptureFflogsEstimatesSafely(encounter);
        }
        var boundByDuty = isBoundByDuty();
        if (boundByDuty)
        {
            Encounter dutyEncounter;
            lock (dutySessionLock)
            {
                wasBoundByDuty = true;
                dutyEncounter = dutySession.Update(encounter, finished, DateTimeOffset.UtcNow);
            }
            stateStore.UpdateCurrent(dutyEncounter);
            return;
        }

        lock (dutySessionLock)
        {
            if (finalizedDutySegmentIds.Contains(snapshot.Id))
            {
                return;
            }
        }

        if (wasBoundByDuty)
        {
            var sameDutyZone = false;
            lock (dutySessionLock)
            {
                sameDutyZone = dutySession.HasData &&
                               string.Equals(
                                   dutySession.ZoneName,
                                   encounter.ZoneName,
                                   StringComparison.OrdinalIgnoreCase);
                if (sameDutyZone)
                {
                    _ = dutySession.Update(encounter, finished, DateTimeOffset.UtcNow);
                }
            }

            FinalizeDutySession(DateTimeOffset.UtcNow);
            lock (dutySessionLock)
            {
                if (sameDutyZone || finalizedDutySegmentIds.Contains(snapshot.Id))
                {
                    return;
                }
            }
        }

        if (!finished)
        {
            stateStore.UpdateCurrent(encounter);
            return;
        }

        stateStore.UpdateCurrent(encounter);
        encounterService.QueueFinishedEncounter(encounter);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var boundByDuty = isBoundByDuty();
        if (boundByDuty)
        {
            wasBoundByDuty = true;
            return;
        }

        if (wasBoundByDuty)
        {
            FinalizeDutySession(DateTimeOffset.UtcNow);
        }
    }

    private void FinalizeDutySession(DateTimeOffset endTime)
    {
        Encounter? completed;
        lock (dutySessionLock)
        {
            wasBoundByDuty = false;
            foreach (var segmentId in dutySession.SegmentIds)
            {
                if (!finalizedDutySegmentIds.Add(segmentId))
                {
                    continue;
                }

                finalizedDutySegmentOrder.Enqueue(segmentId);
                while (finalizedDutySegmentOrder.Count > 256)
                {
                    finalizedDutySegmentIds.Remove(finalizedDutySegmentOrder.Dequeue());
                }
            }
            completed = dutySession.Complete(endTime);
        }

        if (completed is null)
        {
            return;
        }

        completed = CaptureFflogsEstimatesSafely(completed);
        stateStore.UpdateCurrent(completed);
        encounterService.QueueFinishedEncounter(completed);
    }

    private Encounter CaptureFflogsEstimatesSafely(Encounter encounter)
    {
        try
        {
            return captureFflogsEstimates(encounter);
        }
        catch (Exception ex)
        {
            logger.Warning($"FFLogs estimate capture failed; saving encounter without it: {ex.Message}");
            return encounter;
        }
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
