using System.Buffers.Binary;
using System.Text.Json;

namespace DalamudActCompat.Protocol;

// FFLogs log upload runs in a separate DalamudActCompat.Host process started with
// --fflogs-upload instead of in the game. Uploading means running FF Logs' own JavaScript parser
// in an embedded V8 engine over a whole Network log. Inside ffxiv_dx11.exe that work would compete
// with the game for CPU and memory, and a V8 out-of-memory abort ends the process it runs in.
// The helper loads no ACT plugins, so the FFLogs password and session cookie never enter the
// shared Host where third-party plugin code runs.
//
// Each request uses its own pipe connection: one request frame in, one response frame out.
// Closing the pipe is how the game side cancels a running parse or upload. The FFLogs session,
// the parsed fights and a running live upload stay in the helper between requests, so the event
// text of a night of pulls never has to be copied into the game.
public static class FflogsUploadProtocol
{
    public const string ModeArgument = "--fflogs-upload";
    public const string PipeArgument = "--pipe";
    public const string ParentArgument = "--parent";
    public const string CacheArgument = "--cache-root";

    // For the smoke tests: a loopback stand-in for fflogs.com. The helper refuses any other
    // address, so no argument can send an account password somewhere else.
    public const string LoopbackSiteArgument = "--loopback-site";

    // Requests and responses carry settings, fight names and a status snapshot, never log text.
    // The limit only guards against a corrupt length prefix.
    public const int MaximumFrameBytes = 4 * 1024 * 1024;

    public const string Status = "status";
    public const string Login = "login";
    public const string Logout = "logout";
    public const string Prepare = "prepare";
    public const string UploadPrepared = "upload-prepared";
    public const string UploadLatest = "upload-latest";
    public const string UploadEncounter = "upload-encounter";
    public const string LiveStart = "live-start";
    public const string LiveStop = "live-stop";

    // Failures the interface words itself. Anything else arrives as "failed" with the helper's
    // own message.
    public const string NotLoggedIn = "not-logged-in";
    public const string LoginFailed = "login-failed";
    public const string EngineUnavailable = "engine-unavailable";
    public const string NoLog = "no-log";
    public const string NoFights = "no-fights";
    public const string NoMatch = "no-match";
    public const string Failed = "failed";

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"FFLogs upload frame is {payload.Length} bytes; maximum is {MaximumFrameBytes}.");
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // Null when the other side closed the pipe before sending a frame.
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        var read = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            return default;
        }

        if (read < prefix.Length)
        {
            throw new EndOfStreamException("FFLogs upload pipe closed inside a frame header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException($"FFLogs upload frame length {length} is invalid.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload);
    }
}

// A class rather than a record: a record's generated ToString prints every property, and this
// one can hold the account password.
public sealed class FflogsUploadRequest
{
    public string Operation { get; init; } = string.Empty;

    // cn.fflogs.com and www.fflogs.com keep separate accounts and reports.
    public bool ChineseSite { get; init; }

    public string? Email { get; init; }

    public string? Password { get; init; }

    public string? LogDirectory { get; init; }

    public FflogsUploadOptions? Options { get; init; }

    public FflogsUploadEngine? Engine { get; init; }

    // Indexes into the fight list the last "prepare" answered with.
    public IReadOnlyList<int>? FightIndices { get; init; }

    public long EncounterStartUnixMs { get; init; }

    public long EncounterEndUnixMs { get; init; }
}

public sealed record FflogsUploadOptions(
    int Visibility,
    int Region,
    string GuildId,
    string Description,
    bool IncludePreviousFights = false);

// Where the game side placed the verified V8 runtime; the helper ships without one.
public sealed record FflogsUploadEngine(string NativeDirectory, string IcuDataAssembly);

public sealed record FflogsUploadGuild(string Id, string Name);

public sealed record FflogsUploadFight(int Index, string Name, long EndUnixMs);

public sealed record FflogsUploadStatus(
    bool LoggedIn,
    bool ChineseSite,
    string UserName,
    IReadOnlyList<FflogsUploadGuild> Guilds,
    bool LiveActive,
    int LiveFightCount,
    string? LiveReportCode,
    string? LiveError);

// Every answer carries the current status, so the game side never needs a second round trip to
// learn what an operation changed.
public sealed record FflogsUploadResponse(FflogsUploadStatus Status)
{
    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ReportCode { get; init; }

    // How many fights the finished upload put into ReportCode.
    public int FightCount { get; init; }

    public IReadOnlyList<FflogsUploadFight>? Fights { get; init; }
}
