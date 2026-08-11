using System.Windows.Forms;

namespace DalamudActCompat.Host;

internal sealed class SilverDasherWindowsNotifier : IDisposable
{
    private const string NotificationTitle = "银山雀儿 / SilverDasher";
    private readonly Control dispatcher;
    private readonly Func<bool> isGameForeground;
    private readonly WindowsNotificationCenter notificationCenter = new();
    private int disposed;

    public SilverDasherWindowsNotifier(
        Control dispatcher,
        Func<bool>? isGameForeground = null)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.isGameForeground = isGameForeground ?? HostPluginBridge.IsGameForeground;
    }

    public bool TryShow(string message, string detail)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            dispatcher.IsDisposed ||
            !dispatcher.IsHandleCreated ||
            isGameForeground())
        {
            // Returning false deliberately selects the existing typed Dalamud fallback.
            return false;
        }

        try
        {
            return dispatcher.InvokeRequired
                ? (bool)dispatcher.Invoke(
                    (Func<bool>)(() => notificationCenter.TryShow(
                        NotificationTitle,
                        message,
                        detail)))
                : notificationCenter.TryShow(NotificationTitle, message, detail);
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException("silverdasher", "Windows notification", ex);
            return false;
        }
    }

    public void Dispose()
        => Interlocked.Exchange(ref disposed, 1);
}
