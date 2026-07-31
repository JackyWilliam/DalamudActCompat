using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dalamud.Plugin.Services;

namespace DalamudActCompat.ActRuntime;

/// <summary>
/// Keeps legacy ACT plugin clipboard work off plugin and game UI threads.
/// Clipboard contention is retried only on this bounded STA worker.
/// </summary>
internal sealed class StaClipboardService : IDisposable
{
    private const int QueueCapacity = 8;
    private const int MaxClipboardCharacters = 8 * 1024 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private readonly BlockingCollection<ClipboardRequest> requests = new(QueueCapacity);
    private readonly IPluginLog log;
    private readonly Thread thread;
    private int disposed;

    public StaClipboardService(IPluginLog log)
    {
        this.log = log;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DalamudActCompat clipboard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void QueueSetText(string text)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxClipboardCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                $"Clipboard payload exceeds the {MaxClipboardCharacters}-character safety limit.");
        }

        if (!requests.TryAdd(new ClipboardRequest(text, null)))
        {
            log.Warning(
                $"Clipboard request was rejected because the bounded queue reached {QueueCapacity} items.");
        }
    }

    public string GetText()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!requests.TryAdd(new ClipboardRequest(null, completion)))
        {
            throw new InvalidOperationException(
                $"Clipboard request queue is full ({QueueCapacity} items).");
        }

        if (!completion.Task.Wait(ReadTimeout))
        {
            throw new TimeoutException(
                $"Clipboard read did not complete within {ReadTimeout.TotalSeconds:0} seconds.");
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        requests.CompleteAdding();
        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
            log.Warning("Clipboard STA worker did not stop within two seconds; it will exit with the process.");
        }

        while (requests.TryTake(out var pending))
        {
            pending.Completion?.TrySetCanceled();
        }

        requests.Dispose();
    }

    private void Run()
    {
        foreach (var request in requests.GetConsumingEnumerable())
        {
            try
            {
                if (request.Text is not null)
                {
                    RetryClipboard(() => Clipboard.SetText(request.Text));
                    log.Debug($"Copied {request.Text.Length} characters through the clipboard STA worker.");
                }
                else
                {
                    var result = string.Empty;
                    RetryClipboard(() => result = Clipboard.GetText());
                    request.Completion!.TrySetResult(result);
                }
            }
            catch (Exception ex)
            {
                if (request.Completion is not null)
                {
                    request.Completion.TrySetException(ex);
                }
                else
                {
                    log.Error(ex, "Clipboard write failed on the dedicated STA worker.");
                }
            }
        }
    }

    private static void RetryClipboard(Action action)
    {
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (ExternalException ex)
            {
                lastFailure = ex;
                if (attempt < 4)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }

        ExceptionDispatchInfo.Capture(lastFailure!).Throw();
    }

    private sealed record ClipboardRequest(
        string? Text,
        TaskCompletionSource<string>? Completion);
}
