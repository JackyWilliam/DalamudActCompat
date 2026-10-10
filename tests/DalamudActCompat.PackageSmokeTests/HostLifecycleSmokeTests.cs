using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Protocol;

internal static class HostLifecycleSmokeTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task RunAsync(string root)
    {
        var errors = new List<string>();
        await Check("cancelled shutdown", () => CancelledShutdownAsync(root));
        await Check("failed IPC writer", FailedWriterAsync);
        await Check("real Host lifecycle", () => RealHostLifecycleAsync(root));
        await Check("independent startup", IndependentStartupAsync);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        Console.WriteLine("PASS: Host lifecycle fault and cancellation regressions.");

        async Task Check(string name, Func<Task> test)
        {
            try { await test(); }
            catch (Exception ex) { errors.Add(name + ": " + ex.GetBaseException().Message); }
        }
    }

    private static async Task CancelledShutdownAsync(string root)
    {
        await using var peer = await Peer.CreateAsync();
        await using var supervisor = new ActHostSupervisor(Path.Combine(root, "host"), Path.Combine(root, "plugins"),
            Path.Combine(root, "config"), peer.Client, Logger());
        var owner = (CompatibilityHostProcess)typeof(ActHostSupervisor).GetField("process", Hidden)!.GetValue(supervisor)!;
        // An isolated child deliberately ignores shutdown, so pipe cleanup alone
        // cannot make this test pass by causing a cooperative Host to exit.
        await owner.StartAsync(HostLaunchSpec.ForExecutable(Environment.ProcessPath!), ["--host-stubborn-child"], CancellationToken.None);
        typeof(ActHostSupervisor).GetField("state", Hidden)!.SetValue(supervisor, HostSupervisorState.Running);
        using var cancel = new CancellationTokenSource();
        try
        {
            var stopping = supervisor.StopAsync(cancel.Token);
            await peer.ReadUntilAsync(HostMessageTypes.Shutdown);
            cancel.Cancel();
            try { await stopping.WaitAsync(TimeSpan.FromSeconds(6)); }
            catch (OperationCanceledException) { }
            Assert(peer.Client.Status == HostConnectionStatus.Stopped, "Cancelled ACK wait left the IPC connection active.");
            Assert(!owner.IsRunning && supervisor.Snapshot.State == HostSupervisorState.Stopped, "Cancelled stop left the owned process alive.");
        }
        finally
        {
            await owner.StopAsync(CancellationToken.None);
            // Supervisor owns the client; Peer releases only its server handles.
        }
    }

    private static async Task FailedWriterAsync()
    {
        using var releaseWatchdog = new ManualResetEventSlim();
        var watchdogEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var peer = await Peer.CreateAsync(() =>
        {
            // Pause a healthy watchdog tick while the writer faults. Its final
            // status update must not turn this broken session back into Connected.
            if (Interlocked.Increment(ref reads) > 1)
            {
                watchdogEntered.TrySetResult();
                releaseWatchdog.Wait(TimeSpan.FromSeconds(4));
            }
            return null;
        });
        await using var client = peer.Client;
        await watchdogEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Faulted += (_, _) => fault.TrySetResult();
        Assert(client.TryEnqueue("fixture.oversize", HostMessagePriority.Control,
            new string('x', HostProtocol.MaximumFrameBytes)), "Connected test client rejected its queue.");
        try { await fault.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { throw new InvalidOperationException("Writer died without reporting a fault; heartbeat-only watchdog cannot detect it."); }
        Assert(client.Status == HostConnectionStatus.Faulted, "Writer error did not fault the session.");
        releaseWatchdog.Set();
        await ((Task)typeof(HostIpcClient).GetField("watchdogLoop", Hidden)!.GetValue(client)!)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert(client.Status == HostConnectionStatus.Faulted, "Healthy watchdog overwrote the writer's fault.");
    }

    private static async Task RealHostLifecycleAsync(string root)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DalamudActCompat.slnx"))) repository = repository.Parent;
        Assert(repository is not null, "Repository was not found.");
        var packaged = Path.Combine(repository!.FullName, "src", "DalamudActCompat", "bin", "Release", "host");
        var originalDirectory = Path.Combine(root, "old-plugin-directory");
        var currentDirectory = originalDirectory;
        Directory.CreateDirectory(originalDirectory);
        var logger = Logger();
        var client = new HostIpcClient(new EncounterStateStore(), logger,
            () => new HostPermissionSnapshot(new Dictionary<string, IReadOnlyList<string>>
                { ["community.lifecycle"] = ["ReadCombatLogs", "TextToSpeech"] }, ["community.lifecycle"]), expectedTtsSource: "generic");
        await using var supervisor = new ActHostSupervisor(Path.Combine(root, "real-host"), originalDirectory,
            Path.Combine(root, "real-config"), client, logger, packagedHostDirectory: packaged,
            pluginDirectoryProvider: () => currentDirectory);
        var owner = (CompatibilityHostProcess)typeof(ActHostSupervisor).GetField("process", Hidden)!.GetValue(supervisor)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        Assert(await supervisor.StartAutomaticallyAsync(deadline.Token), "Initial automatic start was treated as a user pause.");
        await supervisor.WaitForPluginStartupAsync(deadline.Token);
        var firstSession = supervisor.Snapshot.SessionId;
        await supervisor.StopAsync(deadline.Token);
        currentDirectory = Path.Combine(root, "new-plugin-directory");
        var plugin = Path.Combine(currentDirectory, "community.lifecycle"); Directory.CreateDirectory(plugin);
        var fixture = Path.Combine(repository.FullName, "tests", "DalamudActCompat.HostSmokeTests", "bin", "Release",
            "net10.0-windows10.0.17763.0", "DalamudActCompat.HostSmokeTests.dll");
        File.Copy(fixture, Path.Combine(plugin, "fixture.dll"));
        File.WriteAllText(Path.Combine(plugin, "actcompat.plugin.json"), JsonSerializer.Serialize(new
            { Id = "community.lifecycle", EntryAssembly = "fixture.dll", EntryType = "GenericActPluginHostFixture" }));
        Assert(await supervisor.StartAutomaticallyAsync(deadline.Token), "Maintenance stop blocked automatic resume.");
        await supervisor.WaitForPluginStartupAsync(deadline.Token);
        Assert(client.HostHealthDetail.Contains("community.lifecycle", StringComparison.Ordinal), "Host used the stale plugin directory after restart.");
        var currentSession = supervisor.Snapshot.SessionId;
        var child = (Process)typeof(CompatibilityHostProcess).GetField("process", Hidden)!.GetValue(owner)!;
        var pid = child.Id;
        // Emulate a fault callback delayed behind a manual restart. It must neither
        // kill this new session nor consume its restart budget.
        await (Task)typeof(ActHostSupervisor).GetMethod("RestartAfterFaultAsync", Hidden)!
            .Invoke(supervisor, [new IOException("old session"), firstSession])!;
        Assert(owner.IsRunning && !child.HasExited && supervisor.Snapshot.SessionId == currentSession && child.Id == pid,
            "An old recovery task stopped the replacement Host.");
        Assert(((Queue<DateTimeOffset>)typeof(ActHostSupervisor).GetField("restartHistory", Hidden)!.GetValue(supervisor)!).Count == 0,
            "Stale recovery consumed the new session's retry budget.");

        await supervisor.StopAsync(deadline.Token, userRequested: true);
        Assert(!await supervisor.StartAutomaticallyAsync(deadline.Token) && !owner.IsRunning,
            "Permission refresh restarted a Host stopped by the user.");
        await supervisor.StartAsync(deadline.Token);
        await supervisor.WaitForPluginStartupAsync(deadline.Token);
        currentSession = supervisor.Snapshot.SessionId;
        child = (Process)typeof(CompatibilityHostProcess).GetField("process", Hidden)!.GetValue(owner)!;
        child.Kill(entireProcessTree: true);
        while (supervisor.Snapshot.SessionId == currentSession || supervisor.Snapshot.State != HostSupervisorState.Running)
            await Task.Delay(20, deadline.Token);
        await supervisor.WaitForPluginStartupAsync(deadline.Token);
        Assert(client.HostHealthDetail.Contains("community.lifecycle", StringComparison.Ordinal), "Crash recovery did not reload the generic plugin.");
        await supervisor.StopAsync(deadline.Token, userRequested: true);
    }

    private static PluginLogger Logger() => new(DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>());

    private static async Task IndependentStartupAsync()
    {
        foreach (var failure in new[] { "none", "shared", "matcha", "timeout" })
        {
            var calls = new List<string>();
            var errors = new List<string>();
            await HostStartupSequence.StartAsync(async token =>
                {
                    calls.Add("shared");
                    if (failure == "shared") throw new IOException("fixture");
                    if (failure == "timeout") await Task.Delay(Timeout.Infinite, token);
                    return true;
                }, token =>
                {
                    calls.Add("matcha");
                    if (failure == "matcha") throw new IOException("fixture");
                    return Task.CompletedTask;
                }, token =>
                {
                    token.ThrowIfCancellationRequested();
                    calls.Add("generic");
                    return Task.CompletedTask;
                }, (role, _) => errors.Add(role), CancellationToken.None, TimeSpan.FromMilliseconds(50));
            Assert(calls.SequenceEqual(failure is "shared" or "timeout" ? ["shared", "generic"] : new[] { "shared", "matcha", "generic" }),
                "Another Host's failure/timeout blocked the generic Host.");
            Assert(errors.Count == (failure == "none" ? 0 : 1), "Startup failure was not reported per role.");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var invoked = false;
        try
        {
            await HostStartupSequence.StartAsync(_ => { invoked = true; return Task.FromResult(true); },
                _ => Task.CompletedTask, _ => Task.CompletedTask, (_, _) => { }, cancelled.Token);
            throw new InvalidOperationException("Cancelled plugin shutdown started a Host.");
        }
        catch (OperationCanceledException) { Assert(!invoked, "Startup ignored shutdown cancellation."); }
    }
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly NamedPipeServerStream input;
        private readonly NamedPipeServerStream output;
        public HostIpcClient Client { get; }
        private Peer(NamedPipeServerStream input, NamedPipeServerStream output, Func<string?>? combatLogDirectory)
        {
            this.input = input; this.output = output;
            Client = new HostIpcClient(new EncounterStateStore(), Logger(), combatLogDirectory: combatLogDirectory);
        }
        public static async Task<Peer> CreateAsync(Func<string?>? combatLogDirectory = null)
        {
            var name = "DACT-lifecycle-test-" + Guid.NewGuid().ToString("N");
            var peer = new Peer(
                new NamedPipeServerStream(name + "-g2h", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
                new NamedPipeServerStream(name + "-h2g", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly), combatLogDirectory);
            await Task.WhenAll(peer.input.WaitForConnectionAsync(), peer.output.WaitForConnectionAsync(),
                peer.Client.ConnectAsync(name, "fixture", CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
            await peer.ReadUntilAsync(HostMessageTypes.Permissions);
            return peer;
        }
        public async Task ReadUntilAsync(string type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await HostFrameCodec.ReadAsync(input, timeout.Token) is { } message)
                if (message.Type == type) return;
            throw new EndOfStreamException(type);
        }
        public ValueTask DisposeAsync()
        {
            input.Dispose(); output.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
