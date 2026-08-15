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

    public uint? TerritoryId { get; init; }

    public TimeSpan? CombatDuration { get; init; }

    public bool IsTransitioning { get; init; }

    // One duty pull is the folder; ACT may create several concrete records inside it
    // during phase changes, and users still need those records without flattening the pull.
    public IReadOnlyList<Encounter> SegmentRecords { get; init; } = [];

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
