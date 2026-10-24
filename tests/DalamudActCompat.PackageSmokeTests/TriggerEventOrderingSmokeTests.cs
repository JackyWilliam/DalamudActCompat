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

    private sealed record RunningSupervisor(ActHostSupervisor Supervisor, HostIpcClient Client, BoundedHostMessageQueue Queue);

    // The supervisor normally reaches Running only through a real Host handshake. Setting
    // the two lifecycle fields isolates its publication order without a Host process.
    private static RunningSupervisor CreateRunningSupervisor(string root, string name)
    {
        var scope = Path.Combine(root, "trigger-ordering", name);
        var logger = new PluginLogger(DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>());
        var client = new HostIpcClient(new EncounterStateStore(), logger);
        var supervisor = new ActHostSupervisor(
            Path.Combine(scope, "host"),
            Path.Combine(scope, "plugins"),
            Path.Combine(scope, "config"),
            client,
            logger);
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
