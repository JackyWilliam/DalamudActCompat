using Dalamud.Plugin.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DalamudActCompat.ActRuntime;

internal sealed class HtmlOverlayForm : IDisposable
{
    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x00000020;
    private const nint WsExLayered = 0x00080000;
    private const nint WsExNoActivate = 0x08000000;
    private static readonly Color TransparencyColor = Color.FromArgb(255, 1, 0, 1);

    private readonly Uri pageUri;
    private readonly string userDataDirectory;
    private readonly string loaderPath;
    private readonly string title;
    private readonly bool overlayMode;
    private readonly IPluginLog log;
    private readonly ManualResetEventSlim ready = new();
    private Thread? uiThread;
    private Form? form;
    private WebView2? webView;
    private bool disposing;
    private Exception? startupFailure;

    public HtmlOverlayForm(
        Uri pageUri,
        string userDataDirectory,
        string loaderPath,
        string title,
        bool overlayMode,
        Size clientSize,
        IPluginLog log)
    {
        this.pageUri = pageUri;
        this.userDataDirectory = userDataDirectory;
        this.loaderPath = loaderPath;
        this.title = title;
        this.overlayMode = overlayMode;
        ClientSize = clientSize;
        this.log = log;
    }

    private Size ClientSize { get; }

    public void Show()
    {
        if (uiThread is null)
        {
            uiThread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"DalamudActCompat HTML Overlay: {title}",
            };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException($"{title} did not create its UI thread within 10 seconds.");
            }

            if (startupFailure is not null)
            {
                throw new InvalidOperationException($"{title} could not be created.", startupFailure);
            }
            return;
        }

        if (form is null)
        {
            throw new InvalidOperationException($"{title} is no longer available.");
        }

        form?.BeginInvoke(() =>
        {
            form.Show();
            form.WindowState = FormWindowState.Normal;
            if (!overlayMode)
            {
                form.Activate();
            }
        });
    }

    private void Run()
    {
        try
        {
            form = new Form
            {
                Text = title,
                ClientSize = ClientSize,
                StartPosition = FormStartPosition.CenterScreen,
                TopMost = overlayMode,
                BackColor = overlayMode ? TransparencyColor : SystemColors.Window,
                TransparencyKey = overlayMode ? TransparencyColor : Color.Empty,
                FormBorderStyle = overlayMode ? FormBorderStyle.None : FormBorderStyle.Sizable,
                ShowInTaskbar = !overlayMode,
            };
            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Transparent,
            };
            webView.NavigationCompleted += OnNavigationCompleted;
            form.Controls.Add(webView);
            form.FormClosing += OnFormClosing;
            form.Shown += async (_, _) =>
            {
                if (overlayMode)
                {
                    EnableMouseClickThrough(form.Handle);
                }

                await InitializeWebViewAsync();
            };
            ready.Set();
            Application.Run(form);
        }
        catch (Exception ex)
        {
            startupFailure = ex;
            ready.Set();
            log.Error(ex, $"HTML overlay window failed: {title}.");
        }
        finally
        {
            if (webView is not null)
            {
                webView.NavigationCompleted -= OnNavigationCompleted;
                if (webView.CoreWebView2 is not null)
                {
                    webView.CoreWebView2.ProcessFailed -= OnProcessFailed;
                }
            }

            webView?.Dispose();
            form?.Dispose();
            webView = null;
            form = null;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (webView is null || webView.CoreWebView2 is not null)
        {
            return;
        }

        try
        {
            if (!File.Exists(loaderPath))
            {
                throw new FileNotFoundException(
                    "The packaged WebView2Loader.dll was not found.",
                    loaderPath);
            }

            NativeLibrary.Load(loaderPath);
            Directory.CreateDirectory(userDataDirectory);
            var options = new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required");
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataDirectory,
                options: options);
            await webView.EnsureCoreWebView2Async(environment);
            var core = webView.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 initialized without a CoreWebView2 instance.");
            core.ProcessFailed += OnProcessFailed;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            webView.Source = pageUri;
            log.Information($"Opened {title}: {webView.Source}");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"HTML overlay WebView2 initialization failed for {title}. Ensure the WebView2 Runtime is installed.");
            MessageBox.Show(
                $"{title} 浏览器初始化失败。请确认 Microsoft Edge WebView2 Runtime 已安装，并查看 Dalamud 日志。",
                "Dalamud ACT Compat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            log.Information($"HTML overlay navigation completed: {webView?.Source}");
            return;
        }

        log.Error(
            $"HTML overlay navigation failed: {args.WebErrorStatus}; URI: {webView?.Source}");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        log.Error($"HTML overlay browser process failed: {args.ProcessFailedKind}; {args.Reason}");
    }

    private static void EnableMouseClickThrough(nint windowHandle)
    {
        var extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle);
        SetWindowLongPtr(
            windowHandle,
            GwlExStyle,
            extendedStyle | WsExTransparent | WsExLayered | WsExNoActivate);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs args)
    {
        if (disposing)
        {
            return;
        }

        args.Cancel = true;
        form?.Hide();
    }

    public void Dispose()
    {
        disposing = true;
        if (form is not null)
        {
            try
            {
                form.Invoke(form.Close);
            }
            catch (InvalidOperationException)
            {
            }
        }

        uiThread?.Join(TimeSpan.FromSeconds(5));
        ready.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newLong);
}
