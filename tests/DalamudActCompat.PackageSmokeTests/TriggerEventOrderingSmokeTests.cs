using System.Reflection;
using Dalamud.Plugin.Services;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Protocol;

// Game-side regressions for the order and completeness of trigger-relevant events that
// cross the Host IPC boundary. Each case reports independently for diagnosis.
internal static class TriggerEventOrderingSmokeTests
{
    internal static async Task<IReadOnlyList<string>> RunAsync(string testRoot)
    {
        var failures = new List<string>();
        async Task Case(string name, Func<Task> body)
        {
            try
            {
                await body();
                Console.WriteLine($"TRIGGER_ORDERING {name}: PASS");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"TRIGGER_ORDERING {name}: FAIL {ex.Message}");
            }
        }

        await Case("zone-follows-earlier-logs", () => ZoneFollowsEarlierLogsAsync(testRoot));
        await Case("combat-follows-earlier-logs", () => CombatFollowsEarlierLogsAsync(testRoot));
        await Case("combat-cycle-is-not-coalesced", () => CombatCycleIsNotCoalescedAsync(testRoot));
        await Case("noisy-combat-keeps-both-edges", () => NoisyCombatKeepsBothEdgesAsync(testRoot));
        await Case("ordered-state-survives-data-overflow", OrderedStateSurvivesDataOverflowAsync);
        await Case("expired-messages-are-counted", ExpiredMessagesAreCountedAsync);
        await Case("entity-epochs-order-captures-after-lines", () => { EntityEpochsOrderCapturesAfterLines(); return Task.CompletedTask; });
        await Case("log-lines-name-the-next-entity-capture", () => LogLinesNameTheNextEntityCaptureAsync(testRoot));
        return failures;
    }

    private static async Task ZoneFollowsEarlierLogsAsync(string root)
    {
        var running = CreateRunningSupervisor(root, "zone-order");
        try
        {
            running.Supervisor.PublishLog(DateTimeOffset.Now, "00|raw|LINE_BEFORE_ZONE|", "[00:00:00.000] 0 00:0000:LINE_BEFORE_ZONE", false);
            running.Supervisor.PublishZone(1238, "Zone After Line");
            var sent = await DrainAsync(running.Queue);
            var lineIndex = sent.FindIndex(envelope => envelope.Type == HostMessageTypes.LogBatch &&
                envelope.Payload.GetRawText().Contains("LINE_BEFORE_ZONE", StringComparison.Ordinal));
            var zoneIndex = sent.FindIndex(envelope => envelope.Type == HostMessageTypes.ZoneChanged);
            Require(lineIndex >= 0 && zoneIndex > lineIndex,
                $"a zone change overtook a log line produced before it; sent=[{Describe(sent)}]");
            Require(sent[zoneIndex].Deadline is null,
                "an ordered zone change can still expire and leave Triggernometry in the old zone");
        }
        finally
        {
            await StopAsync(running);
        }
    }

    private static async Task CombatFollowsEarlierLogsAsync(string root)
    {
        var running = CreateRunningSupervisor(root, "combat-order");
        try
        {
            running.Supervisor.PublishLog(DateTimeOffset.Now, "00|raw|LAST_HIT|", "[00:00:00.000] 0 00:0000:LAST_HIT", false);
            running.Supervisor.PublishEncounter(finished: false);
            var sent = await DrainAsync(running.Queue);
            var lineIndex = sent.FindIndex(envelope => envelope.Type == HostMessageTypes.LogBatch);
            var combatIndex = sent.FindIndex(envelope => envelope.Type == HostMessageTypes.CombatStarted);
            Require(lineIndex >= 0 && combatIndex > lineIndex,
                $"a combat transition overtook a log line produced before it; sent=[{Describe(sent)}]");
        }
        finally
        {
            await StopAsync(running);
        }
    }

    private static async Task CombatCycleIsNotCoalescedAsync(string root)
    {
        var running = CreateRunningSupervisor(root, "combat-cycle");
        try
        {
            // A wipe followed by an immediate re-pull while the Host reader is busy.
            running.Supervisor.PublishEncounter(finished: false);
            running.Supervisor.PublishEncounter(finished: true);
            running.Supervisor.PublishEncounter(finished: false);
            var combat = (await DrainAsync(running.Queue))
                .Where(envelope => envelope.Type is HostMessageTypes.CombatStarted or HostMessageTypes.CombatEnded)
                .Select(envelope => envelope.Type)
                .ToArray();
            Require(combat.SequenceEqual([HostMessageTypes.CombatStarted, HostMessageTypes.CombatEnded, HostMessageTypes.CombatStarted]),
                $"combat end/start was lost before reaching Triggernometry; sent=[{string.Join(",", combat)}]");
        }
        finally
        {
            await StopAsync(running);
        }
    }

    private static async Task NoisyCombatKeepsBothEdgesAsync(string root)
    {
        var running = CreateRunningSupervisor(root, "combat-noisy");
        try
        {
            for (var index = 0; index < HostProtocol.ControlQueueCapacity * 4; index++)
            {
                running.Supervisor.PublishEncounter(finished: index % 2 != 0);
            }

            var combat = (await DrainAsync(running.Queue))
                .Where(envelope => envelope.Type is HostMessageTypes.CombatStarted or HostMessageTypes.CombatEnded)
                .Select(envelope => envelope.Type)
                .ToArray();
            Require(combat.Length is > 0 and <= 3 &&
                    combat.Contains(HostMessageTypes.CombatStarted) &&
                    combat.Contains(HostMessageTypes.CombatEnded) &&
                    combat[^1] == HostMessageTypes.CombatEnded,
                $"noisy alternation was not bounded to its latest state with both edges; sent=[{string.Join(",", combat)}]");
        }
        finally
        {
            await StopAsync(running);
        }
    }

    private static async Task OrderedStateSurvivesDataOverflowAsync()
    {
        using var queue = new BoundedHostMessageQueue();
        var session = Guid.NewGuid().ToString("N");
        queue.TryEnqueue(HostEnvelope.Create(session, 1, HostMessageTypes.ZoneChanged, HostMessagePriority.Data,
            new HostZoneEvent(1238, "Kept", DateTimeOffset.UtcNow)));
        for (var index = 0; index < HostProtocol.DataQueueCapacity + 16; index++)
        {
            queue.TryEnqueue(HostEnvelope.Create(session, index + 2, HostMessageTypes.LogBatch, HostMessagePriority.Data, new { index }));
        }

        var sent = await DrainAsync(queue);
        Require(sent.Count(envelope => envelope.Type == HostMessageTypes.ZoneChanged) == 1,
            "data back-pressure discarded an ordered zone change");
    }

    private static async Task ExpiredMessagesAreCountedAsync()
    {
        using var queue = new BoundedHostMessageQueue();
        var session = Guid.NewGuid().ToString("N");
        queue.TryEnqueue(HostEnvelope.Create(session, 1, HostMessageTypes.LogBatch, HostMessagePriority.Data,
            new { value = "expired" }, deadline: DateTimeOffset.UtcNow.AddSeconds(-1)));
        queue.TryEnqueue(HostEnvelope.Create(session, 2, HostMessageTypes.LogBatch, HostMessagePriority.Data,
            new { value = "fresh" }));
        var first = (await DrainAsync(queue)).FirstOrDefault();
        Require(first?.Payload.GetProperty("value").GetString() == "fresh",
            "an expired message was handed to the writer instead of being discarded by the queue");
        Require(queue.DroppedDataMessages == 1,
            $"an expired message was discarded without being counted (dropped={queue.DroppedDataMessages})");
    }

    // Player report: Triggernometry read a tether source's position before the capture that
    // carried it had arrived. The clock is what lets the Host tell which capture a line needs.
    private static void EntityEpochsOrderCapturesAfterLines()
    {
        var clock = new HostEntityEpochClock();
        Require(!clock.IsCaptureAwaited, "a capture is awaited before any line was forwarded");
        var first = clock.BeginCapture();
        clock.MarkPublished(first);
        var line = clock.RequireNextCapture();
        Require(line == first + 1, "a line produced after a capture began must wait for the next one");
        Require(clock.RequireNextCapture() == line, "lines produced between two captures share the next capture");
        Require(clock.IsCaptureAwaited, "an unchanged object table would not be published for a waiting line");
        var second = clock.BeginCapture();
        Require(second == line, "the capture after the line does not carry the epoch the line waits for");
        // A line produced while that capture is being read cannot be covered by it.
        var during = clock.RequireNextCapture();
        Require(during == second + 1, "a line produced during a capture was promised that same capture");
        clock.MarkPublished(second);
        Require(clock.IsCaptureAwaited, "the line produced during the capture lost its pending capture");
        clock.MarkPublished(clock.BeginCapture());
        Require(!clock.IsCaptureAwaited, "idle captures keep being published after every line was satisfied");
    }

    private static async Task LogLinesNameTheNextEntityCaptureAsync(string root)
    {
        var clock = new HostEntityEpochClock();
        var running = CreateRunningSupervisor(root, "entity-epoch", clock);
        try
        {
            var applied = clock.BeginCapture();
            clock.MarkPublished(applied);
            running.Supervisor.PublishLog(DateTimeOffset.Now, "35|raw|TETHER|", "[00:00:00.000] 35 23:4001A001::10001234::0000:0000:002D:", false);
            var capture = clock.BeginCapture();
            running.Supervisor.PublishFfxivEntityDelta(new HostFfxivEntityDelta(
                1363, 0x10001234, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], [], capture));
            clock.MarkPublished(capture);
            // No Host is attached, so the flush timer is not running; an ordered state event
            // flushes the lines produced before it.
            running.Supervisor.PublishZone(1363, "Dancing Mad (Ultimate)");
            var sent = await DrainAsync(running.Queue);
            var batch = sent.FirstOrDefault(envelope => envelope.Type == HostMessageTypes.LogBatch)
                        ?? throw new InvalidOperationException($"no log batch was sent; sent=[{Describe(sent)}]");
            var required = batch.Payload.EnumerateArray().Single().GetProperty("EntityEpoch").GetInt64();
            var delta = sent.FirstOrDefault(envelope => envelope.Type == HostMessageTypes.FfxivEntityDelta)
                        ?? throw new InvalidOperationException($"an unchanged capture was not sent; sent=[{Describe(sent)}]");
            Require(required == applied + 1 && delta.Payload.GetProperty("Epoch").GetInt64() == required,
                $"the tether line waits for epoch {required}, but the capture after it was sent as {delta.Payload.GetProperty("Epoch").GetInt64()}");
        }
        finally
        {
            await StopAsync(running);
        }
    }

    private sealed record RunningSupervisor(ActHostSupervisor Supervisor, HostIpcClient Client, BoundedHostMessageQueue Queue);

    // The supervisor normally reaches Running only through a real Host handshake. Setting
    // the two lifecycle fields isolates its publication order without a Host process.
    private static RunningSupervisor CreateRunningSupervisor(string root, string name, HostEntityEpochClock? entityEpochClock = null)
    {
        var scope = Path.Combine(root, "trigger-ordering", name);
        var logger = new PluginLogger(DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>());
        var client = new HostIpcClient(new EncounterStateStore(), logger);
        var supervisor = new ActHostSupervisor(
            Path.Combine(scope, "host"),
            Path.Combine(scope, "plugins"),
            Path.Combine(scope, "config"),
            client,
            logger,
            entityEpochClock: entityEpochClock);
        SetField(client, "status", HostConnectionStatus.Connected);
        SetField(supervisor, "state", HostSupervisorState.Running);
        var queue = (BoundedHostMessageQueue)(typeof(HostIpcClient)
            .GetField("outbound", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!);
        return new RunningSupervisor(supervisor, client, queue);
    }

    private static async Task StopAsync(RunningSupervisor running)
    {
        SetField(running.Supervisor, "state", HostSupervisorState.Stopped);
        SetField(running.Client, "status", HostConnectionStatus.Stopped);
        // The supervisor owns and disposes its IPC client.
        await running.Supervisor.DisposeAsync();
    }

    private static async Task<List<HostEnvelope>> DrainAsync(BoundedHostMessageQueue queue)
    {
        var sent = new List<HostEnvelope>();
        while (true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try
            {
                sent.Add(await queue.DequeueAsync(timeout.Token));
            }
            catch (OperationCanceledException)
            {
                return sent;
            }
        }
    }

    private static string Describe(IEnumerable<HostEnvelope> envelopes)
        => string.Join(",", envelopes.Select(envelope => envelope.Type));

    private static void SetField(object target, string name, object value)
        => (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name)).SetValue(target, value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
