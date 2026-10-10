namespace DalamudActCompat.ActRuntime;

internal sealed class CactbotFrameRecoveryPolicy
{
    private readonly Queue<long> attempts = new();

    internal bool TryRecover(long nowMilliseconds)
    {
        // A covered window can also stop receiving frames. Bound surface refreshes so
        // occlusion or a persistent driver fault cannot cause a continuous flicker loop.
        while (attempts.TryPeek(out var oldest) && nowMilliseconds - oldest >= 300_000)
        {
            attempts.Dequeue();
        }
        if (attempts.Count >= 3 ||
            (attempts.Count > 0 && nowMilliseconds - attempts.Last() < 30_000))
        {
            return false;
        }

        attempts.Enqueue(nowMilliseconds);
        return true;
    }
}
