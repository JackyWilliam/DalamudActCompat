using System.Drawing;
using System.Windows.Forms;

namespace DalamudActCompat.Host;

internal sealed class SilverDasherWindowsNotifier : IDisposable
{
    private const string NotificationTitle = "银山雀儿 / SilverDasher";
    private const int MaximumShellBodyCharacters = 255;
    private readonly Control dispatcher;
    private NotifyIcon? notifyIcon;
    private int disposed;

    public SilverDasherWindowsNotifier(Control dispatcher)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool TryShow(string message, string detail)
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
                ? (bool)dispatcher.Invoke(
                    (Func<bool>)(() => ShowOnUiThread(message, detail)))
                : ShowOnUiThread(message, detail);
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException(
                "silverdasher",
                "Windows notification",
                ex);
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
            if (!dispatcher.IsDisposed && dispatcher.IsHandleCreated)
            {
                if (dispatcher.InvokeRequired)
                {
                    dispatcher.Invoke((Action)DisposeOnUiThread);
                }
                else
                {
                    DisposeOnUiThread();
                }
            }
            else
            {
                DisposeOnUiThread();
            }
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException(
                "silverdasher",
                "Windows notification shutdown",
                ex);
        }
    }

    private bool ShowOnUiThread(string message, string detail)
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

        var body = string.IsNullOrWhiteSpace(detail)
            ? message
            : $"{message}{Environment.NewLine}{detail}";
        if (body.Length > MaximumShellBodyCharacters)
        {
            body = $"{body[..(MaximumShellBodyCharacters - 1)]}…";
        }

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
