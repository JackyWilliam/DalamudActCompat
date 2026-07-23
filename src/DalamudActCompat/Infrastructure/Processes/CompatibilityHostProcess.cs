using System.Diagnostics;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Infrastructure.Processes;

public sealed class CompatibilityHostProcess : IAsyncDisposable
{
    private readonly PluginLogger logger;
    private Process? process;

    public CompatibilityHostProcess(PluginLogger logger)
    {
        this.logger = logger;
    }

    public bool IsRunning => process is { HasExited: false };

    public Task StartAsync(HostLaunchSpec launchSpec, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        if (launchSpec.RequiresExistingExecutable && !File.Exists(launchSpec.FileName))
        {
            throw new FileNotFoundException("Compatibility host executable was not found.", launchSpec.FileName);
        }

        var startInfo = new ProcessStartInfo(launchSpec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = launchSpec.WorkingDirectory,
        };
        foreach (var argument in launchSpec.PrefixArguments.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        process = Process.Start(startInfo) ?? throw new InvalidOperationException("Compatibility host process did not start.");
        _ = DrainOutputAsync(process, cancellationToken);
        _ = DrainErrorAsync(process, cancellationToken);
        logger.Information("Compatibility host process started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var current = process;
        if (current is null)
        {
            return;
        }

        if (!current.HasExited)
        {
            current.Kill(entireProcessTree: true);
            await current.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        current.Dispose();
        process = null;
        logger.Information("Compatibility host process stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await StopAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task DrainOutputAsync(Process target, CancellationToken cancellationToken)
    {
        try
        {
            while (!target.HasExited && !cancellationToken.IsCancellationRequested)
            {
                var line = await target.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    logger.Information($"host: {line}");
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            logger.Warning($"Host output drain stopped: {ex.Message}");
        }
    }

    private async Task DrainErrorAsync(Process target, CancellationToken cancellationToken)
    {
        try
        {
            while (!target.HasExited && !cancellationToken.IsCancellationRequested)
            {
                var line = await target.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    logger.Warning($"host error: {line}");
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            logger.Warning($"Host error drain stopped: {ex.Message}");
        }
    }
}

public sealed record HostLaunchSpec(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> PrefixArguments,
    bool RequiresExistingExecutable)
{
    public static HostLaunchSpec ForExecutable(string executablePath)
        => new(
            executablePath,
            Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            Array.Empty<string>(),
            true);

    public static HostLaunchSpec ForDotnet(string assemblyPath)
        => new(
            "dotnet",
            Path.GetDirectoryName(assemblyPath) ?? Environment.CurrentDirectory,
            [assemblyPath],
            false);
}
