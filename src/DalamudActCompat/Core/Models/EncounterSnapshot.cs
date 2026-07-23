namespace DalamudActCompat.Core.Models;

public sealed record EncounterSnapshot(
    Encounter? Current,
    IReadOnlyList<Encounter> Recent,
    DateTimeOffset CreatedAt)
{
    public static EncounterSnapshot Empty { get; } = new(null, Array.Empty<Encounter>(), DateTimeOffset.UtcNow);
}
