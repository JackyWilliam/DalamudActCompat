namespace DalamudActCompat.Infrastructure.Processes;

internal static class HostStartupSequence
{
    internal static async Task StartAsync(
        Func<CancellationToken, Task<bool>> shared,
        Func<CancellationToken, Task> matcha,
        Func<CancellationToken, Task> generic,
        Action<string, Exception> reportFailure,
        CancellationToken cancellationToken,
        TimeSpan? stageTimeout = null)
    {
        // Matcha depends on the shared Host's initialization order. Generic imports
        // must still start when either of the other roles fails or uses its budget.
        if (await RunAsync("shared", shared).ConfigureAwait(false))
            await RunAsync("matcha", async token => { await matcha(token).ConfigureAwait(false); return true; }).ConfigureAwait(false);
        await RunAsync("generic", async token => { await generic(token).ConfigureAwait(false); return true; }).ConfigureAwait(false);

        async Task<bool> RunAsync(string role, Func<CancellationToken, Task<bool>> start)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(stageTimeout ?? TimeSpan.FromSeconds(45));
            try { return await start(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                reportFailure(role, ex);
                return false;
            }
        }
    }
}
