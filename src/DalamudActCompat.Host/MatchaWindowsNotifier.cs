using System.Drawing;
using System.Windows.Forms;

namespace DalamudActCompat.Host;

internal sealed class MatchaWindowsNotifier : IDisposable
{
    private const string NotificationTitle = "抹茶 / Cafe.Matcha";
    private const int MaximumShellBodyCharacters = 255;
    private readonly Control dispatcher;
    private NotifyIcon? notifyIcon;
    private int disposed;

    public MatchaWindowsNotifier(Control dispatcher)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool TryShow(string message)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            dispatcher.IsDisposed ||
            !dispatcher.IsHandleCreated)
        {
            return false;
        }

        try
        {
            return dispatcher.InvokeRequired
                ? (bool)dispatcher.Invoke((Func<bool>)(() => ShowOnUiThread(message)))
                : ShowOnUiThread(message);
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException("matcha", "Windows notification", ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!dispatcher.IsDisposed && dispatcher.IsHandleCreated && dispatcher.InvokeRequired)
            {
                dispatcher.Invoke((Action)DisposeOnUiThread);
            }
            else
            {
                DisposeOnUiThread();
            }
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException("matcha", "Windows notification shutdown", ex);
        }
    }

    private bool ShowOnUiThread(string message)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        notifyIcon ??= new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = NotificationTitle,
            Visible = true,
        };

        var body = message.Length <= MaximumShellBodyCharacters
            ? message
            : $"{message[..(MaximumShellBodyCharacters - 1)]}…";
        notifyIcon.ShowBalloonTip(
            10_000,
            NotificationTitle,
            body,
            ToolTipIcon.Info);
        return true;
    }

    private void DisposeOnUiThread()
    {
        if (notifyIcon is null)
        {
            return;
        }

        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        notifyIcon = null;
    }
}
