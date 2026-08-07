using System.Runtime.ExceptionServices;

namespace DalamudActCompat.Compatibility.PluginHost;

internal static class BundledPluginInstallCoordinator
{
    public static async Task ExecuteAsync(
        bool hostWasRunning,
        bool parserWasRunning,
        Func<CancellationToken, Task> stopHost,
        Func<CancellationToken, Task> stopParser,
        Func<CancellationToken, Task> install,
        Func<CancellationToken, Task> startHost,
        Func<CancellationToken, Task> startParser,
        Func<bool> shouldResume,
        CancellationToken cancellationToken,
        TimeSpan resumeTimeout)
    {
        ArgumentNullException.ThrowIfNull(stopHost);
        ArgumentNullException.ThrowIfNull(stopParser);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(startHost);
        ArgumentNullException.ThrowIfNull(startParser);
        ArgumentNullException.ThrowIfNull(shouldResume);

        Exception? operationFailure = null;
        var hostPauseAttempted = false;
        var parserPauseAttempted = false;
        try
        {
            if (hostWasRunning)
            {
                hostPauseAttempted = true;
                await stopHost(cancellationToken).ConfigureAwait(false);
            }

            if (parserWasRunning)
            {
                parserPauseAttempted = true;
                await stopParser(cancellationToken).ConfigureAwait(false);
            }

            await install(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            operationFailure = ex;
        }

        List<Exception>? resumeFailures = null;
        if (shouldResume())
        {
            using var resumeCancellation = new CancellationTokenSource(resumeTimeout);
            if (hostPauseAttempted)
            {
                try
                {
                    await startHost(resumeCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    (resumeFailures ??= []).Add(ex);
                }
            }

            if (parserPauseAttempted)
            {
                try
                {
                    await startParser(resumeCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    (resumeFailures ??= []).Add(ex);
                }
            }
        }

        if (operationFailure is not null && resumeFailures is { Count: > 0 })
        {
            throw new AggregateException(
                "Bundled plugin installation failed and one or more ACT services could not be restored.",
                [operationFailure, .. resumeFailures]);
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (resumeFailures is { Count: > 0 })
        {
            throw new AggregateException(
                "Bundled plugin installation completed, but one or more ACT services could not be restored.",
                resumeFailures);
        }
    }
}
