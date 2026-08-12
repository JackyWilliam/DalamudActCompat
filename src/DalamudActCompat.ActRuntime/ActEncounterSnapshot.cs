namespace DalamudActCompat.ActRuntime;

public sealed record ActEncounterSnapshot(
    Guid Id,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    string ZoneName,
    string EnemyName,
    IReadOnlyList<ActCombatantSnapshot> Combatants)
{
    public TimeSpan? CombatDuration { get; init; }

    public bool IsTransitioning { get; init; }

    // Combatants contain ACT totals, while this transient roster metadata lets the duty
    // accumulator distinguish a replacement from an additional historical participant.
    public IReadOnlyList<string> CurrentPartyMemberIds { get; init; } = [];

    public int PartyCapacity { get; init; }
}

public sealed record ActCombatantSnapshot(
    string Id,
    string Name,
    string Job,
    bool IsLocalPlayer,
    long TotalDamage,
    long TotalHealing,
    int Deaths,
    double Dps = 0,
    double EncDps = 0,
    double ExtDps = 0,
    int DamageHits = 0,
    int CriticalHits = 0,
    int CriticalDirectHits = 0,
    double Rdps = 0);
