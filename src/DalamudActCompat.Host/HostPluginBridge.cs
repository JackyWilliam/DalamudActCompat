using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Host;

public static class HostPluginBridge
{
    private const int ClipboardQueueCapacity = 8;
    private const int MaximumClipboardCharacters = 8 * 1024 * 1024;
    private const int MaximumClipboardLogItems = 200_000;
    private static readonly BlockingCollection<ClipboardRequest> ClipboardRequests =
        new(ClipboardQueueCapacity);
    private static readonly Thread ClipboardThread = CreateClipboardThread();
    private static Func<string, HostMessagePriority, object, string?, DateTimeOffset?, bool>? sender;
    private static IReadOnlyDictionary<string, HashSet<string>> permissions =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> DiagnosticRepeats =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, PendingTtsAuthorization> PendingTts =
        new(StringComparer.Ordinal);
    private static readonly FfxivDataRepository FfxivRepositoryInstance = new();
    private static readonly object TriggerZoneListenerLock = new();
    private static WeakReference<object>? triggerZoneListener;
    private static Action<string>? ttsWriter;
    private static Action<string>? clipboardWriterForTests;
    private static long triggerEventDrops;
    private static int pendingTtsCount;

    internal static void Configure(
        Func<string, HostMessagePriority, object, string?, DateTimeOffset?, bool> messageSender)
    {
        sender = messageSender;
    }

