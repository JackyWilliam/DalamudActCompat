using System.Reflection;
using System.Net.Http;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using DalamudActCompat.Host;
using DalamudActCompat.Protocol;

internal static class ManualPluginSmokeTests
{
    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags HiddenInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string uploaderDirectory, string titanDll)
    {
        var root = Path.Combine(Path.GetTempPath(), "DACT-manual-" + Guid.NewGuid().ToString("N"));
        var pluginRoot = Path.Combine(root, "plugins");
        Directory.CreateDirectory(pluginRoot);
        var originalHashes = new[] { Path.Combine(uploaderDirectory, "ACTLogsUploader.dll"), titanDll }
            .Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        Stage("actlogsuploader", uploaderDirectory, "ACTLogsUploader.dll", "ACTLogsUploader.Plugin");
        var titanRoot = Path.Combine(pluginRoot, "act.jsb_5");
        Directory.CreateDirectory(titanRoot);
        File.Copy(titanDll, Path.Combine(titanRoot, "神兵科技.dll"));
        WriteManifest(titanRoot, "act.jsb_5", "神兵科技.dll", "JJJSBTT");
        var logDirectory = Path.Combine(root, "network-logs");
        Directory.CreateDirectory(logDirectory);
        SetLogDirectory(logDirectory);
        SetPermissions([]);
        var tts = new List<string>();
        Func<string, HostMessagePriority, object, string?, DateTimeOffset?, bool> send = (type, _, payload, _, _) =>
        {
            if (type == HostMessageTypes.MatchaTtsRequest && payload is HostTtsRequest speech)
            { lock (tts) tts.Add(speech.Source + ":" + speech.Text); }
            return true;
        };
        typeof(HostPluginBridge).GetMethod("Configure", HiddenStatic)!.Invoke(null, [send]);
        var runtimeType = typeof(HostPluginBridge).Assembly.GetType("DalamudActCompat.Host.LegacyPluginRuntime")!;
        using var runtime = (IDisposable)Activator.CreateInstance(runtimeType,
            [pluginRoot, Path.Combine(root, "config"), new[] { "actlogsuploader", "act.jsb_5" }, true])!;
        runtimeType.GetMethod("Start")!.Invoke(runtime, null);
        var loaded = ((IEnumerable<string>)runtimeType.GetProperty("LoadedPluginIds")!.GetValue(runtime)!).ToArray();
        Assert(loaded.Length == 2, "Both real plugins must initialize in the generic Host: " + string.Join(',', loaded));
        var uploader = ActGlobals.oFormActMain.ActPlugins.Single(p => p.pluginObj.GetType().FullName == "ACTLogsUploader.Plugin").pluginObj;
        var titan = (Control)ActGlobals.oFormActMain.ActPlugins.Single(p => p.pluginObj.GetType().FullName == "JJJSBTT").pluginObj;
        var uploadAssembly = uploader.GetType().Assembly;
        var settingsType = uploadAssembly.GetType("ACTLogsUploader.Config.PluginSettings")!;
        var settingsPath = (string)settingsType.GetProperty("DefaultConfigPath")!.GetValue(null)!;
        Assert(settingsPath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Uploader accessed the real ACT profile.");
        var settings = uploader.GetType().GetField("_settings", HiddenInstance)!.GetValue(uploader)!;
        Assert(settingsType.GetProperty("AutoLogin")!.GetValue(settings) is false &&
               settingsType.GetProperty("AutoUpload")!.GetValue(settings) is false, "Unexpected automatic upload/login.");
        var resolve = uploader.GetType().GetMethod("ResolveLogDirectory")!;
        Assert((string?)resolve.Invoke(uploader, null) == logDirectory, "Uploader did not receive real network-log directory.");
        var changed = Path.Combine(root, "changed-logs"); Directory.CreateDirectory(changed);
        SetLogDirectory(changed);
        Assert((string?)resolve.Invoke(uploader, null) == changed, "Uploader retained a stale writer directory.");
        settingsType.GetProperty("LogDirectory")!.SetValue(settings, logDirectory);
        Assert((string?)resolve.Invoke(uploader, null) == logDirectory, "Explicit uploader directory was overridden.");
        settingsType.GetProperty("LogDirectory")!.SetValue(settings, "");
        SetLogDirectory(null);
        Assert(resolve.Invoke(uploader, null) is null, "Missing bridge guessed a different ACT installation.");
        settingsType.GetProperty("RegionCode")!.SetValue(settings, "OFFLINE-FIXTURE");
        settingsType.GetProperty("Password")!.SetValue(settings, "offline-synthetic-password");
        settingsType.GetMethod("Save")!.Invoke(settings, [null]);
        var reloaded = settingsType.GetMethod("Load")!.Invoke(null, [null])!;
        Assert((string?)settingsType.GetProperty("RegionCode")!.GetValue(reloaded) == "OFFLINE-FIXTURE", "Uploader settings round trip failed.");
        Assert((string?)settingsType.GetProperty("Password")!.GetValue(reloaded) == "offline-synthetic-password" &&
               !File.ReadAllText(settingsPath).Contains("offline-synthetic-password", StringComparison.Ordinal), "Uploader DPAPI credential round trip failed.");

        // Initialize the supplied native V8 engine with an arithmetic expression only:
        // no FFLogs sign-in, remote parser fetch, real logs or production uploads.
        uploadAssembly.GetType("ACTLogsUploader.Bootstrap")!.GetMethod("PrepareV8")!.Invoke(null, null);
        var v8 = Assembly.Load("ClearScript.V8").GetType("Microsoft.ClearScript.V8.V8ScriptEngine")!;
        using (var engine = (IDisposable)Activator.CreateInstance(v8)!)
        {
            var result = v8.GetMethod("Evaluate", [typeof(string)])!.Invoke(engine, ["6 * 7"]);
            Assert(Convert.ToInt32(result) == 42, "Supplied V8 dependencies did not execute.");
        }
        var parserType = uploadAssembly.GetType("ACTLogsUploader.Upload.ParserEngine")!;
        using var http = new HttpClient(new OfflineOnlyHandler());
        using (var parser = (IDisposable)Activator.CreateInstance(parserType, [http, "https://fixture.invalid"])!)
        {
            var cache = (string)parserType.GetProperty("CacheDir", HiddenInstance)!.GetValue(parser)!;
            Assert(cache.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Parser cache escaped the DACT profile.");
            Directory.CreateDirectory(cache);
            // Use the upstream startup/shim/host-object/JSON pipeline, with a cached
            // synthetic parser. Any accidental HTTP request fails immediately.
            File.WriteAllText(Path.Combine(cache, "fflogs_parser_v8_v3_fixture_invalid.js"),
                "window.addEventListener('message', function(e) { if (e.data.message === 'get-parser-version') " +
                "window.sendToHost('get-parser-version-completed', 0, null, 42); }); /*" + new string('x', 11000) + "*/");
            ((Task)parserType.GetMethod("StartParserAsync", HiddenInstance)!.Invoke(parser, null)!).GetAwaiter().GetResult();
            Assert(parserType.GetProperty("ParserVersion")!.GetValue(parser) is 42, "Uploader parser-to-host IPC failed.");
        }
        Assert(!titan.IsDisposed, "Titan UI is not alive.");
        // Logs must work before the user opens the plugin's configuration window.
        var log = new HostLogEvent(DateTimeOffset.Now, "fixture", false, "00:JJJSBTT {Alpha:Bravo:Charlie:Delta:Echo:Foxtrot:Golf:Hotel}");
        runtimeType.GetMethod("AcceptLogs")!.Invoke(runtime, [new[] { log }]);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !ReadTargets().Contains("Alpha")) Thread.Sleep(20);
        Assert(ReadTargets().SequenceEqual(new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel" }), "Live ACT party-macro log did not reach Titan.");
        Throws(() => HostPluginBridge.SendTitanTts("denied"));
        Throws(() => HostPluginBridge.FindTitanGameWindow("anything"));
        SetPermissions(["TextToSpeech", "GameCommand", "NativeSystemAccess"]);
        Assert(HostPluginBridge.FindTitanGameWindow("unrelated") == IntPtr.Zero, "No game PID must not select a window by title.");
        // Exercise the original three-target ordering through ACT callbacks, without
        // enabling its hotkey worker or addressing a real game window.
        var battleLogs = new[] { "Charlie", "Alpha", "Bravo" }.Select(name =>
            new HostLogEvent(DateTimeOffset.Now, "fixture", false, $"21:泰坦:2B6B:花岗岩牢狱:10234E83:{name}:0:0:0:0:0:")).ToArray();
        runtimeType.GetMethod("AcceptLogs")!.Invoke(runtime, [battleLogs]);
        Assert(tts.Contains("generic:石牢第一"), "Titan did not resolve real ACT mechanic lines to its speech callback.");
        var count = tts.Count;
        runtimeType.GetMethod("AcceptLogs")!.Invoke(runtime, [battleLogs.Select(line => line with { IsImport = true }).ToArray()]);
        Assert(tts.Count == count, "Imported logs unexpectedly triggered Titan.");
        var speechMethod = titan.GetType().GetMethod("TTS", HiddenInstance)!;
        speechMethod.Invoke(titan, ["石牢第二"]);
        Assert(tts.Contains("generic:石牢第二"), "Titan TTS did not reach the generic IPC route.");
        SetPermissions([]);
        Throws(() => HostPluginBridge.SendTitanKey(IntPtr.Zero, Keys.F5));
        runtime.Dispose();
        Assert(File.Exists(Path.Combine(root, "config", "FanHua.JJJSBTT.Settings.bin")), "Titan settings were not saved on unload.");
        Assert(originalHashes.SequenceEqual(new[] { Path.Combine(uploaderDirectory, "ACTLogsUploader.dll"), titanDll }
            .Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))), "An original DLL was modified.");
        Console.WriteLine("PASS: real manual plugins in generic Host, V8, isolated settings, log-directory changes, ACT logs, TTS, permission denial and unload.");

        string[] ReadTargets() => (string[])titan.GetType().GetField("_targets", HiddenInstance)!.GetValue(titan)!;
        void Stage(string id, string source, string dll, string entry)
        {
            var destination = Path.Combine(pluginRoot, id); Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source, "*.dll")) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            WriteManifest(destination, id, dll, entry);
        }
    }

    private static void WriteManifest(string directory, string id, string dll, string entry)
        => File.WriteAllText(Path.Combine(directory, "actcompat.plugin.json"), JsonSerializer.Serialize(new { Id = id, EntryAssembly = dll, EntryType = entry }));
    private static void SetLogDirectory(string? directory)
        => typeof(HostPluginBridge).GetMethod("ConfigureCombatLogDirectory", HiddenStatic)!.Invoke(null, [directory]);
    private static void SetPermissions(string[] grants)
        => typeof(HostPluginBridge).GetMethod("ConfigurePermissions", HiddenStatic)!.Invoke(null,
            [new HostPermissionSnapshot(new Dictionary<string, IReadOnlyList<string>> { ["act.jsb_5"] = grants }, ["act.jsb_5", "actlogsuploader"])]);
    private static void Throws(Action action)
    {
        try { action(); } catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected permission rejection.");
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class OfflineOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The manual-plugin smoke test must not use the network.");
    }
}
