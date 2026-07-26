using Dalamud.Plugin.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace DalamudActCompat.ActRuntime;

internal sealed class CactbotOverlayForm : IDisposable
{
    private static readonly Color TransparencyColor = Color.FromArgb(255, 1, 0, 1);

    private readonly string htmlPath;
    private readonly string webSocketUri;
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

    public CactbotOverlayForm(
        string htmlPath,
        string webSocketUri,
        string userDataDirectory,
        string loaderPath,
        string title,
        bool overlayMode,
        IPluginLog log)
    {
        this.htmlPath = htmlPath;
        this.webSocketUri = webSocketUri;
        this.userDataDirectory = userDataDirectory;
        this.loaderPath = loaderPath;
        this.title = title;
        this.overlayMode = overlayMode;
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

    public void ShowAlert(string text, int durationMilliseconds = 5000)
    {
        if (webView?.CoreWebView2 is null || form is null)
        {
            log.Warning($"Cactbot alert was dropped before the browser was ready: {text}");
            return;
        }

        form.BeginInvoke(async () =>
        {
            form.Show();
            var textJson = JsonSerializer.Serialize(text);
            var script = $$"""
                (() => {
                  let host = document.getElementById('dalamud-act-compat-alert');
                  if (!host) {
                    host = document.createElement('div');
                    host.id = 'dalamud-act-compat-alert';
                    Object.assign(host.style, {
                      position: 'fixed', left: '50%', top: '18%',
                      transform: 'translateX(-50%)', zIndex: '2147483647',
                      padding: '12px 24px', borderRadius: '8px',
                      background: 'rgba(0, 0, 0, 0.72)', color: '#fff',
                      fontFamily: '"Microsoft YaHei UI", sans-serif',
                      fontSize: '32px', fontWeight: '700',
                      textShadow: '0 2px 4px #000', pointerEvents: 'none'
                    });
                    document.body.appendChild(host);
                  }
                  host.textContent = {{textJson}};
                  host.style.display = 'block';
                  clearTimeout(window.__dalamudActCompatAlertTimer);
                  window.__dalamudActCompatAlertTimer =
                    setTimeout(() => host.style.display = 'none', {{durationMilliseconds}});
                })();
                """;
            try
            {
                await webView.ExecuteScriptAsync(script);
                log.Information($"Cactbot compatibility alert displayed: {text}");
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Could not display Cactbot compatibility alert: {text}");
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
                ClientSize = overlayMode ? new Size(900, 320) : new Size(1100, 760),
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
            core.ProcessFailed += OnProcessFailed;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            webView.Source = BuildUri();
            log.Information($"Opened {title}: {webView.Source}");
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

    private Uri BuildUri()
    {
        // OverlayPlugin explicitly recommends leaving OVERLAY_WS unescaped because
        // a number of overlays parse the URI before URLSearchParams decoding.
        return new Uri($"{new Uri(htmlPath).AbsoluteUri}?OVERLAY_WS={webSocketUri}");
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

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        log.Error($"Cactbot browser process failed: {args.ProcessFailedKind}; {args.Reason}");
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
