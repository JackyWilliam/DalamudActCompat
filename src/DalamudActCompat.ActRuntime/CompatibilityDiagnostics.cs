namespace DalamudActCompat.ActRuntime;

public enum CompatibilityStageState
{
    Success,
    Failed,
    NotImplemented,
    NotTested,
}

public sealed record CompatibilityStageResult(
    string Stage,
    CompatibilityStageState State,
    string Detail,
    DateTimeOffset Timestamp);

public sealed record ActPluginDiagnostic(
    string PluginId,
    string Category,
    string ExceptionType,
    string Message,
    string FullStack,
    string? SourceAssembly,
    string? SourceType,
    string? SourceMethod,
    string Trigger,
    int ManagedThreadId,
    bool IsUiThread,
    int RepeatCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
