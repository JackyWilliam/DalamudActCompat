using System.Collections.Concurrent;
using System.Reflection;

namespace DalamudActCompat.ActRuntime;

public enum CompatibilityStageState
{
    Success,
    Failed,
    NotImplemented,
    NotTested,
}

public sealed record CompatibilityStageResult(
    string Stage,
    CompatibilityStageState State,
    string Detail,
    DateTimeOffset Timestamp);

public sealed record ActPluginDiagnostic(
    string PluginId,
    string Category,
    string ExceptionType,
    string Message,
    string FullStack,
    string? SourceAssembly,
    string? SourceType,
    string? SourceMethod,
    string Trigger,
    int ManagedThreadId,
    bool IsUiThread,
    int RepeatCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

internal sealed class PluginDiagnosticJournal
{
    private const int MaximumEntries = 1_000;
    private readonly string pluginId;
    private readonly ConcurrentDictionary<string, ActPluginDiagnostic> entries =
        new(StringComparer.Ordinal);

    public PluginDiagnosticJournal(string pluginId)
    {
        this.pluginId = pluginId;
    }

    public IReadOnlyList<ActPluginDiagnostic> Snapshot()
        => entries.Values
            .OrderBy(entry => entry.FirstSeen)
            .ToArray();

    public void Record(Exception exception, string category, string trigger, bool isUiThread)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var root = Unwrap(exception);
        var target = root.TargetSite;
        var now = DateTimeOffset.UtcNow;
        var key = string.Join(
            "|",
            category,
            root.GetType().FullName,
            root.Message,
            target?.DeclaringType?.FullName,
            target?.Name);
        entries.AddOrUpdate(
            key,
            _ => Create(root, category, trigger, isUiThread, target, now),
            (_, current) => current with
            {
                RepeatCount = current.RepeatCount + 1,
                LastSeen = now,
                Trigger = trigger,
                ManagedThreadId = Environment.CurrentManagedThreadId,
                IsUiThread = isUiThread,
            });
        Trim();
    }

    public void RecordMessage(
        string category,
        string message,
        string trigger,
        string? sourceAssembly,
        string? sourceType,
        string? sourceMethod,
        DateTimeOffset timestamp)
    {
        var key = string.Join("|", category, message, sourceType, sourceMethod);
        entries.AddOrUpdate(
            key,
            _ => new ActPluginDiagnostic(
                pluginId,
                category,
                "Triggernometry.InternalErrorRecord",
                message,
                "上游仅保存了错误消息，没有保留 Exception 对象或堆栈；兼容宿主不能伪造完整堆栈。",
                sourceAssembly,
                sourceType,
                sourceMethod,
                trigger,
                Environment.CurrentManagedThreadId,
                false,
                1,
                timestamp,
                timestamp),
            (_, current) => current with
            {
                RepeatCount = current.RepeatCount + 1,
                LastSeen = timestamp,
                Trigger = trigger,
            });
        Trim();
    }

    private ActPluginDiagnostic Create(
        Exception exception,
        string category,
        string trigger,
        bool isUiThread,
        MethodBase? target,
        DateTimeOffset now)
        => new(
            pluginId,
            category,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.ToString(),
            exception.Source,
            target?.DeclaringType?.FullName,
            target?.Name,
            trigger,
            Environment.CurrentManagedThreadId,
            isUiThread,
            1,
            now,
            now);

    private static Exception Unwrap(Exception exception)
        => exception is TargetInvocationException { InnerException: { } inner }
            ? Unwrap(inner)
            : exception;

    private void Trim()
    {
        var excess = entries.Count - MaximumEntries;
        if (excess <= 0)
        {
            return;
        }

        foreach (var key in entries
                     .OrderBy(pair => pair.Value.LastSeen)
                     .Take(excess)
                     .Select(pair => pair.Key))
        {
            entries.TryRemove(key, out _);
        }
    }
}

internal sealed class TriggernometryDiagnosticMonitor : IDisposable
{
    private readonly object proxy;
    private readonly PluginDiagnosticJournal journal;
    private readonly Dalamud.Plugin.Services.IPluginLog log;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private readonly System.Threading.Timer timer;
    private int polling;
    private int disposed;

    public TriggernometryDiagnosticMonitor(
        object proxy,
        PluginDiagnosticJournal journal,
        Dalamud.Plugin.Services.IPluginLog log)
    {
        this.proxy = proxy;
        this.journal = journal;
        this.log = log;
        timer = new System.Threading.Timer(
            _ => Poll(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        timer.Dispose();
    }

    private void Poll()
    {
        if (Volatile.Read(ref disposed) != 0 ||
            Interlocked.Exchange(ref polling, 1) != 0)
        {
            return;
        }

        try
        {
            var realPlugin = proxy.GetType()
                .GetField("Instance", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(proxy);
            var logDictionary = realPlugin?.GetType()
                .GetField("log", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(realPlugin) as System.Collections.IDictionary;
            if (logDictionary is null)
            {
                return;
            }

            foreach (System.Collections.DictionaryEntry pair in logDictionary)
            {
                if (!string.Equals(pair.Key?.ToString(), "Error", StringComparison.Ordinal) ||
                    pair.Value is not System.Collections.IEnumerable queue)
                {
                    continue;
                }

                object[] entries;
                lock (pair.Value)
                {
                    entries = queue.Cast<object>().ToArray();
                }

                foreach (var entry in entries)
                {
                    Capture(entry);
                }
            }
        }
        catch (Exception ex)
        {
            journal.Record(
                ex,
                "Triggernometry 专属行为",
                "读取 Triggernometry 内部结构化错误日志",
                false);
        }
        finally
        {
            Volatile.Write(ref polling, 0);
        }
    }

    private void Capture(object entry)
    {
        var entryType = entry.GetType();
        var message = entryType.GetProperty("Message")?.GetValue(entry)?.ToString() ?? string.Empty;
        var timestampValue = entryType.GetProperty("Timestamp")?.GetValue(entry);
        var timestamp = timestampValue is DateTime dateTime
            ? new DateTimeOffset(dateTime)
            : DateTimeOffset.UtcNow;
        var identity = $"{timestamp.UtcTicks}|{message}";
        lock (seen)
        {
            if (!seen.Add(identity))
            {
                return;
            }

            seenOrder.Enqueue(identity);
            while (seenOrder.Count > 8_192)
            {
                seen.Remove(seenOrder.Dequeue());
            }
        }

        var repositoryFailure =
            message.Contains("repository", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("仓库", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("更新", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("超时", StringComparison.OrdinalIgnoreCase);
        journal.RecordMessage(
            repositoryFailure ? "Triggernometry 远程仓库更新" : "Triggernometry 专属行为",
            message,
            repositoryFailure ? "启动时远程仓库更新" : "Triggernometry 内部错误日志",
            entryType.Assembly.GetName().Name,
            repositoryFailure ? "Triggernometry.Core.Repository" : entryType.FullName,
            repositoryFailure ? "CheckAndUpdateAsync/UpdateAsync" : null,
            timestamp);
        log.Error($"Triggernometry diagnostic: {message}");
    }
}
