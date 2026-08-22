using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DalamudActCompat.Host;

internal sealed class AuthorizedTtsQueue : IAsyncDisposable
{
    private readonly Channel<PendingRequest> queue;
    private readonly ConcurrentDictionary<string, PendingRequest> pending =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<Action<string>?> writerAccessor;
    private readonly Action<Exception> errorReporter;
    private readonly Task worker;
    private readonly int capacity;
    private int count;

    public AuthorizedTtsQueue(
        int capacity,
        Func<Action<string>?> writerAccessor,
        Action<Exception> errorReporter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
        this.writerAccessor = writerAccessor ?? throw new ArgumentNullException(nameof(writerAccessor));
        this.errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
        queue = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        worker = Task.Run(DispatchAsync);
    }

    public int Count => Volatile.Read(ref count);

    public string Reserve(string text, DateTimeOffset requestedAt, DateTimeOffset deadline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (Interlocked.Increment(ref count) > capacity)
        {
            Interlocked.Decrement(ref count);
            throw new InvalidOperationException("The bounded TTS authorization queue is full.");
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var request = new PendingRequest(correlationId, text, requestedAt, deadline);
        if (!pending.TryAdd(correlationId, request) || !queue.Writer.TryWrite(request))
        {
            pending.TryRemove(correlationId, out _);
            Interlocked.Decrement(ref count);
            throw new InvalidOperationException("Could not reserve a TTS authorization request.");
        }

        return correlationId;
    }

    public bool Complete(string correlationId, bool allowed, DateTimeOffset completedAt)
    {
        if (!pending.TryGetValue(correlationId, out var request))
        {
            return false;
        }

        request.Authorization.TrySetResult(allowed && completedAt < request.Deadline);
        return true;
    }

    public bool Cancel(string correlationId)
    {
        if (!pending.TryGetValue(correlationId, out var request))
        {
            return false;
        }

        request.Authorization.TrySetResult(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task DispatchAsync()
    {
        DateTimeOffset? lastRequestAt = null;
        DateTimeOffset? lastDispatchAt = null;
        await foreach (var request in queue.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
        {
            var allowed = false;
            try
            {
                var remaining = request.Deadline - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    allowed = await request.Authorization.Task
                        .WaitAsync(remaining, lifetime.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                pending.TryRemove(request.CorrelationId, out _);
                Interlocked.Decrement(ref count);
            }

            if (!allowed)
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            if (lastRequestAt is { } previousRequest &&
                lastDispatchAt is { } previousDispatch &&
                request.RequestedAt < previousDispatch)
            {
                // A broker stall can authorize several already-due requests together. Preserve
                // their original request cadence instead of releasing them as a speech burst.
                var requestedGap = request.RequestedAt - previousRequest;
                var delay = previousDispatch + requestedGap - now;
                if (requestedGap > TimeSpan.Zero && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
                }
            }

            try
            {
                var writer = writerAccessor()
                             ?? throw new InvalidOperationException(
                                 "The isolated ACT TTS provider was unloaded before dispatch.");
                writer(request.Text);
                lastRequestAt = request.RequestedAt;
                lastDispatchAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                errorReporter(ex);
            }
        }
    }

    private sealed record PendingRequest(
        string CorrelationId,
        string Text,
        DateTimeOffset RequestedAt,
        DateTimeOffset Deadline)
    {
        public TaskCompletionSource<bool> Authorization { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
