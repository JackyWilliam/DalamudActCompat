namespace DalamudActCompat.Core.Models;

public sealed record DeathEvent(
    DateTimeOffset Timestamp,
    string CombatantId,
    string CombatantName,
    string? LastDamageSource);
