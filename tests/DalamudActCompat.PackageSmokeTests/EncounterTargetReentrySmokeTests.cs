using DalamudActCompat.ActRuntime;

internal static class EncounterTargetReentrySmokeTests
{
    public static void Run()
    {
        var start = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        ActPlayerIdentity[] party = [new("Player", "", "PLD", true, false) { EntityId = 0x10000001 }];
        foreach (var scenario in new[] { "same-id", "no-targetability", "multiple", "death-reentry", "new-id", "surviving-boss" })
        {
            var tracker = new EncounterDurationTracker();
            tracker.ObserveTargetPresence(start.AddSeconds(-1), "40000010", "Boss");
            if (scenario == "surviving-boss") tracker.ObserveTargetPresence(start.AddSeconds(-1), "40000020", "Other boss");
            tracker.StartEncounter(start, party);
            void Hit(int second, string id = "40000010")
                => tracker.ObserveConfirmedDamage(start.AddSeconds(second), "10000001", "Player", "", id, "Boss");
            void Raw(int second, string payload) => tracker.ObserveRawLine(start.AddSeconds(second), payload, party);
            Hit(0);
            if (scenario == "surviving-boss") Hit(1, "40000020");
            if (scenario != "no-targetability") tracker.ObserveTargetability(start.AddSeconds(300), "40000010", "Boss", false);
            Raw(300, scenario == "death-reentry" ? "25|time|40000010|Boss" : "261|time|Remove|40000010");
            var returnedId = scenario == "new-id" ? "40000011" : "40000010";
            Raw(330, $"261|time|Add|{returnedId}|Name|Boss");
            // Duplicate presence reports must not start another lifetime or discard known downtime.
            Raw(331, $"03|time|{returnedId}|Boss|0|0|0");
            if (scenario != "no-targetability") tracker.ObserveTargetability(start.AddSeconds(330), returnedId, "Boss", true);
            Hit(330, returnedId);
            if (scenario == "multiple")
            {
                Raw(380, "261|time|Remove|40000010");
                Raw(390, "261|time|Add|40000010|Name|Boss");
                Hit(390);
            }
            Hit(551, returnedId);
            var expected = scenario == "surviving-boss" ? 551 : scenario == "multiple" ? 511 : 521;
            Equal(tracker.ResolveDamageMetricDurationSeconds(start.AddSeconds(551), true), expected, scenario);
            if (scenario != "surviving-boss")
            {
                Check(tracker.IsTransitioningAt(start.AddSeconds(310)), scenario + ": past disappearance was erased");
                Check(!tracker.IsTransitioningAt(start.AddSeconds(340)), scenario + ": returned boss is still unavailable");
            }
            tracker.StartEncounter(start.AddSeconds(600), party);
            tracker.ObserveConfirmedDamage(start.AddSeconds(600), "10000001", "Player", "", returnedId, "Boss");
            tracker.ObserveConfirmedDamage(start.AddSeconds(610), "10000001", "Player", "", returnedId, "Boss");
            Equal(tracker.ResolveDamageMetricDurationSeconds(start.AddSeconds(610), true), 10, scenario + " next encounter");
            Console.WriteLine($"PASS reentry {scenario}: {expected} seconds");
        }
    }

    private static void Equal(double actual, double expected, string message)
        => Check(Math.Abs(actual - expected) < 0.001, $"{message}: expected {expected}, actual {actual}");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
