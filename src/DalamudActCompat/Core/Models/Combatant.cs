namespace DalamudActCompat.Core.Models;

public sealed record Combatant(
    string Id,
    string Name,
    string Job,
    bool IsLocalPlayer,
    long TotalDamage,
    long TotalHealing,
    int Deaths);
