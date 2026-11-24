using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Fflogs.Upload;

// The game-side face of FFLogs log upload. Parsing and uploading happen in the helper process
// (FflogsUploadProtocol); this class only sends requests, keeps the last status the helper
// reported, and stores the remembered password. Every operation returns immediately with a task,
// because the callers are draw-loop code.
public sealed class FflogsUploadService : IDisposable
{
    private const long StatusIntervalMs = 2000;
    private static readonly FflogsUploadStatus SignedOut = new(false, false, string.Empty, [], false, 0, null, null);

    private readonly FflogsUploadCredentialStore credentialStore;
    private readonly FflogsUploadHelper helper;
    private readonly Func<CancellationToken, Action<int>, Task<FflogsUploadEngine>> resolveEngine;

    // Cancelled on unload and deliberately never disposed: an operation that is just starting
    // may still link to its token.
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private FflogsUploadStatus status = SignedOut;
    private FflogsUploadEngine? engine;
    private Task? statusPoll;
    private long lastStatusPoll;
    private int engineDownloadPercent = -1;
    private const long LatestLogIntervalMs = 3000;
    private string? latestLogDirectory;
    private FflogsLatestLog? latestLog;
    private Task? latestLogScan;
    private long latestLogScannedAt;

    // resolveEngine fetches the verified V8 runtime and reports download progress in percent.
    public FflogsUploadService(
        PluginLogger? logger,
        Func<string?> hostExecutable,
        string credentialPath,
        string parserCacheDirectory,
        Func<CancellationToken, Action<int>, Task<FflogsUploadEngine>> resolveEngine,
        string? loopbackSite = null)
    {
        credentialStore = new FflogsUploadCredentialStore(credentialPath);
        helper = new FflogsUploadHelper(hostExecutable, parserCacheDirectory, logger, loopbackSite);
        this.resolveEngine = resolveEngine;
    }

    public FflogsUploadStatus Status
    {
        get
        {
            lock (sync) return PinnedStatus ?? status;
        }
    }

    // For the UI baseline and help screenshots: they draw the signed-in states without a helper
    // process, which the status would otherwise fall back from on every Tick.
    internal FflogsUploadStatus? PinnedStatus { get; set; }

    public bool IsLoggedIn => Status.LoggedIn;

    public bool IsLiveLogging => Status.LiveActive;

    // True once this session has verified the parser engine on disk. Before the first parse it is
    // unknown rather than missing: checking would mean hashing the files on every look.
    public bool EngineVerified
    {
        get
        {
            lock (sync) return engine is not null;
        }
    }

    // Null unless the parser engine is being downloaded right now.
    public int? EngineDownloadPercent
        => Volatile.Read(ref engineDownloadPercent) is >= 0 and var percent ? percent : null;

    internal int HelperProcessId => helper.ProcessId;

    public bool HasSavedPassword => credentialStore.HasSavedPassword;

    public string? LoadSavedPassword() => credentialStore.Load();

    public void SavePassword(string password) => credentialStore.Save(password);

    public void ClearSavedPassword() => credentialStore.Clear();

