using DalamudActCompat.Host;

internal static class TriggernometryActorControlTests
{
    internal static void Run()
    {
        const string init = "[21:38:41.638] 273 111:4001895C:019D:1:2:0:0:";
        const string second = "[21:38:41.639] 273 111:40018959:019D:40:80:0:0:";
        uint zone = 1363;
        long clock = 0;
        var ready = new HashSet<uint>();
        var delivered = new List<string>();
        var warnings = new List<string>();
        var gate = new TriggernometryActorControlGate(() => zone, ready.Contains,
            (_, line, _) => delivered.Add(line), warnings.Add, () => clock);
        void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("ActorControl gate: " + reason);
        }
        void Clean()
        {
            gate.Reset(); delivered.Clear(); warnings.Clear(); ready.Clear(); clock = 0; zone = 1363;
        }

        gate.Accept(false, "ordinary", "U7b");
        gate.Accept(true, init, "U7b");
        zone = 128;
        gate.Accept(false, init, "Limsa");
        Require(delivered.SequenceEqual(["ordinary", init, init]) && !gate.HasPending,
            "normal/import/other-zone logs must not wait");

        Clean();
        ready.Add(0x4001895C);
        gate.Accept(false, init, "U7b");
        Require(delivered.SequenceEqual([init]) && !gate.HasPending, "ready entities must be immediate");

        Clean();
        gate.Accept(false, init, "U7b");
        gate.Accept(false, "ice", "U7b");
        gate.Accept(false, "fire", "U7b");
        gate.Accept(false, "spread", "U7b");
        Require(delivered.Count == 0, "dependent mechanics overtook the missing initialization");
        clock = 50; ready.Add(0x4001895C); gate.Pump(); gate.Pump();
        Require(delivered.SequenceEqual([init, "ice", "fire", "spread"]) && !gate.HasPending,
            "late arrival must release the original ordered stream exactly once");

        Clean();
        gate.Accept(false, init, "U7b"); gate.Accept(false, init, "U7b");
        ready.Add(0x4001895C); gate.Pump(); gate.Pump();
        Require(delivered.SequenceEqual([init, init]), "do not add retries or suppress upstream's duplicate inputs");

        Clean();
        gate.Accept(false, init, "U7b"); gate.Accept(false, second, "U7b"); gate.Accept(false, "after", "U7b");
        clock = 400; ready.Add(0x4001895C); gate.Pump();
        Require(delivered.SequenceEqual([init]), "a second missing object must retain its dependent events");
        clock = 500; gate.Pump();
        Require(delivered.SequenceEqual([init, second, "after"]) && warnings.Count == 1 && !gate.HasPending,
            "multiple missing objects must share one bounded wait, even without new traffic");
        ready.Add(0x40018959); gate.Pump();
        Require(delivered.Count == 3, "an expired event must not replay when the object eventually arrives");

        Clean();
        gate.Accept(false, init, "U7b"); gate.Accept(false, "stale", "U7b");
        zone = 128; ready.Add(0x4001895C); gate.Pump();
        Require(delivered.Count == 0 && !gate.HasPending, "a new-zone snapshot must discard old events");
        zone = 1363; gate.Accept(false, init, "U7b");
        Require(delivered.SequenceEqual([init]), "zone return must not retain an earlier backlog");

        Clean();
        gate.Accept(false, init, "U7b"); gate.Reset(); ready.Add(0x4001895C); gate.Pump();
        Require(delivered.Count == 0, "explicit zone reset/unload/revocation must discard the backlog");

        Clean();
        gate.Accept(false, init, "U7b");
        for (var i = 0; i < TriggernometryActorControlGate.MaximumLines; i++) gate.Accept(false, i.ToString(), "U7b");
        Require(delivered.Count == TriggernometryActorControlGate.MaximumLines + 1 && warnings.Count == 1 && !gate.HasPending,
            "queue overflow must fail open once without losing or duplicating queued input");

        Clean();
        gate.Accept(false, init, "U7b");
        gate.Accept(false, new string('x', TriggernometryActorControlGate.MaximumCharacters), "U7b");
        Require(delivered.Count == 2 && warnings.Count == 1 && !gate.HasPending, "byte-size protection must bound retained strings");

        // The source log is queued before its variable actions finish. A recovered
        // initialization needs a completion signal, not a guessed extra sleep.
        Clean();
        var initialized = true;
        var barrierGate = new TriggernometryActorControlGate(() => zone, ready.Contains,
            (_, line, _) => delivered.Add(line), warnings.Add, () => clock,
            beforeReplay: line => { if (line == init) initialized = false; },
            replayReady: () => initialized, cancelReplay: () => initialized = true);
        barrierGate.Accept(false, init, "U7b");
        ready.Add(0x4001895C); barrierGate.Pump();
        barrierGate.Accept(false, "ice", "U7b"); barrierGate.Accept(false, "fire", "U7b");
        Require(delivered.SequenceEqual([init]) && barrierGate.HasPending,
            "empty source backlog must still hold new input until initialization actions finish");
        initialized = true; barrierGate.Pump();
        Require(delivered.SequenceEqual([init, "ice", "fire"]) && !barrierGate.HasPending,
            "completion must release dependents once and in order");
        ready.Clear(); delivered.Clear();
        barrierGate.Accept(false, init, "U7b"); ready.Add(0x4001895C); barrierGate.Pump();
        clock = 500; barrierGate.Pump();
        Require(!barrierGate.HasPending && initialized, "missing completion must share the original deadline");
        Console.WriteLine("U7b ActorControl ordering: ready/late/duplicate/expiry/zone/reset/queue bounds passed.");
    }
}
