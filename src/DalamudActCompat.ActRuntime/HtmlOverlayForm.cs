using Dalamud.Plugin.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Collections.Concurrent;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace DalamudActCompat.ActRuntime;

internal sealed class HtmlOverlayForm : IDisposable
{
    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x00000020;
    private const nint WsExToolWindow = 0x00000080;
    private const nint WsExAppWindow = 0x00040000;
    private const nint WsExLayered = 0x00080000;
    private const nint WsExNoActivate = 0x08000000;
    private const nint HwndTopMost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int VirtualKeyLeftButton = 0x01;
    private const int ResizeGripSize = 36;
    private const int DragThreshold = 6;
    private const int MinimumOverlayWidth = 120;
    private const int MinimumOverlayHeight = 80;
    private const int MaximumBrowserInputRegions = 2048;
    private const int MaximumWebViewInitializationAttempts = 6;
    private const int WindowMessageNonClientHitTest = 0x0084;
    private const int HitTestTransparent = -1;
    private const string CactbotRenderMessagePrefix = "dalamud-act-compat:cactbot-render:";
    private const string InputRegionMessagePrefix = "dalamud-act-compat:input-regions:";
    private const string InputRegionDiagnosticMessagePrefix =
        "dalamud-act-compat:input-region-diagnostic:";
    private static readonly Color TransparencyColor = Color.FromArgb(255, 1, 0, 1);
    private static readonly SemaphoreSlim WebViewInitializationGate = new(1, 1);
    private static readonly object LoaderSync = new();
    private static nint interactionOwner;
    private static nint loaderHandle;
    private static string? loadedLoaderPath;
    private static int loaderUserCount;
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
    internal const string OverlayInputRegionScript =
        """
        (() => {
          const install = () => {
            if (window.__dalamudActCompatInputRegionsInstalled)
              return;
            window.__dalamudActCompatInputRegionsInstalled = true;

            const prefix = 'dalamud-act-compat:input-regions:';
            const maximumRegions = 2048;
            let scheduled = false;
            let lastPayload = '';
            let diagnostics = null;
            if (window.__dalamudActCompatInputRegionDiagnostics === true) {
              diagnostics = {
                windowStarted: performance.now(),
                refreshCount: 0,
                refreshMilliseconds: 0,
                peakRefreshMilliseconds: 0,
                scannedElements: 0,
                peakScannedElements: 0,
                finalRectangles: 0,
                peakFinalRectangles: 0,
                regionChanges: 0,
                currentScannedElements: 0,
              };
            }

            const hasVisibleColor = (value) => {
              if (!value || value === 'transparent')
                return false;
              if (value.startsWith('rgba')) {
                const match = value.match(/,\s*([0-9.]+)\s*\)$/);
                if (match)
                  return Number(match[1]) > 0.01;
              }
              const slashAlpha = value.match(/\/\s*([0-9.]+)%?\s*\)$/);
              if (slashAlpha) {
                const alpha = Number(slashAlpha[1]);
                return value.includes('%') ? alpha > 1 : alpha > 0.01;
              }
              return true;
            };

            const addRect = (rectangles, rect) => {
              const left = Math.max(0, rect.left);
              const top = Math.max(0, rect.top);
              const right = Math.min(window.innerWidth, rect.right);
              const bottom = Math.min(window.innerHeight, rect.bottom);
              if (right - left < 0.5 || bottom - top < 0.5)
                return;
              const round = (value) => Math.round(value * 100) / 100;
              rectangles.push([
                round(left),
                round(top),
                round(right - left),
                round(bottom - top),
              ]);
            };

            const isRendered = (element, style) => {
              if (style.display === 'none' || style.visibility === 'hidden' ||
                  style.visibility === 'collapse' || Number(style.opacity) <= 0.01)
                return false;
              if (typeof element.checkVisibility === 'function') {
                try {
                  if (!element.checkVisibility({
                    checkOpacity: true,
                    checkVisibilityCSS: true,
                  }))
                    return false;
                } catch (_) {
                }
              }
              return element.getClientRects().length > 0;
            };

            const paintsBox = (element, style) => {
              const tag = element.tagName.toLowerCase();
              if (['img', 'canvas', 'svg', 'video', 'iframe', 'input', 'button',
                   'select', 'textarea', 'meter', 'progress'].includes(tag))
                return true;
              if (hasVisibleColor(style.backgroundColor) || style.backgroundImage !== 'none' ||
                  style.boxShadow !== 'none' || style.filter !== 'none')
                return true;
              if (parseFloat(style.outlineWidth) > 0 && style.outlineStyle !== 'none' &&
                  hasVisibleColor(style.outlineColor))
                return true;
              for (const side of ['Top', 'Right', 'Bottom', 'Left']) {
                if (parseFloat(style[`border${side}Width`]) > 0 &&
                    style[`border${side}Style`] !== 'none' &&
                    hasVisibleColor(style[`border${side}Color`]))
                  return true;
              }
              return element.matches(
                       'a[href],button,input,select,textarea,[contenteditable="true"],' +
                       '[onclick],[role="button"],[role="link"],[tabindex]:not([tabindex="-1"])') ||
                     (style.pointerEvents !== 'none' && style.cursor === 'pointer');
            };

            const collectRegions = () => {
              const rectangles = [];
              const elements = document.querySelectorAll('*');
              if (diagnostics !== null)
                diagnostics.currentScannedElements = elements.length;
              for (const element of elements) {
                const style = getComputedStyle(element);
                if (!isRendered(element, style))
                  continue;

                if (paintsBox(element, style)) {
                  for (const rect of element.getClientRects())
                    addRect(rectangles, rect);
                }

                if (hasVisibleColor(style.color)) {
                  for (const node of element.childNodes) {
                    if (node.nodeType !== Node.TEXT_NODE || !(node.textContent || '').trim())
                      continue;
                    const range = document.createRange();
                    range.selectNodeContents(node);
                    for (const rect of range.getClientRects())
                      addRect(rectangles, rect);
                    range.detach();
                  }
                }
              }

              rectangles.sort((left, right) =>
                right[2] * right[3] - left[2] * left[3]);
              const regions = [];
              for (const rect of rectangles) {
                const contained = regions.some((outer) =>
                  rect[0] >= outer[0] - 0.5 && rect[1] >= outer[1] - 0.5 &&
                  rect[0] + rect[2] <= outer[0] + outer[2] + 0.5 &&
                  rect[1] + rect[3] <= outer[1] + outer[3] + 0.5);
                if (!contained)
                  regions.push(rect);
                if (regions.length >= maximumRegions)
                  break;
              }
              return regions;
            };

            const report = () => {
              scheduled = false;
              if (diagnostics === null) {
                const payload = JSON.stringify({
                  viewportWidth: window.innerWidth,
                  viewportHeight: window.innerHeight,
                  rectangles: collectRegions(),
                });
                if (payload === lastPayload)
                  return;
                lastPayload = payload;
                try {
                  window.chrome?.webview?.postMessage(prefix + payload);
                } catch (_) {
                }
                return;
              }

              const startedAt = performance.now();
              const regions = collectRegions();
              const payload = JSON.stringify({
                viewportWidth: window.innerWidth,
                viewportHeight: window.innerHeight,
                rectangles: regions,
              });
              const elapsedMilliseconds = performance.now() - startedAt;
              diagnostics.refreshCount++;
              diagnostics.refreshMilliseconds += elapsedMilliseconds;
              diagnostics.peakRefreshMilliseconds = Math.max(
                diagnostics.peakRefreshMilliseconds,
                elapsedMilliseconds);
              diagnostics.scannedElements += diagnostics.currentScannedElements;
              diagnostics.peakScannedElements = Math.max(
                diagnostics.peakScannedElements,
                diagnostics.currentScannedElements);
              diagnostics.finalRectangles += regions.length;
              diagnostics.peakFinalRectangles = Math.max(
                diagnostics.peakFinalRectangles,
                regions.length);
              if (payload !== lastPayload)
                diagnostics.regionChanges++;

              const now = performance.now();
              const diagnosticWindowMilliseconds = now - diagnostics.windowStarted;
              if (diagnosticWindowMilliseconds >= 10000) {
                try {
                  window.chrome?.webview?.postMessage(
                    'dalamud-act-compat:input-region-diagnostic:' + JSON.stringify({
                      windowMilliseconds: diagnosticWindowMilliseconds,
                      refreshCount: diagnostics.refreshCount,
                      totalRefreshMilliseconds: diagnostics.refreshMilliseconds,
                      peakRefreshMilliseconds: diagnostics.peakRefreshMilliseconds,
                      totalScannedElements: diagnostics.scannedElements,
                      peakScannedElements: diagnostics.peakScannedElements,
                      totalFinalRectangles: diagnostics.finalRectangles,
                      peakFinalRectangles: diagnostics.peakFinalRectangles,
                      regionChanges: diagnostics.regionChanges,
                    }));
                } catch (_) {
                }
                diagnostics.windowStarted = now;
                diagnostics.refreshCount = 0;
                diagnostics.refreshMilliseconds = 0;
                diagnostics.peakRefreshMilliseconds = 0;
                diagnostics.scannedElements = 0;
                diagnostics.peakScannedElements = 0;
                diagnostics.finalRectangles = 0;
                diagnostics.peakFinalRectangles = 0;
                diagnostics.regionChanges = 0;
              }

              if (payload === lastPayload)
                return;
              lastPayload = payload;
              try {
                window.chrome?.webview?.postMessage(prefix + payload);
              } catch (_) {
              }
            };

            const schedule = () => {
              if (scheduled)
                return;
              scheduled = true;
              requestAnimationFrame(report);
            };

            const observed = new WeakSet();
            const resizeObserver = new ResizeObserver(schedule);
            const observeTree = (root) => {
              if (!(root instanceof Element))
                return;
              if (!observed.has(root)) {
                observed.add(root);
                resizeObserver.observe(root);
              }
              for (const element of root.querySelectorAll('*')) {
                if (!observed.has(element)) {
                  observed.add(element);
                  resizeObserver.observe(element);
                }
              }
            };
            const unobserveTree = (root) => {
              if (!(root instanceof Element))
                return;
              if (observed.delete(root))
                resizeObserver.unobserve(root);
              for (const element of root.querySelectorAll('*')) {
                if (observed.delete(element))
                  resizeObserver.unobserve(element);
              }
            };
            observeTree(document.documentElement);
            new MutationObserver((mutations) => {
              for (const mutation of mutations) {
                for (const node of mutation.addedNodes)
                  observeTree(node);
                for (const node of mutation.removedNodes)
                  unobserveTree(node);
              }
              schedule();
            }).observe(document.documentElement, {
              attributes: true,
              childList: true,
              characterData: true,
              subtree: true,
            });
            window.addEventListener('resize', schedule);
            window.addEventListener('scroll', schedule, true);
            window.addEventListener('load', schedule, true);
            document.fonts?.ready?.then(schedule).catch(() => {});
            window.setInterval(schedule, 1000);
            schedule();
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
    private readonly Action<string>? failureNotification;
    private readonly ManualResetEventSlim ready = new();
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly CancellationTokenSource lifecycleCancellation = new();
    private readonly TaskCompletionSource<bool> shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, byte> browserProcessIds = new();
    private Thread? uiThread;
    private Form? form;
    private Form? inputProxy;
    private EditChromeForm? editChrome;
    private WebView2? webView;
    private BrowserInputRegion? browserInputRegion;
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
    private bool loaderAcquired;
    private Exception? startupFailure;
    private BrowserState browserState;
    private string browserStateDetail = string.Empty;
    private int recoveryScheduled;
    private int browserRecoveryCount;
    private int disposeStarted;
    private int shutdownFinalized;
    private Task? initializationTask;
    private bool navigationStarted;
    private long regionRebuildWindowStarted = Stopwatch.GetTimestamp();
    private int regionRebuildCount;
    private int regionRebuildRectangleCount;
    private int regionRebuildPeakRectangles;
    private double regionRebuildTotalMilliseconds;
    private double regionRebuildPeakMilliseconds;

    public HtmlOverlayForm(
        Uri pageUri,
        string userDataDirectory,
        string loaderPath,
        string title,
        bool overlayMode,
        HtmlOverlayWindowSettings? settings,
        Size clientSize,
        bool debugMode,
        IPluginLog log,
        Action<string>? failureNotification = null)
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
        this.failureNotification = failureNotification;
    }

    private Size ClientSize { get; }

    public bool IsVisibleEditing
        => visible && settings?.IsEditing == true;

    public IReadOnlyCollection<int> BrowserProcessIds => browserProcessIds.Keys.ToArray();

    public Task ShutdownCompletion => shutdownCompletion.Task;

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
            form.BeginInvoke(async () =>
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

                if (browserState == BrowserState.Failed)
                {
                    await RetryFailedWebViewAsync();
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
            inputProxy?.Hide();
            editChrome?.Hide();
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

            webView = CreateWebViewControl();
            form.Controls.Add(webView);
            if (overlayMode)
            {
                inputProxy = CreateInputProxy();
                editChrome = CreateEditChrome();
            }
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
                if (disposing)
                {
                    form.Close();
                    return;
                }
                if (overlayMode)
                {
                    ApplyOverlaySettings();
                }

                await TrackInitializationAsync();
            };
            if (disposing)
            {
                ready.Set();
                return;
            }
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
            try
            {
                visible = false;
                if (settings is not null)
                {
                    settings.IsVisible = false;
                }
                StopEditMonitor();
                DetachWebViewEvents(webView);

                if (inputProxy is not null)
                {
                    inputProxy.MouseClick -= OnInputProxyMouseClick;
                    var inputRegion = inputProxy.Region;
                    inputProxy.Region = null;
                    inputRegion?.Dispose();
                    inputProxy.Dispose();
                }
                editChrome?.Dispose();
                webView?.Dispose();
                form?.Dispose();
                inputProxy = null;
                editChrome = null;
                webView = null;
                form = null;
                browserInputRegion = null;
            }
            finally
            {
                CompleteShutdownAfterInitialization();
            }
        }
    }

    private WebView2 CreateWebViewControl()
    {
        var control = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.Transparent,
        };
        control.NavigationCompleted += OnNavigationCompleted;
        return control;
    }

    private void ReplaceWebViewControl()
    {
        if (disposing || form is null)
        {
            throw new OperationCanceledException(lifecycleCancellation.Token);
        }

        var previous = webView;
        DetachWebViewEvents(previous);
        if (previous is not null)
        {
            form.Controls.Remove(previous);
            previous.Dispose();
        }

        browserInputRegion = null;
        navigationStarted = false;
        webView = CreateWebViewControl();
        form.Controls.Add(webView);
        webView.BringToFront();
        ApplyInputProxySettings();
    }

    private void DetachWebViewEvents(WebView2? control)
    {
        if (control is not null)
        {
            control.NavigationCompleted -= OnNavigationCompleted;
            if (control.CoreWebView2 is not null)
            {
                control.CoreWebView2.ProcessFailed -= OnProcessFailed;
                control.CoreWebView2.WebMessageReceived -= OnBrowserWebMessage;
            }
        }

        if (consoleEventReceiver is not null)
        {
            consoleEventReceiver.DevToolsProtocolEventReceived -= OnBrowserConsoleMessage;
            consoleEventReceiver = null;
        }
        if (exceptionEventReceiver is not null)
        {
            exceptionEventReceiver.DevToolsProtocolEventReceived -= OnBrowserException;
            exceptionEventReceiver = null;
        }
    }

    private void AcquireLoader()
    {
        if (loaderAcquired)
        {
            return;
        }

        lock (LoaderSync)
        {
            var fullPath = Path.GetFullPath(loaderPath);
            if (loaderHandle == nint.Zero)
            {
                loaderHandle = NativeLibrary.Load(fullPath);
                loadedLoaderPath = fullPath;
            }
            else if (!string.Equals(
                         loadedLoaderPath,
                         fullPath,
                         StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"WebView2Loader.dll is already loaded from '{loadedLoaderPath}'.");
            }

            loaderUserCount++;
            loaderAcquired = true;
        }
    }

    private void ReleaseLoader()
    {
        if (!loaderAcquired)
        {
            return;
        }

        lock (LoaderSync)
        {
            loaderAcquired = false;
            loaderUserCount = Math.Max(0, loaderUserCount - 1);
            if (loaderUserCount != 0 || loaderHandle == nint.Zero)
            {
                return;
            }

            NativeLibrary.Free(loaderHandle);
            loaderHandle = nint.Zero;
            loadedLoaderPath = null;
        }
    }

    private void SetBrowserState(BrowserState state, string detail)
    {
        browserState = state;
        browserStateDetail = detail;
        ApplyInputProxySettings();
        ApplyEditChromeSettings();
    }

    private Task TrackInitializationAsync()
    {
        var task = InitializeWebViewAsync();
        while (true)
        {
            var previousInitialization = Volatile.Read(ref initializationTask);
            var trackedInitialization = CombineInitializationTasks(previousInitialization, task);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref initializationTask,
                        trackedInitialization,
                        previousInitialization),
                    previousInitialization))
            {
                return task;
            }
        }
    }

    internal static Task CombineInitializationTasks(Task? previousInitialization, Task currentInitialization)
        => previousInitialization is { IsCompleted: false }
            ? Task.WhenAll(previousInitialization, currentInitialization)
            : currentInitialization;

    private async Task RetryFailedWebViewAsync()
    {
        if (disposing || browserState != BrowserState.Failed ||
            Volatile.Read(ref recoveryScheduled) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref browserRecoveryCount, 0);
        try
        {
            var core = webView?.CoreWebView2;
            if (core is not null && navigationStarted)
            {
                try
                {
                    core.Reload();
                    SetBrowserState(BrowserState.Navigating, "正在重新加载页面");
                    return;
                }
                catch (Exception ex)
                {
                    log.Warning(ex, $"Could not reload the failed page for {title}; rebuilding WebView2.");
                }
            }

            ReplaceWebViewControl();
            await TrackInitializationAsync();
        }
        catch (OperationCanceledException) when (disposing)
        {
        }
        catch (Exception ex)
        {
            var message = DescribeWebViewInitializationFailure(ex);
            SetBrowserState(BrowserState.Failed, message);
            log.Error(ex, $"HTML overlay WebView2 retry failed for {title}: {message}");
            failureNotification?.Invoke($"{title}：{message}");
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (disposing)
        {
            return;
        }

        var cancellationToken = lifecycleCancellation.Token;
        var gateAcquired = false;
        try
        {
            await initializationGate.WaitAsync(cancellationToken);
            gateAcquired = true;
            if (disposing || webView?.CoreWebView2 is not null)
            {
                return;
            }

            SetBrowserState(BrowserState.Initializing, "正在初始化浏览器");
            if (!File.Exists(loaderPath))
            {
                throw new FileNotFoundException(
                    "The packaged WebView2Loader.dll was not found.",
                    loaderPath);
            }

            AcquireLoader();
            Directory.CreateDirectory(userDataDirectory);
            for (var attempt = 1; attempt <= MaximumWebViewInitializationAttempts; attempt++)
            {
                Exception? retryFailure = null;
                await WebViewInitializationGate.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentWebView = webView
                        ?? throw new OperationCanceledException(cancellationToken);
                    var options = new CoreWebView2EnvironmentOptions(
                        "--autoplay-policy=no-user-gesture-required");
                    var environment = await CoreWebView2Environment.CreateAsync(
                        userDataFolder: userDataDirectory,
                        options: options);
                    await currentWebView.EnsureCoreWebView2Async(environment);
                    break;
                }
                catch (Exception ex) when (
                    attempt < MaximumWebViewInitializationAttempts &&
                    IsTransientWebViewInitializationFailure(ex))
                {
                    retryFailure = ex;
                }
                finally
                {
                    WebViewInitializationGate.Release();
                }

                if (retryFailure is not null)
                {
                    log.Warning(
                        retryFailure,
                        $"HTML overlay WebView2 initialization was interrupted for {title}; " +
                        $"recreating the control and retrying " +
                        $"({attempt}/{MaximumWebViewInitializationAttempts - 1}).");
                    ReplaceWebViewControl();
                    var delayMilliseconds = 250 * (1 << Math.Min(attempt - 1, 4));
                    await Task.Delay(delayMilliseconds, cancellationToken);
                }
            }

            var core = webView?.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 initialized without a CoreWebView2 instance.");
            browserProcessIds.TryAdd(checked((int)core.BrowserProcessId), 0);
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
                core.WebMessageReceived += OnBrowserWebMessage;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    $"window.__dalamudActCompatInputRegionDiagnostics = " +
                    $"{(debugMode ? "true" : "false")};");
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    OverlayInputRegionScript);
            }
            if (IsCactbotRaidbossPage(pageUri))
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    CactbotResponsiveAlertLayoutScript);
            }

            SetBrowserState(BrowserState.Ready, "浏览器已就绪");
            webView!.Source = pageUri;
            navigationStarted = true;
            SetBrowserState(BrowserState.Navigating, "页面加载中");
            ApplyInputProxySettings();
            log.Information($"Opened {title}: {webView.Source}");
        }
        catch (OperationCanceledException) when (disposing || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var message = DescribeWebViewInitializationFailure(ex);
            SetBrowserState(BrowserState.Failed, message);
            log.Error(ex, $"HTML overlay WebView2 initialization failed for {title}: {message}");
            failureNotification?.Invoke($"{title}：{message}");
        }
        finally
        {
            if (gateAcquired)
            {
                initializationGate.Release();
            }
        }
    }

    internal static bool IsTransientWebViewInitializationFailure(Exception exception)
        => ExceptionChainAny(
            exception,
            static candidate => candidate is COMException
            {
                HResult: unchecked((int)0x80004004) or
                         unchecked((int)0x80070020) or
                         unchecked((int)0x800700AA) or
                         unchecked((int)0x8007139F),
            });

    internal static string DescribeWebViewInitializationFailure(Exception exception)
    {
        if (ExceptionChainAny(
                exception,
                static candidate => candidate is FileNotFoundException))
        {
            return "插件随包的 WebView2Loader.dll 缺失，请重新安装插件";
        }

        if (ExceptionChainAny(
                exception,
                static candidate => candidate is WebView2RuntimeNotFoundException))
        {
            return "未检测到 Microsoft Edge WebView2 Runtime";
        }

        if (ExceptionChainAny(
                exception,
                static candidate => candidate is UnauthorizedAccessException or
                    COMException { HResult: unchecked((int)0x80070005) }))
        {
            return "无法访问浏览器配置目录，请检查目录权限或安全软件拦截";
        }

        if (IsTransientWebViewInitializationFailure(exception))
        {
            return "旧浏览器进程仍在退出或配置暂时占用，自动恢复已达到上限；请稍后重开窗口";
        }

        return "浏览器初始化失败，详情已写入 Dalamud 日志";
    }

    private static bool ExceptionChainAny(
        Exception exception,
        Func<Exception, bool> predicate)
    {
        if (predicate(exception))
        {
            return true;
        }

        if (exception is AggregateException aggregate &&
            aggregate.InnerExceptions.Any(inner => ExceptionChainAny(inner, predicate)))
        {
            return true;
        }

        return exception.InnerException is not null &&
               !ReferenceEquals(exception.InnerException, exception) &&
               ExceptionChainAny(exception.InnerException, predicate);
    }

    private async void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            Interlocked.Exchange(ref browserRecoveryCount, 0);
            SetBrowserState(BrowserState.Loaded, "页面已加载");
            log.Information($"HTML overlay navigation completed: {webView?.Source}");
            await NotifyOverlayStateAsync();
            return;
        }

        SetBrowserState(BrowserState.Failed, $"页面加载失败：{args.WebErrorStatus}");
        log.Error(
            $"HTML overlay navigation failed: {args.WebErrorStatus}; URI: {webView?.Source}");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        log.Error($"HTML overlay browser process failed: {args.ProcessFailedKind}; {args.Reason}");
        if (disposing)
        {
            return;
        }

        if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            SetBrowserState(BrowserState.Failed, $"浏览器进程异常：{args.ProcessFailedKind}");
            ScheduleBrowserRecovery();
            return;
        }

        if (args.ProcessFailedKind is
            CoreWebView2ProcessFailedKind.RenderProcessExited or
            CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
        {
            SetBrowserState(BrowserState.Failed, $"浏览器进程异常：{args.ProcessFailedKind}");
            try
            {
                webView?.CoreWebView2?.Reload();
                SetBrowserState(BrowserState.Navigating, "渲染进程已重启，正在重新加载");
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"Could not reload {title} after a renderer failure.");
                ScheduleBrowserRecovery();
            }
        }
    }

    private void ScheduleBrowserRecovery()
    {
        if (disposing || form is null ||
            Interlocked.CompareExchange(ref recoveryScheduled, 1, 0) != 0)
        {
            return;
        }

        var recoveryAttempt = Interlocked.Increment(ref browserRecoveryCount);
        if (recoveryAttempt > 3)
        {
            Interlocked.Exchange(ref recoveryScheduled, 0);
            var message = "浏览器进程连续异常，自动恢复已达到上限；请重新打开悬浮窗";
            SetBrowserState(BrowserState.Failed, message);
            failureNotification?.Invoke($"{title}：{message}");
            return;
        }

        try
        {
            form.BeginInvoke(async () =>
            {
                try
                {
                    if (disposing)
                    {
                        return;
                    }

                    ReplaceWebViewControl();
                    await TrackInitializationAsync();
                }
                catch (Exception ex)
                {
                    if (!disposing)
                    {
                        log.Error(ex, $"HTML overlay browser recovery failed for {title}.");
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref recoveryScheduled, 0);
                }
            });
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref recoveryScheduled, 0);
        }
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

        if (debugMode &&
            message.StartsWith(InputRegionDiagnosticMessagePrefix, StringComparison.Ordinal))
        {
            LogBrowserInputRegionDiagnostic(
                message[InputRegionDiagnosticMessagePrefix.Length..]);
            return;
        }

        if (message.StartsWith(InputRegionMessagePrefix, StringComparison.Ordinal))
        {
            UpdateBrowserInputRegion(message[InputRegionMessagePrefix.Length..]);
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

    private void LogBrowserInputRegionDiagnostic(string json)
    {
        BrowserInputRegionDiagnosticPayload? diagnostic;
        try
        {
            diagnostic = JsonSerializer.Deserialize<BrowserInputRegionDiagnosticPayload>(json);
        }
        catch (JsonException ex)
        {
            log.Warning(ex, $"Ignored invalid browser input-region diagnostics from {title}.");
            return;
        }

        if (diagnostic is null || diagnostic.RefreshCount <= 0 ||
            !double.IsFinite(diagnostic.WindowMilliseconds) ||
            diagnostic.WindowMilliseconds <= 0 ||
            !double.IsFinite(diagnostic.TotalRefreshMilliseconds) ||
            !double.IsFinite(diagnostic.PeakRefreshMilliseconds))
        {
            return;
        }

        var refreshesPerSecond = diagnostic.RefreshCount * 1000d / diagnostic.WindowMilliseconds;
        var averageRefreshMilliseconds =
            diagnostic.TotalRefreshMilliseconds / diagnostic.RefreshCount;
        var averageScannedElements =
            diagnostic.TotalScannedElements / (double)diagnostic.RefreshCount;
        var averageFinalRectangles =
            diagnostic.TotalFinalRectangles / (double)diagnostic.RefreshCount;
        log.Debug(
            $"{title} input-region scan diagnostic over " +
            $"{diagnostic.WindowMilliseconds / 1000d:0.0}s: " +
            $"refreshes={diagnostic.RefreshCount} ({refreshesPerSecond:0.00}/s), " +
            $"CPU={diagnostic.TotalRefreshMilliseconds:0.00}ms total / " +
            $"{averageRefreshMilliseconds:0.00}ms avg / " +
            $"{diagnostic.PeakRefreshMilliseconds:0.00}ms peak, " +
            $"elements={averageScannedElements:0} avg / {diagnostic.PeakScannedElements} peak, " +
            $"rectangles={averageFinalRectangles:0.0} avg / {diagnostic.PeakFinalRectangles} peak, " +
            $"regionChanges={diagnostic.RegionChanges}.");
    }

    private void UpdateBrowserInputRegion(string json)
    {
        if (json.Length > 262_144)
        {
            log.Warning($"Ignored an oversized browser input-region update from {title}.");
            return;
        }

        BrowserInputRegionPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BrowserInputRegionPayload>(json);
        }
        catch (JsonException ex)
        {
            log.Warning(ex, $"Ignored an invalid browser input-region update from {title}.");
            return;
        }

        if (payload is null || !float.IsFinite(payload.ViewportWidth) ||
            !float.IsFinite(payload.ViewportHeight) ||
            payload.ViewportWidth <= 0 || payload.ViewportHeight <= 0 ||
            payload.ViewportWidth > 100_000 || payload.ViewportHeight > 100_000 ||
            payload.Rectangles is null ||
            payload.Rectangles.Length > MaximumBrowserInputRegions)
        {
            return;
        }

        var rectangles = new List<RectangleF>(payload.Rectangles.Length);
        foreach (var values in payload.Rectangles)
        {
            if (values is not { Length: 4 } || values.Any(value => !float.IsFinite(value)))
            {
                continue;
            }

            var left = Math.Clamp(values[0], 0, payload.ViewportWidth);
            var top = Math.Clamp(values[1], 0, payload.ViewportHeight);
            var right = Math.Clamp(values[0] + values[2], 0, payload.ViewportWidth);
            var bottom = Math.Clamp(values[1] + values[3], 0, payload.ViewportHeight);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            rectangles.Add(RectangleF.FromLTRB(left, top, right, bottom));
        }

        browserInputRegion = new BrowserInputRegion(
            new SizeF(payload.ViewportWidth, payload.ViewportHeight),
            rectangles.ToArray());
        ApplyInputProxyRegion();
        log.Debug($"{title} browser input region updated: {rectangles.Count} rectangles.");
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

    internal static nint CalculateOverlayExtendedStyle(
        nint currentStyle,
        bool isClickThrough)
    {
        // A borderless ownerless overlay is otherwise eligible for Alt+Tab and Task View.
        // Keep it an explicit tool window through every click-through style transition.
        var style = (currentStyle | WsExLayered | WsExToolWindow) & ~WsExAppWindow;
        return isClickThrough
            ? style | WsExTransparent | WsExNoActivate
            : style & ~(WsExTransparent | WsExNoActivate);
    }

    internal static PointF CalculateBrowserInputPoint(
        Size proxySize,
        SizeF viewportSize,
        Point proxyPoint)
    {
        if (proxySize.Width <= 0 || proxySize.Height <= 0 ||
            viewportSize.Width <= 0 || viewportSize.Height <= 0)
        {
            return PointF.Empty;
        }

        return new PointF(
            Math.Clamp(
                proxyPoint.X * viewportSize.Width / proxySize.Width,
                0,
                Math.Max(0, viewportSize.Width - 1)),
            Math.Clamp(
                proxyPoint.Y * viewportSize.Height / proxySize.Height,
                0,
                Math.Max(0, viewportSize.Height - 1)));
    }

    internal static Rectangle[] CalculateInputProxyRectangles(
        Size proxySize,
        SizeF viewportSize,
        IReadOnlyList<RectangleF> browserRectangles)
    {
        if (proxySize.Width <= 0 || proxySize.Height <= 0 ||
            viewportSize.Width <= 0 || viewportSize.Height <= 0)
        {
            return [];
        }

        const int padding = 2;
        var scaleX = proxySize.Width / viewportSize.Width;
        var scaleY = proxySize.Height / viewportSize.Height;
        var rectangles = new List<Rectangle>(browserRectangles.Count);
        foreach (var browserRectangle in browserRectangles)
        {
            var left = Math.Clamp(
                (int)Math.Floor(browserRectangle.Left * scaleX) - padding,
                0,
                proxySize.Width);
            var top = Math.Clamp(
                (int)Math.Floor(browserRectangle.Top * scaleY) - padding,
                0,
                proxySize.Height);
            var right = Math.Clamp(
                (int)Math.Ceiling(browserRectangle.Right * scaleX) + padding,
                0,
                proxySize.Width);
            var bottom = Math.Clamp(
                (int)Math.Ceiling(browserRectangle.Bottom * scaleY) + padding,
                0,
                proxySize.Height);
            if (right > left && bottom > top)
            {
                rectangles.Add(Rectangle.FromLTRB(left, top, right, bottom));
            }
        }

        return rectangles.ToArray();
    }

    private Form CreateInputProxy()
    {
        var proxy = new InputProxyForm
        {
            Text = $"{title} Input",
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            TopMost = true,
            BackColor = Color.Black,
            Opacity = 0.01,
        };
        proxy.MouseClick += OnInputProxyMouseClick;
        return proxy;
    }

    private EditChromeForm CreateEditChrome()
        => new(title, TransparencyColor);

    private void ApplyEditChromeSettings()
    {
        if (editChrome is null || form is null || settings is null)
        {
            return;
        }

        editChrome.Bounds = form.Bounds;
        editChrome.SetStatus(browserState, browserStateDetail);
        if (!form.Visible || !settings.IsEditing)
        {
            editChrome.Hide();
            return;
        }

        if (!editChrome.Visible)
        {
            editChrome.Show(form);
        }

        SetWindowPos(
            editChrome.Handle,
            HwndTopMost,
            form.Left,
            form.Top,
            form.Width,
            form.Height,
            SwpNoActivate);
        editChrome.Invalidate();
    }

    private void ApplyInputProxySettings()
    {
        if (inputProxy is null || form is null || settings is null)
        {
            return;
        }

        inputProxy.Bounds = form.Bounds;
        ApplyInputProxyRegion();
        if (!form.Visible || settings.IsClickThrough ||
            !settings.IsEditing && browserState != BrowserState.Loaded)
        {
            inputProxy.Hide();
            return;
        }

        if (!inputProxy.Visible)
        {
            inputProxy.Show(form);
        }

        SetWindowPos(
            inputProxy.Handle,
            HwndTopMost,
            form.Left,
            form.Top,
            form.Width,
            form.Height,
            SwpNoActivate);
    }

    private void ApplyInputProxyRegion()
    {
        if (inputProxy is null || settings is null)
        {
            return;
        }

        if (!debugMode)
        {
            Region? fastRegion = null;
            if (!settings.IsEditing && browserInputRegion is not null)
            {
                fastRegion = new Region();
                fastRegion.MakeEmpty();
                foreach (var rectangle in CalculateInputProxyRectangles(
                             inputProxy.ClientSize,
                             browserInputRegion.ViewportSize,
                             browserInputRegion.Rectangles))
                {
                    fastRegion.Union(rectangle);
                }
            }

            var previousFastRegion = inputProxy.Region;
            inputProxy.Region = fastRegion;
            previousFastRegion?.Dispose();
            return;
        }

        var diagnosticTimer = Stopwatch.StartNew();
        Region? nextRegion = null;
        var rectangleCount = 0;
        if (!settings.IsEditing && browserInputRegion is not null)
        {
            nextRegion = new Region();
            nextRegion.MakeEmpty();
            var rectangles = CalculateInputProxyRectangles(
                inputProxy.ClientSize,
                browserInputRegion.ViewportSize,
                browserInputRegion.Rectangles);
            rectangleCount = rectangles.Length;
            foreach (var rectangle in rectangles)
            {
                nextRegion.Union(rectangle);
            }
        }

        var previousRegion = inputProxy.Region;
        inputProxy.Region = nextRegion;
        previousRegion?.Dispose();
        if (nextRegion is not null)
        {
            diagnosticTimer.Stop();
            RecordRegionRebuildDiagnostic(diagnosticTimer.Elapsed.TotalMilliseconds, rectangleCount);
        }
    }

    private void RecordRegionRebuildDiagnostic(double elapsedMilliseconds, int rectangleCount)
    {
        regionRebuildCount++;
        regionRebuildRectangleCount += rectangleCount;
        regionRebuildPeakRectangles = Math.Max(regionRebuildPeakRectangles, rectangleCount);
        regionRebuildTotalMilliseconds += elapsedMilliseconds;
        regionRebuildPeakMilliseconds = Math.Max(regionRebuildPeakMilliseconds, elapsedMilliseconds);

        var window = Stopwatch.GetElapsedTime(regionRebuildWindowStarted);
        if (window < TimeSpan.FromSeconds(10))
        {
            return;
        }

        log.Debug(
            $"{title} Windows input-region rebuild diagnostic over {window.TotalSeconds:0.0}s: " +
            $"rebuilds={regionRebuildCount} ({regionRebuildCount / window.TotalSeconds:0.00}/s), " +
            $"CPU={regionRebuildTotalMilliseconds:0.00}ms total / " +
            $"{regionRebuildTotalMilliseconds / regionRebuildCount:0.00}ms avg / " +
            $"{regionRebuildPeakMilliseconds:0.00}ms peak, " +
            $"rectangles={regionRebuildRectangleCount / (double)regionRebuildCount:0.0} avg / " +
            $"{regionRebuildPeakRectangles} peak.");
        regionRebuildWindowStarted = Stopwatch.GetTimestamp();
        regionRebuildCount = 0;
        regionRebuildRectangleCount = 0;
        regionRebuildPeakRectangles = 0;
        regionRebuildTotalMilliseconds = 0;
        regionRebuildPeakMilliseconds = 0;
    }

    private async void OnInputProxyMouseClick(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left || settings?.IsClickThrough != false ||
            inputProxy is null || webView?.CoreWebView2 is not { } core ||
            interaction != OverlayInteraction.None)
        {
            return;
        }

        try
        {
            var viewportJson = await core.ExecuteScriptAsync(
                "[window.innerWidth, window.innerHeight]");
            var viewport = JsonSerializer.Deserialize<float[]>(viewportJson);
            if (viewport is not [> 0, > 0])
            {
                return;
            }

            var point = CalculateBrowserInputPoint(
                inputProxy.ClientSize,
                new SizeF(viewport[0], viewport[1]),
                args.Location);
            form?.Activate();
            webView.Focus();
            await DispatchBrowserMouseEventAsync(
                core,
                "mouseMoved",
                point,
                "none",
                0,
                0);
            await DispatchBrowserMouseEventAsync(
                core,
                "mousePressed",
                point,
                "left",
                1,
                Math.Max(1, args.Clicks));
            await DispatchBrowserMouseEventAsync(
                core,
                "mouseReleased",
                point,
                "left",
                0,
                Math.Max(1, args.Clicks));
            log.Debug(
                $"{title} forwarded input-proxy click to browser at " +
                $"{point.X:0.##},{point.Y:0.##}.");
        }
        catch (Exception) when (disposing)
        {
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to forward an input-proxy click to {title}.");
        }
    }

    private static Task<string> DispatchBrowserMouseEventAsync(
        CoreWebView2 core,
        string type,
        PointF point,
        string button,
        int buttons,
        int clickCount)
        => core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new
            {
                type,
                x = point.X,
                y = point.Y,
                button,
                buttons,
                clickCount,
            }));

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

            var currentStyle = GetWindowLongPtr(form.Handle, GwlExStyle);
            var extendedStyle = CalculateOverlayExtendedStyle(
                currentStyle,
                settings.IsClickThrough);
            if (currentStyle != extendedStyle)
            {
                SetWindowLongPtr(form.Handle, GwlExStyle, extendedStyle);
                // Extended styles can remain cached until SetWindowPos applies
                // SWP_FRAMECHANGED. Interactive overlays must also be allowed to
                // activate so WebView2, rather than the focused game, owns clicks.
                SetWindowPos(
                    form.Handle,
                    HwndTopMost,
                    0,
                    0,
                    0,
                    0,
                    SwpNoSize | SwpNoMove | SwpNoActivate | SwpFrameChanged);
            }
            if (settings.IsEditing)
            {
                StartEditMonitor();
            }
            else
            {
                StopEditMonitor();
            }
            ApplyInputProxySettings();
            ApplyEditChromeSettings();
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
        if (overlayMode && inputProxy is not null && form is not null)
        {
            inputProxy.Bounds = form.Bounds;
            ApplyInputProxyRegion();
            if (editChrome is not null)
            {
                editChrome.Bounds = form.Bounds;
            }
        }

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
        inputProxy?.Hide();
        editChrome?.Hide();
        if (disposing)
        {
            return;
        }

        args.Cancel = true;
        form?.Hide();
    }

    private void CompleteShutdownAfterInitialization()
    {
        var pendingInitialization = Volatile.Read(ref initializationTask);
        if (pendingInitialization is { IsCompleted: false })
        {
            _ = pendingInitialization.ContinueWith(
                static (_, state) => ((HtmlOverlayForm)state!).FinalizeShutdown(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        FinalizeShutdown();
    }

    private void FinalizeShutdown()
    {
        if (Interlocked.Exchange(ref shutdownFinalized, 1) != 0)
        {
            return;
        }

        try
        {
            ReleaseLoader();
            initializationGate.Dispose();
            lifecycleCancellation.Dispose();
            ready.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to release all HTML overlay resources for {title}.");
        }
        finally
        {
            shutdownCompletion.TrySetResult(true);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        disposing = true;
        browserState = BrowserState.Disposing;
        browserStateDetail = "正在关闭";
        lifecycleCancellation.Cancel();
        if (form is not null)
        {
            try
            {
                form.BeginInvoke(async () =>
                {
                    try
                    {
                        var pendingInitialization = Volatile.Read(ref initializationTask);
                        if (pendingInitialization is not null)
                        {
                            try
                            {
                                await pendingInitialization;
                            }
                            catch (Exception ex)
                            {
                                if (ex is not OperationCanceledException and not ObjectDisposedException)
                                {
                                    log.Warning(
                                        ex,
                                        $"Initialization ended unexpectedly while closing {title}.");
                                }
                            }
                        }

                        inputProxy?.Close();
                        editChrome?.Close();
                        form.Close();
                    }
                    catch (Exception ex)
                    {
                        if (ex is not InvalidOperationException and not ObjectDisposedException)
                        {
                            log.Warning(ex, $"Could not finish closing {title} on its UI thread.");
                        }
                    }
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        }
        else if (uiThread is null)
        {
            FinalizeShutdown();
        }

        var uiThreadStopped = uiThread is null ||
                              ReferenceEquals(Thread.CurrentThread, uiThread) ||
                              uiThread.Join(TimeSpan.FromSeconds(5));
        var pendingInitialization = Volatile.Read(ref initializationTask);
        if (!uiThreadStopped || !shutdownCompletion.Task.IsCompleted)
        {
            log.Warning(
                $"{title} did not finish its WebView2 shutdown within 5 seconds; " +
                (pendingInitialization is { IsCompleted: false }
                    ? "initialization is still completing; "
                    : string.Empty) +
                "its loader and session lock will be released after the UI thread exits safely.");
        }
    }

    public static void WaitForBrowserProcessesExit(
        IEnumerable<int> processIds,
        TimeSpan timeout,
        IPluginLog log)
    {
        var pending = processIds
            .Where(static processId => processId > 0)
            .Distinct()
            .ToArray();
        var timer = Stopwatch.StartNew();
        foreach (var processId in pending)
        {
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.WaitForExit((int)Math.Max(1, remaining.TotalMilliseconds));
                }
            }
            catch (ArgumentException)
            {
                // The browser exited before Process.GetProcessById observed it.
            }
            catch (InvalidOperationException)
            {
                // The process exited while its state was being queried.
            }
        }

        var survivors = pending.Where(IsProcessRunning).ToArray();
        if (survivors.Length > 0)
        {
            log.Warning(
                $"WebView2 browser shutdown exceeded {timeout.TotalSeconds:0.#} seconds; " +
                $"still running: {string.Join(", ", survivors)}.");
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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

    private enum BrowserState
    {
        NotStarted,
        Initializing,
        Ready,
        Navigating,
        Loaded,
        Failed,
        Disposing,
    }

    private sealed class EditChromeForm : Form
    {
        private readonly string overlayTitle;
        private BrowserState state;
        private string detail = string.Empty;

        public EditChromeForm(string overlayTitle, Color transparencyColor)
        {
            this.overlayTitle = overlayTitle;
            Text = $"{overlayTitle} Edit Boundary";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = transparencyColor;
            TransparencyKey = transparencyColor;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= unchecked((int)(
                    WsExLayered |
                    WsExTransparent |
                    WsExNoActivate |
                    WsExToolWindow));
                parameters.ExStyle &= unchecked((int)~WsExAppWindow);
                return parameters;
            }
        }

        public void SetStatus(BrowserState nextState, string nextDetail)
        {
            if (state == nextState && string.Equals(detail, nextDetail, StringComparison.Ordinal))
            {
                return;
            }

            state = nextState;
            detail = nextDetail;
            Invalidate();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WindowMessageNonClientHitTest)
            {
                message.Result = HitTestTransparent;
                return;
            }

            base.WndProc(ref message);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            if (ClientSize.Width <= 1 || ClientSize.Height <= 1)
            {
                return;
            }

            args.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var borderColor = state == BrowserState.Failed
                ? Color.FromArgb(255, 240, 80, 80)
                : Color.FromArgb(255, 255, 190, 70);
            using var borderPen = new Pen(borderColor, 3);
            args.Graphics.DrawRectangle(
                borderPen,
                1.5f,
                1.5f,
                ClientSize.Width - 3,
                ClientSize.Height - 3);

            var headerWidth = Math.Min(ClientSize.Width - 6, 520);
            var headerHeight = Math.Min(ClientSize.Height - 6, 48);
            if (headerWidth > 0 && headerHeight > 0)
            {
                using var headerBrush = new SolidBrush(Color.FromArgb(235, 24, 28, 36));
                args.Graphics.FillRectangle(headerBrush, 3, 3, headerWidth, headerHeight);
                var statusText = string.IsNullOrWhiteSpace(detail)
                    ? state.ToString()
                    : detail;
                TextRenderer.DrawText(
                    args.Graphics,
                    $"{overlayTitle} · 编辑模式",
                    Font,
                    new Rectangle(10, 7, Math.Max(1, headerWidth - 16), 18),
                    Color.White,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(
                    args.Graphics,
                    $"拖动窗口；右下角缩放 · {statusText}",
                    Font,
                    new Rectangle(10, 25, Math.Max(1, headerWidth - 16), 18),
                    borderColor,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            using var gripPen = new Pen(borderColor, 3);
            for (var offset = 8; offset <= ResizeGripSize; offset += 8)
            {
                args.Graphics.DrawLine(
                    gripPen,
                    ClientSize.Width - offset,
                    ClientSize.Height - 3,
                    ClientSize.Width - 3,
                    ClientSize.Height - offset);
            }
        }
    }

    private sealed class BrowserInputRegionPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("viewportWidth")]
        public float ViewportWidth { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("viewportHeight")]
        public float ViewportHeight { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("rectangles")]
        public float[][]? Rectangles { get; init; }
    }

    private sealed class BrowserInputRegionDiagnosticPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("windowMilliseconds")]
        public double WindowMilliseconds { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("refreshCount")]
        public int RefreshCount { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("totalRefreshMilliseconds")]
        public double TotalRefreshMilliseconds { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("peakRefreshMilliseconds")]
        public double PeakRefreshMilliseconds { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("totalScannedElements")]
        public long TotalScannedElements { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("peakScannedElements")]
        public int PeakScannedElements { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("totalFinalRectangles")]
        public long TotalFinalRectangles { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("peakFinalRectangles")]
        public int PeakFinalRectangles { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("regionChanges")]
        public int RegionChanges { get; init; }
    }

    private sealed record BrowserInputRegion(SizeF ViewportSize, RectangleF[] Rectangles);

    private sealed class OverlayHostForm : Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= unchecked((int)(WsExLayered | WsExNoActivate | WsExToolWindow));
                parameters.ExStyle &= unchecked((int)~WsExAppWindow);
                return parameters;
            }
        }
    }

    private sealed class InputProxyForm : Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= unchecked((int)(WsExNoActivate | WsExToolWindow));
                parameters.ExStyle &= unchecked((int)~WsExAppWindow);
                return parameters;
            }
        }
    }
}
