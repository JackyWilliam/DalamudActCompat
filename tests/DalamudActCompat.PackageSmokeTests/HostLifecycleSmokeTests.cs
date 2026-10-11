using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
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
        await Check("stop during recovery start", () => StopDuringRecoveryAsync(root, PackagedHostDirectory()));
        await Check("stop during failing recovery", () => StopDuringRecoveryAsync(root, FailingHostPackage(root)));
        await Check("blocked Host reader", BlockedHostReaderAsync);
        await Check("circuit with failing cleanup", () => CircuitOpensWhenCleanupFailsAsync(root));
        await Check("sign-out during permission refresh", () => SignOutDuringPermissionRefreshAsync(root));
        await Check("factory reset lock ownership", () => FactoryResetLockOwnershipAsync(root));
        await Check("sign-out during maintenance resume", () => SignOutDuringMaintenanceResumeAsync(root));
        await Check("stale memory recovery", () => StaleMemoryRecoveryAsync(root));
        await Check("stop during restart stop phase", () => StopDuringRestartStopPhaseAsync(root));
        await Check("unload during automatic recovery", () => UnloadDuringRecoveryAsync(root));
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

    private static async Task UnloadDuringRecoveryAsync(string root)
    {
        foreach (var memory in new[] { false, true })
        {
            await using var peer = await Peer.CreateAsync();
            var log = DispatchProxy.Create<IPluginLog, RecoveryDisposalLogProxy>();
            var supervisor = new ActHostSupervisor(Path.Combine(root, "unload-host"), Path.Combine(root, "unload-plugins"),
                Path.Combine(root, "unload-config"), peer.Client, new PluginLogger(log), enableMemoryProtection: memory);
            var owner = (CompatibilityHostProcess)typeof(ActHostSupervisor).GetField("process", Hidden)!.GetValue(supervisor)!;
            await owner.StartAsync(HostLaunchSpec.ForExecutable(Environment.ProcessPath!), ["--host-stubborn-child"], CancellationToken.None);
            typeof(ActHostSupervisor).GetField("state", Hidden)!.SetValue(supervisor, HostSupervisorState.Running);
            typeof(ActHostSupervisor).GetField("manuallyStopped", Hidden)!.SetValue(supervisor, false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                if (memory) await PublishMemoryPressureAsync(supervisor, deadline.Token);
                else typeof(ActHostSupervisor).GetMethod("OnIpcFaulted", Hidden)!
                    .Invoke(supervisor, [peer.Client, new IOException("fixture recovery fault")]);
                // A real pipe and stubborn child keep recovery inside mandatory
                // cleanup beyond the old two-second disposal budget.
                await peer.ReadUntilAsync(HostMessageTypes.Shutdown);
                await supervisor.DisposeAsync().AsTask().WaitAsync(deadline.Token);
                Assert(!owner.IsRunning && supervisor.Snapshot.State == HostSupervisorState.Stopped,
                    "Unload left an automatically recovering Host alive.");
                Assert((int)typeof(ActHostSupervisor).GetField(memory ? "memoryRecoveryScheduled" : "restartScheduled", Hidden)!
                    .GetValue(supervisor)! == 0, "Unload returned before its automatic recovery completed.");
                Assert(((RecoveryDisposalLogProxy)(object)log).Errors.IsEmpty,
                    "Automatic recovery accessed resources during or after disposal: " +
                    string.Join("; ", ((RecoveryDisposalLogProxy)(object)log).Errors));
            }
            finally { await owner.StopAsync(CancellationToken.None); }
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

    private static async Task StopDuringRecoveryAsync(string root, string hostPackage)
    {
        var scope = Path.Combine(root, "stop-" + Guid.NewGuid().ToString("N"));
        var logger = Logger();
        var client = new HostIpcClient(new EncounterStateStore(), logger);
        await using var supervisor = new ActHostSupervisor(Path.Combine(scope, "host"), Path.Combine(scope, "plugins"),
            Path.Combine(scope, "config"), client, logger, packagedHostDirectory: hostPackage);
        var type = typeof(ActHostSupervisor);
        var owner = (CompatibilityHostProcess)type.GetField("process", Hidden)!.GetValue(supervisor)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        type.GetField("manuallyStopped", Hidden)!.SetValue(supervisor, false);
        type.GetField("sessionId", Hidden)!.SetValue(supervisor, "crashed-session");
        type.GetField("state", Hidden)!.SetValue(supervisor, HostSupervisorState.Faulted);
        type.GetField("restartScheduled", Hidden)!.SetValue(supervisor, 1);
        // Same entry point OnIpcFaulted schedules after a crash. Its start phase holds the
        // lifecycle lock (a Host that never opens its pipe holds it for the 10 s connect).
        var recovery = (Task)type.GetMethod("RestartAfterFaultAsync", Hidden)!
            .Invoke(supervisor, [new IOException("fixture crash"), "crashed-session"])!;
        while (supervisor.Snapshot.State != HostSupervisorState.Starting) await Task.Delay(5, deadline.Token);
        using (var uiBudget = new CancellationTokenSource(TimeSpan.FromMilliseconds(1)))
        {
            // The UI deadline expires before the recovery releases the lock.
            try { await supervisor.StopAsync(uiBudget.Token, userRequested: true); }
            catch (OperationCanceledException) { }
        }
        await recovery.WaitAsync(deadline.Token);
        Assert(!owner.IsRunning && supervisor.Snapshot.State == HostSupervisorState.Stopped,
            $"Recovery overrode a user stop whose lock wait timed out (state={supervisor.Snapshot.State}).");
        Assert((int)type.GetField("restartScheduled", Hidden)!.GetValue(supervisor)! == 0,
            "Recovery scheduled another restart after the user's stop.");
        Assert(!await supervisor.StartAutomaticallyAsync(deadline.Token),
            "A user stop that timed out did not block automatic restarts.");
    }

    private static async Task BlockedHostReaderAsync()
    {
        var session = Guid.NewGuid().ToString("N");
        var pipe = "DACT-lifecycle-blocked-" + session;
        var start = new ProcessStartInfo(Path.Combine(PackagedHostDirectory(), "DalamudActCompat.Host.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "--pipe", pipe, "--session", session, "--enable-fault-injection" })
            start.ArgumentList.Add(argument);
        using var host = Process.Start(start)!;
        _ = host.StandardOutput.ReadToEndAsync();
        _ = host.StandardError.ReadToEndAsync();
        var client = new HostIpcClient(new EncounterStateStore(), Logger());
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Faulted += (_, _) => fault.TrySetResult();
        try
        {
            await client.ConnectAsync(pipe, session, CancellationToken.None);
            await Task.Delay(1500);
            // A plugin callback stuck on the Host reader thread: heartbeats continue, and the
            // next frame cannot be read, so written and acknowledged sequences stay equal.
            Assert(client.TryEnqueue(HostMessageTypes.FaultInject, HostMessagePriority.Control,
                new HostFaultInjection("block-reader", 30_000)), "Fault injection was not queued.");
            await Task.Delay(300);
            Assert(client.TryEnqueue(HostMessageTypes.TtsRequest, HostMessagePriority.Control,
                new HostTtsRequest("fixture", "fixture")), "Probe frame was not queued.");
            // The stall limit is 15 s; the injected block lasts 30 s.
            try { await fault.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException) { throw new InvalidOperationException("A blocked Host reader was not detected while heartbeats stayed healthy."); }
            Assert(client.Status == HostConnectionStatus.Faulted, "Blocked reader did not fault the session.");
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await client.DisposeAsync();
        }
    }

    private static async Task CircuitOpensWhenCleanupFailsAsync(string root)
    {
        var logger = Logger();
        var client = new HostIpcClient(new EncounterStateStore(), logger);
        var supervisor = new ActHostSupervisor(Path.Combine(root, "circuit-host"), Path.Combine(root, "circuit-plugins"),
            Path.Combine(root, "circuit-config"), client, logger);
        var type = typeof(ActHostSupervisor);
        var ipcLock = typeof(HostIpcClient).GetField("lifecycleLock", Hidden)!;
        var original = ipcLock.GetValue(client)!;
        // A disposed IPC lock makes every cleanup throw, like a child that outlives Kill.
        var broken = new SemaphoreSlim(1, 1);
        broken.Dispose();
        ipcLock.SetValue(client, broken);
        try
        {
            type.GetField("manuallyStopped", Hidden)!.SetValue(supervisor, false);
            type.GetField("sessionId", Hidden)!.SetValue(supervisor, "crashed-session");
            type.GetField("state", Hidden)!.SetValue(supervisor, HostSupervisorState.Faulted);
            type.GetField("restartScheduled", Hidden)!.SetValue(supervisor, 1);
            await (Task)type.GetMethod("RestartAfterFaultAsync", Hidden)!
                .Invoke(supervisor, [new IOException("fixture crash"), "crashed-session"])!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (supervisor.Snapshot.State != HostSupervisorState.CircuitOpen && !deadline.IsCancellationRequested)
                await Task.Delay(10);
            await Task.Delay(300);
            Assert(supervisor.Snapshot.State == HostSupervisorState.CircuitOpen &&
                   (bool)type.GetField("manuallyStopped", Hidden)!.GetValue(supervisor)! &&
                   (bool)type.GetField("automaticStartBlocked", Hidden)!.GetValue(supervisor)!,
                $"Failing cleanup bypassed the restart circuit (state={supervisor.Snapshot.State}).");
            Assert((int)type.GetField("restartScheduled", Hidden)!.GetValue(supervisor)! == 0,
                "Recovery kept rescheduling after the circuit opened.");
        }
        finally
        {
            ipcLock.SetValue(client, original);
            await supervisor.DisposeAsync();
        }
    }

    // Drives the real Plugin entry points: a multi-stage permission refresh holds the topology
    // lock while the shared restart waits for a Host that never opens its pipe, then the user
    // signs out. The sign-out stop must not queue behind the refresh's per-role deadlines.
    private static async Task SignOutDuringPermissionRefreshAsync(string root)
    {
        await using var fixture = new PluginFixture(root, "sign-out-refresh", FailingHostPackage(root));
        // RestartAsync only acts on a Host that is not manually stopped, as after startup.
        typeof(ActHostSupervisor).GetField("manuallyStopped", Hidden)!.SetValue(fixture.Shared, false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        fixture.Invoke("ApplyActPermissionChanges", false);
        while (fixture.Shared.Snapshot.State != HostSupervisorState.Starting) await Task.Delay(5, deadline.Token);
        fixture.SignOut();
        var signOut = Stopwatch.StartNew();
        await ((Task)fixture.Invoke("StopDactHostsAsync", "Fixture sign-out", false)!).WaitAsync(deadline.Token);
        Assert(signOut.Elapsed < TimeSpan.FromSeconds(5),
            $"Sign-out waited {signOut.Elapsed.TotalSeconds:0.0} s behind the permission refresh.");
        await Task.WhenAll(fixture.BackgroundOperations).WaitAsync(deadline.Token);
        fixture.AssertHostsStopped("Sign-out");
        Assert(string.IsNullOrEmpty(fixture.Generic.Snapshot.SessionId), "The refresh started the generic Host after sign-out.");
    }

    // FactoryResetCoreAsync takes the file-operation gate, then the topology lock. A deadline
    // that ends while it waits for the second lock must still release the first one.
    private static async Task FactoryResetLockOwnershipAsync(string root)
    {
        await using var fixture = new PluginFixture(root, "factory-reset");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.TopologyLock.WaitAsync(deadline.Token);
        try
        {
            using var cancel = new CancellationTokenSource();
            var reset = (Task<string>)fixture.Invoke("FactoryResetCoreAsync", cancel.Token)!;
            while (fixture.FileGate.CurrentCount != 0) await Task.Delay(5, deadline.Token);
            cancel.Cancel();
            try
            {
                await reset.WaitAsync(deadline.Token);
                throw new InvalidOperationException("Factory reset ignored cancellation while waiting for the topology lock.");
            }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            Assert(fixture.FileGate.CurrentCount == 1, "A cancelled factory reset leaked the file-operation gate.");
        }
        finally
        {
            fixture.TopologyLock.Release();
        }

        // A completed reset releases both locks. Synthetic files only, in the fixture directory.
        File.WriteAllText(Path.Combine(fixture.Paths.ConfigDirectory, "fixture.cfg"), "fixture");
        var parser = new TestParserEngine(ParserState.Stopped);
        fixture.Set("parserEngine", parser);
        fixture.Set("factoryResetService",
            new FactoryResetService(parser, fixture.Paths, fixture.Configuration, fixture.Logger, () => { }));
        var backup = await ((Task<string>)fixture.Invoke("FactoryResetCoreAsync", CancellationToken.None)!)
            .WaitAsync(deadline.Token);
        Assert(fixture.FileGate.CurrentCount == 1 && fixture.TopologyLock.CurrentCount == 1 && Directory.Exists(backup),
            "A completed factory reset did not release both locks.");
    }

    // Sign-out stops the parser first and the Hosts after the topology lock. A maintenance
    // operation holding that lock can resume the parser in between; sign-out must still end
    // with everything stopped, and an old session's resume must not reach a new session.
    private static async Task SignOutDuringMaintenanceResumeAsync(string root)
    {
        foreach (var scenario in new[] { "host-resume", "before-parser-resume", "quick-re-sign-in", "resumer-ignores-session" })
        {
            await using var fixture = new PluginFixture(root, "resume-" + scenario);
            var parser = new TestParserEngine(ParserState.Running);
            fixture.Set("parserEngine", parser);
            var oldSession = fixture.Session.Token;
            var bound = scenario != "resumer-ignores-session";
            var hostResumeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHostResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? signOut = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await fixture.TopologyLock.WaitAsync(deadline.Token);
            // The coordinator and resume gating InstallBundledPluginsAsync uses, holding the
            // topology lock throughout, with the install and shared Host start made controllable.
            var install = Task.Run(async () =>
            {
                try
                {
                    await BundledPluginInstallCoordinator.ExecuteAsync(
                        hostWasRunning: true,
                        parserWasRunning: true,
                        _ => Task.CompletedTask,
                        parser.StopAsync,
                        _ => Task.CompletedTask,
                        async _ =>
                        {
                            hostResumeEntered.TrySetResult();
                            await releaseHostResume.Task;
                            if (scenario == "before-parser-resume") signOut = fixture.SignOutAndStopAsync();
                        },
                        parser.StartAsync,
                        () => !bound || (!oldSession.IsCancellationRequested && fixture.AccessAllowed),
                        CancellationToken.None,
                        TimeSpan.FromSeconds(5),
                        bound ? oldSession : CancellationToken.None);
                }
                finally
                {
                    fixture.TopologyLock.Release();
                }
            });
            await hostResumeEntered.Task.WaitAsync(deadline.Token);
            var startsBeforeSignOut = parser.StartCount;
            if (scenario != "before-parser-resume")
            {
                signOut = fixture.SignOutAndStopAsync();
                // Sign-out stops the parser before it waits for the topology lock.
                while (parser.Status.State != ParserState.Stopped) await Task.Delay(5, deadline.Token);
                if (scenario == "quick-re-sign-in") fixture.SignIn();
            }
            releaseHostResume.TrySetResult();
            await install.WaitAsync(deadline.Token);
            await signOut!.WaitAsync(deadline.Token);
            Assert(parser.Status.State == ParserState.Stopped,
                $"{scenario}: sign-out completed with the parser {parser.Status.State}.");
            fixture.AssertHostsStopped($"{scenario}: sign-out");
            if (bound)
            {
                Assert(parser.StartCount == startsBeforeSignOut,
                    $"{scenario}: a resume from the signed-out session restarted the parser.");
            }
        }
    }

    // A memory-pressure recovery queued for an old session must not restart the session that a
    // restart queued ahead of it already put in place, nor spend that session's recovery budget.
    private static async Task StaleMemoryRecoveryAsync(string root)
    {
        var scope = Path.Combine(root, "memory-" + Guid.NewGuid().ToString("N"));
        var logger = Logger();
        await using var supervisor = new ActHostSupervisor(Path.Combine(scope, "host"), Path.Combine(scope, "plugins"),
            Path.Combine(scope, "config"), new HostIpcClient(new EncounterStateStore(), logger), logger,
            enableMemoryProtection: true, packagedHostDirectory: PackagedHostDirectory());
        var type = typeof(ActHostSupervisor);
        var owner = (CompatibilityHostProcess)type.GetField("process", Hidden)!.GetValue(supervisor)!;
        var lifecycle = (SemaphoreSlim)type.GetField("lifecycleLock", Hidden)!.GetValue(supervisor)!;
        var history = (Queue<DateTimeOffset>)type.GetField("memoryRecoveryHistory", Hidden)!.GetValue(supervisor)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Assert(await supervisor.StartAutomaticallyAsync(deadline.Token), "Memory fixture Host did not start.");
        await supervisor.WaitForPluginStartupAsync(deadline.Token);
        await lifecycle.WaitAsync(deadline.Token);
        Task<bool> restart;
        try
        {
            restart = supervisor.RestartAsync(deadline.Token);
            await PublishMemoryPressureAsync(supervisor, deadline.Token);
        }
        finally
        {
            lifecycle.Release();
        }

        Assert(await restart.WaitAsync(deadline.Token), "The queued restart did not run.");
        var replacement = supervisor.Snapshot.SessionId;
        var pid = ChildProcessId(owner);
        await WaitForMemoryRecoveryIdleAsync(supervisor, deadline.Token);
        Assert(supervisor.Snapshot.SessionId == replacement && ChildProcessId(owner) == pid &&
               supervisor.Snapshot.State == HostSupervisorState.Running,
            "A stale memory recovery restarted the replacement shared Host.");
        Assert(history.Count == 0, "A stale memory recovery spent the replacement session's budget.");

        // Pressure reported by the current session still recovers it and is counted once.
        await supervisor.WaitForPluginStartupAsync(deadline.Token);
        await PublishMemoryPressureAsync(supervisor, deadline.Token);
        await WaitForMemoryRecoveryIdleAsync(supervisor, deadline.Token);
        Assert(supervisor.Snapshot.SessionId != replacement && history.Count == 1,
            "Current-session memory pressure no longer recovers the shared Host.");
        await supervisor.StopAsync(deadline.Token, userRequested: true);
    }

    // Two synthetic heartbeats 16 s apart in sample time cross the sustained 3 GiB limit; no
    // memory is allocated. A real heartbeat can reset the threshold in between, so retry.
    private static async Task PublishMemoryPressureAsync(ActHostSupervisor supervisor, CancellationToken cancellationToken)
    {
        var observe = typeof(ActHostSupervisor).GetMethod("OnResourceSampleReceived", Hidden)!;
        var scheduled = typeof(ActHostSupervisor).GetField("memoryRecoveryScheduled", Hidden)!;
        while ((int)scheduled.GetValue(supervisor)! == 0)
        {
            var start = DateTimeOffset.UtcNow;
            foreach (var at in new[] { start, start.AddSeconds(16) })
                observe.Invoke(supervisor, [null, new HostResourceSample(at, 3L << 30, 3L << 30, 8L << 30, 40)]);
            if ((int)scheduled.GetValue(supervisor)! == 0) await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task WaitForMemoryRecoveryIdleAsync(ActHostSupervisor supervisor, CancellationToken cancellationToken)
    {
        var scheduled = typeof(ActHostSupervisor).GetField("memoryRecoveryScheduled", Hidden)!;
        while ((int)scheduled.GetValue(supervisor)! != 0) await Task.Delay(20, cancellationToken);
    }

    private static int ChildProcessId(CompatibilityHostProcess owner)
        => ((Process)typeof(CompatibilityHostProcess).GetField("process", Hidden)!.GetValue(owner)!).Id;

    // RestartAsync marks its own stop phase with manuallyStopped. A user stop that arrives in
    // that phase and times out waiting for the lock must still win over the restart.
    private static async Task StopDuringRestartStopPhaseAsync(string root)
    {
        var scope = Path.Combine(root, "restart-stop-" + Guid.NewGuid().ToString("N"));
        var logger = Logger();
        var client = new HostIpcClient(new EncounterStateStore(), logger);
        await using var supervisor = new ActHostSupervisor(Path.Combine(scope, "host"), Path.Combine(scope, "plugins"),
            Path.Combine(scope, "config"), client, logger, packagedHostDirectory: PackagedHostDirectory());
        var owner = (CompatibilityHostProcess)typeof(ActHostSupervisor).GetField("process", Hidden)!.GetValue(supervisor)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        Assert(await supervisor.StartAutomaticallyAsync(deadline.Token), "Restart fixture Host did not start.");
        await supervisor.WaitForPluginStartupAsync(deadline.Token);
        // Holding the IPC lock keeps RestartAsync inside its stop phase.
        var ipcLock = (SemaphoreSlim)typeof(HostIpcClient).GetField("lifecycleLock", Hidden)!.GetValue(client)!;
        await ipcLock.WaitAsync(deadline.Token);
        Task<bool> restart;
        try
        {
            restart = supervisor.RestartAsync(deadline.Token);
            while (supervisor.Snapshot.State != HostSupervisorState.Stopping) await Task.Delay(5, deadline.Token);
            using var uiBudget = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
            try { await supervisor.StopAsync(uiBudget.Token, userRequested: true); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            ipcLock.Release();
        }

        Assert(!await restart.WaitAsync(deadline.Token), "Restart reported success after a user stop during its stop phase.");
        Assert(!owner.IsRunning && supervisor.Snapshot.State == HostSupervisorState.Stopped,
            $"A restart overrode a user stop requested during its stop phase (state={supervisor.Snapshot.State}).");
        Assert(!await supervisor.StartAutomaticallyAsync(deadline.Token), "The user stop did not block automatic starts.");
    }

    // An uninitialized Plugin with only the fields the Host topology entry points read, the
    // pattern CombatLogLifecycleSmokeTests uses for other private Plugin entry points.
    private sealed class PluginFixture : IAsyncDisposable
    {
        public PluginFixture(string root, string name, string? sharedHostPackage = null)
        {
            var scope = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
            Paths = new PluginPaths(Path.Combine(scope, "config"), Path.Combine(scope, "plugins"));
            Paths.EnsureCreated();
            var installer = new ActPluginPackageInstaller(Paths);
            var runtime = (SelfHostedActRuntime)RuntimeHelpers.GetUninitializedObject(typeof(SelfHostedActRuntime));
            typeof(SelfHostedActRuntime).GetField("networkCaptureSync", Hidden)!.SetValue(runtime, new object());
            Shared = CreateSupervisor(scope, "shared-host", sharedHostPackage);
            Matcha = CreateSupervisor(scope, "matcha-host", null);
            Generic = CreateSupervisor(scope, "generic-host", null);
            Set("configuration", Configuration);
            Set("paths", Paths);
            Set("logger", Logger);
            Set("packageInstaller", installer);
            Set("bundledPluginManager", new BundledActPluginManager("fixture", installer, Configuration));
            Set("actRuntime", runtime);
            Set("hostSupervisor", Shared);
            Set("matchaHostSupervisor", Matcha);
            Set("genericHostSupervisor", Generic);
            Set("hostTopologyLock", TopologyLock);
            Set("cactbotFileOperationGate", FileGate);
            Set("permissionSnapshotLock", new object());
            Set("cloudRuntimeCancellationLock", new object());
            Set("cloudRuntimeCancellation", Session);
            Set("independentHostStartupCancellation", unload);
            Set("backgroundOperationLock", new object());
            Set("backgroundOperations", BackgroundOperations);
            Set("cloudRuntimeAuthorized", 1);
            Account = DiscordBindingSmokeTests.BoundFixture(Paths);
            Set("cloudClient", Account);
        }

        private readonly CancellationTokenSource unload = new();
        private DalamudActCompat.Infrastructure.Cloud.CloudClientService Account { get; }
        public Plugin Plugin { get; } = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        public PluginPaths Paths { get; }
        public PluginConfiguration Configuration { get; } = new();
        public PluginLogger Logger { get; } = HostLifecycleSmokeTests.Logger();
        public SemaphoreSlim TopologyLock { get; } = new(1, 1);
        public SemaphoreSlim FileGate { get; } = new(1, 1);
        public CancellationTokenSource Session { get; private set; } = new();
        public List<Task> BackgroundOperations { get; } = [];
        public ActHostSupervisor Shared { get; }
        public ActHostSupervisor Matcha { get; }
        public ActHostSupervisor Generic { get; }
        public bool AccessAllowed => (bool)Invoke("IsDactAccessAllowed")!;

        public void Set(string field, object value) => typeof(Plugin).GetField(field, Hidden)!.SetValue(Plugin, value);

        public object? Invoke(string method, params object?[] arguments)
            => typeof(Plugin).GetMethod(method, Hidden)!.Invoke(Plugin, arguments);

        // What SynchronizeCloudAuthenticationState does before ReconcileCloudRuntimeAsync stops.
        public void SignOut()
        {
            Set("cloudRuntimeAuthorized", 0);
            Invoke("CancelCloudRuntimeSession");
        }

        public Task SignOutAndStopAsync()
        {
            SignOut();
            return (Task)Invoke("StopDactComponentsAsync", "Fixture sign-out")!;
        }

        public void SignIn()
        {
            Session = new CancellationTokenSource();
            Set("cloudRuntimeCancellation", Session);
            Set("cloudRuntimeAuthorized", 1);
        }

        public void AssertHostsStopped(string context)
        {
            foreach (var (name, host) in new[] { ("shared", Shared), ("Matcha", Matcha), ("generic", Generic) })
            {
                Assert(host.Snapshot.State == HostSupervisorState.Stopped && !host.Snapshot.ProcessRunning,
                    $"{context} left the {name} Host {host.Snapshot.State}.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Shared.DisposeAsync();
            await Matcha.DisposeAsync();
            await Generic.DisposeAsync();
            Account.Dispose();
            Session.Dispose();
            unload.Dispose();
        }

        private ActHostSupervisor CreateSupervisor(string scope, string name, string? package)
            => new(Path.Combine(scope, name), Paths.ActPluginDirectory, Paths.ConfigDirectory,
                new HostIpcClient(new EncounterStateStore(), Logger), Logger, packagedHostDirectory: package);
    }

    private static string PackagedHostDirectory()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DalamudActCompat.slnx"))) repository = repository.Parent;
        Assert(repository is not null, "Repository was not found.");
        return Path.Combine(repository!.FullName, "src", "DalamudActCompat", "bin", "Release", "host");
    }

    // A Host executable that exits without opening its pipe keeps ConnectAsync, and the
    // recovery's lifecycle lock, busy for the full connect timeout.
    private static string FailingHostPackage(string root)
    {
        var package = Path.Combine(root, "failing-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(package, "DalamudActCompat.Host.exe"));
        return package;
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

public class RecoveryDisposalLogProxy : NoOpPluginLogProxy
{
    public readonly System.Collections.Concurrent.ConcurrentQueue<Exception> Errors = new();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (args?.OfType<Exception>().FirstOrDefault() is { } error) Errors.Enqueue(error);
        return base.Invoke(targetMethod, args);
    }
}
