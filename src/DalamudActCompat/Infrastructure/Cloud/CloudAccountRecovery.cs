namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed partial class CloudClientService
{
    internal bool HasRecoveryKeyFor(string username)
    {
        lock (stateLock)
            return new[] { credentials, storedAccount }.Any(account => account is not null &&
                string.Equals(account.Username, username.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(account.RecoveryKey));
    }

    private void ApplyRecoveryStatus(CloudStoredCredentials current, CloudRecoveryStatus? status)
    {
        if (sharedAccountStore is not null && sharedAccountStore.Read().Revision != sharedRevision) return;
        lock (stateLock)
        {
            if (credentials?.Token != current.Token || activeBan is not null) return;
            snapshot = snapshot with { Recovery = status,
                DiscordLinkCode = status?.DiscordBound == true ? null : snapshot.DiscordLinkCode };
        }
    }

    private async Task RunRecoveryProtectionMonitorAsync(CloudStoredCredentials current, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsCurrentSession(current))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var status = (await apiClient.GetAccountStatusAsync(current.Token, timeout.Token).ConfigureAwait(false)).Recovery;
                if (status?.Available == true && !status.Protected)
                {
                    var enrolled = await apiClient.ProtectRecoveryKeyAsync(current.Token, current.RecoveryKey, timeout.Token).ConfigureAwait(false);
                    status = enrolled with { DiscordAvailable = status.DiscordAvailable };
                }
                ApplyRecoveryStatus(current, status);
                if (status?.Protected == true) return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Enrollment is retried without blocking login or replacing its
                // useful status message. Never log requests containing data keys.
            }
            try { await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false); }
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
                    StatusMessage = "绑定码已生成，请复制后发给 Discord 机器人；10 分钟内有效。" };
            }
        }, cancellationToken);
}
