namespace DalamudActCompat.Core.Models;

public static class SampleEncounterFactory
{
    public static Encounter Create(DateTimeOffset now)
    {
        var start = now - TimeSpan.FromSeconds(120);
        var combatants = new[]
        {
            new Combatant("local", "You", "SAM", true, 1_245_000, 18_000, 0),
            new Combatant("p2", "Party Member A", "WHM", false, 420_000, 965_000, 0),
            new Combatant("p3", "Party Member B", "DRG", false, 1_030_000, 12_000, 1),
            new Combatant("p4", "Party Member C", "BRD", false, 880_000, 24_000, 0),
        };

        return new Encounter(
            Guid.NewGuid(),
            start,
            null,
            "Sample Zone",
            "Training Dummy",
            combatants,
            Array.Empty<DamageEvent>(),
            Array.Empty<HealEvent>(),
            Array.Empty<DeathEvent>(),
            Array.Empty<ActionSummary>(),
            new[]
            {
                new JobSummary("SAM", 1_245_000, 18_000, 1),
                new JobSummary("WHM", 420_000, 965_000, 1),
                new JobSummary("DRG", 1_030_000, 12_000, 1),
                new JobSummary("BRD", 880_000, 24_000, 1),
            });
    }
}
