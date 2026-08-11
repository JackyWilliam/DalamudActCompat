using Microsoft.Toolkit.Uwp.Notifications;

namespace DalamudActCompat.Host;

internal sealed class WindowsNotificationCenter
{
    private static readonly object RegistrationLock = new();
    private static bool registered;

    public bool TryShow(string title, string message, string? detail = null)
    {
        try
        {
            EnsureRegistered();
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                builder.AddText(detail);
            }

            // The compatibility manager gives an unpackaged Host a stable Windows
            // notification identity without requiring the Windows App SDK runtime.
            builder.Show(notification =>
            {
                notification.ExpirationTime = DateTimeOffset.Now.AddHours(12);
            });
            return true;
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException("windows-notification", "Notification Center", ex);
            return false;
        }
    }

    private static void EnsureRegistered()
    {
        lock (RegistrationLock)
        {
            if (registered)
            {
                return;
            }

            // Registration is required for unpackaged desktop notification delivery.
            // Activation is intentionally informational; clicking an alert does not
            // start a privileged action in either the Host or the game.
            ToastNotificationManagerCompat.OnActivated += static _ => { };
            registered = true;
        }
    }
}
