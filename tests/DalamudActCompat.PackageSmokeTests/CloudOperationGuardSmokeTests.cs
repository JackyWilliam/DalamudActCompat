using DalamudActCompat.Infrastructure.Cloud;

internal static class CloudOperationGuardSmokeTests
{
    public static async Task RunAsync(string projectRoot)
    {
        await ValidateWholeOperationAsync();
        await ValidateFailureAndCancellationAsync();
        await ValidateRejectedAndSkippedWorkAsync();
        ValidateWiring(projectRoot);
        Console.WriteLine("Cloud operation busy guard smoke tests passed (offline scheduling + full lifecycle).");
    }

    private static async Task ValidateWholeOperationAsync()
    {
        var guard = new CloudOperationGuard();
        var scheduler = new DeferredScheduler();
        var entered = Enumerable.Range(0, 4).Select(_ => Signal()).ToArray();
        var release = Enumerable.Range(0, 4).Select(_ => Signal()).ToArray();
        var executions = 0;
        var idle = CloudClientSnapshot.SignedOut("上传完成") with { IsSignedIn = true };
        Check(guard.TryStart(scheduler.Schedule, async () =>
        {
            Interlocked.Increment(ref executions);
            // Preparation, stop, upload, and recovery are separate admission
            // boundaries. The UI must not become clickable between any two.
            for (var phase = 0; phase < entered.Length; phase++)
            {
                entered[phase].SetResult();
                await release[phase].Task;
            }
        }), "The first cloud operation was rejected.");
        Check(guard.IsBusy && executions == 0 && guard.GetUiSnapshot(idle).IsBusy,
            "Busy protection started after background execution instead of on click.");
        Check(guard.GetUiSnapshot(idle).StatusMessage != idle.StatusMessage,
            "The preparation/recovery UI prematurely reports upload complete.");
        scheduler.Start.SetResult();

        for (var phase = 0; phase < entered.Length; phase++)
        {
            await entered[phase].Task.WaitAsync(TimeSpan.FromSeconds(5));
            var attempts = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(
                () => guard.TryStart(scheduler.Schedule, () => throw new Exception("Duplicate executed")))));
            Check(attempts.All(accepted => !accepted) && scheduler.Count == 1,
                $"Concurrent clicks queued another operation during phase {phase}.");
            Check(guard.GetUiSnapshot(idle).IsBusy,
                $"The service idle state unlocked the UI during phase {phase}.");
            var error = idle with { StatusIsError = true, StatusMessage = "synthetic upload failure" };
            Check(guard.GetUiSnapshot(error).StatusMessage == error.StatusMessage,
                "Busy projection hid the actual operation failure.");
            var upload = idle with { IsBusy = true, StatusMessage = "synthetic upload progress" };
            Check(guard.GetUiSnapshot(upload).StatusMessage == upload.StatusMessage,
                "Busy projection overwrote service progress.");
            release[phase].SetResult();
        }

        await scheduler.Work!.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!guard.IsBusy && guard.GetUiSnapshot(idle) == idle && executions == 1,
            "Completion did not restore the unchanged service snapshot.");
        var next = new DeferredScheduler();
        Check(guard.TryStart(next.Schedule, () => Task.CompletedTask), "A new click remained blocked after completion.");
        next.Start.SetResult();
        await next.Work!;
        Check(!guard.IsBusy, "The next operation retained busy state.");
    }

    private static async Task ValidateFailureAndCancellationAsync()
    {
        foreach (var cancel in new[] { false, true })
        {
            for (var failingPhase = 0; failingPhase < 4; failingPhase++)
            {
                var guard = new CloudOperationGuard();
                var scheduler = new DeferredScheduler();
                Check(guard.TryStart(scheduler.Schedule, async () =>
                {
                    for (var phase = 0; phase <= failingPhase; phase++)
                    {
                        await Task.Yield();
                        Check(guard.IsBusy, "Busy state ended before failure cleanup.");
                    }
                    if (cancel) throw new OperationCanceledException(new CancellationToken(true));
                    throw new InvalidOperationException("synthetic stage failure");
                }), "Failure scenario was not admitted.");
                scheduler.Start.SetResult();
                try
                {
                    await scheduler.Work!.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new Exception("The synthetic failure disappeared.");
                }
                catch (OperationCanceledException) when (cancel) { }
                catch (InvalidOperationException) when (!cancel) { }
                Check(!guard.IsBusy, $"Failure/cancellation left phase {failingPhase} busy.");
            }
        }
    }

    private static async Task ValidateRejectedAndSkippedWorkAsync()
    {
        var guard = new CloudOperationGuard();
        Check(!guard.TryStart((_, _) => null, () => Task.CompletedTask) && !guard.IsBusy,
            "Scheduler shutdown rejection leaked admission.");
        try
        {
            guard.TryStart((_, _) => throw new InvalidOperationException("synthetic scheduler failure"),
                () => Task.CompletedTask);
            throw new Exception("Scheduling failure disappeared.");
        }
        catch (InvalidOperationException) { }
        Check(!guard.IsBusy, "Scheduling failure leaked admission.");

        var scheduler = new DeferredScheduler { Skip = true };
        Check(guard.TryStart(scheduler.Schedule, () => throw new Exception("Banned work executed")),
            "Skipped-work scenario was not admitted.");
        scheduler.Start.SetResult();
        await scheduler.Work!.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!guard.IsBusy, "A ban skipping queued work leaked admission.");
    }

    private static void ValidateWiring(string projectRoot)
    {
        var source = File.ReadAllText(Path.Combine(projectRoot, "src", "DalamudActCompat", "Plugin", "Plugin.cs"));
        var background = source[source.IndexOf("private Task? StartBackgroundOperation(", StringComparison.Ordinal)..
            source.IndexOf("private void BeginBackgroundOperationShutdown(", StringComparison.Ordinal)];
        Check(background.Contains("finally", StringComparison.Ordinal) &&
              background.IndexOf("try", StringComparison.Ordinal) < background.IndexOf("cloudAccessBlocked", StringComparison.Ordinal) &&
              background.IndexOf("onCompleted?.Invoke();", StringComparison.Ordinal) > background.IndexOf("finally", StringComparison.Ordinal),
            "Tracked completion no longer covers banned/skipped background work.");
        Check(source.Contains("cloudOperationGuard.TryStart(StartBackgroundOperation", StringComparison.Ordinal) &&
              source.Contains("cloudOperationGuard.GetUiSnapshot(cloudClient.Snapshot)", StringComparison.Ordinal) &&
              source.Contains("cloudOperationGuard.IsBusy || cloudClient.Snapshot.IsBusy", StringComparison.Ordinal) &&
              source.Contains("Interlocked.CompareExchange(ref cloudAutoSyncDueUtcTicks, dueTicks, 0)", StringComparison.Ordinal),
            "Cloud UI, admission, or auto-sync retry is no longer wired to the full-operation guard.");
        var ui = File.ReadAllText(Path.Combine(projectRoot, "src", "DalamudActCompat", "UI", "ControlCenterWindow.cs"));
        var card = ui[ui.IndexOf("private void DrawCloudBackupCard(", StringComparison.Ordinal)..];
        Check(card.IndexOf("ImGui.BeginDisabled(snapshot.IsBusy)", StringComparison.Ordinal) <
              card.IndexOf("cloud.Upload();", StringComparison.Ordinal) &&
              card.Contains("###CloudUploadCurrent", StringComparison.Ordinal) &&
              card.Contains("Working…", StringComparison.Ordinal),
            "The upload control lost its disabled/busy label or stable ImGui ID.");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class DeferredScheduler
    {
        public TaskCompletionSource Start { get; } = Signal();
        public Task? Work { get; private set; }
        public int Count { get; private set; }
        public bool Skip { get; init; }

        public Task? Schedule(Func<Task> operation, Action completed)
        {
            Count++;
            Work = Task.Run(async () =>
            {
                await Start.Task;
                try
                {
                    if (Skip) return;
                    await operation();
                }
                finally { completed(); }
            });
            return Work;
        }
    }
}
