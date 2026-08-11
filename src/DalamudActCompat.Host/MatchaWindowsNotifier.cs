using System.Windows.Forms;

namespace DalamudActCompat.Host;

internal sealed class MatchaWindowsNotifier : IDisposable
{
    private const string NotificationTitle = "抹茶 / Cafe.Matcha";
    private readonly Control dispatcher;
    private readonly Func<bool> isGameForeground;
    private readonly WindowsNotificationCenter notificationCenter = new();
    private int disposed;

    public MatchaWindowsNotifier(Control dispatcher, Func<bool>? isGameForeground = null)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.isGameForeground = isGameForeground ?? HostPluginBridge.IsGameForeground;
    }

    public bool TryShow(string message)
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
                    (Func<bool>)(() => notificationCenter.TryShow(NotificationTitle, message)))
                : notificationCenter.TryShow(NotificationTitle, message);
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException("matcha", "Windows notification", ex);
            return false;
        }
    }

    public void Dispose()
        => Interlocked.Exchange(ref disposed, 1);
}
