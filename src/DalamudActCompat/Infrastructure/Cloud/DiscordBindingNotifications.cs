namespace DalamudActCompat.Infrastructure.Cloud;

internal enum DiscordBindingNotice
{
    Verifying,
    Verified,
    Unbound,
    Failed,
}

// Framework polls every frame. Only binding transitions produce notices; login
// and logout remain silent, and an unknown binding is never called "unbound".
internal sealed class DiscordBindingNotifications
{
    private string? username;
    private DiscordBindingNotice? previous;

    internal DiscordBindingNotice? Update(CloudClientSnapshot account)
    {
        if (!account.IsSignedIn || account.ActiveBan is not null)
        {
            username = null;
            previous = null;
            return null;
        }
        if (!string.Equals(username, account.Username, StringComparison.Ordinal))
        {
            username = account.Username;
            previous = null;
        }

        var next = !string.IsNullOrWhiteSpace(account.RecoveryStatusError) ? DiscordBindingNotice.Failed :
            account.Recovery is null ? DiscordBindingNotice.Verifying :
            account.Recovery.DiscordBound ? DiscordBindingNotice.Verified : DiscordBindingNotice.Unbound;
        if (next == previous) return null;
        previous = next;
        return next;
    }
}
