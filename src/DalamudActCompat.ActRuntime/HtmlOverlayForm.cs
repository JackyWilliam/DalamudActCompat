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
    private const nint HwndTopMost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const int VirtualKeyLeftButton = 0x01;
    private const int ResizeGripSize = 36;
    private const int DragThreshold = 6;
    private const int MinimumOverlayWidth = 120;
    private const int MinimumOverlayHeight = 80;
    private const string CactbotRenderMessagePrefix = "dalamud-act-compat:cactbot-render:";
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
              #popup-text-container {
                z-index: 2147483646 !important;
                pointer-events: none !important;
              }
              #container:not(.hide-alerts).dalamud-act-compat-alert-repair #popup-text-container {
                display: block !important;
                visibility: visible !important;
                opacity: 1 !important;
                position: fixed !important;
                inset: 0 !important;
                width: 100vw !important;
                height: 100vh !important;
                overflow: visible !important;
              }
              #container:not(.hide-alerts).dalamud-act-compat-alert-repair #popup-text-container .text,
              #container:not(.hide-alerts).dalamud-act-compat-alert-repair #popup-text-container .holder,
              #container:not(.hide-alerts).dalamud-act-compat-alert-repair #popup-text-container .holder > * {
                display: block !important;
                visibility: visible !important;
                opacity: 1 !important;
              }
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

            const report = (detail) => {
              try {
                window.chrome?.webview?.postMessage(
                  'dalamud-act-compat:cactbot-render:' + JSON.stringify(detail));
              } catch (_) {
              }
            };
            const stateOf = (element) => {
              if (!element)
                return null;
              const style = getComputedStyle(element);
              const rect = element.getBoundingClientRect();
              return {
                display: style.display,
                visibility: style.visibility,
                opacity: style.opacity,
                left: Math.round(rect.left),
                top: Math.round(rect.top),
                width: Math.round(rect.width),
                height: Math.round(rect.height),
              };
            };
            const isVisible = (state) => state &&
              state.display !== 'none' &&
              state.visibility !== 'hidden' &&
              Number(state.opacity) > 0 &&
              state.width > 0 &&
              state.height > 0;
            let reportedHealthyAlert = false;
            const inspectAlert = (alert) => requestAnimationFrame(() => {
              const container = document.getElementById('container');
              const popup = document.getElementById('popup-text-container');
              if (!container || !popup || !alert)
                return;

              const alertsDisabled = container.classList.contains('hide-alerts');
              const before = {
                popup: stateOf(popup),
                alert: stateOf(alert),
              };
              const needsRepair = !alertsDisabled &&
                (!isVisible(before.popup) || !isVisible(before.alert));
              if (needsRepair)
                container.classList.add('dalamud-act-compat-alert-repair');

              const detail = {
                alertsDisabled,
                repaired: needsRepair,
                text: (alert.textContent || '').trim(),
                before,
                after: needsRepair ? {
                  popup: stateOf(popup),
                  alert: stateOf(alert),
                } : before,
              };
              if (needsRepair || !reportedHealthyAlert) {
                report(detail);
                reportedHealthyAlert = true;
              }
            });
            for (const holder of document.querySelectorAll('#popup-text-container .holder')) {
              new MutationObserver((mutations) => {
                for (const mutation of mutations) {
                  for (const node of mutation.addedNodes) {
                    if (node instanceof HTMLElement)
                      inspectAlert(node);
                  }
                }
              }).observe(holder, { childList: true });
            }
          };
          if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', install, { once: true });
          else
            install();
        })();
        """;
    internal const string OverlayEditIndicatorScript =
        """
        (() => {
          const install = () => {
            if (document.getElementById('dalamud-act-compat-edit-indicator'))
              return;
            const style = document.createElement('style');
            style.id = 'dalamud-act-compat-edit-indicator';
            style.textContent = `
              html[data-dalamud-act-compat-editing='true'] body::after {
                content: 'ACT Overlay · 编辑模式';
                position: fixed;
                inset: 0;
                box-sizing: border-box;
                border: 2px solid rgba(232, 196, 91, 0.96);
                padding: 5px 8px;
                color: #f7dfa0;
                background: rgba(14, 22, 34, 0.10);
                font: 13px/1.4 sans-serif;
                text-shadow: 0 1px 3px #000;
                pointer-events: none;
                z-index: 2147483647;
              }
              html[data-dalamud-act-compat-editing='true'] body::before {
                content: '';
                position: fixed;
                right: 4px;
                bottom: 4px;
                width: 28px;
                height: 28px;
                box-sizing: border-box;
                background: repeating-linear-gradient(
                  135deg,
                  transparent 0 5px,
                  rgba(247, 223, 160, 0.96) 5px 7px);
                cursor: nwse-resize;
                pointer-events: auto;
                z-index: 2147483647;
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
    private OverlayInteraction pendingInteraction;
    private Point pendingInteractionStartCursor;
    private Rectangle pendingInteractionStartBounds;
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
                    webView.CoreWebView2.WebMessageReceived -= OnBrowserWebMessage;
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
            const int maximumAttempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var options = new CoreWebView2EnvironmentOptions(
                        "--autoplay-policy=no-user-gesture-required");
                    var environment = await CoreWebView2Environment.CreateAsync(
                        userDataFolder: userDataDirectory,
                        options: options);
                    await webView.EnsureCoreWebView2Async(environment);
                    break;
                }
                catch (Exception ex) when (
                    attempt < maximumAttempts && IsTransientWebViewInitializationFailure(ex))
                {
                    log.Warning(
                        ex,
                        $"HTML overlay WebView2 initialization was interrupted for {title}; " +
                        $"retrying ({attempt}/{maximumAttempts - 1}).");
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt));
                }
            }

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
            if (overlayMode)
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    OverlayEditIndicatorScript);
            }
            if (IsCactbotRaidbossPage(pageUri))
            {
                core.WebMessageReceived += OnBrowserWebMessage;
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

    internal static bool IsTransientWebViewInitializationFailure(Exception exception)
        => exception is COMException { HResult: unchecked((int)0x80004004) } ||
           exception.InnerException is not null &&
           IsTransientWebViewInitializationFailure(exception.InnerException);

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

    private void OnBrowserWebMessage(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        string message;
        try
        {
            message = args.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        if (!message.StartsWith(CactbotRenderMessagePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var windowState = form is null
            ? "window=missing"
            : $"windowVisible={form.Visible}, topMost={form.TopMost}, " +
              $"bounds={form.Left},{form.Top},{form.ClientSize.Width},{form.ClientSize.Height}";
        log.Information(
            $"Cactbot alert render state: {message[CactbotRenderMessagePrefix.Length..]}; " +
            windowState);

        if (overlayMode && settings?.IsVisible == true && form?.Visible == true)
        {
            SetWindowPos(
                form.Handle,
                HwndTopMost,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoActivate);
        }
    }

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

    internal static bool HasExceededDragThreshold(Point startCursor, Point currentCursor)
        => Math.Abs(currentCursor.X - startCursor.X) >= DragThreshold ||
           Math.Abs(currentCursor.Y - startCursor.Y) >= DragThreshold;

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
        => !settings.IsClickThrough;

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
            ClearPendingInteraction();
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
        else if (pendingInteraction != OverlayInteraction.None)
        {
            if (!isButtonDown)
            {
                ClearPendingInteraction();
            }
            else if (pendingInteraction == OverlayInteraction.Resize ||
                     HasExceededDragThreshold(pendingInteractionStartCursor, cursor))
            {
                BeginOverlayInteraction(form.Handle);
            }
        }
        else if (ShouldBeginOverlayInteraction(
                     settings.IsEditing,
                     form.Visible,
                     isButtonDown,
                     leftButtonWasDown,
                     form.Bounds.Contains(cursor)))
        {
            pendingInteraction = GetOverlayInteraction(
                form.ClientSize,
                form.PointToClient(cursor));
            pendingInteractionStartCursor = cursor;
            pendingInteractionStartBounds = form.Bounds;
            if (pendingInteraction == OverlayInteraction.Resize)
            {
                BeginOverlayInteraction(form.Handle);
            }
        }

        leftButtonWasDown = isButtonDown;
    }

    private void StopEditMonitor()
    {
        EndOverlayInteraction();
        ClearPendingInteraction();
        if (editMonitor is null)
        {
            return;
        }

        editMonitor.Stop();
        editMonitor.Tick -= OnEditMonitorTick;
        editMonitor.Dispose();
        editMonitor = null;
    }

    private void BeginOverlayInteraction(nint windowHandle)
    {
        if (pendingInteraction == OverlayInteraction.None ||
            !TryAcquireInteraction(windowHandle))
        {
            ClearPendingInteraction();
            return;
        }

        interactionWindowHandle = windowHandle;
        interaction = pendingInteraction;
        interactionStartCursor = pendingInteractionStartCursor;
        interactionStartBounds = pendingInteractionStartBounds;
        ClearPendingInteraction();
        SetCapture(windowHandle);
        ownsMouseCapture = GetCapture() == windowHandle;
        if (!ownsMouseCapture)
        {
            EndOverlayInteraction();
            return;
        }

        log.Debug(
            $"{title} edit interaction started: {interaction}; " +
            $"cursor={interactionStartCursor.X},{interactionStartCursor.Y}; " +
            $"bounds={interactionStartBounds.Left},{interactionStartBounds.Top}," +
            $"{interactionStartBounds.Width},{interactionStartBounds.Height}.");
    }

    private void ClearPendingInteraction()
    {
        pendingInteraction = OverlayInteraction.None;
        pendingInteractionStartCursor = Point.Empty;
        pendingInteractionStartBounds = Rectangle.Empty;
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
                // Native cursor polling owns drag and resize independently of WebView2.
                // Keeping WebView2 enabled while editing lets a click without a drag
                // continue to activate controls in the overlay page.
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
            var isEditing = overlayMode && settings?.IsEditing == true;
            await core.ExecuteScriptAsync(
                "document.documentElement.dataset.dalamudActCompatLocked = " +
                $"'{isLocked.ToString().ToLowerInvariant()}';" +
                "document.documentElement.dataset.dalamudActCompatEditing = " +
                $"'{isEditing.ToString().ToLowerInvariant()}';" +
                "document.dispatchEvent(new CustomEvent('onOverlayStateUpdate', " +
                $"{{ detail: {{ isLocked: {isLocked.ToString().ToLowerInvariant()}, " +
                $"isEditing: {isEditing.ToString().ToLowerInvariant()} }} }}));");
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

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
