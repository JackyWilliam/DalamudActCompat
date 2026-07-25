namespace DalamudActCompat.ActRuntime;

public sealed record ActEncounterSnapshot(
    Guid Id,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    string ZoneName,
    string EnemyName,
    IReadOnlyList<ActCombatantSnapshot> Combatants);

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
    double ExtDps = 0);
