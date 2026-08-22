using DalamudActCompat.Infrastructure.Ipc;

namespace DalamudActCompat.Infrastructure.Processes;

public enum HostMemoryProtectionState
{
    Disabled,
    Normal,
    Monitoring,
    DeferredForCombat,
    EmergencyCountdown,
    Recycling,
    Ignored,
    CircuitOpen,
}

public sealed record HostMemoryProtectionSnapshot(
    HostMemoryProtectionState State,
    long WorkingSetBytes,
    long PrivateBytes,
    long AvailablePhysicalMemoryBytes,
    DateTimeOffset? ThresholdSince,
    DateTimeOffset? CountdownEndsAt,
    string Detail)
{
    public static HostMemoryProtectionSnapshot Disabled { get; } = new(
        HostMemoryProtectionState.Disabled,
        0,
        0,
        0,
        null,
        null,
        "Memory protection is disabled for this Host.");
}

internal sealed class HostMemoryProtectionPolicy
{
    internal const long AutomaticRecycleBytes = 3L * 1024 * 1024 * 1024;
    internal const long EmergencyRecycleBytes = 4L * 1024 * 1024 * 1024;
    internal const long LowAvailableMemoryBytes = 2L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan SustainedLimitDuration = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan EmergencyCountdown = TimeSpan.FromSeconds(10);

    private readonly object syncRoot = new();
    private DateTimeOffset? thresholdSince;
    private DateTimeOffset? emergencyCountdownEndsAt;
    private bool ignored;
    private HostResourceSample lastSample = new(DateTimeOffset.MinValue, 0, 0, 0, 0);
    private HostMemoryProtectionSnapshot snapshot = new(
        HostMemoryProtectionState.Normal,
        0,
        0,
        0,
        null,
        null,
        "Shared Host memory is below the automatic protection threshold.");

    public HostMemoryProtectionSnapshot Snapshot
    {
        get
        {
            lock (syncRoot)
            {
                return snapshot;
            }
        }
    }

    public HostMemoryProtectionObservation Observe(HostResourceSample sample, bool inCombat)
    {
        // Heartbeats arrive on the pipe reader while the ignore button runs on Dalamud's
        // framework thread; one lock keeps countdown and ignore transitions atomic.
        lock (syncRoot)
        {
            lastSample = sample;
            if (ignored)
            {
                return SetSnapshot(
                    HostMemoryProtectionState.Ignored,
                    "Automatic memory recovery is ignored for the current Host session.",
                    shouldRecycle: false);
            }

            if (sample.PrivateBytes < AutomaticRecycleBytes)
            {
                thresholdSince = null;
                emergencyCountdownEndsAt = null;
                return SetSnapshot(
                    HostMemoryProtectionState.Normal,
                    "Shared Host memory is below the automatic protection threshold.",
                    shouldRecycle: false);
            }

            thresholdSince ??= sample.Timestamp;
            var lowSystemMemory = sample.AvailablePhysicalMemoryBytes > 0 &&
                                  sample.AvailablePhysicalMemoryBytes < LowAvailableMemoryBytes;
            var emergency = sample.PrivateBytes >= EmergencyRecycleBytes || lowSystemMemory;
            if (emergency)
            {
                emergencyCountdownEndsAt ??= sample.Timestamp + EmergencyCountdown;
                var shouldRecycle = sample.Timestamp >= emergencyCountdownEndsAt.Value;
                return SetSnapshot(
                    HostMemoryProtectionState.EmergencyCountdown,
                    lowSystemMemory
                        ? "System memory is low while the shared Host is above 3 GiB."
                        : "Shared Host private memory reached the 4 GiB emergency threshold.",
                    shouldRecycle);
            }

            emergencyCountdownEndsAt = null;
            if (sample.Timestamp - thresholdSince.Value < SustainedLimitDuration)
            {
                return SetSnapshot(
                    HostMemoryProtectionState.Monitoring,
                    "Shared Host private memory must remain above 3 GiB for 15 seconds.",
                    shouldRecycle: false);
            }

            if (inCombat)
            {
                return SetSnapshot(
                    HostMemoryProtectionState.DeferredForCombat,
                    "Automatic recovery is waiting for combat to end.",
                    shouldRecycle: false);
            }

            return SetSnapshot(
                HostMemoryProtectionState.Monitoring,
                "Shared Host private memory remained above 3 GiB outside combat.",
                shouldRecycle: true);
        }
    }

    public void IgnoreCurrentSession()
    {
        lock (syncRoot)
        {
            ignored = true;
            emergencyCountdownEndsAt = null;
            _ = SetSnapshot(
                HostMemoryProtectionState.Ignored,
                "Automatic memory recovery is ignored for the current Host session.",
                shouldRecycle: false);
        }
    }

    public void ResetForNewSession()
    {
        lock (syncRoot)
        {
            ignored = false;
            thresholdSince = null;
            emergencyCountdownEndsAt = null;
            snapshot = new HostMemoryProtectionSnapshot(
                HostMemoryProtectionState.Normal,
                0,
                0,
                0,
                null,
                null,
                "Shared Host memory is below the automatic protection threshold.");
        }
    }

    public void MarkRecycling(string detail)
    {
        lock (syncRoot)
        {
            snapshot = CreateSnapshot(HostMemoryProtectionState.Recycling, detail);
        }
    }

    public void MarkCircuitOpen(string detail)
    {
        lock (syncRoot)
        {
            snapshot = CreateSnapshot(HostMemoryProtectionState.CircuitOpen, detail);
        }
    }

    private HostMemoryProtectionObservation SetSnapshot(
        HostMemoryProtectionState state,
        string detail,
        bool shouldRecycle)
    {
        var previousState = snapshot.State;
        snapshot = CreateSnapshot(state, detail);
        return new HostMemoryProtectionObservation(
            snapshot,
            shouldRecycle,
            previousState != state);
    }

    private HostMemoryProtectionSnapshot CreateSnapshot(
        HostMemoryProtectionState state,
        string detail)
        => new(
            state,
            lastSample.WorkingSetBytes,
            lastSample.PrivateBytes,
            lastSample.AvailablePhysicalMemoryBytes,
            thresholdSince,
            emergencyCountdownEndsAt,
            detail);
}

internal sealed record HostMemoryProtectionObservation(
    HostMemoryProtectionSnapshot Snapshot,
    bool ShouldRecycle,
    bool StateChanged);

public sealed class HostMemoryProtectionEventArgs(
    HostMemoryProtectionSnapshot snapshot) : EventArgs
{
    public HostMemoryProtectionSnapshot Snapshot { get; } = snapshot;
}
