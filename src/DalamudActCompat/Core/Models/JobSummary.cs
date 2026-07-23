namespace DalamudActCompat.Core.Models;

public sealed record JobSummary(
    string Job,
    long TotalDamage,
    long TotalHealing,
    int Combatants);
