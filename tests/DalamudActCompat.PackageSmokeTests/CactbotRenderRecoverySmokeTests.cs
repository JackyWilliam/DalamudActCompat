using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;

internal static class CactbotRenderRecoverySmokeTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type OverlayType = typeof(HtmlOverlayWindowSettings).Assembly
        .GetType("DalamudActCompat.ActRuntime.HtmlOverlayForm", true)!;

    internal static void Run()
    {
        var type = OverlayType.Assembly.GetType("DalamudActCompat.ActRuntime.CactbotFrameRecoveryPolicy", true)!;
        var policy = Activator.CreateInstance(type)!;
        bool Attempt(long now) => (bool)type.GetMethod("TryRecover", Instance)!.Invoke(policy, [now])!;
        Check(Attempt(0), "First stalled surface was not recoverable.");
        Check(!Attempt(29_999) && Attempt(30_000), "Recovery did not enforce the cooldown.");
        Check(Attempt(60_000) && !Attempt(299_999), "Persistent stalls exceeded the three-attempt budget.");
        Check(Attempt(300_000) && !Attempt(300_001), "Rolling recovery window lost its budget.");
        Console.WriteLine("Cactbot bounded surface recovery policy passed.");
    }

    internal static async Task RunLiveAsync(string root, string loaderPath, bool frameOnly = false)
    {
        var page = Path.Combine(root, "ui", "raidboss", "raidboss.html");
        Directory.CreateDirectory(Path.GetDirectoryName(page)!);
        await File.WriteAllTextAsync(page, """
            <!doctype html><meta charset="utf-8">
            <style>body{margin:0;color:white;background:#182030}
            #popup-text-container{height:0} .holder{position:absolute;top:20px}</style>
            <div id="container"><div id="popup-text-container"><div class="holder"></div></div></div>
            <script>
              window.loadId=crypto.randomUUID();window.framesSeen=0;window.timerTicks=0;
              const tick=()=>{framesSeen++;requestAnimationFrame(tick)};tick();
              setInterval(()=>timerTicks++,200);
              window.ready=true;
            </script>
            """);
        var settings = new HtmlOverlayWindowSettings
        {
            Left = 80, Top = 80, Width = 700, Height = 230, ZoomFactor = 1.1f,
            IsClickThrough = true, IsLocked = true, OpenOnStartup = true,
        };
        var log = DispatchProxy.Create<IPluginLog, CactbotRenderTestLog>();
        var messages = ((CactbotRenderTestLog)(object)log).Messages;
        var overlay = (IDisposable)Activator.CreateInstance(OverlayType, Instance, null,
            [new Uri(page), Path.Combine(root, "profile"), loaderPath, "Cactbot recovery smoke",
             true, settings, new Size(700, 230), false, log, null, null], null)!;
        try
        {
            Call(overlay, "Show");
            Control? view = null;
            await Until(async () =>
            {
                view = (Control?)OverlayType.GetField("webView", Instance)!.GetValue(overlay);
                return view?.IsHandleCreated == true && await Script(view, "window.ready===true") == "true";
            }, "Browser fixture did not load.");
            var browser = view!;
            var form = (Form)OverlayType.GetField("form", Instance)!.GetValue(overlay)!;
            var loadId = await Script(browser, "loadId");
            var bounds = await Ui(form, () => Task.FromResult(form.Bounds));
            var controller = await Ui(browser, () => Task.FromResult(FindController(browser)));

            // Desynchronize the real Chromium surface from the still-visible native host.
            // This reproduces stopped animation frames with a live JS/TTS timer, without
            // monkey-patching requestAnimationFrame or the recovery implementation.
            await Ui(browser, () => { Set(controller, "IsVisible", false); return Task.CompletedTask; });
            await Task.Delay(1400);
            var frozen = await Snapshot(browser);
            await Task.Delay(1200);
            var stillFrozen = await Snapshot(browser);
            Check(stillFrozen.frames == frozen.frames && stillFrozen.ticks > frozen.ticks,
                "Fault injection did not reproduce stopped frames with live timers.");
            Console.WriteLine("Reproduced: animation frames stopped while page timers continued.");

            if (!frameOnly)
            {
                await Script(browser, "document.querySelector('.holder').innerHTML='<div class=text>机制文字</div>';true");
                await Task.Delay(100);
                Check(await Script(browser, "document.querySelector('#container').classList.contains('dalamud-act-compat-alert-repair')") == "true",
                    "Alert layout repair waited for the stalled animation frame.");
            }
            await Until(async () => (await Snapshot(browser)).frames > stillFrozen.frames + 2,
                "Visible Cactbot surface did not recover animation frames.");
            Check(await Script(browser, "loadId") == loadId, "Recovery reloaded the active encounter.");
            Check(await Ui(form, () => Task.FromResult(form.Bounds == bounds && form.Visible)), "Recovery changed the host layout or visibility.");
            Check(settings.IsClickThrough && settings.IsLocked && settings.ZoomFactor == 1.1f && settings.OpenOnStartup,
                "Recovery changed saved overlay preferences.");
            Check(messages.Any(x => x.Contains("animation frames resumed", StringComparison.Ordinal)), "Recovery was not confirmed by a subsequent animation frame.");
            Console.WriteLine("Surface recovered without a page reload, preference change or layout change.");

            if (!frameOnly)
            {
                await ValidateAlertGuards(browser, messages);
                await ValidateHiddenGuards(overlay, browser, form, settings);

                // Only terminate the GPU child reported by this isolated test profile.
                // Never enumerate or stop unrelated game/browser processes.
                var gpuPid = await Ui(browser, () =>
                {
                    var environment = Get(Get(browser, "CoreWebView2"), "Environment");
                    var processes = (IEnumerable)environment.GetType().GetMethod("GetProcessInfos")!.Invoke(environment, null)!;
                    var gpu = processes.Cast<object>().Single(x => Get(x, "Kind").ToString() == "Gpu");
                    return Task.FromResult(Convert.ToInt32(Get(gpu, "ProcessId")));
                });
                using (var process = Process.GetProcessById(gpuPid)) process.Kill();
                await Until(() => Task.FromResult(messages.Any(x => x.Contains("GpuProcessExited", StringComparison.Ordinal))),
                    "The isolated GPU crash did not reach the production failure handler.");
                var afterGpu = await Snapshot(browser);
                await Until(async () => (await Snapshot(browser)).frames > afterGpu.frames + 2,
                    "Animation did not resume after the isolated GPU restart.");
                Check(await Script(browser, "loadId") == loadId, "A GPU restart unnecessarily reloaded Cactbot.");
                Console.WriteLine("GPU restart preserved the existing page and resumed animation.");
            }
        }
        finally
        {
            overlay.Dispose();
            await ((Task)OverlayType.GetProperty("ShutdownCompletion")!.GetValue(overlay)!).WaitAsync(TimeSpan.FromSeconds(15));
            var browserProcessIds = (IEnumerable<int>)OverlayType.GetProperty("BrowserProcessIds")!.GetValue(overlay)!;
            OverlayType.GetMethod("WaitForBrowserProcessesExit")!.Invoke(null,
                [browserProcessIds, TimeSpan.FromSeconds(10), log]);
        }
        Console.WriteLine("Cactbot native render recovery smoke passed.");
    }

    private static async Task ValidateAlertGuards(Control browser, ConcurrentQueue<string> messages)
    {
        var before = messages.Count(x => x.Contains("Cactbot alert render state", StringComparison.Ordinal));
        await Script(browser, """
            (()=>{const c=document.querySelector('#container');c.className='hide-alerts';
            const h=document.querySelector('.holder');h.innerHTML='<div class=text>已过期</div>';h.innerHTML='';return true})()
            """);
        await Task.Delay(100);
        Check(before == messages.Count(x => x.Contains("Cactbot alert render state", StringComparison.Ordinal)), "Detached alerts generated misleading repair diagnostics.");
        await Script(browser, "document.querySelector('.holder').innerHTML='<div class=text>用户隐藏</div>';true");
        await Task.Delay(100);
        Check(await Script(browser, "document.querySelector('#container').className") == "\"hide-alerts\"", "Repair overrode disabled text alerts.");
        Console.WriteLine("Detached alerts and the explicit text-hide setting were preserved.");
    }

    private static async Task ValidateHiddenGuards(IDisposable overlay, Control browser, Form form, HtmlOverlayWindowSettings settings)
    {
        var refreshed = 0;
        await Ui(browser, () => { browser.VisibleChanged += (_, _) => refreshed++; return Task.CompletedTask; });
        foreach (var mode in new[] { "manual", "foreground", "combat", "closed" })
        {
            if (mode == "manual") { settings.IsUserHidden = true; Call(overlay, "ApplySettings"); }
            if (mode == "foreground") Call(overlay, "SetTemporarilyHidden", true);
            if (mode == "combat") { settings.AutoHideOutOfCombat = true; Call(overlay, "ApplySettings"); }
            if (mode == "closed") Call(overlay, "Hide");
            await Until(() => Ui(form, () => Task.FromResult(!form.Visible)), "Suppression did not hide the fixture.");
            var previous = refreshed;
            await Ui(browser, () =>
            {
                // Give each scenario a fresh budget so the cooldown cannot mask a broken visibility guard.
                var policyType = OverlayType.Assembly.GetType("DalamudActCompat.ActRuntime.CactbotFrameRecoveryPolicy", true)!;
                OverlayType.GetField("frameRecoveryPolicy", Instance)!.SetValue(overlay, Activator.CreateInstance(policyType));
                Call(overlay, "HandleCactbotFrameHealth", "stalled");
                return Task.CompletedTask;
            });
            Check(previous == refreshed && !await Ui(form, () => Task.FromResult(form.Visible)), "Recovery reopened a " + mode + " overlay.");
            settings.IsUserHidden = false;
            settings.AutoHideOutOfCombat = false;
            Call(overlay, "SetTemporarilyHidden", false);
            Call(overlay, "Show");
            await Until(() => Ui(form, () => Task.FromResult(form.Visible)), "Fixture did not reopen.");
        }
        Console.WriteLine("Manual, foreground, out-of-combat and closed-window suppression passed.");
    }

    private static object FindController(object browser)
        => browser.GetType().GetFields(Instance).Single(x => x.FieldType.Name == "CoreWebView2Controller").GetValue(browser)!;
    private static object Get(object target, string property) => target.GetType().GetProperty(property, Instance)!.GetValue(target)!;
    private static void Set(object target, string property, object value) => target.GetType().GetProperty(property, Instance)!.SetValue(target, value);
    private static void Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Instance)!.Invoke(target, args);
    private static Task<string> Script(Control browser, string script) => Ui(browser, async () =>
    {
        var core = Get(browser, "CoreWebView2");
        return core is null ? "null" : await (Task<string>)core.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(core, [script])!;
    });
    private static async Task<(int frames, int ticks)> Snapshot(Control browser)
    {
        var data = JsonSerializer.Deserialize<int[]>(await Script(browser, "[framesSeen,timerTicks]"))!;
        return (data[0], data[1]);
    }
    private static async Task Until(Func<Task<bool>> predicate, string message)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (await predicate()) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException(message);
    }
    private static Task Ui(Control control, Func<Task> action) => Ui(control, async () => { await action(); return true; });
    private static Task<T> Ui<T>(Control control, Func<Task<T>> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.BeginInvoke(async () => { try { result.SetResult(await action()); } catch (Exception ex) { result.SetException(ex); } });
        return result.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public class CactbotRenderTestLog : DispatchProxy
{
    internal readonly ConcurrentQueue<string> Messages = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name is "Warning" or "Information" or "Error")
            Messages.Enqueue(string.Join(" | ", args ?? []));
        return method?.ReturnType.IsValueType == true && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