    // Keeps the status current while an upload control is on screen: a live upload counts
    // fights on its own, and a helper that exited took the FFLogs session with it.
    public void Tick()
    {
        if (helper.ProcessId == 0)
        {
            lock (sync) status = SignedOut;
            return;
        }

        var now = Environment.TickCount64;
        lock (sync)
        {
            if (statusPoll is { IsCompleted: false } || now - lastStatusPoll < StatusIntervalMs) return;
            lastStatusPoll = now;
            statusPoll = RunAsync(async token =>
            {
                try
                {
                    await SendAsync(new FflogsUploadRequest { Operation = FflogsUploadProtocol.Status }, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is FflogsUploadException or OperationCanceledException)
                {
                    // The next tick asks again; a dead helper is noticed above.
                }

                return true;
            }, CancellationToken.None);
        }
    }

    public Task LoginAsync(string email, string password, bool chineseSite, CancellationToken cancellationToken = default)
        => RunAsync(async token =>
        {
            await SendAsync(new FflogsUploadRequest
            {
                Operation = FflogsUploadProtocol.Login,
                ChineseSite = chineseSite,
                Email = email,
                Password = password,
            }, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task LogoutAsync()
        => RunAsync(async token =>
        {
            // Without a running helper there is no session to end, and none should be started.
            if (helper.ProcessId != 0)
                await SendAsync(new FflogsUploadRequest { Operation = FflogsUploadProtocol.Logout }, token).ConfigureAwait(false);
            return true;
        }, CancellationToken.None);

    public Task<IReadOnlyList<FflogsUploadFight>> PrepareAsync(string logDirectory, CancellationToken cancellationToken = default)
        => RunAsync(async token =>
        {
            var answer = await SendAsync(new FflogsUploadRequest
            {
                Operation = FflogsUploadProtocol.Prepare,
                LogDirectory = logDirectory,
                Engine = await EnsureEngineAsync(token).ConfigureAwait(false),
            }, token).ConfigureAwait(false);
            return answer.Fights ?? [];
        }, cancellationToken);

    public Task<FflogsUploadResult> UploadPreparedAsync(IReadOnlyList<int> fightIndices, FflogsUploadOptions options, CancellationToken cancellationToken = default)
        => RunAsync(async token => Result(await SendAsync(new FflogsUploadRequest
        {
            Operation = FflogsUploadProtocol.UploadPrepared,
            FightIndices = fightIndices,
            Options = options,
        }, token).ConfigureAwait(false), options), cancellationToken);

    public Task<FflogsUploadResult> UploadLatestAsync(string logDirectory, FflogsUploadOptions options, CancellationToken cancellationToken = default)
        => RunAsync(async token => Result(await SendAsync(new FflogsUploadRequest
        {
            Operation = FflogsUploadProtocol.UploadLatest,
            LogDirectory = logDirectory,
            Options = options,
            Engine = await EnsureEngineAsync(token).ConfigureAwait(false),
        }, token).ConfigureAwait(false), options), cancellationToken);

    public Task<FflogsUploadResult> UploadEncounterAsync(
        string logDirectory,
        DateTimeOffset encounterStart,
        DateTimeOffset encounterEnd,
        FflogsUploadOptions options,
        CancellationToken cancellationToken = default)
        => RunAsync(async token => Result(await SendAsync(new FflogsUploadRequest
        {
            Operation = FflogsUploadProtocol.UploadEncounter,
            LogDirectory = logDirectory,
            Options = options,
            EncounterStartUnixMs = encounterStart.ToUnixTimeMilliseconds(),
            EncounterEndUnixMs = encounterEnd.ToUnixTimeMilliseconds(),
            Engine = await EnsureEngineAsync(token).ConfigureAwait(false),
        }, token).ConfigureAwait(false), options), cancellationToken);

    // The newest Network log of a directory, for display only. The directory is read off the draw
    // thread and the answer kept for a few seconds; until the first read finishes this is null.
    public FflogsLatestLog? LatestLog(string directory)
    {
        var now = Environment.TickCount64;
        lock (sync)
        {
            var sameDirectory = string.Equals(latestLogDirectory, directory, StringComparison.OrdinalIgnoreCase);
            if (sameDirectory && (latestLogScan is { IsCompleted: false } || now - latestLogScannedAt < LatestLogIntervalMs))
                return latestLog;
            if (!sameDirectory) latestLog = null;
            latestLogDirectory = directory;
            latestLogScannedAt = now;
            latestLogScan = Task.Run(() =>
            {
                FflogsLatestLog? found = null;
                try
                {
                    var newest = new DirectoryInfo(directory).EnumerateFiles("*.log")
                        .OrderByDescending(static file => file.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (newest is not null) found = new FflogsLatestLog(newest.Name, newest.LastWriteTime);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A missing or unreadable directory simply has no latest log to show.
                }

                lock (sync)
                {
                    if (string.Equals(latestLogDirectory, directory, StringComparison.OrdinalIgnoreCase)) latestLog = found;
                }
            });
            return latestLog;
        }
    }

    public static string ReportUrl(bool chineseSite, string reportCode)
        => $"https://{(chineseSite ? "cn" : "www")}.fflogs.com/reports/{reportCode}";

    public Task StartLiveAsync(string logDirectory, FflogsUploadOptions options, CancellationToken cancellationToken = default)
        => RunAsync(async token =>
        {
            await SendAsync(new FflogsUploadRequest
            {
                Operation = FflogsUploadProtocol.LiveStart,
                LogDirectory = logDirectory,
                Options = options,
                Engine = await EnsureEngineAsync(token).ConfigureAwait(false),
            }, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task StopLiveAsync()
        => RunAsync(async token =>
        {
            if (helper.ProcessId != 0)
                await SendAsync(new FflogsUploadRequest { Operation = FflogsUploadProtocol.LiveStop }, token).ConfigureAwait(false);
            return true;
        }, CancellationToken.None);

    private static FflogsUploadResult Result(FflogsUploadResponse answer, FflogsUploadOptions options)
        => new(
            answer.ReportCode ?? throw new FflogsUploadException(FflogsUploadProtocol.Failed, "The upload finished without a report code."),
            answer.FightCount,
            answer.Status.ChineseSite,
            options.Visibility,
            DateTime.Now);

    private Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            return await work(linked.Token).ConfigureAwait(false);
        }, CancellationToken.None);

    private async Task<FflogsUploadEngine> EnsureEngineAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (engine is not null) return engine;
        }

        try
        {
            var resolved = await resolveEngine(cancellationToken, percent => Volatile.Write(ref engineDownloadPercent, percent))
                .ConfigureAwait(false);
            lock (sync) engine = resolved;
            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FflogsUploadException(FflogsUploadProtocol.EngineUnavailable, ex.GetBaseException().Message);
        }
        finally
        {
            Volatile.Write(ref engineDownloadPercent, -1);
        }
    }

    private async Task<FflogsUploadResponse> SendAsync(FflogsUploadRequest request, CancellationToken cancellationToken)
    {
        FflogsUploadResponse? answer;
        var pipe = await helper.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (pipe.ConfigureAwait(false))
        {
            // Closing the pipe is the cancel signal the helper listens for.
            using (cancellationToken.Register(static state => ((Stream)state!).Dispose(), pipe))
            {
                try
                {
                    await FflogsUploadProtocol.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
                    answer = await FflogsUploadProtocol.ReadAsync<FflogsUploadResponse>(pipe, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested &&
                                           ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
                {
                    throw new FflogsUploadException(FflogsUploadProtocol.Failed,
                        $"The FFLogs upload helper stopped during the request: {ex.Message}");
                }
            }
        }

        if (answer is null)
        {
            throw new FflogsUploadException(FflogsUploadProtocol.Failed,
                "The FFLogs upload helper closed the connection without an answer.");
        }

        lock (sync) status = answer.Status;
        if (answer.ErrorCode is not null)
            throw new FflogsUploadException(answer.ErrorCode, answer.ErrorMessage ?? answer.ErrorCode);
        return answer;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        // Killing the helper also ends a live upload; FF Logs closes a report nobody feeds.
        helper.Dispose();
    }
}

// A finished upload as the interface shows it: where the report is, how much went into it, and
// the visibility it was created with (FflogsSettings.UploadVisibility numbering).
public sealed record FflogsUploadResult(string ReportCode, int FightCount, bool ChineseSite, int Visibility, DateTime FinishedAt)
{
    public string Url => FflogsUploadService.ReportUrl(ChineseSite, ReportCode);
}

public sealed record FflogsLatestLog(string Name, DateTime WrittenAt);
