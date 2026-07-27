using DalamudActCompat.ActRuntime;
using DalamudActCompat.Core.Models;

namespace DalamudActCompat.Parser;

public static class ActEncounterMapper
{
    public static Encounter Map(ActEncounterSnapshot source)
    {
        var combatants = source.Combatants
            .Select(static combatant => new Combatant(
                combatant.Id,
                combatant.Name,
                combatant.Job,
                combatant.IsLocalPlayer,
                combatant.TotalDamage,
                combatant.TotalHealing,
                combatant.Deaths,
                NormalizeRate(combatant.Dps),
                NormalizeRate(combatant.EncDps),
                NormalizeRate(combatant.ExtDps)))
            .ToArray();
        var jobs = combatants
            .Where(static combatant => !string.IsNullOrWhiteSpace(combatant.Job))
            .GroupBy(static combatant => combatant.Job, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new JobSummary(
                group.Key,
                group.Sum(static combatant => combatant.TotalDamage),
                group.Sum(static combatant => combatant.TotalHealing),
                group.Count()))
            .ToArray();

        return new Encounter(
            source.Id,
            source.StartTime,
            source.EndTime,
            source.ZoneName,
            source.EnemyName,
            combatants,
            Array.Empty<DamageEvent>(),
            Array.Empty<HealEvent>(),
            Array.Empty<DeathEvent>(),
            Array.Empty<ActionSummary>(),
            jobs);
    }

    private static double NormalizeRate(double value)
        => double.IsFinite(value) ? value : 0;
}
