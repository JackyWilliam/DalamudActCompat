namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed partial class CloudClientService
{
    private readonly Dictionary<string, uint> observedUltimateClears = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim skinSyncGate = new(1, 1);
    private readonly CloudSkinUnlockStore skinUnlockStore;

    private void ApplySkinUnlockStatus(CloudStoredCredentials current, CloudAccountStatus status)
    {
        if (status.User is not { Id.Length: > 0 } user ||
            !string.Equals(user.Username, current.Username, StringComparison.OrdinalIgnoreCase)) return;
        if (sharedAccountStore is not null && sharedAccountStore.Read().Revision != sharedRevision) return;
        lock (stateLock)
        {
            if (credentials?.Token != current.Token || activeBan is not null) return;
            var error = (string?)null;
            try
            {
                if (skinUnlockStore.Read(user.Id) is { } duty) observedUltimateClears.TryAdd(user.Id, duty);
            }
            catch { error = "本机解锁记录读取失败，请登录已通关角色重新确认。"; }
            // An old heartbeat may arrive after the grant request. Permanent rewards
            // merge by OR, never by replacement, and only for this verified account ID.
            var confirmed = status.Skins?.Ultimate == true ||
                            snapshot.SkinAccountId == user.Id && snapshot.UltimateSkinsConfirmed;
            var observed = observedUltimateClears.ContainsKey(user.Id);
            snapshot = snapshot with { SkinAccountId = user.Id, UltimateSkinsConfirmed = confirmed,
                UltimateSkinsUnlocked = confirmed || observed, UltimateSkinSyncPending = observed && !confirmed,
                SkinSyncError = confirmed ? null : error ?? snapshot.SkinSyncError };
        }
    }

    public void ObserveUltimateClear(string expectedAccountId, uint instanceContentId)
    {
        if (!CloudSkinUnlockStore.IsUltimate(instanceContentId)) return;
        lock (stateLock)
        {
            if (!snapshot.CanUseDact || snapshot.SkinAccountId != expectedAccountId || snapshot.UltimateSkinsUnlocked) return;
            observedUltimateClears[expectedAccountId] = instanceContentId;
            string? error = null;
            try { skinUnlockStore.Remember(expectedAccountId, instanceContentId); }
            catch { error = "本机解锁记录保存失败，正在尝试同步到账号。"; }
            snapshot = snapshot with { UltimateSkinsUnlocked = true, UltimateSkinSyncPending = true, SkinSyncError = error };
        }
    }

    private async Task SyncUltimateSkinsAsync(CloudStoredCredentials current, CancellationToken cancellationToken)
    {
        if (!await skinSyncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            string accountId; uint duty;
            if (sharedAccountStore is not null && sharedAccountStore.Read().Revision != sharedRevision) return;
            lock (stateLock)
            {
                if (credentials?.Token != current.Token || !snapshot.CanUseDact || !snapshot.UltimateSkinSyncPending ||
                    snapshot.SkinAccountId is not { } id || !observedUltimateClears.TryGetValue(id, out duty)) return;
                accountId = id;
                // A disk-full observation is still valid in memory. Retry the durable
                // outbox, but do not let that prevent saving the reward to the server.
                try { skinUnlockStore.Remember(accountId, duty); } catch { }
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            var result = await apiClient.UnlockUltimateSkinsAsync(current.Token, duty, deadline.Token).ConfigureAwait(false);
            if (!result.Ultimate) throw new InvalidDataException("Server did not confirm the skin unlock.");
            if (sharedAccountStore is not null && sharedAccountStore.Read().Revision != sharedRevision) return;
            lock (stateLock)
            {
                if (credentials?.Token != current.Token || snapshot.SkinAccountId != accountId || activeBan is not null) return;
                snapshot = snapshot with { UltimateSkinsUnlocked = true, UltimateSkinsConfirmed = true,
                    UltimateSkinSyncPending = false, SkinSyncError = null };
            }
        }
        finally { skinSyncGate.Release(); }
    }

    private async Task RunSkinUnlockMonitorAsync(CloudStoredCredentials current, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsCurrentSession(current))
        {
            var delay = TimeSpan.FromSeconds(2);
            try { await SyncUltimateSkinsAsync(current, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (CloudApiException ex) when (ex.ToBanNotice() is { } ban)
            {
                if (ShouldApplyBan(current, ban)) ApplyBan(ban);
                return;
            }
            catch (CloudApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                if (IsCurrentSession(current)) InvalidateSessionPreservingRecoveryKey("登录已失效，请重新登录。", true);
                return;
            }
            catch
            {
                lock (stateLock)
                    if (credentials?.Token == current.Token && snapshot.UltimateSkinSyncPending)
                        snapshot = snapshot with { SkinSyncError = "皮肤已在本机解锁，账号同步暂未完成，将自动重试。" };
                delay = TimeSpan.FromSeconds(30);
            }
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }
}
