namespace DalamudActCompat.Core.Models;

public sealed record Encounter(
    Guid Id,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    string ZoneName,
    string EnemyName,
    IReadOnlyList<Combatant> Combatants,
    IReadOnlyList<DamageEvent> DamageEvents,
    IReadOnlyList<HealEvent> HealEvents,
    IReadOnlyList<DeathEvent> DeathEvents,
    IReadOnlyList<ActionSummary> ActionSummaries,
    IReadOnlyList<JobSummary> JobSummaries)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public Encounter? FflogsRankingEncounter { get; init; }

    public TimeSpan? CombatDuration { get; init; }

    public TimeSpan Duration => (EndTime ?? DateTimeOffset.UtcNow) - StartTime;

    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan EffectiveDuration
        => CombatDuration is { } combatDuration && combatDuration > TimeSpan.Zero
            ? combatDuration
            : Duration;

    public bool IsActive => EndTime is null;

    public long TotalDamage => Combatants.Sum(static combatant => combatant.TotalDamage);

    public long TotalHealing => Combatants.Sum(static combatant => combatant.TotalHealing);

    public int TotalDeaths => Combatants.Sum(static combatant => combatant.Deaths);
}
