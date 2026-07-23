namespace DalamudActCompat.Core.Models;

public sealed record DamageEvent(
    DateTimeOffset Timestamp,
    string SourceId,
    string TargetId,
    string ActionName,
    long Amount,
    bool IsCritical,
    bool IsDirect);