    internal static void ConfigurePermissions(HostPermissionSnapshot snapshot)
    {
        permissions = snapshot.AllowedCapabilities.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToHashSet(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in permissions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"permission plugin={pair.Key} allowed=[{string.Join(",", pair.Value.Order())}]");
        }
    }

    internal static FfxivDataRepository FfxivRepository => FfxivRepositoryInstance;

    internal static void ApplyFfxivEntitySnapshot(HostFfxivEntitySnapshot snapshot)
        => FfxivRepositoryInstance.Apply(snapshot);

    internal static void ConfigureTtsWriter(Action<string>? writer)
        => Volatile.Write(ref ttsWriter, writer);

    public static void PlayTtsFromGame(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.Length > 2000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                "TTS text exceeds 2000 characters.");
        }

        var writer = Volatile.Read(ref ttsWriter)
                     ?? throw new InvalidOperationException(
                         "No isolated ACT TTS provider is loaded in the external Host.");
        writer(text);
    }

    internal static void ConfigureClipboardWriterForTests(Action<string> writer)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ACTCOMPAT_ENABLE_TEST_HOOKS"),
                "1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Clipboard test hooks are disabled outside explicit smoke-test processes.");
        }

        Volatile.Write(ref clipboardWriterForTests, writer);
    }

    public static bool CheckTriggernometryAdministratorCapability(bool warnIfNotAdmin)
    {
        bool administrator;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            administrator = new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        }

        if (!administrator && warnIfNotAdmin)
        {
            Console.WriteLine(
                "Triggernometry capability: standard logs, combat/zone events, regex, configuration, " +
                "clipboard and brokered TTS do not require administrator rights. Elevated external actions " +
                "are not granted by the Host.");
        }

        return administrator;
    }

    public static bool IsExpectedTriggernometryCompatibilityNotice(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           message.Contains("鲶鱼精邮差扩展", StringComparison.Ordinal) &&
           message.Contains("ACT 未以管理员权限运行", StringComparison.Ordinal);

    public static void EnqueueTriggerEventBounded<T>(Queue<T> queue, T item)
    {
        const int capacity = 8192;
        if (queue.Count >= capacity)
        {
            queue.Dequeue();
            var dropped = Interlocked.Increment(ref triggerEventDrops);
            if ((dropped & (dropped - 1)) == 0)
            {
                Console.Error.WriteLine(
                    $"Triggernometry event queue full; dropped oldest. Total={dropped}.");
            }
        }

        queue.Enqueue(item);
    }

    public static void ReportUnstoppableTriggernometryThread(Thread thread)
        => Console.Error.WriteLine(
            $"Triggernometry thread '{thread.Name ?? thread.ManagedThreadId.ToString()}' did not stop. " +
            "Thread.Abort is disabled; the supervisor may terminate this Host process.");

    public static void SetClipboardText(string text)
    {
        Demand("postnamazu", "Clipboard");
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumClipboardCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                $"Clipboard payload exceeds {MaximumClipboardCharacters} characters.");
        }

        if (!ClipboardRequests.TryAdd(new ClipboardRequest(text, null, null)))
        {
            throw new InvalidOperationException(
                $"Clipboard queue is full ({ClipboardQueueCapacity}).");
        }
    }

    public static string GetClipboardText()
    {
        Demand("postnamazu", "Clipboard");
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ClipboardRequests.TryAdd(new ClipboardRequest(null, completion, null)))
        {
            throw new InvalidOperationException(
                $"Clipboard queue is full ({ClipboardQueueCapacity}).");
        }

        if (!completion.Task.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("Clipboard read exceeded two seconds.");
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    public static void CopyPostNamazuLog(ListBox list, bool copyAll)
    {
        Demand("postnamazu", "Clipboard");
        ArgumentNullException.ThrowIfNull(list);
        IList source = copyAll ? list.Items : list.SelectedItems;
        if (source.Count > MaximumClipboardLogItems)
        {
            throw new InvalidOperationException(
                $"PostNamazu log selection exceeds {MaximumClipboardLogItems} items.");
        }

        var snapshot = new object?[source.Count];
        source.CopyTo(snapshot, 0);
        if (snapshot.Length == 0)
        {
            return;
        }

        if (!ClipboardRequests.TryAdd(new ClipboardRequest(null, null, snapshot)))
        {
            throw new InvalidOperationException(
                $"Clipboard queue is full ({ClipboardQueueCapacity}).");
        }
    }

    public static void AttachPostNamazu(object plugin)
    {
        var state = plugin.GetType().GetProperty(
            "State",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        if (state?.PropertyType.IsEnum == true)
        {
            state.SetValue(plugin, Enum.Parse(state.PropertyType, "Ready"));
        }

        plugin.GetType().GetMethod(
                "LogACT",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(plugin, ["AttachedExternalHost"]);
    }

    public static void UsePostNamazuOverlayAdapter(object integrationManager)
    {
        ArgumentNullException.ThrowIfNull(integrationManager);
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var plugin = integrationManager.GetType().GetField("_plugin", flags)
                         ?.GetValue(integrationManager)
                     ?? throw new MissingFieldException(
                         integrationManager.GetType().FullName,
                         "_plugin");
        var pluginUi = plugin.GetType().GetField("PluginUi", flags)?.GetValue(plugin)
                       ?? throw new MissingFieldException(
                           plugin.GetType().FullName,
                           "PluginUi");
        var log = pluginUi.GetType().GetMethod(
                      "Log",
                      flags,
                      null,
                      [typeof(string)],
                      null)
                  ?? throw new MissingMethodException(pluginUi.GetType().FullName, "Log");
        log.Invoke(
            pluginUi,
            ["OverlayPlugin bridge active through the game-side event dispatcher."]);
        Console.WriteLine(
            "PostNamazu selected the cross-process game-side OverlayPlugin adapter.");
    }

    public static void SkipLegacyProcessMonitoring(object _)
    {
    }

    public static void SendPostNamazuCommand(string command)
    {
        Demand("postnamazu", "GameCommand");
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!command.StartsWith('/') || command.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "PostNamazu command must be a single slash-prefixed semantic command.",
                nameof(command));
        }

        var queued = sender?.Invoke(
            HostMessageTypes.CommandRequest,
            HostMessagePriority.Critical,
            new HostCommandRequest(
                "postnamazu",
                "postnamazu.chat",
                new Dictionary<string, string> { ["text"] = command }),
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddSeconds(2)) == true;
        if (!queued)
        {
            throw new InvalidOperationException("Game command broker queue rejected the request.");
        }
    }

    public static void SendPostNamazuMark(string payload)
        => SendPostNamazuSemanticAction("postnamazu.mark", payload);

    public static void SendPostNamazuWaymark(string payload)
        => SendPostNamazuSemanticAction("postnamazu.place", payload);

    public static void SendPostNamazuPictoAct(string payload)
        => SendPostNamazuSemanticAction("postnamazu.pictoact", payload);

    public static void SendTts(string text)
    {
        Demand("triggernometry", "TextToSpeech");
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.Length > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(text), "TTS text exceeds 2000 characters.");
        }

        if (Volatile.Read(ref ttsWriter) is null)
        {
            throw new InvalidOperationException(
                "No isolated ACT TTS provider is loaded in the external Host.");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var expired in PendingTts
                     .Where(pair => pair.Value.Deadline <= now)
                     .Select(pair => pair.Key))
        {
            if (PendingTts.TryRemove(expired, out _))
            {
                Interlocked.Decrement(ref pendingTtsCount);
            }
        }

        if (Interlocked.Increment(ref pendingTtsCount) > 64)
        {
            Interlocked.Decrement(ref pendingTtsCount);
            throw new InvalidOperationException("The bounded TTS authorization queue is full.");
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var deadline = now.AddSeconds(2);
        if (!PendingTts.TryAdd(correlationId, new PendingTtsAuthorization(text, deadline)))
        {
            Interlocked.Decrement(ref pendingTtsCount);
            throw new InvalidOperationException("Could not reserve a TTS authorization request.");
        }

        if (sender?.Invoke(
                HostMessageTypes.CommandRequest,
                HostMessagePriority.Control,
                new HostCommandRequest(
                    "triggernometry",
                    "tts",
                    new Dictionary<string, string> { ["text"] = text }),
                correlationId,
                deadline) != true)
        {
            if (PendingTts.TryRemove(correlationId, out _))
            {
                Interlocked.Decrement(ref pendingTtsCount);
            }
            throw new InvalidOperationException("TTS broker queue rejected the request.");
        }
    }

    private static void SendPostNamazuSemanticAction(string action, string payload)
    {
        Demand("postnamazu", "GameCommand");
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length is 0 or > 32_768)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                "PostNamazu semantic payload must contain at most 32768 characters.");
        }

        var queued = sender?.Invoke(
            HostMessageTypes.CommandRequest,
            HostMessagePriority.Critical,
            new HostCommandRequest(
                "postnamazu",
                action,
                new Dictionary<string, string> { ["payload"] = payload }),
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddSeconds(2)) == true;
        if (!queued)
        {
            throw new InvalidOperationException(
                $"PostNamazu semantic broker queue rejected '{action}'.");
        }
    }

    internal static void CompleteCommand(
        string? correlationId,
        HostCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(correlationId) ||
            !PendingTts.TryRemove(correlationId, out var pending))
        {
            return;
        }

        Interlocked.Decrement(ref pendingTtsCount);

        if (!result.Success || pending.Deadline <= DateTimeOffset.UtcNow)
        {
            return;
        }

        var writer = Volatile.Read(ref ttsWriter)
                     ?? throw new InvalidOperationException(
                         "The isolated ACT TTS provider was unloaded before authorization completed.");
        writer(pending.Text);
    }

    public static void SubscribeTriggernometryZoneChanges(object plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (TriggerZoneListenerLock)
        {
            triggerZoneListener = new WeakReference<object>(plugin);
        }
    }

    public static void UnsubscribeTriggernometryZoneChanges(object plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (TriggerZoneListenerLock)
        {
            if (triggerZoneListener?.TryGetTarget(out var current) == true &&
                ReferenceEquals(current, plugin))
            {
                triggerZoneListener = null;
            }
        }
    }

    internal static void PublishTriggernometryZoneChange(uint territoryId, string zoneName)
    {
        object? plugin;
        lock (TriggerZoneListenerLock)
        {
            plugin = triggerZoneListener?.TryGetTarget(out var current) == true
                ? current
                : null;
        }

        if (plugin is null)
        {
            return;
        }

        try
        {
            var pluginType = plugin.GetType();
            pluginType.GetField(
                    "currentZone",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(plugin, zoneName);
            pluginType.Assembly.GetType("Triggernometry.PluginBridges.BridgeFFXIV")
                ?.GetField(
                    "ZoneID",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public)
                ?.SetValue(null, territoryId);
            var zoneChanged = pluginType.GetMethod(
                                  "ZoneChanged",
                                  System.Reflection.BindingFlags.Instance |
                                  System.Reflection.BindingFlags.NonPublic)
                              ?? throw new MissingMethodException(
                                  pluginType.FullName,
                                  "ZoneChanged");
            zoneChanged.Invoke(plugin, [zoneName]);
        }
        catch (Exception ex)
        {
            ReportException("triggernometry", "ZoneChanged adapter", ex);
        }
    }

    public static void UnsupportedNativeOperation()
        => throw new NotSupportedException(
            "Direct PostNamazu native memory/function access is unavailable in the external Host. " +
            "Use a semantic, whitelisted game bridge command.");

    public static T UnsupportedNativeOperation<T>()
        where T : struct
        => throw new NotSupportedException(
            "Direct PostNamazu native memory/function access is unavailable in the external Host.");

    public static bool ReportException(
        string pluginId,
        string phase,
        Exception exception)
    {
        var target = exception.TargetSite;
        var key =
            $"{pluginId}|{phase}|{exception.GetType().FullName}|{exception.Message}|" +
            $"{target?.DeclaringType?.FullName}|{target?.Name}";
        var repeats = DiagnosticRepeats.Count >= 2_048 &&
                      !DiagnosticRepeats.ContainsKey(key)
            ? 1
            : DiagnosticRepeats.AddOrUpdate(key, 1, static (_, count) => count + 1);
        var shouldReport = repeats <= 3 || (repeats & (repeats - 1)) == 0;
        if (!shouldReport)
        {
            return false;
        }

        var thread = Thread.CurrentThread;
        _ = sender?.Invoke(
            HostMessageTypes.Diagnostic,
            HostMessagePriority.Control,
            new HostDiagnostic(
                DateTimeOffset.UtcNow,
                pluginId,
                phase,
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message,
                exception.ToString(),
                target?.Module.Assembly.GetName().Name ?? string.Empty,
                target?.DeclaringType?.FullName ?? string.Empty,
                target?.Name ?? string.Empty,
                Environment.CurrentManagedThreadId,
                thread.Name,
                Application.MessageLoop,
                repeats),
            null,
            null);
        return true;
    }

    public static bool ReportDiagnosticMessage(
        string pluginId,
        string phase,
        string exceptionType,
        string message,
        string stackTrace,
        string sourceAssembly,
        string sourceType,
        string sourceMethod,
        DateTimeOffset timestamp)
    {
        var key =
            $"{pluginId}|{phase}|{exceptionType}|{message}|{sourceType}|{sourceMethod}";
        var repeats = DiagnosticRepeats.Count >= 2_048 &&
                      !DiagnosticRepeats.ContainsKey(key)
            ? 1
            : DiagnosticRepeats.AddOrUpdate(key, 1, static (_, count) => count + 1);
        var shouldReport = repeats <= 3 || (repeats & (repeats - 1)) == 0;
        if (!shouldReport)
        {
            return false;
        }

        var thread = Thread.CurrentThread;
        _ = sender?.Invoke(
            HostMessageTypes.Diagnostic,
            HostMessagePriority.Control,
            new HostDiagnostic(
                timestamp,
                pluginId,
                phase,
                exceptionType,
                message,
                stackTrace,
                sourceAssembly,
                sourceType,
                sourceMethod,
                Environment.CurrentManagedThreadId,
                thread.Name,
                Application.MessageLoop,
                repeats),
            null,
            null);
        return true;
    }

    public static void DemandPostNamazuNetwork()
        => Demand("postnamazu", "NetworkRequest");

    public static void DemandTriggernometryNetwork()
        => Demand("triggernometry", "NetworkRequest");

    public static bool IsPostNamazuNetworkAllowed()
        => AuditDecision("postnamazu", "NetworkRequest");

    public static void StartPostNamazuHttpListener(HttpListener listener)
    {
        Demand("postnamazu", "NetworkRequest");
        ArgumentNullException.ThrowIfNull(listener);
        var originalPrefixes = listener.Prefixes.Cast<string>().ToArray();
        var useLoopback = OperatingSystem.IsWindows() && !IsCurrentProcessElevated();
        if (useLoopback)
        {
            var compatiblePrefixes = originalPrefixes
                .Select(prefix => prefix
                    .Replace("http://*:", "http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
                    .Replace("http://+:", "http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (!compatiblePrefixes.SequenceEqual(originalPrefixes, StringComparer.OrdinalIgnoreCase))
            {
                listener.Prefixes.Clear();
                foreach (var prefix in compatiblePrefixes)
                {
                    listener.Prefixes.Add(prefix);
                }

                Console.WriteLine(
                    "PostNamazu HTTP listener uses loopback compatibility mode for standard-user Windows sessions.");
            }
        }

        try
        {
            listener.Start();
            Console.WriteLine(
                $"PostNamazu HTTP listener started: {string.Join(",", listener.Prefixes.Cast<string>())}");
        }
        catch (Exception ex)
        {
            ReportException("postnamazu", "HTTP listener startup", ex);
            Console.Error.WriteLine($"PostNamazu HTTP listener failed to start: {ex}");
            throw;
        }
    }

    public static void SkipPostNamazuThreadAbort(Thread _)
    {
        // HttpServer.Listen only uses this thread to queue the HttpListener worker.
        // HttpListener.Stop below this legacy call performs the actual shutdown.
    }

    public static bool IsTriggernometryNetworkAllowed()
        => AuditDecision("triggernometry", "NetworkRequest");

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool IsTriggernometryHighRiskScriptAllowed()
        => AuditDecision("triggernometry", "HighRiskScript");

    public static Process? StartTriggernometryProcess(string fileName)
    {
        Demand("triggernometry", "LaunchExternalProcess");
        return Process.Start(fileName);
    }

    public static Process? StartTriggernometryProcess(
        string fileName,
        string arguments)
    {
        Demand("triggernometry", "LaunchExternalProcess");
        return Process.Start(fileName, arguments);
    }

    public static Process? StartTriggernometryProcess(ProcessStartInfo startInfo)
    {
        Demand("triggernometry", "LaunchExternalProcess");
        return Process.Start(startInfo);
    }

    private static Thread CreateClipboardThread()
    {
        var thread = new Thread(ClipboardWorker)
        {
            IsBackground = true,
            Name = "ACT Host clipboard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static bool IsAllowed(string pluginId, string capability)
        => permissions.TryGetValue(pluginId, out var allowed) &&
           allowed.Contains(capability);

    private static bool AuditDecision(string pluginId, string capability)
    {
        var allowed = IsAllowed(pluginId, capability);
        Console.WriteLine(
            $"permission plugin={pluginId} capability={capability} decision=" +
            (allowed ? "allow" : "deny"));
        return allowed;
    }

    private static void Demand(string pluginId, string capability)
    {
        if (IsAllowed(pluginId, capability))
        {
            Console.WriteLine(
                $"permission plugin={pluginId} capability={capability} decision=allow");
            return;
        }

        Console.Error.WriteLine(
            $"permission plugin={pluginId} capability={capability} decision=deny");
        throw new UnauthorizedAccessException(
            $"ACT plugin '{pluginId}' is not authorized for capability '{capability}'.");
    }

    private static void ClipboardWorker()
    {
        foreach (var request in ClipboardRequests.GetConsumingEnumerable())
        {
            try
            {
                if (request.Text is not null)
                {
                    RetryClipboard(() => WriteClipboardText(request.Text));
                }
                else if (request.Lines is not null)
                {
                    var text = BuildBoundedClipboardText(request.Lines);
                    RetryClipboard(() => WriteClipboardText(text));
                }
                else
                {
                    var result = string.Empty;
                    RetryClipboard(() => result = Clipboard.GetText());
                    request.Completion!.TrySetResult(result);
                }
            }
            catch (Exception ex)
            {
                if (request.Completion is null)
                {
                    Console.Error.WriteLine($"Clipboard write failed: {ex}");
                }
                else
                {
                    request.Completion.TrySetException(ex);
                }
            }
        }
    }

    private static void RetryClipboard(Action action)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (ExternalException ex)
            {
                last = ex;
                if (attempt < 4)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }

        ExceptionDispatchInfo.Capture(last!).Throw();
    }

    private static void WriteClipboardText(string text)
    {
        var testWriter = Volatile.Read(ref clipboardWriterForTests);
        if (testWriter is not null)
        {
            testWriter(text);
            return;
        }

        Clipboard.SetText(text);
    }

    private static string BuildBoundedClipboardText(IReadOnlyList<object?> lines)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var item in lines)
        {
            var line = item?.ToString() ?? string.Empty;
            var separatorLength = builder.Length == 0 ? 0 : Environment.NewLine.Length;
            if (builder.Length + separatorLength + line.Length > MaximumClipboardCharacters)
            {
                throw new InvalidOperationException(
                    $"PostNamazu clipboard text exceeds {MaximumClipboardCharacters} characters.");
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    private sealed record ClipboardRequest(
        string? Text,
        TaskCompletionSource<string>? Completion,
        IReadOnlyList<object?>? Lines);

    private sealed record PendingTtsAuthorization(
        string Text,
        DateTimeOffset Deadline);
}

internal sealed class ConsoleActLogger : IActLogger
{
    public void Error(Exception exception, string message)
    {
        if (HostPluginBridge.ReportException("act-host", message, exception))
        {
            Console.Error.WriteLine($"{message}: {exception}");
        }
    }

    public void Verbose(Exception exception, string message)
        => Console.WriteLine($"{message}: {exception.Message}");

    public void Warning(string message)
        => Console.Error.WriteLine(message);
}
