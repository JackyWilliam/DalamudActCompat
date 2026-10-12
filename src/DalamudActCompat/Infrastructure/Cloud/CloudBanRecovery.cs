namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed partial class CloudClientService
{
    public void BeginBanRecheck(string username, string password)
    {
        // Ban enforcement cancels ordinary cloud operations. This narrowly scoped
        // verification lives until plugin disposal and cannot restart native services.
        lock (monitorLock)
        {
            if (monitorShutdown.IsCancellationRequested) return;
            monitorTasks.RemoveAll(task => task.IsCompleted);
            monitorTasks.Add(Task.Run(() => RecheckBanAsync(username, password, monitorShutdown.Token)));
        }
    }

    internal async Task RecheckBanAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        string? verificationToken = null;
        try
        {
            var originalBan = ActiveBan;
            if (originalBan is null) return;
            UpdateSnapshot(value => value with { IsBusy = true, StatusMessage = "正在验证账号和解封状态…", StatusIsError = false });
            // The normal login endpoint checks the password, account/cascade bans,
            // and this machine. An expired or missing old token is not required.
            var login = await apiClient.LoginAsync(username.Trim(), password,
                machineIdentity.GetDeviceId(), cancellationToken).ConfigureAwait(false);
            verificationToken = login.Token;
            if (string.IsNullOrWhiteSpace(verificationToken))
                throw new InvalidDataException("服务器未返回有效验证会话。");
            var access = await apiClient.GetAccessStatusAsync(verificationToken, cancellationToken).ConfigureAwait(false);
            if (access.Banned) { ApplyBan(ToBanNotice(access)); return; }
            if (!access.SessionActive) throw new InvalidDataException("验证会话已失效，请重试。");
            lock (stateLock)
            {
                // A newer ban arriving during verification must win over this response.
                if (activeBan != originalBan) return;
                LiftBanAfterServerConfirmation();
                snapshot = snapshot with { IsSignedIn = false, StatusMessage = "封禁已解除。请重启游戏或重载 DACT，然后重新登录。", StatusIsError = false };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ex is CloudApiException apiError && apiError.ToBanNotice() is { } ban) ApplyBan(ban);
            else SetFailure(ex.GetBaseException().Message, true);
        }
        finally
        {
            // Verification never replaces shared login or the saved recovery key.
            // Discard its short-lived session even when marker removal fails.
            if (!string.IsNullOrWhiteSpace(verificationToken))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await apiClient.LogoutAsync(verificationToken, cleanup.Token).ConfigureAwait(false); }
                catch { /* Cleanup failure cannot turn a rejected verification into success. */ }
            }
            FinishOperation();
        }
    }
}
