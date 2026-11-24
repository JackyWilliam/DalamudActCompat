using System.Diagnostics;
using System.IO.Pipes;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Fflogs.Upload;

// Owns the --fflogs-upload Host process (FflogsUploadProtocol explains why uploading does not
// run in the game). It is started on the first request and lives until the plugin unloads or
// the game exits; the FFLogs session lives in that process, so a new process starts signed out.
internal sealed class FflogsUploadHelper : IDisposable
{
    // A cold start of the helper takes well under a second; give a slow disk some room.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    private readonly Func<string?> hostExecutable;
    private readonly string cacheDirectory;
    private readonly PluginLogger? logger;
    private readonly string? loopbackSite;
    private readonly object sync = new();
    private Process? process;
    private string? pipeName;
    private bool disposed;

    public FflogsUploadHelper(Func<string?> hostExecutable, string cacheDirectory, PluginLogger? logger, string? loopbackSite = null)
    {
        this.hostExecutable = hostExecutable;
        this.cacheDirectory = cacheDirectory;
        this.logger = logger;
        this.loopbackSite = loopbackSite;
    }

    internal int ProcessId
    {
        get
        {
            lock (sync) return process is { HasExited: false } running ? running.Id : 0;
        }
    }

    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        string? pipe;
        lock (sync)
        {
            pipe = EnsureStartedLocked();
        }

        if (pipe is null)
        {
            throw new FflogsUploadException(FflogsUploadException.HostUnavailable,
                "The DACT Host is not available yet, so the FFLogs upload helper cannot start.");
        }

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
            // A helper that cannot be reached is replaced on the next request.
            lock (sync) StopLocked();
            throw new FflogsUploadException(FflogsUploadException.HostUnavailable,
                $"The FFLogs upload helper did not accept a connection: {ex.Message}");
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
        var name = $"DalamudActCompat-fflogs-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // The helper reports through standard output; an unread pipe would block it.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };
        var arguments = new List<string>
        {
            FflogsUploadProtocol.ModeArgument,
            FflogsUploadProtocol.PipeArgument, name,
            FflogsUploadProtocol.ParentArgument, Environment.ProcessId.ToString(),
            FflogsUploadProtocol.CacheArgument, cacheDirectory,
        };
        if (loopbackSite is not null)
        {
            arguments.Add(FflogsUploadProtocol.LoopbackSiteArgument);
            arguments.Add(loopbackSite);
        }

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Process? started;
        try
        {
            started = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger?.Warning($"FFLogs upload helper could not start: {ex.Message}");
            return null;
        }

        if (started is null) return null;
        started.OutputDataReceived += (_, line) => Forward(line.Data);
        started.ErrorDataReceived += (_, line) => Forward(line.Data is null ? null : "E|" + line.Data);
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        process = started;
        pipeName = name;
        logger?.Information($"FFLogs upload helper process {started.Id} started.");
        return pipeName;
    }

    // UploadLog in the helper prefixes each line with its level.
    private void Forward(string? line)
    {
        if (string.IsNullOrEmpty(line) || logger is null) return;
        var message = line.Length > 2 && line[1] == '|' ? line[2..] : line;
        switch (line[0])
        {
            case 'D' when line.Length > 1 && line[1] == '|':
                // Per-message parser traces; they would bury the rest of the plugin log.
                break;
            case 'W' when line.Length > 1 && line[1] == '|':
            case 'E' when line.Length > 1 && line[1] == '|':
                logger.Warning("[FFLogs upload] " + message);
                break;
            default:
                logger.Information("[FFLogs upload] " + message);
                break;
        }
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

// A failure with a code the interface can word in the player's language (the codes are in
// FflogsUploadProtocol, plus HostUnavailable for failures before the helper answered).
public sealed class FflogsUploadException(string code, string message) : Exception(message)
{
    public const string HostUnavailable = "host-unavailable";

    public string Code { get; } = code;
}
