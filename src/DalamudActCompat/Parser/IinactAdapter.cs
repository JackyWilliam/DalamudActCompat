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
    private readonly Func<string> getLogDirectory;
    private readonly IFramework framework;
    private readonly Func<EncounterModeSnapshot> getEncounterModeSnapshot;
    private readonly Func<bool> parserEnabled;
    private readonly Func<bool> overlayEnabled;
    private readonly Func<IReadOnlyList<RuntimePluginSpec>> customPlugins;
    private readonly Func<Encounter, Encounter> captureFflogsEstimates;
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly object dutySessionLock = new();
    private readonly object frameworkGameStateLock = new();
    private readonly object encounterModeTransitionLock = new();
    private readonly DutyEncounterAccumulator dutySession = new();
    private readonly DutyEncounterFolderAccumulator dutyFolder = new();
    private readonly DutyWipeTracker dutyWipeTracker = new();
    private readonly HashSet<Guid> finalizedDutySegmentIds = [];
    private readonly Queue<Guid> finalizedDutySegmentOrder = [];
    private CancellationTokenSource? activeRun;
    private ParserStatus status = ParserStatus.Disabled;
    private EncounterModeSnapshot frameworkGameState;
    private EncounterMode? accumulatedMode;
    private bool encounterCallbacksSuppressed;
    private bool frameworkUpdatesSubscribed;
    private bool disposed;

    public IinactAdapter(
        SelfHostedActRuntime actRuntime,
        PluginLogger logger,
        EncounterStateStore stateStore,
        EncounterService encounterService,
        Func<string> getLogDirectory,
        IFramework framework,
        Func<EncounterModeSnapshot> getEncounterModeSnapshot,
        Func<bool> parserEnabled,
        Func<bool> overlayEnabled,
        Func<IReadOnlyList<RuntimePluginSpec>> customPlugins,
        Func<Encounter, Encounter>? captureFflogsEstimates = null)
    {
        this.actRuntime = actRuntime;
        this.logger = logger;
        this.stateStore = stateStore;
        this.encounterService = encounterService;
        this.getLogDirectory = getLogDirectory;
        this.framework = framework;
        this.getEncounterModeSnapshot = getEncounterModeSnapshot;
        this.parserEnabled = parserEnabled;
        this.overlayEnabled = overlayEnabled;
        this.customPlugins = customPlugins;
        this.captureFflogsEstimates = captureFflogsEstimates ?? (static encounter => encounter);
        frameworkGameState = getEncounterModeSnapshot();
        dutyWipeTracker.Reset(frameworkGameState.DutyPartyWiped);
        // Subscribe only after the initial state exists so a concurrent ACT callback cannot
        // observe the default snapshot during construction.
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
            // Resolve on every start so a settings change can take effect through the existing
            // restart path without rebuilding the parser adapter and all of its subscriptions.
            RefreshFrameworkGameState();
            actRuntime.StartParser(getLogDirectory());
            SubscribeFrameworkUpdates();

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
        UnsubscribeFrameworkUpdates();
        activeRun?.Cancel();
        activeRun?.Dispose();
        activeRun = null;
        lock (encounterModeTransitionLock)
        {
            encounterCallbacksSuppressed = true;
            FinalizeAccumulatedEncounter(DateTimeOffset.UtcNow, completeFolder: true);
        }
        try
        {
            actRuntime.StopParser();
        }
        finally
        {
            lock (encounterModeTransitionLock)
            {
                encounterCallbacksSuppressed = false;
            }
        }
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
        lock (encounterModeTransitionLock)
        {
            var currentEncounterId = stateStore.GetSnapshot().Current?.Id;
            var gameState = ReadFrameworkGameState();
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
                dutyWipeTracker.Reset(gameState.DutyPartyWiped);
                accumulatedMode = EncounterModePolicy.AccumulatesSegments(gameState.Mode)
                    ? gameState.Mode
                    : null;
            }

            stateStore.ResetCurrent();
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
                UnsubscribeFrameworkUpdates();
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
        // Serializing callbacks with the Framework transition closes the narrow race where
        // the final old-mode ACT update could otherwise arrive while its accumulator closes.
        lock (encounterModeTransitionLock)
        {
            if (encounterCallbacksSuppressed)
            {
                return;
            }
            OnEncounterChangedSerialized(snapshot, finished);
        }
    }

    public bool TryStopForAccessRevocation()
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            throw new InvalidOperationException(
                "Access-revocation parser teardown must run on the framework update thread.");
        }
        if (!lifecycleLock.Wait(0))
        {
            return false;
        }

        try
        {
            if (!disposed)
            {
                // Dalamud hook lifetimes and framework callbacks share this thread boundary;
                // the cloud SSE worker must never tear them down concurrently.
                StopCore(updateStatus: true);
            }
            return true;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    private void OnEncounterChangedSerialized(ActEncounterSnapshot snapshot, bool finished)
    {
        // ACT may publish from its worker thread. Read one coherent primitive-only snapshot
        // instead of touching Dalamud services or mixing values from different framework frames.
        var gameState = ReadFrameworkGameState();
        var encounter = ActEncounterMapper.Map(snapshot) with
        {
            TerritoryId = snapshot.TerritoryId != 0
                ? snapshot.TerritoryId
                : gameState.TerritoryId,
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

        var segmentMode = snapshot.EncounterMode;
        if (EncounterModePolicy.AccumulatesSegments(segmentMode))
        {
            Encounter displayEncounter;
            lock (dutySessionLock)
            {
                // Mode transitions are finalized by the Framework callback before the ACT
                // runtime ends its old segment. Rejecting a mismatched late callback prevents
                // that old segment from reopening the just-closed accumulator.
                if (!CanAccumulateSegment(segmentMode, gameState.Mode, accumulatedMode))
                {
                    return;
                }

                accumulatedMode = segmentMode;
                displayEncounter = dutySession.Update(
                    encounter,
                    finished,
                    DateTimeOffset.UtcNow,
                    snapshot.CurrentPartyMemberIds,
                    snapshot.PartyCapacity);
            }

            // ACT may finish individual records during downtime or a phase boundary. Those
            // records remain part of the live cumulative display until the game confirms a wipe.
            stateStore.UpdateCurrent(displayEncounter);
            return;
        }

        if (gameState.Mode != EncounterMode.OpenWorld)
        {
            if (finished)
            {
                // The open-world segment that was force-ended on entry still belongs in
                // history, but it must not replace the newly active duty display.
                encounterService.QueueFinishedEncounter(encounter);
            }
            return;
        }

        if (!finished)
        {
            stateStore.UpdateCurrent(encounter);
            return;
        }

        // A finished pull remains visible until ACT publishes meaningful data for the next
        // pull. Clearing here made every meter lose the result users were still reviewing.
        stateStore.UpdateCurrent(encounter);
        encounterService.QueueFinishedEncounter(encounter);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        lock (encounterModeTransitionLock)
        {
            OnFrameworkUpdateSerialized();
        }
    }

    private void RefreshFrameworkGameState()
    {
        lock (encounterModeTransitionLock)
        {
            var gameState = getEncounterModeSnapshot();
            lock (frameworkGameStateLock)
            {
                frameworkGameState = gameState;
            }
            dutyWipeTracker.Reset(gameState.DutyPartyWiped);
        }
    }

    private void SubscribeFrameworkUpdates()
    {
        if (frameworkUpdatesSubscribed)
        {
            return;
        }

        // StartParser registers the runtime first. Registering the accumulator second lets
        // the runtime publish its final old-mode snapshot before this handler closes the mode.
        framework.Update += OnFrameworkUpdate;
        frameworkUpdatesSubscribed = true;
    }

    private void UnsubscribeFrameworkUpdates()
    {
        if (!frameworkUpdatesSubscribed)
        {
            return;
        }

        framework.Update -= OnFrameworkUpdate;
        frameworkUpdatesSubscribed = false;
    }

    private void OnFrameworkUpdateSerialized()
    {
        var previousGameState = ReadFrameworkGameState();
        var gameState = getEncounterModeSnapshot();
        lock (frameworkGameStateLock)
        {
            frameworkGameState = gameState;
        }

        if (ShouldFinalizeAccumulatedMode(previousGameState.Mode, gameState.Mode))
        {
            FinalizeAccumulatedEncounter(DateTimeOffset.UtcNow, completeFolder: true);
        }

        if (gameState.Mode == EncounterMode.DutyAttempt)
        {
            if (previousGameState.Mode != EncounterMode.DutyAttempt)
            {
                dutyWipeTracker.Reset(gameState.DutyPartyWiped);
            }
            if (dutyWipeTracker.Observe(
                    trackDutyAttempt: true,
                    gameState.InCombat,
                    gameState.DutyPartyWiped))
            {
                // Only an observed all-party death creates the next pull. Ordinary combat
                // flag drops keep accumulating exactly like the original meter behavior.
                FinalizeAccumulatedEncounter(DateTimeOffset.UtcNow, completeFolder: false);
            }
            return;
        }

        // A local eight-player party wipe never represents a 48-player field-duty wipe.
        dutyWipeTracker.Reset();
    }

    private EncounterModeSnapshot ReadFrameworkGameState()
    {
        lock (frameworkGameStateLock)
        {
            return frameworkGameState;
        }
    }

    private void FinalizeAccumulatedEncounter(DateTimeOffset endTime, bool completeFolder)
    {
        Encounter? completedPull;
        Encounter? folderSnapshot;
        lock (dutySessionLock)
        {
            RememberFinalizedSegmentsUnsafe(dutySession.SegmentIds);
            completedPull = dutySession.Complete(endTime);
            if (completedPull is not null)
            {
                completedPull = CaptureFflogsEstimatesSafely(completedPull);
                folderSnapshot = dutyFolder.Add(completedPull);
            }
            else
            {
                folderSnapshot = null;
            }

            if (completeFolder)
            {
                folderSnapshot = dutyFolder.Complete() ?? folderSnapshot;
                accumulatedMode = null;
            }
        }

        if (completedPull is not null)
        {
            stateStore.UpdateCurrent(completedPull);
        }
        if (folderSnapshot is not null)
        {
            encounterService.QueueFinishedEncounter(folderSnapshot);
        }
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

    internal static bool CanAccumulateSegment(
        EncounterMode segmentMode,
        EncounterMode gameMode,
        EncounterMode? currentAccumulatorMode)
        => EncounterModePolicy.AccumulatesSegments(segmentMode) &&
           segmentMode == gameMode &&
           (currentAccumulatorMode is null || currentAccumulatorMode == segmentMode);

    internal static bool ShouldFinalizeAccumulatedMode(
        EncounterMode previousMode,
        EncounterMode currentMode)
        => previousMode != currentMode &&
           EncounterModePolicy.AccumulatesSegments(previousMode);

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
