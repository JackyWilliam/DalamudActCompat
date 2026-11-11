using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Ai;

// Owns the --ai-relay Host process that sends AI 助手 requests from outside the game
// (AiRelayProtocol explains why). It is started on the first request and lives until the plugin
// unloads or the game exits.
internal sealed class AiRelay : IDisposable
{
    // A cold start of the relay process takes well under a second; give a slow disk some room.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    private readonly Func<string?> hostExecutable;
    private readonly PluginLogger? logger;
    private readonly object sync = new();
    private Process? process;
    private string? pipeName;
    private bool disposed;

    public AiRelay(Func<string?> hostExecutable, PluginLogger? logger)
    {
        this.hostExecutable = hostExecutable;
        this.logger = logger;
    }

    internal int ProcessId
    {
        get
        {
            lock (sync) return process is { HasExited: false } running ? running.Id : 0;
        }
    }

    // Null when no relay can be used right now; the caller then sends the request directly.
    public async Task<Stream?> TryConnectAsync(CancellationToken cancellationToken)
    {
        string? pipe;
        lock (sync)
        {
            pipe = EnsureStartedLocked();
        }

        if (pipe is null) return null;
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            logger?.Warning($"AI relay did not accept a connection ({ex.Message}); sending this request from the game process.");
            // A relay that cannot be reached is replaced on the next request.
            lock (sync) StopLocked();
            return null;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string? EnsureStartedLocked()
    {
        if (disposed) return null;
        if (process is { HasExited: false }) return pipeName;
        StopLocked();
        var executable = hostExecutable();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return null;
        var name = $"DalamudActCompat-ai-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };
        foreach (var argument in new[]
                 {
                     AiRelayProtocol.ModeArgument,
                     AiRelayProtocol.PipeArgument, name,
                     AiRelayProtocol.ParentArgument, Environment.ProcessId.ToString(),
                 })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger?.Warning($"AI relay could not start ({ex.Message}); AI requests are sent from the game process.");
            return null;
        }

        if (process is null) return null;
        pipeName = name;
        logger?.Information($"AI relay process {process.Id} started.");
        return pipeName;
    }

    private void StopLocked()
    {
        var current = process;
        process = null;
        pipeName = null;
        if (current is null) return;
        try
        {
            if (!current.HasExited) current.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It exited on its own in the meantime.
        }
        finally
        {
            current.Dispose();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            StopLocked();
        }
    }
}

// The HttpMessageHandler behind every AI 助手 request, including the Anthropic SDK's. It hands
// the request to the relay when one is available and otherwise sends it itself, as DACT always
// did before the relay existed.
internal sealed class AiRelayHandler : HttpMessageHandler
{
    private static readonly HashSet<string> FramingHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Content-Length", "Connection", "Keep-Alive",
    };

    private readonly Func<AiRelay?> relay;
    private readonly HttpMessageInvoker direct = new(new HttpClientHandler(), disposeHandler: true);

    public AiRelayHandler(Func<AiRelay?> relay)
    {
        this.relay = relay;
    }

    // For the package smoke test: which path the last request took.
    internal string LastRoute { get; private set; } = "none";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pipe = relay() is { } current
            ? await current.TryConnectAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (pipe is null)
        {
            LastRoute = "direct";
            return await direct.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        LastRoute = "relay";
        await using (pipe.ConfigureAwait(false))
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var headers = request.Headers
                .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .SelectMany(header => header.Value.Select(value => new AiRelayHeader(header.Key, value)))
                .ToArray();
            var outgoing = new AiRelayRequest(request.Method.Method, request.RequestUri!.AbsoluteUri, headers, body);
            AiRelayResponse? answer;
            // Closing the pipe is the cancel signal the relay listens for.
            using (cancellationToken.Register(static state => ((Stream)state!).Dispose(), pipe))
            {
                try
                {
                    await AiRelayProtocol.WriteAsync(pipe, outgoing, cancellationToken).ConfigureAwait(false);
                    answer = await AiRelayProtocol.ReadAsync<AiRelayResponse>(pipe, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested &&
                                           ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
                {
                    throw new HttpRequestException($"The AI relay process stopped during the request: {ex.Message}", ex);
                }
            }

            if (answer is null)
                throw new HttpRequestException("The AI relay process closed the connection without an answer.");
            if (answer.Failure is not null)
                throw new HttpRequestException(answer.FailureMessage ?? answer.Failure);

            var response = new HttpResponseMessage((HttpStatusCode)answer.Status)
            {
                ReasonPhrase = answer.ReasonPhrase,
                RequestMessage = request,
                Content = new ByteArrayContent(answer.Body ?? []),
            };
            foreach (var header in answer.Headers)
            {
                // The relay already read the whole body; framing headers would describe the
                // original connection, not this buffered copy.
                if (FramingHeaders.Contains(header.Name)) continue;
                if (!response.Headers.TryAddWithoutValidation(header.Name, header.Value))
                    response.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }

            return response;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) direct.Dispose();
        base.Dispose(disposing);
    }
}
