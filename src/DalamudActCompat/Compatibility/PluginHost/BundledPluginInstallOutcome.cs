namespace DalamudActCompat.Compatibility.PluginHost;

public sealed record BundledPluginInstallOutcome(
    bool RuntimeReady,
    string? RuntimeWarning)
{
    public static BundledPluginInstallOutcome Ready { get; } = new(true, null);

    public static BundledPluginInstallOutcome RuntimeRecoveryPending(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(false, exception.GetBaseException().Message);
    }
}
