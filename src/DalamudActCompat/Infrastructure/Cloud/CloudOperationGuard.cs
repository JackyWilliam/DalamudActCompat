namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed class CloudOperationGuard
{
    private int busy;

    public bool IsBusy => Volatile.Read(ref busy) != 0;

    public bool TryStart(
        Func<Func<Task>, Action, Task?> schedule,
        Func<Task> operation)
    {
        // Claim the operation on the caller's thread, before queuing work. The
        // service's IsBusy covers HTTP/export only, not parser stop and recovery.
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            // The tracked scheduler must run completion even when a ban skips
            // the operation. A continuation here would outlive plugin teardown.
            if (schedule(operation, Complete) is not null)
            {
                return true;
            }
        }
        catch
        {
            Complete();
            throw;
        }

        Complete();
        return false;
    }

    public CloudClientSnapshot GetUiSnapshot(CloudClientSnapshot snapshot)
        => !IsBusy
            ? snapshot
            : snapshot with
            {
                IsBusy = true,
                StatusMessage = snapshot.IsBusy || snapshot.StatusIsError || snapshot.ActiveBan is not null
                    ? snapshot.StatusMessage
                    : "正在准备云端操作或恢复运行组件，请稍候…",
            };

    private void Complete() => Volatile.Write(ref busy, 0);
}
