using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using DalamudActCompat.Protocol;
using Newtonsoft.Json.Linq;

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
    private static readonly ConcurrentDictionary<string, PendingOverlayCall> PendingOverlayCalls =
        new(StringComparer.Ordinal);
    private static readonly FfxivDataRepository FfxivRepositoryInstance = new();
    private static readonly object TriggerZoneListenerLock = new();
    private static readonly object PostNamazuQueueLock = new();
    private static readonly object PostNamazuTaskLock = new();
    private static readonly object SilverDasherContextLock = new();
    private static readonly object MatchaContextLock = new();
    private static readonly List<string> PostNamazuQueueIds = [];
    private static readonly HashSet<Task> PostNamazuQueueTasks = [];
    private static readonly CancellationTokenSource PostNamazuQueueShutdown = new();
    private static WeakReference<object>? triggerZoneListener;
    private static Action<string>? ttsWriter;
    private static Func<string, string, bool>? silverDasherNotificationWriter;
    private static Func<string, bool>? matchaNotificationWriter;
    private static Action<string>? clipboardWriterForTests;
    private static long triggerEventDrops;
    private static int pendingTtsCount;
    private static int pendingOverlayCallCount;
    private static bool postNamazuShutdownStarted;
    private static Task<bool>? postNamazuShutdownTask;
    private static SilverDasherDataSubscription? silverDasherSubscription;
    private static HostZoneEvent? silverDasherZone;
    private static string? silverDasherRoot;
    private static MatchaDataSubscription? matchaSubscription;
    private static string? matchaRoot;
    private static string? matchaConfigRoot;

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

    internal static bool IsGameForeground()
        => GameForegroundDetector.IsGameForeground(FfxivRepositoryInstance.GetGameProcessId());

    internal static void ConfigureGameProcess(int processId)
    {
        FfxivRepositoryInstance.SetGameProcessId(processId);
        Console.WriteLine($"game process registered for ACT compatibility: pid={processId}");
    }

    internal static void ConfigureSilverDasherRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(
                $"SilverDasher plugin root does not exist: {fullRoot}");
        }

        lock (SilverDasherContextLock)
        {
            silverDasherRoot = fullRoot;
        }
    }

    internal static void ConfigureSilverDasherSubscription(
        SilverDasherDataSubscription? subscription)
    {
        lock (SilverDasherContextLock)
        {
            silverDasherSubscription = subscription;
        }
    }

    internal static void ConfigureMatchaContext(
        string pluginRoot,
        string configRoot,
        MatchaDataSubscription? subscription)
    {
        var fullPluginRoot = Path.GetFullPath(pluginRoot);
        var fullConfigRoot = Path.GetFullPath(configRoot);
        if (!Directory.Exists(fullPluginRoot))
        {
            throw new DirectoryNotFoundException(
                $"Matcha plugin root does not exist: {fullPluginRoot}");
        }

        Directory.CreateDirectory(fullConfigRoot);
        lock (MatchaContextLock)
        {
            matchaRoot = fullPluginRoot;
            matchaConfigRoot = fullConfigRoot;
            matchaSubscription = subscription;
        }
    }

    internal static void ClearMatchaContext()
    {
        lock (MatchaContextLock)
        {
            matchaSubscription = null;
            matchaRoot = null;
            matchaConfigRoot = null;
        }
    }

    public static Assembly LoadSilverDasherAssembly(string assemblyPath)
    {
        DemandSilverDasherCapability("ReadLocalConfiguration");
        var fullPath = Path.GetFullPath(assemblyPath);
        string root;
        lock (SilverDasherContextLock)
        {
            root = silverDasherRoot
                   ?? throw new InvalidOperationException(
                       "SilverDasher runtime root has not been configured.");
        }

        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            throw new UnauthorizedAccessException(
                $"SilverDasher dependency path is outside its plugin root: {fullPath}");
        }

        return Path.GetFileName(fullPath).ToLowerInvariant() switch
        {
            "silverdasher.core.dll" =>
                LegacyAssemblyRewriter.LoadSilverDasherCore(fullPath),
            "silverdasher.managedzodiark.dll" =>
                LegacyAssemblyRewriter.LoadSilverDasherManagedZodiark(fullPath),
            _ => Assembly.Load(File.ReadAllBytes(fullPath)),
        };
    }

    public static Process GetSilverDasherGameProcess()
    {
        DemandSilverDasherCapability("NativeGameMemory");
        return FfxivRepositoryInstance.GetGameProcessForAuthorizedBridge()!;
    }

    public static void SendSilverDasherTts(string text)
    {
        DemandSilverDasherCapability("TextToSpeech");
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

    public static bool SendSilverDasherNotification(string message, string detail)
    {
        DemandSilverDasherCapability("ReadCombatLogs");
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(detail);
        if (message.Length is 0 or > 512 || detail.Length > 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "SilverDasher notification text must contain at most 512 characters per field.");
        }

        var windowsWriter = Volatile.Read(ref silverDasherNotificationWriter);
        if (windowsWriter is not null)
        {
            try
            {
                if (windowsWriter(message, detail))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                ReportException(
                    "silverdasher",
                    "Windows notification",
                    ex);
            }
        }

        return sender?.Invoke(
                   HostMessageTypes.SilverDasherNotification,
                   HostMessagePriority.Critical,
                   new HostSilverDasherNotification(message, detail),
                   null,
                   DateTimeOffset.UtcNow.AddSeconds(2)) == true;
    }

    public static string NormalizeSilverDasherMqttPayload(string payload)
    {
        DemandSilverDasherCapability("NetworkRequest");
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0 || payload.Length > 64 * 1024)
        {
            return payload;
        }

        JObject root;
        try
        {
            root = JObject.Parse(payload);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return payload;
        }

        var changed = NormalizeInteger(root, "i") |
                      NormalizeInteger(root, "hp") |
                      NormalizeInteger(root, "m");
        if (root["c"] is JObject coordinates)
        {
            changed |= NormalizeInteger(coordinates, "x") |
                       NormalizeInteger(coordinates, "y");
        }

        return changed
            ? root.ToString(Newtonsoft.Json.Formatting.None)
            : payload;
    }

    private static bool NormalizeInteger(JObject value, string propertyName)
    {
        if (value[propertyName] is not JValue
            {
                Type: JTokenType.String,
                Value: string text,
            } token ||
            !int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return false;
        }

        token.Value = number;
        return true;
    }

    public static void DemandSilverDasherCapability(string capability)
        => Demand("silverdasher", capability);

    public static void DemandMatchaCapability(string capability)
        => Demand("matcha", capability);

    public static string ReadMatchaTextFile(string path)
    {
        DemandMatchaCapability("ReadLocalConfiguration");
        var fullPath = ValidateMatchaPath(path, write: false);
        return File.ReadAllText(fullPath);
    }

    public static void WriteMatchaTextFile(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (contents.Length > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contents),
                "Matcha file output exceeds 8 MiB.");
        }

        // Matcha cannot open its settings unless it can persist its own configuration and
        // telemetry choice. This bridge is already confined to the dedicated Matcha config root,
        // so it is not an arbitrary file-write capability. User-selected JSON exports remain
        // separately gated by WriteFiles in WriteMatchaUserTextFile.
        var fullPath = ValidateMatchaPath(path, write: true);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
    }

    public static string ReadMatchaUserTextFile(string path)
    {
        DemandMatchaCapability("ReadLocalConfiguration");
        var fullPath = ValidateMatchaUserJsonPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "The Matcha template selected by the user does not exist.",
                fullPath);
        }
        if (file.Length > 8 * 1024 * 1024)
        {
            throw new InvalidDataException(
                "The Matcha template selected by the user exceeds 8 MiB.");
        }

        return File.ReadAllText(fullPath);
    }

    public static void WriteMatchaUserTextFile(string path, string contents)
    {
        DemandMatchaCapability("WriteFiles");
        ArgumentNullException.ThrowIfNull(contents);
        if (contents.Length > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contents),
                "Matcha template output exceeds 8 MiB.");
        }

        var fullPath = ValidateMatchaUserJsonPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidDataException(
                            "The Matcha template path has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The Matcha template directory does not exist: {directory}");
        }

        File.WriteAllText(fullPath, contents);
    }

    public static Process? StartMatchaProcess(ProcessStartInfo startInfo)
    {
        DemandMatchaCapability("LaunchExternalProcess");
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!Uri.TryCreate(startInfo.FileName, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException(
                "Matcha may launch only an explicit HTTP or HTTPS project link.");
        }

        startInfo.UseShellExecute = true;
        startInfo.RedirectStandardInput = false;
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        return Process.Start(startInfo);
    }

    public static void SendMatchaTts(string text)
    {
        DemandMatchaCapability("TextToSpeech");
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.Length > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(text));
        }

        if (sender?.Invoke(
                HostMessageTypes.MatchaTtsRequest,
                HostMessagePriority.Control,
                new HostTtsRequest(text, "matcha"),
                null,
                DateTimeOffset.UtcNow.AddSeconds(2)) != true)
        {
            throw new InvalidOperationException("Matcha TTS IPC queue rejected the request.");
        }
    }

    public static void SendGenericTts(string text)
    {
        if (!permissions.Values.Any(allowed => allowed.Contains("TextToSpeech")))
        {
            throw new UnauthorizedAccessException(
                "No plugin in the generic ACT Host is authorized for TextToSpeech.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.Length > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(text));
        }

        // Generic ACT's PlayTts delegate has no caller identity. The whole process is
        // already explicitly trusted, so the request is audited at the Host boundary.
        if (sender?.Invoke(
                HostMessageTypes.MatchaTtsRequest,
                HostMessagePriority.Control,
                new HostTtsRequest(text, "generic"),
                null,
                DateTimeOffset.UtcNow.AddSeconds(2)) != true)
        {
            throw new InvalidOperationException("Generic ACT TTS IPC queue rejected the request.");
        }
    }

    public static bool SendMatchaNotification(string message, string notificationKind = "")
    {
        DemandMatchaCapability("ReadCombatLogs");
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(message));
        }

        var windowsWriter = Volatile.Read(ref matchaNotificationWriter);
        if (windowsWriter is not null)
        {
            try
            {
                if (windowsWriter(message))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                ReportException("matcha", "Windows notification", ex);
            }
        }

        var fallbackAccepted = sender?.Invoke(
            HostMessageTypes.MatchaNotification,
            HostMessagePriority.Critical,
            new HostMatchaNotification(message, ResolveMatchaNotificationKind(notificationKind)),
            null,
            DateTimeOffset.UtcNow.AddSeconds(2)) == true;
        if (!fallbackAccepted)
        {
            // Matcha's patched caller deliberately suppresses its blocking MessageBox;
            // keep the rejected real-time alert observable in the Host diagnostics.
            Console.Error.WriteLine(
                "Matcha typed game-side notification fallback rejected the message.");
        }

        return fallbackAccepted;
    }

    internal static HostMatchaNotificationKind ResolveMatchaNotificationKind(string notificationKind)
        // Matcha supplies its EventType name through the compatibility bridge so icon routing
        // stays stable across languages and user-configured formatter text.
        => notificationKind switch
        {
            "InitZone" => HostMatchaNotificationKind.WorldChanged,
            "MatchAlert" => HostMatchaNotificationKind.DutyEntered,
            _ => HostMatchaNotificationKind.General,
        };

    internal static void RelayMatchaLogLine(string line)
    {
        DemandMatchaCapability("ReadCombatLogs");
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        if (line.Length > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }

        if (sender?.Invoke(
                HostMessageTypes.MatchaLogLine,
                HostMessagePriority.Data,
                new HostMatchaLogLine(line),
                null,
                DateTimeOffset.UtcNow.AddSeconds(2)) != true)
        {
            throw new InvalidOperationException("Matcha log IPC queue rejected the line.");
        }
    }

    internal static void PublishMatchaNetwork(
        HostMatchaNetworkEvent networkEvent,
        bool sent)
    {
        if (!IsAllowed("matcha", "ReadCombatLogs"))
        {
            return;
        }

        var subscription = Volatile.Read(ref matchaSubscription);
        if (sent)
        {
            subscription?.PublishSent(
                networkEvent.Connection,
                networkEvent.Epoch,
                networkEvent.Message);
        }
        else
        {
            subscription?.PublishReceived(
                networkEvent.Connection,
                networkEvent.Epoch,
                networkEvent.Message);
        }
    }

    internal static void PublishSilverDasherNetwork(
        HostSilverDasherNetworkEvent networkEvent)
    {
        if (!IsAllowed("silverdasher", "ReadCombatLogs"))
        {
            return;
        }

        Volatile.Read(ref silverDasherSubscription)?.PublishNetwork(
            networkEvent.Connection,
            networkEvent.Epoch,
            networkEvent.Message);
    }

    internal static void PublishSilverDasherLogs(IReadOnlyList<HostLogEvent> logs)
    {
        if (IsAllowed("silverdasher", "ReadCombatLogs"))
        {
            Volatile.Read(ref silverDasherSubscription)?.PublishLogs(logs);
        }
    }

    internal static void PublishSilverDasherZone(HostZoneEvent zone)
    {
        lock (SilverDasherContextLock)
        {
            silverDasherZone = zone;
        }

        if (IsAllowed("silverdasher", "ReadCombatLogs"))
        {
            Volatile.Read(ref silverDasherSubscription)?.PublishZone(
                zone.TerritoryId,
                zone.ZoneName);
        }
    }

    internal static void ReplaySilverDasherState()
    {
        var subscription = Volatile.Read(ref silverDasherSubscription);
        if (subscription is null)
        {
            return;
        }

        HostZoneEvent? zone;
        lock (SilverDasherContextLock)
        {
            zone = silverDasherZone;
        }

        if (zone is not null && IsAllowed("silverdasher", "ReadCombatLogs"))
        {
            subscription.PublishZone(zone.TerritoryId, zone.ZoneName);
        }

        if (IsAllowed("silverdasher", "NativeGameMemory") &&
            FfxivRepositoryInstance.GetGameProcessForAuthorizedBridge() is { } process)
        {
            subscription.PublishProcess(process);
        }
    }

    public static bool IsPostNamazuNativeRuntimeAllowed()
        => IsAllowed("postnamazu", "GameCommand") &&
           IsAllowed("postnamazu", "NativeGameMemory");

    internal static void ApplyFfxivEntitySnapshot(HostFfxivEntitySnapshot snapshot)
        => FfxivRepositoryInstance.Apply(snapshot);

    internal static void ConfigureTtsWriter(Action<string>? writer)
        => Volatile.Write(ref ttsWriter, writer);

    internal static void ConfigureSilverDasherNotificationWriter(
        Func<string, string, bool>? writer)
        => Volatile.Write(ref silverDasherNotificationWriter, writer);

    internal static void ConfigureMatchaNotificationWriter(Func<string, bool>? writer)
        => Volatile.Write(ref matchaNotificationWriter, writer);

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

    public static bool CheckTriggernometryPostNamazuAdministratorRequirement()
    {
        if (!IsPostNamazuNativeRuntimeAllowed())
        {
            Console.WriteLine(
                "Triggernometry/PostNamazu native attachment is disabled; " +
                "the legacy ACT administrator notice does not apply to semantic bridge mode.");
        }

        // BridgeNamazu only uses this result to emit the legacy "run ACT as administrator"
        // compatibility notice. It is not an authorization decision. The external Host keeps
        // the real Windows token for Triggernometry's global security policy, and PostNamazu
        // native actions are still independently permission-gated and fail closed when process
        // access is unavailable.
        return true;
    }

    public static bool AllowTriggernometryCactbotTtsSuppression(string? triggerSetName)
    {
        if (!string.Equals(
                triggerSetName?.Trim(),
                "DancingMadUltimate",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The currently distributed U7b resource disables this Cactbot set on entry but never
        // restores it. Keep Cactbot as the fallback announcer so one resource load cannot leave
        // the entire ultimate silent for the rest of the session.
        Console.WriteLine(
            "Triggernometry compatibility: kept DancingMadUltimate Cactbot TTS enabled because " +
            "the U7b resource does not restore it after suppression.");
        return false;
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

    public static void SendPostNamazuCommand(string command)
    {
        Demand("postnamazu", "GameCommand");
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!command.StartsWith('/'))
        {
            throw new ArgumentException(
                "PostNamazu command must begin with '/'.",
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

    public static string NormalizePostNamazuMarkPayload(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        JObject root;
        try
        {
            root = JObject.Parse(payload);
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            throw new InvalidDataException(
                "PostNamazu mark payload is not valid JSON.",
                exception);
        }

        var actorToken = root.GetValue("ActorID", StringComparison.OrdinalIgnoreCase);
        if (actorToken?.Type != JTokenType.String)
        {
            return payload;
        }

        var actorText = actorToken.Value<string>()?.Trim();
        if (actorText is null ||
            !actorText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return payload;
        }

        if (!uint.TryParse(
                actorText.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var actorId))
        {
            throw new InvalidDataException(
                $"PostNamazu ActorID '{actorText}' is not a valid UInt32 hexadecimal value.");
        }

        actorToken.Replace(new JValue(actorId));
        return root.ToString(Newtonsoft.Json.Formatting.None);
    }

    public static void SendPostNamazuMark(string payload)
        => SendPostNamazuSemanticAction("postnamazu.mark", payload);

    public static void SendPostNamazuWaymark(string payload)
        => SendPostNamazuSemanticAction("postnamazu.place", payload);

    public static void SendPostNamazuPictoAct(string payload)
        => SendPostNamazuSemanticAction("postnamazu.pictoact", payload);

    public static void SendPostNamazuPreset(string payload)
        => SendPostNamazuSemanticAction("postnamazu.preset", payload);

    public static void SendPostNamazuKey(string payload)
        => SendPostNamazuSemanticAction("postnamazu.sendkey", payload);

    public static void SendPostNamazuQueue(object module, string payload)
    {
        Demand("postnamazu", "GameCommand");
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        var actions = JsonSerializer.Deserialize<PostNamazuQueueAction[]>(
                          payload,
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new InvalidDataException(
                          "PostNamazu queue payload did not contain an action array.");
        Task task;
        lock (PostNamazuTaskLock)
        {
            if (postNamazuShutdownStarted)
            {
                throw new InvalidOperationException(
                    "The ACT Host is stopping and cannot start another PostNamazu queue.");
            }

            task = Task.Run(
                () => RunPostNamazuQueueAsync(module, actions, PostNamazuQueueShutdown.Token),
                CancellationToken.None);
            PostNamazuQueueTasks.Add(task);
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                if (completedTask.IsFaulted)
                {
                    ReportException(
                        "postnamazu",
                        "Queue task",
                        completedTask.Exception?.GetBaseException()
                        ?? new InvalidOperationException("Unknown PostNamazu queue failure."));
                }

                lock (PostNamazuTaskLock)
                {
                    PostNamazuQueueTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public static void BreakPostNamazuQueue(string pattern)
    {
        Demand("postnamazu", "GameCommand");
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        lock (PostNamazuQueueLock)
        {
            if (string.Equals(pattern, "all", StringComparison.OrdinalIgnoreCase))
            {
                PostNamazuQueueIds.Clear();
                return;
            }

            var expression = new Regex($"^{pattern}$", RegexOptions.CultureInvariant);
            PostNamazuQueueIds.RemoveAll(expression.IsMatch);
        }
    }

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

    public static JToken? CallTriggernometryOverlayHandler(object request)
    {
        Demand("triggernometry", "HighRiskScript");
        ArgumentNullException.ThrowIfNull(request);
        if (request is not JObject payload)
        {
            throw new ArgumentException(
                "Triggernometry OverlayPlugin calls must contain a JSON object.",
                nameof(request));
        }

        var serialized = payload.ToString(Newtonsoft.Json.Formatting.None);
        if (serialized.Length is 0 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Triggernometry OverlayPlugin payload must contain at most 65536 characters.");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var expired in PendingOverlayCalls
                     .Where(pair => pair.Value.Deadline <= now)
                     .Select(pair => pair.Key))
        {
            if (PendingOverlayCalls.TryRemove(expired, out var pending))
            {
                Interlocked.Decrement(ref pendingOverlayCallCount);
                pending.Completion.TrySetException(
                    new TimeoutException("The game-side OverlayPlugin call expired."));
            }
        }

        if (Interlocked.Increment(ref pendingOverlayCallCount) > 32)
        {
            Interlocked.Decrement(ref pendingOverlayCallCount);
            throw new InvalidOperationException(
                "The bounded Triggernometry OverlayPlugin call queue is full.");
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var deadline = now.AddSeconds(2);
        var completion = new TaskCompletionSource<HostCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PendingOverlayCalls.TryAdd(
                correlationId,
                new PendingOverlayCall(completion, deadline)))
        {
            Interlocked.Decrement(ref pendingOverlayCallCount);
            throw new InvalidOperationException(
                "Could not reserve a Triggernometry OverlayPlugin request.");
        }

        try
        {
            if (sender?.Invoke(
                    HostMessageTypes.CommandRequest,
                    HostMessagePriority.Control,
                    new HostCommandRequest(
                        "triggernometry",
                        "triggernometry.overlay",
                        new Dictionary<string, string> { ["payload"] = serialized }),
                    correlationId,
                    deadline) != true)
            {
                throw new InvalidOperationException(
                    "Triggernometry OverlayPlugin broker queue rejected the request.");
            }

            var result = completion.Task
                .WaitAsync(TimeSpan.FromSeconds(2))
                .GetAwaiter()
                .GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Game-side OverlayPlugin rejected the call: {result.Detail ?? result.Status}");
            }

            return string.IsNullOrWhiteSpace(result.Detail)
                ? null
                : JToken.Parse(result.Detail);
        }
        finally
        {
            if (PendingOverlayCalls.TryRemove(correlationId, out _))
            {
                Interlocked.Decrement(ref pendingOverlayCallCount);
            }
        }
    }

    private static async Task RunPostNamazuQueueAsync(
        object module,
        IReadOnlyList<PostNamazuQueueAction> actions,
        CancellationToken cancellationToken)
    {
        var queueId = string.Empty;
        try
        {
            foreach (var action in actions)
            {
                if (action.D < 0)
                {
                    throw new InvalidDataException(
                        "PostNamazu queue delay cannot be negative.");
                }

                await Task.Delay(action.D, cancellationToken).ConfigureAwait(false);
                lock (PostNamazuQueueLock)
                {
                    if (queueId.Length > 0 && !PostNamazuQueueIds.Contains(queueId))
                    {
                        return;
                    }
                }

                var command = action.C?.Trim()
                              ?? throw new InvalidDataException(
                                  "PostNamazu queue action has no command.");
                var actionPayload = action.P ?? string.Empty;
                if (command.Equals("qid", StringComparison.OrdinalIgnoreCase))
                {
                    lock (PostNamazuQueueLock)
                    {
                        if (queueId.Length > 0)
                        {
                            PostNamazuQueueIds.Remove(queueId);
                        }

                        queueId = actionPayload;
                        if (queueId.Length > 0)
                        {
                            PostNamazuQueueIds.Add(queueId);
                        }
                    }
                    continue;
                }

                DispatchPostNamazuRegisteredAction(module, command, actionPayload);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportException("postnamazu", "Queue action", ex);
        }
        finally
        {
            if (queueId.Length > 0)
            {
                lock (PostNamazuQueueLock)
                {
                    PostNamazuQueueIds.Remove(queueId);
                }
            }
        }
    }

    internal static Task<bool> StopPostNamazuQueuesAsync()
    {
        var shouldCancel = false;
        Task[] tasks;
        lock (PostNamazuTaskLock)
        {
            if (postNamazuShutdownTask is not null)
            {
                return postNamazuShutdownTask;
            }

            if (!postNamazuShutdownStarted)
            {
                postNamazuShutdownStarted = true;
                shouldCancel = true;
            }

            tasks = PostNamazuQueueTasks.ToArray();
        }

        if (shouldCancel)
        {
            PostNamazuQueueShutdown.Cancel();
        }

        lock (PostNamazuTaskLock)
        {
            postNamazuShutdownTask ??= StopPostNamazuQueuesCoreAsync(tasks);
            return postNamazuShutdownTask;
        }
    }

    private static async Task<bool> StopPostNamazuQueuesCoreAsync(Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine(
                "PostNamazu compatibility queue tasks did not stop within two seconds; " +
                "the isolated Host process will finish shutdown.");
            return false;
        }
        catch (Exception ex)
        {
            ReportException("postnamazu", "Queue shutdown", ex);
            return true;
        }
    }

    private static void DispatchPostNamazuRegisteredAction(
        object module,
        string command,
        string payload)
    {
        const BindingFlags flags =
            BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        var moduleBase = module.GetType().BaseType
                         ?? throw new MissingMemberException(
                             module.GetType().FullName,
                             "NamazuModule base type");
        var plugin = moduleBase.GetProperty("PostNamazu", flags)?.GetValue(null)
                     ?? throw new MissingMemberException(
                         moduleBase.FullName,
                         "PostNamazu");
        var doAction = plugin.GetType().GetMethod(
                           "DoAction",
                           flags,
                           binder: null,
                           [typeof(string), typeof(string)],
                           modifiers: null)
                       ?? throw new MissingMethodException(
                           plugin.GetType().FullName,
                           "DoAction");
        doAction.Invoke(plugin, [command, payload]);
    }

    internal static void CompleteCommand(
        string? correlationId,
        HostCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return;
        }

        if (PendingOverlayCalls.TryRemove(correlationId, out var overlayCall))
        {
            Interlocked.Decrement(ref pendingOverlayCallCount);
            overlayCall.Completion.TrySetResult(result);
            return;
        }

        if (!PendingTts.TryRemove(correlationId, out var pending))
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
        var useLoopback = ShouldUsePostNamazuLoopbackFallback(originalPrefixes);
        if (useLoopback)
        {
            _ = TryUsePostNamazuLoopbackPrefixes(listener, originalPrefixes);
        }

        try
        {
            listener.Start();
            if (useLoopback)
            {
                Console.WriteLine(
                    "PostNamazu HTTP wildcard binding was denied by Windows URL ACL; " +
                    "the listener visibly fell back to loopback-only mode.");
            }
        }
        catch (Exception ex)
        {
            ReportException("postnamazu", "HTTP listener startup", ex);
            Console.Error.WriteLine($"PostNamazu HTTP listener failed to start: {ex}");
            throw;
        }

        Console.WriteLine(
            $"PostNamazu HTTP listener started: {string.Join(",", listener.Prefixes.Cast<string>())}");
    }

    private static bool ShouldUsePostNamazuLoopbackFallback(
        IReadOnlyList<string> originalPrefixes)
    {
        if (!OperatingSystem.IsWindows() ||
            !originalPrefixes.Any(prefix =>
                prefix.Contains("http://*:", StringComparison.OrdinalIgnoreCase) ||
                prefix.Contains("http://+:", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        using var probe = new HttpListener();
        foreach (var prefix in originalPrefixes)
        {
            probe.Prefixes.Add(prefix);
        }

        try
        {
            probe.Start();
            probe.Stop();
            return false;
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            return true;
        }
    }

    public static void SkipPostNamazuThreadAbort(Thread _)
    {
        // HttpServer.Listen only uses this thread to queue the HttpListener worker.
        // HttpListener.Stop below this legacy call performs the actual shutdown.
    }

    public static bool IsTriggernometryNetworkAllowed()
        => AuditDecision("triggernometry", "NetworkRequest");

    private static bool TryUsePostNamazuLoopbackPrefixes(
        HttpListener listener,
        IReadOnlyList<string> originalPrefixes)
    {
        var compatiblePrefixes = originalPrefixes
            .Select(prefix => prefix
                .Replace("http://*:", "http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
                .Replace("http://+:", "http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (compatiblePrefixes.SequenceEqual(originalPrefixes, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        listener.Prefixes.Clear();
        foreach (var prefix in compatiblePrefixes)
        {
            listener.Prefixes.Add(prefix);
        }

        return true;
    }

    public static bool IsTriggernometryHighRiskScriptAllowed()
        => AuditDecision("triggernometry", "HighRiskScript");

    public static Process? StartTriggernometryProcess(string fileName)
    {
        Demand("triggernometry", "LaunchExternalProcess");
        return IsWebAddress(fileName)
            ? Process.Start(PrepareTriggernometryStartInfo(new ProcessStartInfo(fileName)))
            : Process.Start(fileName);
    }

    public static Process? StartTriggernometryProcess(
        string fileName,
        string arguments)
    {
        Demand("triggernometry", "LaunchExternalProcess");
        if (!IsWebAddress(fileName))
        {
            return Process.Start(fileName, arguments);
        }

        var startInfo = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
        };
        return Process.Start(PrepareTriggernometryStartInfo(startInfo));
    }

    public static Process? StartTriggernometryProcess(ProcessStartInfo startInfo)
    {
        Demand("triggernometry", "LaunchExternalProcess");
        if (ShouldSkipTriggernometryPlaceholderProcess(startInfo))
        {
            Console.WriteLine(
                "Triggernometry placeholder LaunchProcess test skipped; " +
                "use the live-values test to open the configured target.");
            return null;
        }

        return Process.Start(PrepareTriggernometryStartInfo(startInfo));
    }

    public static void SkipTriggernometryStartupUpdateCheck(
        object plugin,
        bool isManual)
    {
        _ = plugin;
        _ = isManual;
        Console.WriteLine(
            "Triggernometry startup update check delegated to the managed bundled-plugin updater.");
    }

    private static ProcessStartInfo PrepareTriggernometryStartInfo(ProcessStartInfo startInfo)
    {
        if (!IsWebAddress(startInfo.FileName))
        {
            return startInfo;
        }

        startInfo.UseShellExecute = true;
        startInfo.RedirectStandardInput = false;
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        startInfo.CreateNoWindow = false;
        return startInfo;
    }

    private static bool ShouldSkipTriggernometryPlaceholderProcess(ProcessStartInfo startInfo)
        => string.Equals(startInfo.FileName?.Trim(), "test", StringComparison.OrdinalIgnoreCase) &&
           string.IsNullOrWhiteSpace(startInfo.Arguments);

    private static bool IsWebAddress(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string ValidateMatchaPath(string path, bool write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        string pluginRoot;
        string configRoot;
        lock (MatchaContextLock)
        {
            pluginRoot = matchaRoot
                         ?? throw new InvalidOperationException(
                             "Matcha plugin root has not been configured.");
            configRoot = matchaConfigRoot
                         ?? throw new InvalidOperationException(
                             "Matcha config root has not been configured.");
        }

        static bool IsInside(string candidate, string root)
            => candidate.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

        if (write ? !IsInside(fullPath, configRoot) :
            !IsInside(fullPath, configRoot) && !IsInside(fullPath, pluginRoot))
        {
            throw new UnauthorizedAccessException(
                $"Matcha {(write ? "write" : "read")} path is outside its assigned roots: {fullPath}");
        }

        return fullPath;
    }

    private static string ValidateMatchaUserJsonPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(
                Path.GetExtension(fullPath),
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "Matcha template import and export accepts only JSON files selected by the user.");
        }

        return fullPath;
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

    private sealed record PendingOverlayCall(
        TaskCompletionSource<HostCommandResult> Completion,
        DateTimeOffset Deadline);

    private sealed record PostNamazuQueueAction(string? C, string? P, int D);

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
