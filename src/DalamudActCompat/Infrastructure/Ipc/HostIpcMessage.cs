using DalamudActCompat.Core.Models;

namespace DalamudActCompat.Infrastructure.Ipc;

internal sealed record HostIpcMessage(
    string Type,
    HostEncounterDto? Current,
    IReadOnlyList<HostEncounterDto>? Recent,
    string? Message);

internal sealed record HostEncounterDto(
    Guid Id,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    string ZoneName,
    string EnemyName,
    IReadOnlyList<HostCombatantDto> Combatants);

internal sealed record HostCombatantDto(
    string Id,
    string Name,
    string Job,
    bool IsLocalPlayer,
    long TotalDamage,
    long TotalHealing,
    int Deaths);

internal static class HostIpcMapper
{
    public static Encounter ToEncounter(HostEncounterDto source)
    {
        var combatants = source.Combatants
            .Select(static combatant => new Combatant(
                combatant.Id,
                combatant.Name,
                combatant.Job,
                combatant.IsLocalPlayer,
                combatant.TotalDamage,
                combatant.TotalHealing,
                combatant.Deaths))
            .ToArray();

        var jobSummaries = combatants
            .GroupBy(static combatant => combatant.Job)
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
            jobSummaries);
    }
}
