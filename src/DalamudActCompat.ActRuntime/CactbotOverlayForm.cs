using Dalamud.Plugin.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DalamudActCompat.ActRuntime;

internal sealed class CactbotOverlayForm : IDisposable
{
    private static readonly Color TransparencyColor = Color.FromArgb(255, 1, 0, 1);

    private readonly string raidbossHtmlPath;
    private readonly string webSocketUri;
    private readonly string userDataDirectory;
    private readonly string loaderPath;
    private readonly IPluginLog log;
    private readonly ManualResetEventSlim ready = new();
    private Thread? uiThread;
    private Form? form;
    private WebView2? webView;
    private bool disposing;

    public CactbotOverlayForm(
        string raidbossHtmlPath,
        string webSocketUri,
        string userDataDirectory,
        string loaderPath,
        IPluginLog log)
    {
        this.raidbossHtmlPath = raidbossHtmlPath;
        this.webSocketUri = webSocketUri;
        this.userDataDirectory = userDataDirectory;
        this.loaderPath = loaderPath;
        this.log = log;
    }

    public void Show()
    {
        if (uiThread is null)
        {
            uiThread = new Thread(Run)
            {
                IsBackground = true,
                Name = "DalamudActCompat Cactbot Overlay",
            };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            ready.Wait(TimeSpan.FromSeconds(10));
            return;
        }

        form?.BeginInvoke(() =>
        {
            form.Show();
            form.WindowState = FormWindowState.Normal;
            form.Activate();
        });
    }

    private void Run()
    {
        try
        {
            form = new Form
            {
                Text = "Cactbot Raidboss",
                ClientSize = new Size(900, 320),
                StartPosition = FormStartPosition.CenterScreen,
                TopMost = true,
                BackColor = TransparencyColor,
                TransparencyKey = TransparencyColor,
            };
            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Transparent,
            };
            webView.NavigationCompleted += OnNavigationCompleted;
            form.Controls.Add(webView);
            form.FormClosing += OnFormClosing;
            form.Shown += async (_, _) => await InitializeWebViewAsync();
            ready.Set();
            Application.Run(form);
        }
        catch (Exception ex)
        {
            ready.Set();
            log.Error(ex, "Cactbot overlay window failed.");
        }
        finally
        {
            if (webView is not null)
            {
                webView.NavigationCompleted -= OnNavigationCompleted;
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
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            webView.Source = BuildRaidbossUri();
            log.Information($"Opened Cactbot raidboss overlay: {webView.Source}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Cactbot WebView2 initialization failed. Ensure the WebView2 Runtime is installed.");
            MessageBox.Show(
                "Cactbot 浏览器初始化失败。请确认 Microsoft Edge WebView2 Runtime 已安装，并查看 Dalamud 日志。",
                "Dalamud ACT Compat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private Uri BuildRaidbossUri()
    {
        var builder = new UriBuilder(new Uri(raidbossHtmlPath))
        {
            Query = $"OVERLAY_WS={Uri.EscapeDataString(webSocketUri)}",
        };
        return builder.Uri;
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            log.Information($"Cactbot navigation completed: {webView?.Source}");
            return;
        }

        log.Error(
            $"Cactbot navigation failed: {args.WebErrorStatus}; URI: {webView?.Source}");
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
}
