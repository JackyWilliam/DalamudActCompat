namespace DalamudActCompat.Compatibility.Cactbot;

public enum CactbotOperationState
{
    Idle,
    Checking,
    Installing,
    Ready,
    Error,
}

public sealed record CactbotOperationStatus(
    CactbotOperationState State,
    string? ErrorMessage = null);

internal static class CactbotOperationLifecycle
{
    public static bool CanPublishCompletion(
        bool shutdownStarted,
        CancellationToken cancellationToken)
        => !shutdownStarted && !cancellationToken.IsCancellationRequested;

    public static Task PublishIfActiveAsync(
        bool shutdownStarted,
        CancellationToken cancellationToken,
        Func<Task> publish)
        => CanPublishCompletion(shutdownStarted, cancellationToken)
            ? publish()
            : Task.CompletedTask;
}
