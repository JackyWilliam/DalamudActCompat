namespace DalamudActCompat.Core.Models;

public sealed record HealEvent(
    DateTimeOffset Timestamp,
    string SourceId,
    string TargetId,
    string ActionName,
    long Amount,
    long Overheal,
    bool IsCritical);
