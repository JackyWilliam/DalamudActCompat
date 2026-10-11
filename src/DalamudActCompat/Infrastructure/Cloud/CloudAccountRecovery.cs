namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed partial class CloudClientService
{
    private long recoveryStatusRequestSequence;
    private long appliedRecoveryStatusRequest;

    private long NextRecoveryStatusRequest() => Interlocked.Increment(ref recoveryStatusRequestSequence);

    internal bool HasRecoveryKeyFor(string username)
    {
        lock (stateLock)
            return new[] { credentials, storedAccount }.Any(account => account is not null &&
                string.Equals(account.Username, username.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(account.RecoveryKey));
    }

    private void ApplyRecoveryStatus(CloudStoredCredentials current, CloudRecoveryStatus? status, long requestVersion)
    {
        if (sharedAccountStore is not null && sharedAccountStore.Read().Revision != sharedRevision) return;
        lock (stateLock)
        {
            if (credentials?.Token != current.Token || activeBan is not null || requestVersion < appliedRecoveryStatusRequest) return;
            appliedRecoveryStatusRequest = requestVersion;
            snapshot = snapshot with { Recovery = status,
                DiscordLinkCode = status?.DiscordBound == true ? null : snapshot.DiscordLinkCode,
                RecoveryStatusError = status is null ? "服务器未返回 Discord 绑定状态，请稍后重试。" : null };
        }
    }

    private async Task RefreshRecoveryProtectionAsync(CloudStoredCredentials current, CancellationToken cancellationToken)
    {
        var requestVersion = NextRecoveryStatusRequest();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var account = await apiClient.GetAccountStatusAsync(current.Token, timeout.Token).ConfigureAwait(false);
            ApplySkinUnlockStatus(current, account);
            var status = account.Recovery;
            if (status?.Available == true && !status.Protected)
            {
                var enrolled = await apiClient.ProtectRecoveryKeyAsync(current.Token, current.RecoveryKey, timeout.Token).ConfigureAwait(false);
                status = enrolled with { DiscordAvailable = status.DiscordAvailable };
            }
            ApplyRecoveryStatus(current, status, requestVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CloudApiException ex) when (ex.ToBanNotice() is { } ban)
        {
            if (ShouldApplyBan(current, ban)) ApplyBan(ban);
            throw;
        }
        catch (CloudApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            if (IsCurrentSession(current)) InvalidateSessionPreservingRecoveryKey("登录已失效，请重新登录。", true);
            throw;
        }
        catch
        {
            lock (stateLock)
            {
                if (credentials?.Token == current.Token && activeBan is null && requestVersion >= appliedRecoveryStatusRequest)
                {
                    appliedRecoveryStatusRequest = requestVersion;
                    snapshot = snapshot with { RecoveryStatusError = "暂时无法检查 Discord 绑定或建立恢复保护，请检查网络后重试。" };
                }
            }
            throw;
        }
    }

    public Task RefreshRecoveryStatusAsync(CancellationToken cancellationToken)
        => RunExclusiveAsync("正在检查 Discord 绑定状态…", async token =>
        {
            var current = RequireCredentials();
            await RefreshRecoveryProtectionAsync(current, token).ConfigureAwait(false);
            lock (stateLock)
            {
                if (credentials?.Token != current.Token || activeBan is not null) return;
                snapshot = snapshot with { StatusIsError = false, StatusMessage = snapshot.Recovery?.DiscordBound == true
                    ? "Discord 绑定已确认。" : "尚未完成 Discord 绑定，请在 /dact 菜单点「绑定账号」。" };
            }
        }, cancellationToken);

    private async Task RunRecoveryProtectionMonitorAsync(CloudStoredCredentials current, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsCurrentSession(current))
        {
            try
            {
                await RefreshRecoveryProtectionAsync(current, cancellationToken).ConfigureAwait(false);
                if (Snapshot.Recovery is { Protected: true, DiscordBound: true }) return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Retry while the binding gate is open. Heartbeats take over once
                // bound; never log a request containing the original backup key.
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    public Task CreateDiscordLinkCodeAsync(CancellationToken cancellationToken)
        => RunExclusiveAsync("正在生成 Discord 绑定码…", async token =>
        {
            var current = RequireCredentials();
            var code = await apiClient.CreateDiscordLinkCodeAsync(current.Token,
                envelopeService.CreateRecoveryVerifier(current.RecoveryKey), token).ConfigureAwait(false);
            if (sharedAccountStore is not null && sharedAccountStore.Read().Revision != sharedRevision) return;
            lock (stateLock)
            {
                if (credentials?.Token != current.Token || activeBan is not null) return;
                snapshot = snapshot with { DiscordLinkCode = code, StatusIsError = false,
                    StatusMessage = "绑定码已准备好。前往 DACT Discord，在 /dact 菜单点「绑定账号」后粘贴；10 分钟内有效。" };
            }
        }, cancellationToken);
}
