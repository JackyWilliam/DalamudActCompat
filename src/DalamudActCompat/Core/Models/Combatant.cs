namespace DalamudActCompat.Core.Models;

public sealed record Combatant(
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
    double? FflogsPercentile = null,
    string? FflogsEncounterName = null,
    double Rdps = 0,
    DateTimeOffset? FflogsDataUpdatedAt = null,
    string? FflogsMetric = null,
    bool FflogsDataStale = false,
    int DirectHits = 0);
