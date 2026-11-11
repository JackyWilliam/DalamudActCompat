using System.Buffers.Binary;
using System.Text.Json;

namespace DalamudActCompat.Protocol;

// AI 助手 requests leave the PC from a separate DalamudActCompat.Host process started with
// --ai-relay instead of from the game. Accelerators such as NetEase UU redirect every outbound
// connection of ffxiv_dx11.exe into their own tunnel (verified 2026-11-10 with UU 6.21: the game's
// sockets all went to 127.0.0.1:1111 owned by uu.exe, while the Host child process connected out
// directly). Players abroad who accelerate the CN client then reach OpenAI or Anthropic from an
// unsupported region and every request fails. The relay process loads no ACT plugins, so the
// player's API key never enters the shared Host where third-party plugin code runs.
//
// Each request uses its own pipe connection: one request frame in, one response frame out. That
// keeps the relay free of sequencing, and closing the pipe is how the game side cancels a call.
public static class AiRelayProtocol
{
    public const string ModeArgument = "--ai-relay";
    public const string PipeArgument = "--pipe";
    public const string ParentArgument = "--parent";

    // A review request carries the trimmed combat log; replies are capped at 16k output tokens.
    // The limit only guards against a corrupt length prefix.
    public const int MaximumFrameBytes = 64 * 1024 * 1024;

    // A failure the relay met before any HTTP response existed (DNS, refused or reset connection,
    // TLS). The game side turns it back into an HttpRequestException.
    public const string ConnectFailure = "connect";

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"AI relay frame is {payload.Length} bytes; maximum is {MaximumFrameBytes}.");
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
            throw new EndOfStreamException("AI relay pipe closed inside a frame header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException($"AI relay frame length {length} is invalid.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload);
    }
}

public sealed record AiRelayHeader(string Name, string Value);

public sealed record AiRelayRequest(
    string Method,
    string Url,
    IReadOnlyList<AiRelayHeader> Headers,
    byte[]? Body);

// Either an HTTP response (Status > 0) or a transport failure (Failure set).
public sealed record AiRelayResponse(
    int Status,
    string? ReasonPhrase,
    IReadOnlyList<AiRelayHeader> Headers,
    byte[]? Body,
    string? Failure = null,
    string? FailureMessage = null);
