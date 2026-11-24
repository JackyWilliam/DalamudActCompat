using System.IO;
using System.IO.Pipes;
using System.Runtime.Loader;
using DalamudActCompat.Protocol;
using Microsoft.ClearScript;

namespace DalamudActCompat.Host.FflogsUpload;

// The --fflogs-upload mode of the Host executable (see FflogsUploadProtocol for why it exists).
internal static class FflogsUploadServer
{
    public static async Task<int> RunAsync(string[] args)
    {
        var pipeName = string.Empty;
        var cacheRoot = string.Empty;
        string? loopbackSite = null;
        var parentProcessId = 0;
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index] == FflogsUploadProtocol.PipeArgument) pipeName = args[index + 1];
            else if (args[index] == FflogsUploadProtocol.CacheArgument) cacheRoot = args[index + 1];
            else if (args[index] == FflogsUploadProtocol.LoopbackSiteArgument) loopbackSite = args[index + 1];
            else if (args[index] == FflogsUploadProtocol.ParentArgument) _ = int.TryParse(args[index + 1], out parentProcessId);
        }

        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(cacheRoot) || parentProcessId <= 0)
        {
            Console.Error.WriteLine("Missing --pipe <name>, --cache-root <directory> or --parent <process id>.");
            return 2;
        }

        if (loopbackSite is not null &&
            !(Uri.TryCreate(loopbackSite, UriKind.Absolute, out var site) && site.IsLoopback && site.Scheme == "http"))
        {
            Console.Error.WriteLine("--loopback-site only accepts an http loopback address.");
            return 2;
        }

        using var stop = new CancellationTokenSource();
        // The game owns this process: when it exits (or crashes) nothing is left to upload for.
        _ = AiRelayServer.WatchParentAsync(parentProcessId, stop);
        using var session = new FflogsUploadSession(Path.GetFullPath(cacheRoot), loopbackSite);
        UploadLog.Info($"FFLogs upload helper ready for process {parentProcessId}.");
        while (!stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }

            _ = Task.Run(() => ServeAsync(pipe, session, stop.Token), CancellationToken.None);
        }

        return 0;
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, FflogsUploadSession session, CancellationToken stopping)
    {
        await using var _ = pipe;
        try
        {
            var request = await FflogsUploadProtocol.ReadAsync<FflogsUploadRequest>(pipe, stopping).ConfigureAwait(false);
            if (request is null) return;

            using var call = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            // The game side closes its end when the player cancels.
            var watch = CancelOnDisconnectAsync(pipe, call);
            FflogsUploadResponse response;
            try
            {
                response = await session.HandleAsync(request, call.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Nobody reads an answer on a pipe the caller already closed.
                return;
            }

            await FflogsUploadProtocol.WriteAsync(pipe, response, stopping).ConfigureAwait(false);
            await call.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
        {
            // The caller went away mid-frame or sent garbage; nothing is left to answer.
        }
    }

    private static async Task CancelOnDisconnectAsync(Stream pipe, CancellationTokenSource call)
    {
        try
        {
            // The caller sends nothing after its request, so this read only returns when it
            // closes the pipe.
            var probe = new byte[1];
            await pipe.ReadAsync(probe, call.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        if (!call.IsCancellationRequested)
        {
            await call.CancelAsync().ConfigureAwait(false);
        }
    }
}

// A failure the game side words itself (see the codes in FflogsUploadProtocol).
internal sealed class FflogsUploadFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Everything that outlives a single request: the signed-in client, the fights of the last
// "prepare" and a running live upload.
internal sealed class FflogsUploadSession : IDisposable
{
    private const string NativeLibraryName = "ClearScriptV8.win-x64.dll";

    // DACT closes its own record when combat ends, FF Logs at the last event of the pull, so the
    // two end times differ by seconds. Consecutive pulls are further apart than this.
    private const long EncounterMatchToleranceMs = 180_000;
    private const int MaximumEncounterLogCandidates = 3;

    private static readonly object EngineSync = new();
    private static bool engineConfigured;

    private readonly string cacheRoot;
    private readonly string? loopbackSite;
    private readonly object sync = new();

    // Sign-in, parsing and uploading share one FFLogs session, and a parse holds a V8 engine with
    // the whole log's state, so they run one at a time. Live upload has its own parser.
    private readonly SemaphoreSlim operation = new(1, 1);
    private FflogsUploadClient? client;
    private bool chineseSite;
    private IReadOnlyList<ParserEngine.FightUpload> prepared = [];
    private string preparedFileName = string.Empty;

    public FflogsUploadSession(string cacheRoot, string? loopbackSite)
    {
        this.cacheRoot = cacheRoot;
        this.loopbackSite = loopbackSite;
    }

    public async Task<FflogsUploadResponse> HandleAsync(FflogsUploadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Operation)
            {
                case FflogsUploadProtocol.Status:
                    return Reply();
                case FflogsUploadProtocol.LiveStop:
                    Current()?.StopLiveLog();
                    return Reply();
                case FflogsUploadProtocol.LiveStart:
                    StartLive(request);
                    return Reply();
                case FflogsUploadProtocol.Login:
                case FflogsUploadProtocol.Logout:
                case FflogsUploadProtocol.Prepare:
                case FflogsUploadProtocol.UploadPrepared:
                case FflogsUploadProtocol.UploadLatest:
                case FflogsUploadProtocol.UploadEncounter:
                    await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        return await RunExclusiveAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        operation.Release();
                    }
                default:
                    throw new FflogsUploadFailure(FflogsUploadProtocol.Failed, $"Unknown operation: {request.Operation}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FflogsUploadFailure failure)
        {
            return Reply() with { ErrorCode = failure.Code, ErrorMessage = failure.Message };
        }
        catch (Exception ex)
        {
            UploadLog.Error($"[{request.Operation}] failed", ex);
            return Reply() with { ErrorCode = FflogsUploadProtocol.Failed, ErrorMessage = ex.GetBaseException().Message };
        }
    }

    private async Task<FflogsUploadResponse> RunExclusiveAsync(FflogsUploadRequest request, CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case FflogsUploadProtocol.Login:
                await LoginAsync(request, cancellationToken).ConfigureAwait(false);
                return Reply();
            case FflogsUploadProtocol.Logout:
                Replace(null, false);
                return Reply();
            case FflogsUploadProtocol.Prepare:
            {
                var current = RequireClient();
                ConfigureEngine(request.Engine);
                var path = LatestLog(request.LogDirectory);
                var fights = await current.PrepareUploadsAsync(path, RegionCode, cancellationToken).ConfigureAwait(false);
                lock (sync)
                {
                    prepared = fights;
                    preparedFileName = Path.GetFileName(path);
                }

                return Reply() with { Fights = Summarize(fights) };
            }
            case FflogsUploadProtocol.UploadPrepared:
            {
                var current = RequireClient();
                var options = RequireOptions(request);
                IReadOnlyList<ParserEngine.FightUpload> fights;
                string fileName;
                lock (sync)
                {
                    fights = prepared;
                    fileName = preparedFileName;
                }

                var selected = (request.FightIndices ?? [])
                    .Distinct()
                    .Where(index => index >= 0 && index < fights.Count)
                    .Order()
                    .Select(index => fights[index])
                    .ToList();
                if (selected.Count == 0)
                    throw new FflogsUploadFailure(FflogsUploadProtocol.NoFights, "No prepared fight was selected.");
                var code = await current.UploadPreparedAsync(
                    fileName, selected, options.Region, options.Visibility, options.GuildId, options.Description,
                    cancellationToken).ConfigureAwait(false);
                return Reply() with { ReportCode = code, FightCount = selected.Count };
            }
            case FflogsUploadProtocol.UploadLatest:
            {
                var current = RequireClient();
                var options = RequireOptions(request);
                ConfigureEngine(request.Engine);
                var path = LatestLog(request.LogDirectory);
                var code = await current.UploadLogAsync(
                    path, options.Region, RegionCode, options.Visibility, options.GuildId, options.Description,
                    cancellationToken).ConfigureAwait(false);
                return Reply() with { ReportCode = code, FightCount = current.LastUploadFightCount };
            }
            default:
                return Reply() with
                {
                    ReportCode = await UploadEncounterAsync(request, cancellationToken).ConfigureAwait(false),
                    FightCount = 1,
                };
        }
    }

    private async Task LoginAsync(FflogsUploadRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            throw new FflogsUploadFailure(FflogsUploadProtocol.LoginFailed, "E-mail and password are required.");

        var site = loopbackSite ?? (request.ChineseSite ? "https://cn.fflogs.com" : "https://www.fflogs.com");
        var next = new FflogsUploadClient(site, cacheRoot);
        try
        {
            if (!await next.LoginAsync(request.Email.Trim(), request.Password, cancellationToken).ConfigureAwait(false))
                throw new FflogsUploadFailure(FflogsUploadProtocol.LoginFailed, next.LastLoginError ?? "Sign-in was refused.");
        }
        catch
        {
            next.Dispose();
            throw;
        }

        Replace(next, request.ChineseSite);
    }

    // Uploads the one pull a history record stands for. Only logs written during that pull are
    // parsed: a night of raiding is hundreds of megabytes per file, and the two-pass parse
    // replays the file once per pull it contains.
    private async Task<string> UploadEncounterAsync(FflogsUploadRequest request, CancellationToken cancellationToken)
    {
        var current = RequireClient();
        var options = RequireOptions(request);
        ConfigureEngine(request.Engine);
        var directory = RequireDirectory(request.LogDirectory);
        var start = request.EncounterStartUnixMs;
        var end = Math.Max(request.EncounterEndUnixMs, start);
        var candidates = Directory.GetFiles(directory, "*.log")
            .Select(path => new FileInfo(path))
            // Logs are written one after another, so the pull is in the first file that was
            // still written to after it began, or in the next one when the log rotated during
            // the pull. Creation time is not used: copying a log elsewhere resets it.
            .Where(file => new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds() >= start)
            .OrderBy(file => file.LastWriteTimeUtc)
            .Take(MaximumEncounterLogCandidates)
            .ToList();
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fights = await current.PrepareUploadsAsync(file.FullName, RegionCode, cancellationToken).ConfigureAwait(false);
            // FightStartTime is the time base shared by every fight of the log, not this pull's
            // own start; FightEndTime is the absolute time of the pull's last event and is what
            // the site itself receives as the segment end.
            var match = fights
                .Where(fight => fight.FightEndTime >= start)
                .OrderBy(fight => Math.Abs(fight.FightEndTime - end))
                .FirstOrDefault();
            if (match is null || Math.Abs(match.FightEndTime - end) > EncounterMatchToleranceMs) continue;
            return await current.UploadPreparedAsync(
                file.Name, [match], options.Region, options.Visibility, options.GuildId, options.Description,
                cancellationToken).ConfigureAwait(false);
        }

        throw new FflogsUploadFailure(FflogsUploadProtocol.NoMatch, "No matching fight was found in the saved Network logs.");
    }

    private void StartLive(FflogsUploadRequest request)
    {
        var current = RequireClient();
        var options = RequireOptions(request);
        ConfigureEngine(request.Engine);
        var directory = RequireDirectory(request.LogDirectory);
        if (Directory.GetFiles(directory, "*.log").Length == 0)
            throw new FflogsUploadFailure(FflogsUploadProtocol.NoLog, "No Network log file was found.");
        current.StartLiveLog(directory, options.Region, RegionCode, options.Visibility, options.GuildId,
            options.Description, options.IncludePreviousFights);
    }

    // The parser's own region switch; the report's server region comes from the options.
    private string RegionCode
    {
        get
        {
            lock (sync) return chineseSite ? "CN" : "NA";
        }
    }

    private FflogsUploadClient? Current()
    {
        lock (sync) return client;
    }

    private FflogsUploadClient RequireClient()
        => Current() is { IsLoggedIn: true } current
            ? current
            : throw new FflogsUploadFailure(FflogsUploadProtocol.NotLoggedIn, "Sign in to FFLogs first.");

    private static FflogsUploadOptions RequireOptions(FflogsUploadRequest request)
        => request.Options ?? throw new FflogsUploadFailure(FflogsUploadProtocol.Failed, "Upload options are missing.");

    private static string RequireDirectory(string? directory)
        => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : throw new FflogsUploadFailure(FflogsUploadProtocol.NoLog, "The Network log directory does not exist.");

    private static string LatestLog(string? directory)
    {
        var path = LogFileHelper.GetLatestLogFileFromPath(RequireDirectory(directory));
        return File.Exists(path)
            ? path
            : throw new FflogsUploadFailure(FflogsUploadProtocol.NoLog, "No Network log file was found.");
    }

    private static IReadOnlyList<FflogsUploadFight> Summarize(IReadOnlyList<ParserEngine.FightUpload> fights)
        => fights.Select((fight, index) => new FflogsUploadFight(index, fight.Fight.Name, fight.FightEndTime)).ToArray();

    // Swapping the client ends the previous session: its live upload stops and its report is
    // closed before the new account takes over.
    private void Replace(FflogsUploadClient? next, bool chinese)
    {
        FflogsUploadClient? previous;
        lock (sync)
        {
            previous = client;
            client = next;
            chineseSite = chinese;
            prepared = [];
            preparedFileName = string.Empty;
        }

        previous?.Dispose();
    }

    private FflogsUploadResponse Reply()
    {
        FflogsUploadClient? current;
        bool chinese;
        lock (sync)
        {
            current = client;
            chinese = chineseSite;
        }

        if (current is not { IsLoggedIn: true })
            return new FflogsUploadResponse(new FflogsUploadStatus(false, chinese, string.Empty, [], false, 0, null, null));
        var guilds = current.Guilds.Select(guild => new FflogsUploadGuild(guild.Id, guild.Name)).ToArray();
        return new FflogsUploadResponse(new FflogsUploadStatus(
            true,
            chinese,
            current.Username ?? string.Empty,
            guilds,
            current.IsLiveLogging,
            current.LiveFightCount,
            current.LiveReportCode,
            current.LiveError));
    }

    // The Host ships only ClearScript's managed bindings. The native V8 library and its ICU data
    // are fetched and verified by the game side and passed in by path, once per process.
    private static void ConfigureEngine(FflogsUploadEngine? engine)
    {
        lock (EngineSync)
        {
            if (engineConfigured) return;
            if (engine is null ||
                !File.Exists(Path.Combine(engine.NativeDirectory, NativeLibraryName)) ||
                !File.Exists(engine.IcuDataAssembly))
            {
                throw new FflogsUploadFailure(FflogsUploadProtocol.EngineUnavailable, "The V8 parser engine files are missing.");
            }

            HostSettings.AuxiliarySearchPath = Path.GetFullPath(engine.NativeDirectory);
            var icuData = Path.GetFullPath(engine.IcuDataAssembly);
            AssemblyLoadContext.Default.Resolving += (context, name) =>
                string.Equals(name.Name, "ClearScript.V8.ICUData", StringComparison.OrdinalIgnoreCase)
                    ? context.LoadFromAssemblyPath(icuData)
                    : null;
            engineConfigured = true;
        }
    }

    public void Dispose()
    {
        Replace(null, false);
        operation.Dispose();
    }
}
