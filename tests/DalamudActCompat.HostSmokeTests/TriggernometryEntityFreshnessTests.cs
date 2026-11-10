using DalamudActCompat.Host;

// Unit coverage for the wait that keeps a log line behind the entity capture taken after it.
// The end-to-end reproduction of the player report lives in the U7b tether probe.
internal static class TriggernometryEntityFreshnessTests
{
    internal static void Run()
    {
        long clock = 0;
        long applied = 0;
        var delivered = new List<string>();
        var warnings = new List<string>();
        TriggernometryEntityFreshnessBarrier Create() => new(
            epoch => applied >= epoch, (_, line, _) => delivered.Add(line), warnings.Add, () => clock);
        var barrier = Create();
        void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Entity freshness barrier: " + reason);
        }
        void Clean()
        {
            // A fresh instance: a rate-limited expiry summary must not leak into the next case.
            barrier = Create(); delivered.Clear(); warnings.Clear(); clock += 60_000; applied = 0;
        }

        barrier.Accept(false, "unordered", "zone", 0);
        applied = 5;
        barrier.Accept(false, "current", "zone", 5);
        barrier.Accept(true, "import", "zone", 99);
        Require(delivered.SequenceEqual(["unordered", "current", "import"]) && !barrier.HasPending,
            "lines without a promise, with an applied capture, and imports must not wait");

        Clean();
        applied = 5;
        barrier.Accept(false, "tether", "zone", 6);
        barrier.Accept(false, "derived", "zone", 0);
        barrier.Accept(false, "cast", "zone", 6);
        Require(delivered.Count == 0 && barrier.HasPending, "a line overtook the capture taken after it");
        clock += 30; applied = 6; barrier.Pump();
        Require(delivered.SequenceEqual(["tether", "derived", "cast"]) && !barrier.HasPending && warnings.Count == 0,
            "the capture must release the held lines once, in arrival order");

        // Coalesced IPC can skip epochs; a later capture satisfies an earlier requirement.
        Clean();
        barrier.Accept(false, "a", "zone", 3);
        barrier.Accept(false, "b", "zone", 4);
        applied = 9; barrier.Pump();
        Require(delivered.SequenceEqual(["a", "b"]), "a newer capture did not satisfy older lines");

        // A stalled sender must not hold triggers: forward unchanged after the bound.
        Clean();
        barrier.Accept(false, "first", "zone", 1);
        clock += 100;
        barrier.Accept(false, "second", "zone", 1);
        clock += TriggernometryEntityFreshnessBarrier.MaximumWaitMilliseconds - 100 - 1; barrier.Pump();
        Require(delivered.Count == 0, "the wait ended before its bound");
        clock += 1; barrier.Pump();
        Require(delivered.SequenceEqual(["first"]) && barrier.HasPending,
            "each line has its own bound; a later line must not expire with an earlier one");
        clock += 100; barrier.Pump();
        Require(delivered.SequenceEqual(["first", "second"]) && !barrier.HasPending && warnings.Count == 1,
            "expired lines must be forwarded in order with one summary");
        barrier.Accept(false, "third", "zone", 1);
        clock += TriggernometryEntityFreshnessBarrier.MaximumWaitMilliseconds; barrier.Pump();
        Require(delivered.Count == 3 && warnings.Count == 1, "expiry summaries must be rate limited");
        clock += TriggernometryEntityFreshnessBarrier.DiagnosticIntervalMilliseconds;
        barrier.Accept(false, "fourth", "zone", 1);
        clock += TriggernometryEntityFreshnessBarrier.MaximumWaitMilliseconds; barrier.Pump();
        Require(delivered.Count == 4 && warnings.Count == 2, "a later stall must be reported again");

        // Zone and combat transitions flush; revocation and unload discard.
        Clean();
        barrier.Accept(false, "before-zone", "zone", 1);
        barrier.Flush();
        Require(delivered.SequenceEqual(["before-zone"]) && !barrier.HasPending && warnings.Count == 0,
            "a flush must forward held lines without calling it an expiry");
        barrier.Accept(false, "revoked", "zone", 1);
        barrier.Reset(); applied = 1; barrier.Pump();
        Require(delivered.Count == 1 && !barrier.HasPending, "a reset must discard held lines");

        // Lines TN raises while handling a delivered line stay inside that delivery.
        Clean();
        TriggernometryEntityFreshnessBarrier? nested = null;
        var order = new List<string>();
        nested = new TriggernometryEntityFreshnessBarrier(epoch => applied >= epoch, (_, line, _) =>
        {
            order.Add(line);
            if (line == "outer") nested!.Accept(false, "inner", "zone", 99);
        }, warnings.Add, () => clock);
        nested.Accept(false, "outer", "zone", 1);
        nested.Accept(false, "next", "zone", 1);
        applied = 1; nested.Pump();
        Require(order.SequenceEqual(["outer", "inner", "next"]), "a reentrant line was queued behind later input");

        Clean();
        barrier.Accept(false, "head", "zone", 1);
        for (var i = 0; i < TriggernometryEntityFreshnessBarrier.MaximumLines; i++) barrier.Accept(false, i.ToString(), "zone", 1);
        Require(delivered.Count == TriggernometryEntityFreshnessBarrier.MaximumLines + 1 && warnings.Count == 1 && !barrier.HasPending,
            "queue overflow must fail open once without losing or duplicating input");
        Clean();
        barrier.Accept(false, "head", "zone", 1);
        barrier.Accept(false, new string('x', TriggernometryEntityFreshnessBarrier.MaximumCharacters), "zone", 1);
        Require(delivered.Count == 2 && warnings.Count == 1 && !barrier.HasPending, "size protection must bound retained strings");
        Console.WriteLine("Entity freshness barrier: immediate/held/coalesced/expiry/flush/reset/reentrant/queue bounds passed.");
    }
}
