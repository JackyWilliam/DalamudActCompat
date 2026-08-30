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

    // Combatant rows can arrive gradually after a pull starts. The live roster capacity
    // keeps 4-player and 8/24-player presentation rules stable before everyone acts.
    public int PartyCapacity { get; init; }

    // A folder represents one duty entry; its child records are independent pulls so a
    // wipe never leaks totals into the next attempt.
    public IReadOnlyList<Encounter> SegmentRecords { get; init; } = [];

    public TimeSpan Duration
    {
        get
        {
            var elapsed = (EndTime ?? DateTimeOffset.UtcNow) - StartTime;
            // ACT timestamps and the local wall clock can briefly disagree after a pull
            // resumes. A duration is elapsed time, so exposing a negative value is invalid.
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }
    }

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
