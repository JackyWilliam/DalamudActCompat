using Raynording.Accounts;

namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed partial class CloudClientService
{
    private readonly SharedAccountStore? sharedAccountStore;
    private long sharedRevision;
    private long operationSharedRevision;
    private SharedAuthenticationState? operationSharedAuthentication;
    private int sharedMonitorStarted;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (sharedAccountStore is null)
        {
            await InitializeLegacyAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            var shared = sharedAccountStore.Read();
            if (shared.Revision == 0)
            {
                // Migrate existing auto-login only after the server validates it.
                await InitializeLegacyAsync(cancellationToken).ConfigureAwait(false);
                if (Snapshot.IsSignedIn && credentials is { } current)
                    Interlocked.Exchange(ref sharedRevision,
                        sharedAccountStore.Publish(0, ToSharedAccount(current), persistCurrentAccount).Revision);
            }
            else await ImportSharedAccountAsync(shared, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DropLocalSharedSession(SharedAccountFailureMessage(ex));
        }
        finally
        {
            // A remembered legacy token marks startup busy before the shared file
            // is read. A failed read never enters FinishOperation; release that
            // startup state without clearing another operation's busy flag.
            if (operationGate.Wait(0))
            {
                try { SetSnapshot(Snapshot with { IsBusy = false }); }
                finally { operationGate.Release(); }
            }
            if (Interlocked.Exchange(ref sharedMonitorStarted, 1) == 0)
                lock (monitorLock) monitorTasks.Add(Task.Run(() => MonitorSharedAccountAsync(monitorShutdown.Token)));
        }
    }

    private async Task MonitorSharedAccountAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            try
            {
                var shared = sharedAccountStore!.Read();
                if (shared.Revision != Interlocked.Read(ref sharedRevision) ||
                    (!Snapshot.IsSignedIn && shared.UsableAccount is not null))
                    await ImportSharedAccountAsync(shared, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { DropLocalSharedSession(SharedAccountFailureMessage(ex)); }
        }
    }

    private static string SharedAccountFailureMessage(Exception error)
        => SharedAccountStore.IsUnreadable(error)
            ? "本机共用登录状态已失效，请重新登录。"
            : "共用登录同步失败：" + error.GetBaseException().Message;

    private async Task ImportSharedAccountAsync(SharedAccountState shared, CancellationToken cancellationToken)
    {
        // The existing operation gate also protects migration from backup/login tasks.
        if (!await TryBeginOperationAsync("正在同步共用账号…", cancellationToken).ConfigureAwait(false)) return;
        try
        {
            DropLocalSharedSession("正在验证共用账号…");
            if (shared.UsableAccount is not { } account)
            {
                sharedAccountStore!.IfCurrent(shared.Revision, () =>
                {
                    Interlocked.Exchange(ref sharedRevision, shared.Revision);
                    DropLocalSharedSession("已同步退出登录，请在 DACT 或 RPets 登录。");
                    credentialStore.Clear();
                });
                return;
            }
            await apiClient.ValidateSharedSessionAsync(account.Token, account.Username, cancellationToken).ConfigureAwait(false);
            var current = new CloudStoredCredentials(account.Username, account.Token, account.ExpiresAt, account.RecoveryKey);
            if (!sharedAccountStore!.IfCurrent(shared.Revision, () =>
            {
                LiftBanAfterServerConfirmation();
                lock (stateLock)
                {
                    credentials = current;
                    storedAccount = current;
                    persistCurrentAccount = shared.Remember;
                    friendsSessionGeneration++;
                    Interlocked.Exchange(ref sharedRevision, shared.Revision);
                }
                // Keep old recovery/backup compatibility while shared state owns login.
                string? warning = null;
                try
                {
                    if (shared.Remember) credentialStore.Save(current);
                    else credentialStore.Clear();
                }
                catch { warning = "已同步登录，但旧格式本机恢复副本暂时无法保存。"; }
                SetSignedIn(current, [], null, warning ?? "已同步登录共用账号。", warning is not null);
            })) return;
            StartSessionMonitor(current);
            var backups = await apiClient.ListBackupsAsync(current.Token, cancellationToken).ConfigureAwait(false);
            var invitations = await apiClient.ListInvitationsAsync(current.Token, cancellationToken).ConfigureAwait(false);
            SetSignedIn(current, backups, invitations, "已同步登录共用账号。", false);
        }
        catch (CloudApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            // Publish only against the revision which failed verification.
            try { Interlocked.Exchange(ref sharedRevision, sharedAccountStore!.Publish(shared.Revision, null, false).Revision); }
            catch (OperationCanceledException) { }
            if (ex.ToBanNotice() is { } ban) ApplyBan(ban);
            else DropLocalSharedSession("登录已失效，请重新登录。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { SetFailure("共用账号连接失败，将自动重试：" + ex.GetBaseException().Message, true); }
        finally { FinishOperation(); }
    }

    private void DropLocalSharedSession(string message)
    {
        CancelSessionMonitor();
        lock (stateLock)
        {
            credentials = null;
            storedAccount = null;
            persistCurrentAccount = false;
            friendsSessionGeneration++;
            snapshot = CloudClientSnapshot.SignedOut(message) with { ActiveBan = activeBan, IsBusy = snapshot.IsBusy };
        }
    }

    private void ClearSharedAccount(CloudStoredCredentials? current)
    {
        if (sharedAccountStore is null || current is null) return;
        var shared = sharedAccountStore.Read();
        if (shared.Account?.Token != current.Token) return;
        try { Interlocked.Exchange(ref sharedRevision, sharedAccountStore.Publish(shared.Revision, null, false).Revision); }
        catch (OperationCanceledException) { /* A newer login already owns the shared state. */ }
    }

    private static SharedAccount ToSharedAccount(CloudStoredCredentials current)
        => new(current.Username, current.Token, current.ExpiresAt, current.RecoveryKey);
}
