using System.Collections.Concurrent;
using System.Reflection;
using DalamudActCompat.Host;

// Host-side regressions for trigger reliability. Each case reports independently so one
// run shows every failing mechanism instead of stopping at the first assertion.
internal static class TriggerReliabilityTests
{
    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var failures = new List<string>();
        async Task Case(string name, Func<Task> body)
        {
            try
            {
                await body();
                Console.WriteLine($"TRIGGER_RELIABILITY {name}: PASS");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"TRIGGER_RELIABILITY {name}: FAIL {ex.Message}");
            }
        }

        await Case("tts-authorized-request-survives-slow-writer", AuthorizedTtsSurvivesSlowWriterAsync);
        await Case("triggernometry-event-drops-are-observable", () =>
        {
            TriggernometryEventDropsAreObservable();
            return Task.CompletedTask;
        });
        await Case("referenced-entity-waits-for-snapshot", () =>
        {
            ReferencedEntityWaitsForSnapshot();
            return Task.CompletedTask;
        });
        await Case("log-time-follows-game-time", () =>
        {
            LogTimeFollowsGameTime();
            return Task.CompletedTask;
        });
        await Case("log-time-corrects-host-clock-offset", () =>
        {
            LogTimeCorrectsHostClockOffset();
            return Task.CompletedTask;
        });
        await Case("startup-replay-discards-stale-logs", () =>
        {
            StartupReplayDiscardsStaleLogs();
            return Task.CompletedTask;
        });
        return failures;
    }

    private static void ReferencedEntityWaitsForSnapshot()
    {
        uint zone = 1238;
        long clock = 0;
        var seen = new HashSet<uint> { 0x10001234 };
        var delivered = new List<string>();
        var warnings = new List<string>();
        var gate = new TriggernometryActorControlGate(() => zone, _ => false,
            (_, line, _) => delivered.Add(line), warnings.Add, () => clock, entitySeen: seen.Contains);
        const string cast = "[20:00:00.000] 20 14:40001234:Boss:9C1A:Mechanic:10001234:Player:4.7:";
        const string follow = "[20:00:00.010] 0 00:0000:follow-up";
        gate.Accept(false, cast, "Z");
        gate.Accept(false, follow, "Z");
        Require(delivered.Count == 0, "a line referencing an unseen actor was evaluated before its snapshot");
        clock = 40;
        seen.Add(0x40001234);
        gate.Pump();
        Require(delivered.SequenceEqual([cast, follow]) && !gate.HasPending,
            "the actor snapshot did not release the original ordered lines exactly once");

        delivered.Clear();
        const string ghost = "[20:00:01.000] 21 15:4000DEAD:Ghost:9C1B:Hit:10001234:Player:";
        gate.Accept(false, ghost, "Z");
        clock = 40 + TriggernometryActorControlGate.MaximumEntityWaitMilliseconds - 1;
        gate.Pump();
        Require(delivered.Count == 0, "the entity wait expired early");
        clock = 40 + TriggernometryActorControlGate.MaximumEntityWaitMilliseconds;
        gate.Pump();
        Require(delivered.SequenceEqual([ghost]) && warnings.Count == 1 && !gate.HasPending,
            "an actor that never appears must release the backlog after one bounded wait");
        gate.Accept(false, ghost, "Z");
        gate.Accept(false, "[20:00:02.000] 4 04:4000BEEF:Gone:", "Z");
        gate.Accept(false, "[20:00:02.100] 0 00:0000:10005678 player only", "Z");
        Require(delivered.Count == 4 && !gate.HasPending,
            "expired actors, removal lines, and player-only lines must not wait");

        zone = 1239;
        gate.Accept(false, ghost, "Z");
        Require(delivered.Count == 4 && gate.HasPending, "expired-actor history must not carry into another zone");
        zone = 1240;
        gate.Pump();
        Require(delivered.Count == 4 && !gate.HasPending, "a territory change must discard lines held for old actors");
    }

    private static void LogTimeFollowsGameTime()
    {
        var now = new DateTime(2026, 10, 24, 0, 0, 30, DateTimeKind.Local);
        // A synced clock: packet lines arrive with no measurable offset.
        var clock = new GameClockOffset();
        clock.Observe(new DateTimeOffset(now), new DateTimeOffset(now));
        DateTime Resolve(string text) => HostPluginBridge.ResolveLogTime(text, now, clock);
        Require(Resolve("[00:00:29.250] 0 00:0000:x") == now.Date + new TimeSpan(0, 0, 0, 29, 250),
            "the ACT line time was not used");
        Require(Resolve("[23:59:59.900] 0 00:0000:x") == new DateTime(2026, 10, 23, 23, 59, 59, 900, DateTimeKind.Local),
            "a line logged before midnight was dated tomorrow");
        var network = new DateTimeOffset(now.AddSeconds(-2));
        Require(Resolve($"00|{network:yyyy-MM-ddTHH:mm:ss.fffffffzzz}|0000|x|") == now.AddSeconds(-2),
            "the network line time was not used");
        Require(Resolve("[00:00:40.000] 0 00:0000:x") == now, "a future time was not clamped to the Host clock");
        Require(Resolve("[23:00:00.000] 0 00:0000:x") == now && Resolve("OnCombatStart") == now,
            "implausible or missing times must keep receipt-time semantics");

        HostPluginBridge.BeginTriggernometryFire(now.AddSeconds(-3));
        Require(HostPluginBridge.TriggernometryFireNow() == now.AddSeconds(-3),
            "a fire did not use the event time recorded by TestTrigger");
        var otherThread = Task.Run(HostPluginBridge.TriggernometryFireNow).GetAwaiter().GetResult();
        Require((DateTime.Now - otherThread).Duration() < TimeSpan.FromSeconds(1),
            "another thread's fire inherited an unrelated event time");
        HostPluginBridge.BeginTriggernometryFire(default);
        Require((DateTime.Now - HostPluginBridge.TriggernometryFireNow()).Duration() < TimeSpan.FromSeconds(1),
            "an event without a time did not fall back to the Host clock");
    }

    private static void LogTimeCorrectsHostClockOffset()
    {
        // The reported M12S machine ran 8.5 s ahead of the game server. Every packet line then
        // looked 8.5 s old, so each trigger delay up to 8.5 s fired the moment its line arrived.
        var now = new DateTime(2026, 10, 29, 20, 29, 57, DateTimeKind.Local);
        var ahead = TimeSpan.FromMilliseconds(8_520);
        DateTime Resolve(GameClockOffset clock, DateTime stamped, string body)
            => HostPluginBridge.ResolveLogTime($"[{stamped:HH:mm:ss.fff}] {body}", now, clock);
        DalamudActCompat.Protocol.HostLogEvent Log(string type, DateTimeOffset stamped)
            => new(stamped, $"{type}|{stamped:yyyy-MM-ddTHH:mm:ss.fffffffzzz}|10001234|Player|", false);
        const string packet = "21 15:10001234:Player:9C1A:Hit:10005678:Other:";
        const string chat = "0 00:0038::h1";

        var unmeasured = new GameClockOffset();
        Require(Resolve(unmeasured, now.AddSeconds(-1), packet) == now.AddSeconds(-1),
            "before any packet line was measured, the line time must keep its previous meaning");

        var clock = new GameClockOffset();
        clock.Observe(
            [
                Log("21", new DateTimeOffset(now - ahead)),
                Log("00", new DateTimeOffset(now)),
                Log("26", new DateTimeOffset(now).AddTicks(1)),
            ],
            new DateTimeOffset(now));
        Require(clock.Offset == ahead, $"chat or local-clock lines were taken as the packet clock offset: {clock.Offset}");
        Require(Resolve(clock, now - ahead, packet) == now, "a packet line that just arrived was dated in the past");
        Require(Resolve(clock, now - ahead - TimeSpan.FromMilliseconds(300), packet) == now.AddMilliseconds(-300),
            "a packet line held 300 ms on the Host lost that compensation");
        Require(Resolve(clock, now.AddMilliseconds(-100), chat) == now.AddMilliseconds(-100),
            "a line stamped with the Host clock was shifted by the packet offset");
        Require(Resolve(clock, now - ahead - TimeSpan.FromSeconds(60), packet) == now.AddSeconds(-60),
            "a genuinely old packet line was dated as live");

        var behind = new GameClockOffset();
        behind.Observe(new DateTimeOffset(now.AddSeconds(5)), new DateTimeOffset(now));
        Require(Resolve(behind, now.AddSeconds(5).AddMilliseconds(-200), packet) == now.AddMilliseconds(-200),
            "a Host clock running behind discarded the 200 ms a packet line waited");
        Require(Resolve(behind, now.AddMilliseconds(-100), chat) == now.AddMilliseconds(-100),
            "a Host-clock line was shifted when the Host clock runs behind");

        // Syncing the clock mid-session steps it back by the offset and is adopted at once; a
        // step forward is adopted once the older half-window has aged out.
        var stepped = new GameClockOffset();
        stepped.Observe(new DateTimeOffset(now - ahead), new DateTimeOffset(now));
        var synced = new DateTimeOffset(now.AddSeconds(16));
        stepped.Observe(synced, synced);
        Require(stepped.Offset == TimeSpan.Zero, "a corrected Host clock kept the old offset");
        foreach (var seconds in new[] { 32, 48 })
        {
            var at = new DateTimeOffset(now.AddSeconds(seconds));
            stepped.Observe(at - ahead, at);
        }

        Require(stepped.Offset == ahead, $"a Host clock stepped forward was never adopted: {stepped.Offset}");
    }

    private static void StartupReplayDiscardsStaleLogs()
    {
        var now = DateTimeOffset.UtcNow;
        HostLogEventFactory log = (text, age) => new DalamudActCompat.Protocol.HostLogEvent(now.AddSeconds(-age), text, false);
        var aheadClock = new GameClockOffset();
        aheadClock.Observe(now.AddSeconds(-8.5), now);
        var ahead = new StartupEventBuffer<List<string>>(8, aheadClock);
        ahead.AddLogs([log("live", 8.5 + 0.5), log("old", 8.5 + 60)]);
        var aheadReplayed = new List<string>();
        ahead.Replay(aheadReplayed, (target, logs) => target.AddRange(logs.Select(item => item.Line)), now,
            exception => throw exception);
        Require(aheadReplayed.SequenceEqual(["live"]),
            $"a Host clock 8.5 s ahead made startup replay misjudge line age: [{string.Join(",", aheadReplayed)}]");

        var buffer = new StartupEventBuffer<List<string>>(8, new GameClockOffset());
        buffer.AddLogs([log("stale", 60), log("fresh-1", 1)]);
        buffer.AddEvent(target => target.Add("zone"));
        buffer.AddLogs([log("fresh-2", 0.5)]);
        var replayed = new List<string>();
        var discarded = buffer.Replay(replayed, (target, logs) => target.AddRange(logs.Select(item => item.Line)),
            now, exception => throw exception);
        Require(discarded == 1 && replayed.SequenceEqual(["fresh-1", "zone", "fresh-2"]),
            $"startup replay did not keep fresh events in order while discarding stale ones: [{string.Join(",", replayed)}]");

        buffer.AddEvent(target => target.Add("combat"));
        buffer.AddLogs([.. Enumerable.Range(0, 5).Select(index => log($"a{index}", 0))]);
        buffer.AddLogs([.. Enumerable.Range(0, 5).Select(index => log($"b{index}", 0))]);
        replayed.Clear();
        buffer.Replay(replayed, (target, logs) => target.AddRange(logs.Select(item => item.Line)), now,
            exception => throw exception);
        Require(replayed.First() == "combat" && replayed.Count(line => line.StartsWith('b')) == 5 &&
                !replayed.Any(line => line.StartsWith('a')),
            "the startup log bound discarded state events or kept the older log batch");
    }

    private delegate DalamudActCompat.Protocol.HostLogEvent HostLogEventFactory(string text, double ageSeconds);

    private static async Task AuthorizedTtsSurvivesSlowWriterAsync()
    {
        // A synchronous speech provider may hold the dispatcher longer than the 2 s
        // authorization budget. A request that the game already authorized in time
        // must still be spoken afterwards instead of being discarded silently.
        var spoken = new ConcurrentQueue<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new AuthorizedTtsQueue(
            8,
            () => text =>
            {
                spoken.Enqueue(text);
                if (text == "first")
                {
                    Thread.Sleep(2_500);
                }

                if (spoken.Count == 2)
                {
                    done.TrySetResult();
                }
            },
            exception => done.TrySetException(exception));
        var now = DateTimeOffset.UtcNow;
        var first = queue.Reserve("first", now, now.AddSeconds(2));
        var second = queue.Reserve("second", now, now.AddSeconds(2));
        Require(queue.Complete(first, allowed: true, now) && queue.Complete(second, allowed: true, now),
            "test could not authorize its reserved TTS requests");
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(6)));
        Require(spoken.SequenceEqual(["first", "second"]),
            $"an in-time authorized TTS request was dropped behind a slow provider; spoken=[{string.Join(",", spoken)}]");
    }

    private static void TriggernometryEventDropsAreObservable()
    {
        // The bounded Triggernometry event queue intentionally sheds its oldest events
        // when TN falls far behind. That loss must be visible in the runtime status.
        var describe = typeof(HostPluginBridge).GetMethod(
            "DescribeTriggernometryEventQueue",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Require(describe is not null,
            "the Host exposes no runtime description of Triggernometry event-queue drops");
        var before = (string)describe!.Invoke(null, null)!;
        var queue = new Queue<int>();
        for (var index = 0; index < 8192 + 3; index++)
        {
            HostPluginBridge.EnqueueTriggerEventBounded(queue, index);
        }

        var after = (string)describe.Invoke(null, null)!;
        Require(queue.Count == 8192 && queue.Peek() == 3,
            "the event queue no longer applies its bounded drop-oldest policy");
        Require(DroppedCount(after) - DroppedCount(before) == 3,
            $"the runtime description did not count the dropped events: before='{before}', after='{after}'");
    }

    private static long DroppedCount(string description)
    {
        var marker = "dropped=";
        var start = description.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0, $"no dropped counter in '{description}'");
        start += marker.Length;
        var end = start;
        while (end < description.Length && char.IsDigit(description[end])) end++;
        return long.Parse(description[start..end]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
