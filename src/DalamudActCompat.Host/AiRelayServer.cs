using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Host;

// The --ai-relay mode of the Host executable (see AiRelayProtocol for why it exists). It sends
// each piped request with an HttpClient that has the same defaults the game-side client had
// (system proxy, redirects), so custom endpoints and proxies keep working exactly as before.
internal static class AiRelayServer
{
    // Headers the HTTP stack sets itself; copying them would conflict with the rebuilt request.
    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Transfer-Encoding", "Expect",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var pipeName = string.Empty;
        var parentProcessId = 0;
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index] == AiRelayProtocol.PipeArgument) pipeName = args[index + 1];
            else if (args[index] == AiRelayProtocol.ParentArgument) _ = int.TryParse(args[index + 1], out parentProcessId);
        }

        if (string.IsNullOrWhiteSpace(pipeName) || parentProcessId <= 0)
        {
            Console.Error.WriteLine("Missing --pipe <name> or --parent <process id>.");
            return 2;
        }

        using var stop = new CancellationTokenSource();
        // The game owns this process: when it exits (or crashes) the relay has nothing to serve.
        _ = WatchParentAsync(parentProcessId, stop);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Console.WriteLine($"AI relay ready on {pipeName} for process {parentProcessId}.");
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

            _ = Task.Run(() => ServeAsync(pipe, http, stop.Token), CancellationToken.None);
        }

        return 0;
    }

    private static async Task WatchParentAsync(int parentProcessId, CancellationTokenSource stop)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The parent is already gone.
        }

        await stop.CancelAsync().ConfigureAwait(false);
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, HttpClient http, CancellationToken stopping)
    {
        await using var _ = pipe;
        try
        {
            var request = await AiRelayProtocol.ReadAsync<AiRelayRequest>(pipe, stopping).ConfigureAwait(false);
            if (request is null) return;

            using var call = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            // The game side closes its end when the player cancels or its own timeout fires.
            var watch = CancelOnDisconnectAsync(pipe, call);
            var response = await SendAsync(http, request, call.Token).ConfigureAwait(false);
            if (response is null) return;
            await AiRelayProtocol.WriteAsync(pipe, response, stopping).ConfigureAwait(false);
            await call.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
        {
            // The caller went away mid-frame or sent garbage; nothing is left to answer.
        }
    }

    // Null when the call was abandoned, so no answer is written to a pipe nobody reads.
    internal static async Task<AiRelayResponse?> SendAsync(HttpClient http, AiRelayRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return Failure($"Not an HTTP address: {request.Url}");
        }

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), url);
        if (request.Body is not null)
        {
            message.Content = new ByteArrayContent(request.Body);
        }

        foreach (var header in request.Headers)
        {
            if (SkippedHeaders.Contains(header.Name)) continue;
            if (!message.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                message.Content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .SelectMany(header => header.Value.Select(value => new AiRelayHeader(header.Key, value)))
                .ToArray();
            return new AiRelayResponse((int)response.StatusCode, response.ReasonPhrase, headers, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            return Failure(ex.Message);
        }
    }

    private static AiRelayResponse Failure(string message)
        => new(0, null, [], null, AiRelayProtocol.ConnectFailure, message);

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
