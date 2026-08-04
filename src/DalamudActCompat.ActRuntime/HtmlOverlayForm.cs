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
    private const int VirtualKeyLeftButton = 0x01;
    private const int ResizeGripSize = 16;
    private const int MinimumOverlayWidth = 120;
    private const int MinimumOverlayHeight = 80;
    private static readonly Color TransparencyColor = Color.FromArgb(255, 1, 0, 1);
    private static nint interactionOwner;
    internal const string CactbotResponsiveAlertLayoutScript =
        """
        (() => {
          const install = () => {
            if (document.getElementById('dalamud-act-compat-cactbot-layout'))
              return;
            const style = document.createElement('style');
            style.id = 'dalamud-act-compat-cactbot-layout';
            style.textContent = `
              @media (max-height: 220px) {
                #popup-text-info {
                  top: max(0px, calc(100vh - 2.5em)) !important;
                }
              }
              @media (max-height: 140px) {
                #popup-text-alert {
                  top: max(0px, calc(100vh - 3em)) !important;
                }
              }
            `;
            (document.head || document.documentElement).appendChild(style);
          };
          if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', install, { once: true });
          else
            install();
        })();
        """;

    private readonly Uri pageUri;
    private readonly string userDataDirectory;
    private readonly string loaderPath;
    private readonly string title;
    private readonly bool overlayMode;
    private readonly HtmlOverlayWindowSettings? settings;
    private readonly bool debugMode;
    private readonly IPluginLog log;
    private readonly ManualResetEventSlim ready = new();
    private Thread? uiThread;
    private Form? form;
    private WebView2? webView;
    private CoreWebView2DevToolsProtocolEventReceiver? consoleEventReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? exceptionEventReceiver;
    private System.Windows.Forms.Timer? editMonitor;
    private OverlayInteraction interaction;
    private Point interactionStartCursor;
    private Rectangle interactionStartBounds;
    private nint interactionWindowHandle;
    private bool leftButtonWasDown;
    private bool ownsMouseCapture;
    private volatile bool visible;
    private bool disposing;
    private bool applyingSettings;
    private Exception? startupFailure;

    public HtmlOverlayForm(
        Uri pageUri,
        string userDataDirectory,
        string loaderPath,
        string title,
        bool overlayMode,
        HtmlOverlayWindowSettings? settings,
        Size clientSize,
        bool debugMode,
        IPluginLog log)
    {
        this.pageUri = pageUri;
        this.userDataDirectory = userDataDirectory;
        this.loaderPath = loaderPath;
        this.title = title;
        this.overlayMode = overlayMode;
        this.settings = settings;
        ClientSize = clientSize;
        this.debugMode = debugMode;
        this.log = log;
    }

    private Size ClientSize { get; }

    public bool IsVisibleEditing
        => visible && settings?.IsEditing == true;

    public void Show()
    {
        if (settings is not null)
        {
            settings.IsVisible = true;
        }

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
                if (settings is not null)
                {
                    settings.IsVisible = false;
                }
                throw new TimeoutException($"{title} did not create its UI thread within 10 seconds.");
            }

            if (startupFailure is not null)
            {
                if (settings is not null)
                {
                    settings.IsVisible = false;
                }
                throw new InvalidOperationException($"{title} could not be created.", startupFailure);
            }

            return;
        }

        if (form is null)
        {
            if (settings is not null)
            {
                settings.IsVisible = false;
            }
            throw new InvalidOperationException($"{title} is no longer available.");
        }

        try
        {
            form.BeginInvoke(() =>
            {
                form.Show();
                visible = true;
                if (settings is not null)
                {
                    settings.IsVisible = true;
                }
                form.WindowState = FormWindowState.Normal;
                if (overlayMode)
                {
                    ApplyOverlaySettings();
                }
                else
                {
                    form.Activate();
                }
            });
        }
        catch
        {
            if (settings is not null)
            {
                settings.IsVisible = false;
            }
            throw;
        }
    }

    public void Hide()
    {
        visible = false;
        if (settings is not null)
        {
            settings.IsVisible = false;
        }

        if (form is null)
        {
            return;
        }

        form.BeginInvoke(() =>
        {
            StopEditMonitor();
            form.Hide();
        });
    }

    public void ApplySettings()
    {
        if (!overlayMode || form is null)
        {
            return;
        }

        form.BeginInvoke(ApplyOverlaySettings);
    }

    private void Run()
    {
        try
        {
            var initialSize = new Size(
                settings?.Width is > 0 ? settings.Width.Value : ClientSize.Width,
                settings?.Height is > 0 ? settings.Height.Value : ClientSize.Height);
            form = overlayMode ? new OverlayHostForm() : new Form();
            form.Text = title;
            form.ClientSize = initialSize;
            form.MinimumSize = new Size(MinimumOverlayWidth, MinimumOverlayHeight);
            form.StartPosition = overlayMode && settings?.Left is not null && settings.Top is not null
                ? FormStartPosition.Manual
                : FormStartPosition.CenterScreen;
            form.TopMost = overlayMode;
            form.BackColor = overlayMode ? TransparencyColor : SystemColors.Window;
            form.TransparencyKey = overlayMode ? TransparencyColor : Color.Empty;
            form.FormBorderStyle = overlayMode ? FormBorderStyle.None : FormBorderStyle.Sizable;
            form.ShowInTaskbar = !overlayMode;
            if (form.StartPosition == FormStartPosition.Manual)
            {
                form.Location = new Point(settings!.Left!.Value, settings.Top!.Value);
                if (!Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(form.Bounds)))
                {
                    form.StartPosition = FormStartPosition.CenterScreen;
                }
            }

            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Transparent,
            };
            webView.NavigationCompleted += OnNavigationCompleted;
            form.Controls.Add(webView);
            form.FormClosing += OnFormClosing;
            form.LocationChanged += OnOverlayBoundsChanged;
            form.SizeChanged += OnOverlayBoundsChanged;
            form.Shown += async (_, _) =>
            {
                visible = true;
                if (settings is not null)
                {
                    settings.IsVisible = true;
                }
                ready.Set();
                if (overlayMode)
                {
                    ApplyOverlaySettings();
                }

                await InitializeWebViewAsync();
            };
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
            visible = false;
            if (settings is not null)
            {
                settings.IsVisible = false;
            }
            StopEditMonitor();
            if (webView is not null)
            {
                webView.NavigationCompleted -= OnNavigationCompleted;
                if (webView.CoreWebView2 is not null)
                {
                    webView.CoreWebView2.ProcessFailed -= OnProcessFailed;
                }
                if (consoleEventReceiver is not null)
                {
                    consoleEventReceiver.DevToolsProtocolEventReceived -= OnBrowserConsoleMessage;
                }
                if (exceptionEventReceiver is not null)
                {
                    exceptionEventReceiver.DevToolsProtocolEventReceived -= OnBrowserException;
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
            core.Settings.AreDevToolsEnabled = debugMode;
            core.Settings.IsStatusBarEnabled = false;
            if (debugMode)
            {
                try
                {
                    await EnableBrowserDiagnosticsAsync(core);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, $"Could not enable browser diagnostics for {title}.");
                }
            }
            if (overlayMode && settings is not null)
            {
                webView.ZoomFactor = Math.Clamp(settings.ZoomFactor, 0.25f, 5.0f);
            }
            if (IsCactbotRaidbossPage(pageUri))
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    CactbotResponsiveAlertLayoutScript);
            }

            webView.Source = pageUri;
            log.Information($"Opened {title}: {webView.Source}");
        }
        catch (Exception ex)
        {
            log.Error(
                ex,
                $"HTML overlay WebView2 initialization failed for {title}. Ensure the WebView2 Runtime is installed.");
            MessageBox.Show(
                $"{title} 浏览器初始化失败。请确认 Microsoft Edge WebView2 Runtime 已安装，并查看 Dalamud 日志。",
                "Dalamud ACT Compat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            log.Information($"HTML overlay navigation completed: {webView?.Source}");
            await NotifyOverlayStateAsync();
            return;
        }

        log.Error(
            $"HTML overlay navigation failed: {args.WebErrorStatus}; URI: {webView?.Source}");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        log.Error($"HTML overlay browser process failed: {args.ProcessFailedKind}; {args.Reason}");
    }

    private async Task EnableBrowserDiagnosticsAsync(CoreWebView2 core)
    {
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        consoleEventReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
        consoleEventReceiver.DevToolsProtocolEventReceived += OnBrowserConsoleMessage;
        exceptionEventReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
        exceptionEventReceiver.DevToolsProtocolEventReceived += OnBrowserException;
        log.Information($"HTML overlay browser diagnostics enabled: {title}.");
    }

    private void OnBrowserConsoleMessage(
        object? sender,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        => log.Debug($"{title} browser console: {args.ParameterObjectAsJson}");

    private void OnBrowserException(
        object? sender,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        => log.Error($"{title} browser exception: {args.ParameterObjectAsJson}");

    internal static OverlayInteraction GetOverlayInteraction(
        Size clientSize,
        Point clientPoint)
    {
        return clientPoint.X >= clientSize.Width - ResizeGripSize &&
               clientPoint.Y >= clientSize.Height - ResizeGripSize
            ? OverlayInteraction.Resize
            : OverlayInteraction.Move;
    }

    internal static bool ShouldBeginOverlayInteraction(
        bool isEditing,
        bool isVisible,
        bool isButtonDown,
        bool wasButtonDown,
        bool cursorInside)
        => isEditing &&
           isVisible &&
           isButtonDown &&
           !wasButtonDown &&
           cursorInside;

    internal static Rectangle CalculateInteractionBounds(
        Rectangle startBounds,
        Point startCursor,
        Point currentCursor,
        OverlayInteraction interaction)
    {
        var deltaX = currentCursor.X - startCursor.X;
        var deltaY = currentCursor.Y - startCursor.Y;
        return interaction switch
        {
            OverlayInteraction.Move => new Rectangle(
                startBounds.X + deltaX,
                startBounds.Y + deltaY,
                startBounds.Width,
                startBounds.Height),
            OverlayInteraction.Resize => new Rectangle(
                startBounds.Location,
                new Size(
                    Math.Max(MinimumOverlayWidth, startBounds.Width + deltaX),
                    Math.Max(MinimumOverlayHeight, startBounds.Height + deltaY))),
            _ => startBounds,
        };
    }

    internal static bool ShouldEnableBrowserInput(HtmlOverlayWindowSettings settings)
        => !settings.IsEditing;

    private void StartEditMonitor()
    {
        if (editMonitor is not null)
        {
            return;
        }

        leftButtonWasDown = IsLeftButtonDown();
        editMonitor = new System.Windows.Forms.Timer
        {
            Interval = 15,
        };
        editMonitor.Tick += OnEditMonitorTick;
        editMonitor.Start();
        log.Information($"{title} edit mode enabled; native cursor polling is active.");
    }

    private void OnEditMonitorTick(object? sender, EventArgs args)
    {
        if (form is null || settings?.IsEditing != true)
        {
            StopEditMonitor();
            return;
        }

        var isButtonDown = IsLeftButtonDown();
        if (!GetCursorPos(out var nativeCursor))
        {
            EndOverlayInteraction();
            leftButtonWasDown = isButtonDown;
            return;
        }

        var cursor = new Point(nativeCursor.X, nativeCursor.Y);
        if (interaction != OverlayInteraction.None)
        {
            if (!isButtonDown || GetCapture() != form.Handle)
            {
                EndOverlayInteraction();
            }
            else
            {
                var bounds = CalculateInteractionBounds(
                    interactionStartBounds,
                    interactionStartCursor,
                    cursor,
                    interaction);
                if (interaction == OverlayInteraction.Move)
                {
                    form.Location = bounds.Location;
                }
                else
                {
                    form.ClientSize = bounds.Size;
                }
            }
        }
        else if (ShouldBeginOverlayInteraction(
                     settings.IsEditing,
                     form.Visible,
                     isButtonDown,
                     leftButtonWasDown,
                     form.Bounds.Contains(cursor)) &&
                 TryAcquireInteraction(form.Handle))
        {
            interactionWindowHandle = form.Handle;
            interaction = GetOverlayInteraction(
                form.ClientSize,
                form.PointToClient(cursor));
            interactionStartCursor = cursor;
            interactionStartBounds = form.Bounds;
            SetCapture(form.Handle);
            ownsMouseCapture = GetCapture() == form.Handle;
            if (!ownsMouseCapture)
            {
                EndOverlayInteraction();
            }
            else
            {
                log.Debug(
                    $"{title} edit interaction started: {interaction}; " +
                    $"cursor={cursor.X},{cursor.Y}; " +
                    $"bounds={form.Left},{form.Top},{form.Width},{form.Height}.");
            }
        }

        leftButtonWasDown = isButtonDown;
    }

    private void StopEditMonitor()
    {
        EndOverlayInteraction();
        if (editMonitor is null)
        {
            return;
        }

        editMonitor.Stop();
        editMonitor.Tick -= OnEditMonitorTick;
        editMonitor.Dispose();
        editMonitor = null;
    }

    internal static bool TryAcquireInteraction(nint windowHandle)
        => windowHandle != nint.Zero &&
           Interlocked.CompareExchange(
               ref interactionOwner,
               windowHandle,
               nint.Zero) == nint.Zero;

    internal static void ReleaseInteraction(nint windowHandle)
    {
        if (windowHandle != nint.Zero)
        {
            Interlocked.CompareExchange(
                ref interactionOwner,
                nint.Zero,
                windowHandle);
        }
    }

    private void EndOverlayInteraction()
    {
        var windowHandle = interactionWindowHandle;
        if (ownsMouseCapture && GetCapture() == windowHandle)
        {
            ReleaseCapture();
        }

        if (interaction != OverlayInteraction.None && form is not null)
        {
            log.Debug(
                $"{title} edit interaction ended; " +
                $"bounds={form.Left},{form.Top},{form.Width},{form.Height}.");
        }

        ownsMouseCapture = false;
        interaction = OverlayInteraction.None;
        interactionWindowHandle = nint.Zero;
        ReleaseInteraction(windowHandle);
    }

    private static bool IsLeftButtonDown()
        => (GetAsyncKeyState(VirtualKeyLeftButton) & 0x8000) != 0;

    private void ApplyOverlaySettings()
    {
        if (!overlayMode || form is null || settings is null)
        {
            return;
        }

        applyingSettings = true;
        try
        {
            if (settings.Width is > 0 && settings.Height is > 0)
            {
                form.ClientSize = new Size(settings.Width.Value, settings.Height.Value);
            }

            if (settings.Left is not null && settings.Top is not null)
            {
                var candidate = new Rectangle(
                    settings.Left.Value,
                    settings.Top.Value,
                    form.Width,
                    form.Height);
                if (Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(candidate)))
                {
                    form.Location = candidate.Location;
                }
            }

            if (webView is not null)
            {
                webView.ZoomFactor = Math.Clamp(settings.ZoomFactor, 0.25f, 5.0f);
                // OverlayPlugin's Chromium renderer is off-screen, so its Form receives
                // edit-mode mouse input directly. Windowed WebView2 owns a child HWND;
                // disabling it only while editing restores the same host input routing.
                webView.Enabled = ShouldEnableBrowserInput(settings);
            }

            var extendedStyle = GetWindowLongPtr(form.Handle, GwlExStyle);
            extendedStyle |= WsExLayered | WsExNoActivate;
            extendedStyle = settings.IsClickThrough
                ? extendedStyle | WsExTransparent
                : extendedStyle & ~WsExTransparent;
            SetWindowLongPtr(form.Handle, GwlExStyle, extendedStyle);
            if (settings.IsEditing)
            {
                StartEditMonitor();
            }
            else
            {
                StopEditMonitor();
            }
            _ = NotifyOverlayStateAsync();
        }
        finally
        {
            applyingSettings = false;
        }
    }

    private async Task NotifyOverlayStateAsync()
    {
        var core = webView?.CoreWebView2;
        if (core is null)
        {
            return;
        }

        try
        {
            var isLocked = !overlayMode || settings?.IsLocked != false;
            await core.ExecuteScriptAsync(
                "document.documentElement.dataset.dalamudActCompatLocked = " +
                $"'{isLocked.ToString().ToLowerInvariant()}';" +
                "document.dispatchEvent(new CustomEvent('onOverlayStateUpdate', " +
                $"{{ detail: {{ isLocked: {isLocked.ToString().ToLowerInvariant()} }} }}));");
        }
        catch (Exception) when (disposing)
        {
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to notify {title} about its OverlayPlugin lock state.");
        }
    }

    internal static bool IsCactbotRaidbossPage(Uri uri)
        => uri.IsFile &&
           uri.LocalPath.Replace('\\', '/')
               .EndsWith("/ui/raidboss/raidboss.html", StringComparison.OrdinalIgnoreCase);

    private void OnOverlayBoundsChanged(object? sender, EventArgs args)
    {
        if (!overlayMode || settings is null || form is null || applyingSettings ||
            form.WindowState != FormWindowState.Normal)
        {
            return;
        }

        settings.Left = form.Left;
        settings.Top = form.Top;
        settings.Width = form.ClientSize.Width;
        settings.Height = form.ClientSize.Height;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs args)
    {
        visible = false;
        if (settings is not null)
        {
            settings.IsVisible = false;
        }
        StopEditMonitor();
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    internal enum OverlayInteraction
    {
        None,
        Move,
        Resize,
    }

    private sealed class OverlayHostForm : Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= unchecked((int)(WsExLayered | WsExNoActivate));
                return parameters;
            }
        }
    }
}
