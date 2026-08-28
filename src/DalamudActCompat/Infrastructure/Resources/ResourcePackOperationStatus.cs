namespace DalamudActCompat.Infrastructure.Resources;

public enum ResourcePackOperationState
{
    Unavailable,
    Downloading,
    Ready,
}

public sealed record ResourcePackOperationStatus(
    ResourcePackOperationState State,
    int ProgressPercent = 0,
    string FailureMessage = "")
{
    public static ResourcePackOperationStatus Unavailable(string failureMessage = "")
        => new(ResourcePackOperationState.Unavailable, FailureMessage: failureMessage);

    public static ResourcePackOperationStatus Downloading(int progressPercent = 0)
        => new(ResourcePackOperationState.Downloading, Math.Clamp(progressPercent, 0, 100));

    public static ResourcePackOperationStatus Ready()
        => new(ResourcePackOperationState.Ready, 100);
}

public readonly record struct ResourcePackDownloadProgress(
    long BytesReceived,
    long TotalBytes)
{
    public int Percent => TotalBytes <= 0
        ? 0
        : (int)Math.Clamp(BytesReceived * 100L / TotalBytes, 0, 100);
}
