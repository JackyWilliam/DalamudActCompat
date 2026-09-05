using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Advanced_Combat_Tracker;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Newtonsoft.Json;

internal static class CombatLogLifecycleSmokeTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DactPathWorkflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine($"Only synthetic data under: {root}");
        using (var fixture = new Fixture(Path.Combine(root, "normal")))
        {
            fixture.Parser.Emit("before-change");
            var oldFile = fixture.Parser.ActiveFile!;
            fixture.Apply(fixture.NewDirectory);
            await fixture.DrainAsync();
            fixture.Parser.Emit("after-change");
            Equal(fixture.Resolve(), fixture.NewDirectory, "normal button target");
            Equal(fixture.SavedDirectory(), fixture.NewDirectory, "normal persisted target");
            Check(!Contains(oldFile, "after-change"), "normal restart wrote to old Network file");
            Check(Contains(fixture.Parser.ActiveFile!, "after-change"), "normal new Network file is missing marker");

            // Saving an encounter deliberately uses the independent JSON directory,
            // even when the raw Network writer is in a custom location.
            var store = new EncounterStateStore();
            var repository = new EncounterRepository(new JsonFileStore(), fixture.Paths);
            await using (var encounters = new EncounterService(repository, store, fixture.Configuration,
                             fixture.Logger, fixture.Paths, fixture.Resolve))
            {
                var start = DateTimeOffset.Now.AddSeconds(-15);
                encounters.QueueFinishedEncounter(new Encounter(Guid.NewGuid(), start, start.AddSeconds(10),
                    "Diagnostic Zone", "Diagnostic Dummy", [], [], [], [], [], []));
            }
            Equal(Directory.GetFiles(fixture.Paths.EncounterLogDirectory, "*.json").Length, 1, "default encounter JSON count");
            Equal(Directory.GetFiles(fixture.NewDirectory, "*.json").Length, 0, "Network directory JSON count");
            Console.WriteLine("normal change: button/config/writer=new; old Network unchanged; encounter JSON still in default logs/encounters (by design)");

            // These are actual settings callbacks, not direct assignments to the resolver.
            fixture.Reset();
            await fixture.DrainAsync();
            Equal(fixture.Resolve(), fixture.Paths.CombatLogDirectory, "restore-default button target");
            fixture.Parser.Emit("after-default");
            Check(Contains(fixture.Parser.ActiveFile!, "after-default"), "restore default writer");
            for (var i = 0; i < 12; i++)
            {
                fixture.Apply(Path.Combine(fixture.Root, "rapid-" + i));
                await fixture.DrainAsync();
            }
            await fixture.DrainAsync();
            Equal(fixture.Resolve(), Path.Combine(fixture.Root, "rapid-11"), "rapid changes button");
            Equal(Path.GetDirectoryName(fixture.Parser.ActiveFile), fixture.Resolve(), "rapid changes writer");
            Console.WriteLine("restore default + 12 sequential directory switches: final configuration/button/writer agree");
        }

        using (var fixture = new Fixture(Path.Combine(root, "pending")))
        {
            fixture.Parser.PauseBeforeRestart = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Apply(fixture.NewDirectory);
            Equal(fixture.ActiveResolve(), fixture.Paths.CombatLogDirectory, "pending button must follow actual writer");
            Equal(Path.GetDirectoryName(fixture.Parser.ActiveFile), fixture.Paths.CombatLogDirectory, "pending writer target");
            for (var i = 0; i < 12; i++) fixture.Apply(Path.Combine(fixture.Root, "rejected-" + i));
            Equal(fixture.SavedDirectory(), fixture.NewDirectory, "busy directory changes must not overwrite the admitted request");
            Check(!fixture.Feedback.Last().Success, "busy request was not rejected");
            fixture.Parser.PauseBeforeRestart.SetResult();
            await fixture.DrainAsync();
            Equal(Path.GetDirectoryName(fixture.Parser.ActiveFile), fixture.NewDirectory, "pending final writer target");
            Equal(fixture.Parser.Restarts, 1, "busy directory requests must not queue redundant restarts");
            Console.WriteLine("PASS 12 overlapping directory requests rejected; one admitted restart");
        }

        using (var fixture = new Fixture(Path.Combine(root, "restart-failure")))
        {
            fixture.Apply(fixture.NewDirectory);
            await fixture.DrainAsync();
            fixture.Parser.FailBeforeStop = true;
            fixture.Reset();
            await fixture.DrainAsync();
            fixture.Parser.Emit("after-failed-reset");
            Equal(fixture.ActiveResolve(), fixture.NewDirectory, "failed reset button follows actual writer");
            Equal(Path.GetDirectoryName(fixture.Parser.ActiveFile), fixture.NewDirectory, "failed reset active writer");
            Check(fixture.Feedback.Last().Success == false, "restart failure should be reported");
            fixture.Parser.FailBeforeStop = false;
            fixture.Apply(fixture.Paths.CombatLogDirectory);
            await fixture.DrainAsync();
            Equal(fixture.ActiveResolve(), fixture.Paths.CombatLogDirectory, "same saved directory can be retried after failure");
            Console.WriteLine("failed restart: actual directory retained; retrying identical saved path succeeds");
        }

        foreach (var denied in new[] { "authorization", "shutdown", "blocked-before-execution" })
        {
            using var fixture = new Fixture(Path.Combine(root, denied));
            fixture.Set(denied == "authorization" ? "cloudRuntimeAuthorized" : denied == "shutdown" ? "backgroundOperationShutdownStarted" : "cloudAccessBlocked",
                denied == "authorization" ? (object)0 : denied == "shutdown" ? true : 1);
            fixture.Apply(fixture.NewDirectory);
            await fixture.DrainAsync();
            Equal(fixture.Resolve(), fixture.NewDirectory, denied + " saved path");
            Equal(Path.GetDirectoryName(fixture.Parser.ActiveFile), fixture.Paths.CombatLogDirectory, denied + " retained writer");
            Check(!fixture.Feedback.Last().Success, "skipped restart must report failure");
            Equal(fixture.ActiveResolve(), fixture.Paths.CombatLogDirectory, "skipped restart active button");
            fixture.Set("cloudRuntimeAuthorized", 1);
            fixture.Set("backgroundOperationShutdownStarted", false);
            fixture.Set("cloudAccessBlocked", 0);
            fixture.Apply(fixture.NewDirectory);
            await fixture.DrainAsync();
            Equal(fixture.ActiveResolve(), fixture.NewDirectory, "admission is released after " + denied);
            Console.WriteLine($"PASS {denied}: explicit failure, actual directory, retry admission released");
        }

        using (var fixture = new Fixture(Path.Combine(root, "save-failure")))
        {
            // Only suppress unrelated cloud error publication in the fixture. The
            // production save/rollback logic itself runs without alteration.
            fixture.Set("pluginDisposing", 1);
            fixture.SaveProxy.FailSave = true;
            fixture.Apply(fixture.NewDirectory);
            await fixture.DrainAsync();
            Equal(fixture.Resolve(), fixture.Paths.CombatLogDirectory, "save failure rollback memory");
            Equal(fixture.SavedDirectory(), fixture.Paths.CombatLogDirectory, "save failure persisted config");
            Equal(fixture.Parser.Restarts, 0, "save failure must not restart");
            Check(!fixture.Feedback.Last().Success, "save failure should be reported");
            Console.WriteLine("injected save failure: configuration/button/writer stay old; error reported; no restart");
        }

        using (var fixture = new Fixture(Path.Combine(root, "stopped")))
        {
            await fixture.Parser.StopAsync(default);
            fixture.Apply(fixture.NewDirectory);
            await fixture.DrainAsync();
            Equal(fixture.Parser.Restarts, 0, "stopped parser must remain stopped");
            await fixture.Parser.StartAsync(default);
            Equal(Path.GetDirectoryName(fixture.Parser.ActiveFile), fixture.NewDirectory, "next parser start should use saved path");
            Console.WriteLine("stopped parser: change saves without auto-start; next start uses custom directory");
        }

        using (var fixture = new Fixture(Path.Combine(root, "invalid-path")))
        {
            fixture.Apply(fixture.ConfigFile);
            await fixture.DrainAsync();
            Equal(fixture.Resolve(), fixture.Paths.CombatLogDirectory, "file used as directory must not change config");
            Equal(fixture.Parser.Restarts, 0, "invalid directory must not restart");
            Check(!fixture.Feedback.Last().Success, "invalid directory should report failure");
            Console.WriteLine("invalid directory (existing file): rejected before configuration change or restart");
        }

        using (var fixture = new Fixture(Path.Combine(root, "cloud-path")))
        {
            var service = new PortableConfigurationBackupService();
            var key = service.GenerateRecoveryKey();
            var archive = Path.Combine(root, "synthetic-backup.dactbackup.enc");
            await service.ExportEncryptedAsync(fixture.Paths.ConfigDirectory, archive, key, default);
            fixture.Apply(fixture.NewDirectory);
            await fixture.DrainAsync();
            await fixture.Parser.StopAsync(default);
            await service.RestoreEncryptedAsync(archive,
                fixture.Paths.ConfigDirectory, Path.Combine(root, "synthetic-rollback.dactbackup.enc"), key, default);
            fixture.ApplyRestoredMemory();
            Equal(fixture.Resolve(), fixture.NewDirectory, "cloud restore must retain current local path");
            Equal(fixture.SavedDirectory(), fixture.NewDirectory, "cloud restored file must retain current local path");
            await fixture.Parser.StartAsync(default);
            Equal(Path.GetDirectoryName(fixture.Parser.ActiveFile), fixture.NewDirectory, "post-cloud writer");
            Console.WriteLine("real offline encrypted backup(old) -> custom path -> restore: local custom path preserved in file/memory/next writer");
        }
        ProbeWriterStartingAfterStop(root);
        ProbePendingQueueDrain(root);
        ProbeIdleWriterFlush(root);
        ProbeFailedWriterStops(root);
        Console.WriteLine("PASS log directory lifecycle regressions (synthetic data only)");
    }

    private static void ProbeWriterStartingAfterStop(string root)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var directory = Path.Combine(root, "late-old-writer");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "Network_late_probe.log");
        var form = (FormActMain)RuntimeHelpers.GetUninitializedObject(typeof(FormActMain));
        typeof(FormActMain).GetField("<PluginLog>k__BackingField", flags)!.SetValue(form, new LogLifecycleActLogger());
        typeof(FormActMain).GetField("<LogQueue>k__BackingField", flags)!.SetValue(form, new ConcurrentQueue<string>());
        typeof(FormActMain).GetField("pluginActive", flags)!.SetValue(form, true);
        form.LogFilePath = file;
        form.WriteLogFile = true;
        using var workerReady = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        Exception? failure = null;
        // Deterministically model a started background thread not being scheduled
        // until after Exit. The body invoked after the barrier is the real writer.
        var thread = new Thread(() =>
        {
            workerReady.Set();
            if (!releaseWorker.Wait(TimeSpan.FromSeconds(3))) return;
            try { typeof(FormActMain).GetMethod("LogWriter", flags)!.Invoke(form, null); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            Check(workerReady.Wait(TimeSpan.FromSeconds(3)), "late writer not ready");
            form.ParseRawLogLine("queued-before-stop");
            typeof(FormActMain).GetMethod("Exit", flags)!.Invoke(form, null);
            Check(!File.Exists(file), "late writer file existed before scheduling");
        }
        finally
        {
            releaseWorker.Set();
            Check(thread.Join(TimeSpan.FromSeconds(3)), "late writer did not exit");
            GC.SuppressFinalize(form);
        }
        if (failure is not null) throw failure;
        Check(!File.Exists(file), "retired writer created a file after stop");
        Equal(form.LogQueue.Count, 1, "late writer should leave queued line undrained");
        Console.WriteLine("PASS delayed writer after Exit: no old-folder file created");
    }

    private static FormActMain CreateFileOnlyForm(string path, LogLifecycleActLogger logger)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var form = (FormActMain)RuntimeHelpers.GetUninitializedObject(typeof(FormActMain));
        GC.SuppressFinalize(form);
        typeof(FormActMain).GetField("<PluginLog>k__BackingField", flags)!.SetValue(form, logger);
        typeof(FormActMain).GetField("<LogQueue>k__BackingField", flags)!.SetValue(form, new ConcurrentQueue<string>());
        typeof(FormActMain).GetField("pluginActive", flags)!.SetValue(form, true);
        form.LogFilePath = path;
        form.WriteLogFile = true;
        typeof(FormActMain).GetMethod("StartLogWriterThread", flags)!.Invoke(form, null);
        return form;
    }

    private static void StopFileOnlyForm(FormActMain form)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(FormActMain).GetMethod("Exit", flags)!.Invoke(form, null);
        var worker = (Thread)typeof(FormActMain).GetField("logWriterThread", flags)!.GetValue(form)!;
        Check(worker.Join(TimeSpan.FromSeconds(3)), "File worker did not stop");
        Check(form.ActiveLogFilePath is null, "Stopped writer still advertises an active file");
    }

    private static void ProbeIdleWriterFlush(string root)
    {
        var path = Path.Combine(root, "Network_idle_tail.log");
        var form = CreateFileOnlyForm(path, new LogLifecycleActLogger());
        try
        {
            Check(SpinWait.SpinUntil(() => form.ActiveLogFilePath is not null, TimeSpan.FromSeconds(3)), "Idle writer failed to open");
            for (var i = 0; i < 5; i++) form.ParseRawLogLine($"final-line-{i}");
        }
        finally { StopFileOnlyForm(form); }
        Equal(File.ReadAllLines(path).Length, 5, "Idle writer must flush its accepted tail on Exit");
        Console.WriteLine("PASS idle writer shutdown flushes all five accepted tail lines");
    }

    private static void ProbeFailedWriterStops(string root)
    {
        var directory = Path.Combine(root, "initially-missing-directory");
        var path = Path.Combine(directory, "Network_retry.log");
        var logger = new LogLifecycleActLogger();
        var form = CreateFileOnlyForm(path, logger);
        try
        {
            Check(SpinWait.SpinUntil(() => Volatile.Read(ref logger.Errors) > 0, TimeSpan.FromSeconds(3)), "Missing-directory failure was not observed");
        }
        finally { StopFileOnlyForm(form); }
        Directory.CreateDirectory(directory);
        typeof(FormActMain).GetMethod("StartLogWriterThread", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        Check(!File.Exists(path), "A stopped writer restarted and created a stale file");
        Console.WriteLine("PASS failed writer terminates retries and refuses restart after Exit");
    }

    private static void ProbePendingQueueDrain(string root)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const int count = 8192;
        var directory = Path.Combine(root, "old-writer-drain");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "Network_drain_probe.log");
        var form = (FormActMain)RuntimeHelpers.GetUninitializedObject(typeof(FormActMain));
        typeof(FormActMain).GetField("<PluginLog>k__BackingField", flags)!.SetValue(form, new LogLifecycleActLogger());
        typeof(FormActMain).GetField("<LogQueue>k__BackingField", flags)!.SetValue(form, new ConcurrentQueue<string>());
        typeof(FormActMain).GetField("pluginActive", flags)!.SetValue(form, true);
        form.LogFilePath = file;
        form.WriteLogFile = true;
        var padding = new string('x', 512);
        for (var i = 0; i < count; i++) form.ParseRawLogLine($"queued-{i:D4}|{padding}");
        typeof(FormActMain).GetMethod("StartLogWriterThread", flags)!.Invoke(form, null);
        var writerThread = (Thread)typeof(FormActMain).GetField("logWriterThread", flags)!.GetValue(form)!;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Observe entry into the actual queue-draining loop, then stop. This is
            // synthetic stress, not evidence the customer ever had a full queue.
            while (form.LogQueue.Count == count)
            {
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(3), "drain writer did not start");
                Thread.Yield();
            }
            var queuedAtStop = form.LogQueue.Count;
            typeof(FormActMain).GetMethod("Exit", flags)!.Invoke(form, null);
            Check(writerThread.Join(TimeSpan.FromSeconds(5)), "drain writer did not exit");
            Check(queuedAtStop > 0, "drain scheduling window not captured");
            Check(Contains(file, "queued-8191|"), "pending tail not flushed after stop");
            Console.WriteLine($"Exit during drain: {queuedAtStop} pending at stop; old file received tail and writer exited. This is pending-data flush, not indefinite old-directory recording.");
        }
        finally
        {
            typeof(FormActMain).GetMethod("Exit", flags)!.Invoke(form, null);
            Check(writerThread.Join(TimeSpan.FromSeconds(5)), "drain writer cleanup failed");
            GC.SuppressFinalize(form);
        }
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T actual, T expected, string message)
        => Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{message}: actual={actual}, expected={expected}");

    internal static bool Contains(string file, string marker)
    {
        if (!File.Exists(file)) return false;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Contains(marker, StringComparison.Ordinal);
    }

    internal sealed class Fixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly Plugin plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        private readonly List<Task> tasks = [];
        public string Root { get; }
        public string NewDirectory { get; }
        public string ConfigFile { get; }
        public PluginConfiguration Configuration { get; }
        public PluginPaths Paths { get; }
        public PluginLogger Logger { get; }
        public SyntheticParser Parser { get; }
        public LogLifecycleConfigSaveProxy SaveProxy { get; }
        public ConcurrentQueue<(bool Success, string Message)> Feedback { get; } = new();

        public Fixture(string root)
        {
            Root = root;
            var configRoot = Path.Combine(root, "pluginConfigs");
            Directory.CreateDirectory(configRoot);
            ConfigFile = Path.Combine(configRoot, "DalamudActCompat.json");
            Paths = new PluginPaths(Path.Combine(configRoot, "DalamudActCompat"));
            NewDirectory = Path.Combine(root, "新的 Network 目录");
            Directory.CreateDirectory(Paths.CombatLogDirectory);
            Configuration = new PluginConfiguration { LogDirectory = Paths.CombatLogDirectory, HistoryLimit = 200 };
            Logger = new PluginLogger(DispatchProxy.Create<IPluginLog, LogLifecycleServiceProxy>());
            var pluginInterface = DispatchProxy.Create<IDalamudPluginInterface, LogLifecycleConfigSaveProxy>();
            SaveProxy = (LogLifecycleConfigSaveProxy)pluginInterface;
            SaveProxy.Path = ConfigFile;
            Set("configuration", Configuration);
            Set("paths", Paths);
            Set("logger", Logger);
            Set("text", new UiText(Configuration));
            Set("services", new PluginServices(pluginInterface, null!, null!, null!, null!, null!, null!, null!, null!, null!));
            Set("backgroundOperationLock", new object());
            Set("backgroundOperations", tasks);
            Set("cloudRuntimeAuthorized", 1);
            Parser = new SyntheticParser(Resolve);
            Set("parserEngine", Parser);
            pluginInterface.SavePluginConfig(Configuration);
            Parser.StartAsync(default).GetAwaiter().GetResult();
        }

        public void Set(string field, object value) => typeof(Plugin).GetField(field, Flags)!.SetValue(plugin, value);
        public string Resolve() => (string)typeof(Plugin).GetMethod("ResolveCombatLogDirectory", Flags)!.Invoke(plugin, null)!;
        public string ActiveResolve() => (string)typeof(Plugin).GetMethod("ResolveActiveCombatLogDirectory", Flags)!.Invoke(plugin, null)!;
        public string SavedDirectory() => JsonConvert.DeserializeObject<PluginConfiguration>(File.ReadAllText(ConfigFile))!.LogDirectory;
        public void Apply(string directory) => typeof(Plugin).GetMethod("ApplyCombatLogDirectory", Flags)!
            .Invoke(plugin, [directory, (Action<bool, string>)((success, message) => Feedback.Enqueue((success, message)))]);
        public void Reset() => typeof(Plugin).GetMethod("ResetCombatLogDirectory", Flags)!
            .Invoke(plugin, [(Action<bool, string>)((success, message) => Feedback.Enqueue((success, message)))]);
        public void ApplyRestoredMemory() => typeof(Plugin).GetMethod("ApplyRestoredConfigurationToMemory", Flags)!.Invoke(plugin, null);
        public Task DrainAsync() => Task.WhenAll(tasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(12));
        public void Dispose()
        {
            Parser.PauseBeforeRestart?.TrySetResult();
            DrainAsync().GetAwaiter().GetResult();
            Parser.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    internal sealed class SyntheticParser(Func<string> getDirectory) : IParserEngine
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly SemaphoreSlim lifecycle = new(1, 1);
        private FormActMain? form;
        private int sessions;
        public bool FailBeforeStop { get; set; }
        public TaskCompletionSource? PauseBeforeRestart { get; set; }
        public int Restarts { get; private set; }
        public string? ActiveFile { get; private set; }
        public string? ActiveLogDirectory => form?.ActiveLogFilePath is { } path ? Path.GetDirectoryName(path) : null;
        public ParserStatus Status { get; private set; } = ParserStatus.Disabled;
        public event EventHandler<ParserStatus>? StatusChanged { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (form is not null) return Task.CompletedTask;
            var directory = getDirectory();
            Directory.CreateDirectory(directory);
            ActiveFile = Path.Combine(directory, $"Network_probe_{++sessions}.log");
            form = (FormActMain)RuntimeHelpers.GetUninitializedObject(typeof(FormActMain));
            // Use only production file-writing code, never its GUI/reader/game hooks.
            typeof(FormActMain).GetField("<PluginLog>k__BackingField", Flags)!.SetValue(form, new LogLifecycleActLogger());
            typeof(FormActMain).GetField("<LogQueue>k__BackingField", Flags)!.SetValue(form, new ConcurrentQueue<string>());
            typeof(FormActMain).GetField("pluginActive", Flags)!.SetValue(form, true);
            form.LogFilePath = ActiveFile;
            form.WriteLogFile = true;
            typeof(FormActMain).GetMethod("StartLogWriterThread", Flags)!.Invoke(form, null);
            Check(SpinWait.SpinUntil(() => form.ActiveLogFilePath is not null, TimeSpan.FromSeconds(3)), "Writer failed to open");
            Status = new(ParserState.Running, "Synthetic parser with real file writer", DateTimeOffset.Now);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (form is not null)
            {
                typeof(FormActMain).GetMethod("Exit", Flags)!.Invoke(form, null);
                var thread = (Thread)typeof(FormActMain).GetField("logWriterThread", Flags)!.GetValue(form)!;
                Check(thread.Join(TimeSpan.FromSeconds(3)), "Synthetic writer failed to exit");
                GC.SuppressFinalize(form);
                form = null;
            }
            ActiveFile = null;
            Status = new(ParserState.Stopped, "Stopped", DateTimeOffset.Now);
            return Task.CompletedTask;
        }

        public async Task RestartAsync(CancellationToken cancellationToken)
        {
            if (PauseBeforeRestart is not null) await PauseBeforeRestart.Task.WaitAsync(cancellationToken);
            await lifecycle.WaitAsync(cancellationToken);
            try
            {
                Restarts++;
                if (FailBeforeStop) throw new IOException("Injected restart failure before old writer shutdown");
                await StopAsync(cancellationToken);
                await StartAsync(cancellationToken);
            }
            finally { lifecycle.Release(); }
        }

        public void Emit(string marker)
        {
            form!.ParseRawLogLine(marker);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!Contains(ActiveFile!, marker))
            {
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(3), "Writer marker flush timeout");
                Thread.Sleep(10);
            }
        }
        public void ResetCurrentEncounter() { }
        public async ValueTask DisposeAsync() { await StopAsync(default); lifecycle.Dispose(); }
    }
}

public class LogLifecycleConfigSaveProxy : DispatchProxy
{
    public string Path { get; set; } = "";
    public bool FailSave { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "SavePluginConfig")
        {
            if (FailSave) throw new IOException("Injected configuration write failure");
            // Dalamud's real Save waits synchronously for storage (verified in its
            // DLL); preserve that contract using an isolated synthetic config file.
            File.WriteAllText(Path, JsonConvert.SerializeObject(args![0]));
            return null;
        }
        throw new NotSupportedException("Unexpected Dalamud service access: " + targetMethod.Name);
    }
}


public sealed class LogLifecycleActLogger : IActLogger
{
    public int Errors;
    public void Error(Exception exception, string message)
    {
        Interlocked.Increment(ref Errors);
        Console.Error.WriteLine($"{message}: {exception.Message}");
    }
    public void Verbose(Exception exception, string message) { }
    public void Warning(string message) => Console.Error.WriteLine(message);
}

public class LogLifecycleServiceProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.ReturnType == typeof(void) ? null : targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
}
