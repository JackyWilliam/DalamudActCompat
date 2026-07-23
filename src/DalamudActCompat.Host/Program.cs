namespace DalamudActCompat.Host;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("DalamudActCompat compatibility host starting.");
        Console.WriteLine("IINACT/NotACT and FFXIV_ACT_Plugin integration is reserved for the next implementation step.");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Compatibility host stopped.");
            return 0;
        }
    }
}
