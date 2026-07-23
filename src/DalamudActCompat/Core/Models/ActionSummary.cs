namespace DalamudActCompat.Core.Models;

public sealed record ActionSummary(
    string CombatantId,
    string ActionName,
    long TotalDamage,
    long TotalHealing,
    int Uses,
    int Crits,
    int DirectHits);
