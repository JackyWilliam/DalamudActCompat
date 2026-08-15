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
    private readonly Func<bool> isInCombat;
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
    private volatile bool wasInCombat;
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
        Func<bool> isInCombat,
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
        this.isInCombat = isInCombat;
        this.parserEnabled = parserEnabled;
        this.overlayEnabled = overlayEnabled;
        this.customPlugins = customPlugins;
        this.captureFflogsEstimates = captureFflogsEstimates ?? (static encounter => encounter);
        actRuntime.EncounterChanged += OnEncounterChanged;
        framework.Update += OnFrameworkUpdate;
        wasBoundByDuty = isBoundByDuty();
        wasInCombat = isInCombat();
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
        FinalizeDutyAttempt(DateTimeOffset.UtcNow, leavingDuty: true);
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

    public void ResetCurrentEncounter()
    {
        var currentEncounterId = stateStore.GetSnapshot().Current?.Id;
        lock (dutySessionLock)
        {
            RememberFinalizedSegmentsUnsafe(dutySession.SegmentIds);
            if (currentEncounterId is { } id && id != Guid.Empty)
            {
                RememberFinalizedSegmentsUnsafe([id]);
            }

            // Resetting only the UI lets the next ACT refresh republish the same totals.
            // Closing the underlying segment keeps the meter empty until a genuinely new pull.
            dutySession.Reset();
        }

        wasInCombat = isInCombat();
        stateStore.ResetCurrent();
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
        // ACT can open and immediately close an encounter for a missed action.
        // Do not let that empty snapshot replace the meter or become a history file.
        if (!HasMeaningfulActivity(encounter))
        {
            return;
        }
        // Queue every party job's ranking curve as soon as combat data appears.
        // This must not depend on whether the compact meter happens to draw that row.
        encounter = CaptureFflogsEstimatesSafely(encounter);
        lock (dutySessionLock)
        {
            if (finalizedDutySegmentIds.Contains(snapshot.Id))
            {
                return;
            }
        }

        var boundByDuty = isBoundByDuty();
        if (boundByDuty)
        {
            Encounter displayEncounter;
            Encounter? completedAttempt = null;
            var inCombat = isInCombat();
            // The framework callback owns the previous combat state; updating it here could
            // consume the wipe edge before the pull folder has been finalized.
            lock (dutySessionLock)
            {
                wasBoundByDuty = true;
                displayEncounter = dutySession.Update(
                    encounter,
                    finished,
                    DateTimeOffset.UtcNow,
                    snapshot.CurrentPartyMemberIds,
                    snapshot.PartyCapacity);
                if (finished && !inCombat)
                {
                    RememberFinalizedSegmentsUnsafe(dutySession.SegmentIds);
                    completedAttempt = dutySession.Complete(
                        encounter.EndTime ?? DateTimeOffset.UtcNow);
                }
            }

            if (completedAttempt is null)
            {
                stateStore.UpdateCurrent(displayEncounter);
                return;
            }

            completedAttempt = CaptureFflogsEstimatesSafely(completedAttempt);
            stateStore.UpdateCurrent(completedAttempt);
            encounterService.QueueFinishedEncounter(completedAttempt);
            return;
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
                    _ = dutySession.Update(
                        encounter,
                        finished,
                        DateTimeOffset.UtcNow,
                        snapshot.CurrentPartyMemberIds,
                        snapshot.PartyCapacity);
                }
            }

            FinalizeDutyAttempt(DateTimeOffset.UtcNow, leavingDuty: true);
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
        var inCombat = isInCombat();
        if (boundByDuty)
        {
            var pullEnded = wasBoundByDuty && wasInCombat && !inCombat;
            wasBoundByDuty = true;
            wasInCombat = inCombat;
            if (pullEnded)
            {
                // The game combat flag spans phase transitions, while ACT may split them.
                // Only the true in-combat -> out-of-combat edge closes the pull folder.
                FinalizeDutyAttempt(DateTimeOffset.UtcNow, leavingDuty: false);
            }
            return;
        }

        if (wasBoundByDuty)
        {
            FinalizeDutyAttempt(DateTimeOffset.UtcNow, leavingDuty: true);
        }
        wasInCombat = inCombat;
    }

    private void FinalizeDutyAttempt(DateTimeOffset endTime, bool leavingDuty)
    {
        Encounter? completed;
        lock (dutySessionLock)
        {
            if (leavingDuty)
            {
                wasBoundByDuty = false;
            }
            RememberFinalizedSegmentsUnsafe(dutySession.SegmentIds);
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

    private void RememberFinalizedSegmentsUnsafe(IEnumerable<Guid> segmentIds)
    {
        foreach (var segmentId in segmentIds)
        {
            if (segmentId == Guid.Empty || !finalizedDutySegmentIds.Add(segmentId))
            {
                continue;
            }

            finalizedDutySegmentOrder.Enqueue(segmentId);
            while (finalizedDutySegmentOrder.Count > 256)
            {
                finalizedDutySegmentIds.Remove(finalizedDutySegmentOrder.Dequeue());
            }
        }
    }

    internal static bool HasMeaningfulActivity(Encounter encounter)
        => encounter.TotalDamage > 0 ||
           encounter.TotalHealing > 0 ||
           encounter.TotalDeaths > 0;

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
