namespace DalamudActCompat.Parser;

public enum ParserState
{
    Disabled,
    Initializing,
    Running,
    Stopped,
    MissingDependency,
    VersionIncompatible,
    Faulted,
}

public sealed record ParserStatus(
    ParserState State,
    string Message,
    DateTimeOffset UpdatedAt,
    string? Detail = null)
{
    public static ParserStatus Disabled { get; } = new(ParserState.Disabled, "Parsing disabled.", DateTimeOffset.UtcNow);
}
