using System.Security.Cryptography;
using DalamudActCompat.Infrastructure.Storage;

namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed record CloudClientSnapshot(
    bool IsSignedIn,
    bool IsBusy,
    string? Username,
    DateTimeOffset? SessionExpiresAt,
    IReadOnlyList<CloudBackupVersion> Backups,
    string StatusMessage,
    bool StatusIsError,
    string? RecoveryKeyToSave,
    PortableConfigurationBackupPreview? RestorePreview,
    string? LastRollbackPath,
    CloudInvitationSummary? Invitations = null,
    string? InvitationKeyToShare = null,
    CloudBanNotice? ActiveBan = null,
    bool HasSavedRecoveryKey = false)
{
    public static CloudClientSnapshot SignedOut(string message = "请登录或注册账号。")
        => new(
            false,
            false,
            null,
            null,
            Array.Empty<CloudBackupVersion>(),
            message,
            false,
            null,
            null,
            null);
}

internal sealed class CloudClientService : IDisposable
{
    private readonly object stateLock = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CloudApiClient apiClient;
    private readonly CloudCredentialStore credentialStore;
    private readonly CloudBanStore banStore;
    private readonly CloudMachineIdentity machineIdentity;
    private readonly CloudKeyEnvelopeService envelopeService;
    private readonly PortableConfigurationBackupService backupService;
    private readonly PluginPaths paths;
    private readonly CancellationTokenSource monitorShutdown = new();
    private readonly object monitorLock = new();
    private readonly List<Task> monitorTasks = [];
    private CancellationTokenSource? sessionMonitorCancellation;
    private CloudStoredCredentials? storedAccount;
    private CloudStoredCredentials? credentials;
    private CloudBanNotice? activeBan;
    private bool persistCurrentAccount;
    private CloudClientSnapshot snapshot;

    public CloudClientService(PluginPaths paths)
        : this(
            paths,
            new CloudApiClient(),
            new CloudCredentialStore(paths.CloudCredentialFile),
            new CloudBanStore(paths.CloudBanFile),
            new CloudMachineIdentity(paths.CloudDeviceFile),
            new CloudKeyEnvelopeService(),
            new PortableConfigurationBackupService())
    {
    }

    internal CloudClientService(
        PluginPaths paths,
        CloudApiClient apiClient,
        CloudCredentialStore credentialStore,
        CloudBanStore banStore,
        CloudMachineIdentity machineIdentity,
        CloudKeyEnvelopeService envelopeService,
        PortableConfigurationBackupService backupService)
    {
        this.paths = paths;
        this.apiClient = apiClient;
        this.credentialStore = credentialStore;
        this.banStore = banStore;
        this.machineIdentity = machineIdentity;
        this.envelopeService = envelopeService;
        this.backupService = backupService;
        storedAccount = TryLoadStoredAccount();
        persistCurrentAccount = storedAccount is not null;
        credentials = storedAccount is { } candidate && candidate.Token.Length > 0 &&
                      candidate.ExpiresAt > DateTimeOffset.UtcNow
            ? candidate
            : null;
        activeBan = TryLoadBan();
        // A protected token is only a candidate for auto-login. DACT must remain locked
        // until the server has validated that token and its current ban state.
        snapshot = CloudClientSnapshot.SignedOut(
            credentials is null ? "请登录或注册账号。" : "正在验证已保存的登录状态…") with
        {
            IsBusy = credentials is not null,
            Username = credentials?.Username,
            SessionExpiresAt = credentials?.ExpiresAt,
            LastRollbackPath = FindLatestRollbackPath(),
            ActiveBan = activeBan,
            HasSavedRecoveryKey = storedAccount is not null,
        };
        monitorTasks.Add(Task.Run(() => RunBanMarkerMonitorAsync(monitorShutdown.Token)));
    }

    public event Action<CloudBanNotice>? BanReceived;

    public event Action<CloudBanNotice?>? BanLifted;

    public CloudBanNotice? ActiveBan
    {
        get
        {
            lock (stateLock)
            {
                return activeBan;
            }
        }
    }

    public CloudClientSnapshot Snapshot
    {
        get
        {
            lock (stateLock)
            {
                return snapshot;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var saved = storedAccount;
        if (saved is null || string.IsNullOrWhiteSpace(saved.Token))
        {
            return;
        }
        if (ActiveBan is not null)
        {
            await RunExclusiveAsync("正在确认封禁状态…", async token =>
            {
                var access = await apiClient.GetAccessStatusAsync(saved.Token, token)
                    .ConfigureAwait(false);
                if (access.Banned)
                {
                    ApplyBan(ToBanNotice(access));
                    return;
                }
                LiftBanAfterServerConfirmation();
                InvalidateSessionPreservingRecoveryKey(
                    "封禁已解除。请重启游戏或重载 DACT，然后重新登录。",
                    isError: false);
            }, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (credentials is null)
        {
            return;
        }
        await RunExclusiveAsync("正在验证登录状态…", async token =>
        {
            var current = RequireCredentials();
            if (current.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                ClearCredentials("登录已过期，请重新登录。", isError: false);
                return;
            }
            await apiClient.ValidateSessionAsync(current.Token, token).ConfigureAwait(false);
            var recoveryWarning = await TryEnableRecoveryResetAsync(current, token)
                .ConfigureAwait(false);
            StartSessionMonitor(current);
            var backups = await apiClient.ListBackupsAsync(current.Token, token)
                .ConfigureAwait(false);
            var invitations = await apiClient.ListInvitationsAsync(current.Token, token)
                .ConfigureAwait(false);
            LiftBanAfterServerConfirmation();
            SetSignedIn(
                current,
                backups,
                invitations,
                recoveryWarning is null
                    ? "云账号已连接。"
                    : $"云账号已连接，但{recoveryWarning}。",
                recoveryWarning is not null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task RegisterAsync(
        string username,
        string password,
        string activationKey,
        bool rememberLogin,
        CancellationToken cancellationToken)
        => RunExclusiveAsync("正在注册账号…", async token =>
        {
            var recoveryKey = backupService.GenerateRecoveryKey();
            var envelope = envelopeService.Create(recoveryKey, password);
            var recoveryVerifier = envelopeService.CreateRecoveryVerifier(recoveryKey);
            var response = await apiClient.RegisterAsync(
                    username.Trim(),
                    password,
                    activationKey.Trim(),
                    machineIdentity.GetDeviceId(),
                    envelope,
                    recoveryVerifier,
                    token)
                .ConfigureAwait(false);
            ValidateReturnedEnvelope(response, password, recoveryKey);
            var authentication = SaveAuthentication(response, recoveryKey, rememberLogin);
            var saved = authentication.Credentials;
            LiftBanAfterServerConfirmation();
            var registrationMessage = authentication.PersistenceWarning is null
                ? "注册成功。请立即抄下恢复密钥；它只在本次注册后显示。"
                : $"注册成功，但{authentication.PersistenceWarning}。请立即抄下恢复密钥；它只在本次注册后显示。";
            // Registration is already committed remotely. Publish the recovery key before
            // the optional version-list refresh so a transient read failure cannot hide it.
            SetSignedIn(
                saved,
                Array.Empty<CloudBackupVersion>(),
                null,
                registrationMessage,
                authentication.PersistenceWarning is not null,
                recoveryKey);
            StartSessionMonitor(saved);
            var backups = await apiClient.ListBackupsAsync(saved.Token, token)
                .ConfigureAwait(false);
            var invitations = await apiClient.ListInvitationsAsync(saved.Token, token)
                .ConfigureAwait(false);
            SetSignedIn(
                saved,
                backups,
                invitations,
                registrationMessage,
                authentication.PersistenceWarning is not null,
                recoveryKey);
        }, cancellationToken);

    public Task LoginAsync(
        string username,
        string password,
        string suppliedRecoveryKey,
        bool rememberLogin,
        CancellationToken cancellationToken)
        => RunExclusiveAsync("正在登录…", async token =>
        {
            var response = await apiClient.LoginAsync(
                    username.Trim(),
                    password,
                    machineIdentity.GetDeviceId(),
                    token)
                .ConfigureAwait(false);
            if (response.KeyEnvelope is null)
            {
                throw new InvalidDataException("账号缺少云端加密密钥，无法安全读取备份。");
            }
            string effectiveRecoveryKey;
            try
            {
                effectiveRecoveryKey = envelopeService.Open(response.KeyEnvelope, password);
            }
            catch (CryptographicException ex)
            {
                effectiveRecoveryKey = ResolveRecoveryKey(username, suppliedRecoveryKey);
                var replacementEnvelope = envelopeService.Create(effectiveRecoveryKey, password);
                if (!string.Equals(
                        replacementEnvelope.KeyId,
                        response.KeyEnvelope.KeyId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "恢复密钥与该账号不匹配，无法重新封装云备份密钥。",
                        ex);
                }
                await apiClient.UpdateKeyEnvelopeAsync(
                        response.Token,
                        replacementEnvelope,
                        token)
                    .ConfigureAwait(false);
            }
            var recoveryWarning = await TryEnableRecoveryResetAsync(
                    response.Token,
                    effectiveRecoveryKey,
                    token)
                .ConfigureAwait(false);
            var authentication = SaveAuthentication(
                response,
                effectiveRecoveryKey,
                rememberLogin);
            var saved = authentication.Credentials;
            LiftBanAfterServerConfirmation(response.WasBanRevoked);
            var loginWarning = CombineWarnings(
                authentication.PersistenceWarning,
                recoveryWarning);
            var loginMessage = loginWarning is null
                ? "登录成功。请选择版本后预览或恢复。"
                : $"登录成功，但{loginWarning}。";
            SetSignedIn(
                saved,
                Array.Empty<CloudBackupVersion>(),
                null,
                loginWarning is null
                    ? "登录成功，正在读取云端版本…"
                    : loginMessage,
                loginWarning is not null);
            StartSessionMonitor(saved);
            var backups = await apiClient.ListBackupsAsync(saved.Token, token)
                .ConfigureAwait(false);
            var invitations = await apiClient.ListInvitationsAsync(saved.Token, token)
                .ConfigureAwait(false);
            SetSignedIn(
                saved,
                backups,
                invitations,
                loginMessage,
                loginWarning is not null);
        }, cancellationToken);

    public Task ResetPasswordAsync(
        string username,
        string resetCode,
        string newPassword,
        string recoveryKey,
        bool rememberLogin,
        CloudPasswordResetMethod method,
        CancellationToken cancellationToken)
        => RunExclusiveAsync("正在重置密码…", async token =>
        {
            var effectiveRecoveryKey = ResolveRecoveryKey(username, recoveryKey);
            var envelope = envelopeService.Create(effectiveRecoveryKey, newPassword);
            var deviceId = machineIdentity.GetDeviceId();
            var response = method == CloudPasswordResetMethod.RecoveryKey
                ? await apiClient.ResetPasswordWithRecoveryAsync(
                        username.Trim(),
                        envelopeService.CreateRecoveryVerifier(effectiveRecoveryKey),
                        newPassword,
                        deviceId,
                        envelope,
                        token)
                    .ConfigureAwait(false)
                : await apiClient.ResetPasswordAsync(
                        username.Trim(),
                        resetCode.Trim(),
                        newPassword,
                        deviceId,
                        envelope,
                        token)
                    .ConfigureAwait(false);
            ValidateReturnedEnvelope(response, newPassword, effectiveRecoveryKey);
            var authentication = SaveAuthentication(
                response,
                effectiveRecoveryKey,
                rememberLogin);
            var saved = authentication.Credentials;
            LiftBanAfterServerConfirmation();
            var resetMessage = authentication.PersistenceWarning is null
                ? "密码已重置，其他设备的旧登录已失效。"
                : $"密码已重置，但{authentication.PersistenceWarning}。";
            SetSignedIn(
                saved,
                Array.Empty<CloudBackupVersion>(),
                null,
                authentication.PersistenceWarning is null
                    ? "密码已重置，正在读取云端版本…"
                    : resetMessage,
                authentication.PersistenceWarning is not null);
            StartSessionMonitor(saved);
            var backups = await apiClient.ListBackupsAsync(saved.Token, token)
                .ConfigureAwait(false);
            var invitations = await apiClient.ListInvitationsAsync(saved.Token, token)
                .ConfigureAwait(false);
            SetSignedIn(
                saved,
                backups,
                invitations,
                resetMessage,
                authentication.PersistenceWarning is not null);
        }, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken)
        => RunExclusiveAsync("正在退出登录…", async token =>
        {
            var current = credentials;
            Exception? localCleanupFailure = null;
            try
            {
                // Revoke local access before the network request so a slow or unavailable
                // server cannot leave DACT usable after the user pressed Sign out.
                ClearCredentials("已退出登录。", isError: false);
            }
            catch (Exception ex)
            {
                localCleanupFailure = ex;
            }
            if (current is not null)
            {
                try
                {
                    await apiClient.LogoutAsync(current.Token, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (localCleanupFailure is not null)
                {
                    throw new AggregateException(localCleanupFailure, ex);
                }
            }
            if (localCleanupFailure is not null)
            {
                throw localCleanupFailure;
            }
        }, cancellationToken);

    public Task RefreshBackupsAsync(CancellationToken cancellationToken)
        => RunExclusiveAsync("正在刷新云端版本…", async token =>
        {
            var current = RequireCredentials();
            var backups = await apiClient.ListBackupsAsync(current.Token, token)
                .ConfigureAwait(false);
            var invitations = await apiClient.ListInvitationsAsync(current.Token, token)
                .ConfigureAwait(false);
            SetSignedIn(
                current,
                backups,
                invitations,
                $"已刷新，共 {backups.Count} 个云端版本。",
                false);
        }, cancellationToken);

    public Task CreateInvitationAsync(
        string inviteeContact,
        CancellationToken cancellationToken)
        => RunExclusiveAsync("正在生成好友激活码…", async token =>
        {
            var current = RequireCredentials();
            var created = await apiClient.CreateInvitationAsync(
                    current.Token,
                    inviteeContact,
                    token)
                .ConfigureAwait(false);
            var invitations = await apiClient.ListInvitationsAsync(current.Token, token)
                .ConfigureAwait(false);
            SetSignedIn(
                current,
                Snapshot.Backups,
                invitations,
                "好友激活码已生成；完整激活码只显示这一次。",
                false,
                invitationKeyToShare: created.ActivationKey);
        }, cancellationToken);

    public Task<bool> UploadAsync(
        string pluginConfigurationDirectory,
        CancellationToken cancellationToken)
        => UploadCoreAsync(
            pluginConfigurationDirectory,
            skipIfUnchanged: false,
            "正在加密并上传配置…",
            cancellationToken);

    public Task<bool> AutoUploadIfChangedAsync(
        string pluginConfigurationDirectory,
        CancellationToken cancellationToken)
        => UploadCoreAsync(
            pluginConfigurationDirectory,
            skipIfUnchanged: true,
            "正在自动同步配置…",
            cancellationToken);

    public bool IsPortableConfigurationPath(string path)
        => backupService.IsIncludedPath(paths.ConfigDirectory, path);

    private Task<bool> UploadCoreAsync(
        string pluginConfigurationDirectory,
        bool skipIfUnchanged,
        string busyMessage,
        CancellationToken cancellationToken)
        => RunExclusiveWithResultAsync(busyMessage, async token =>
        {
            var current = RequireCredentials();
            var temporary = CreateTemporaryCloudPath();
            try
            {
                var exported = await backupService.ExportEncryptedAsync(
                        pluginConfigurationDirectory,
                        temporary,
                        current.RecoveryKey,
                        token)
                    .ConfigureAwait(false);
                var currentBackups = Snapshot.Backups;
                if (skipIfUnchanged &&
                    currentBackups.FirstOrDefault()?.ContentId is { } latestContentId &&
                    latestContentId.Equals(exported.ContentId, StringComparison.Ordinal))
                {
                    SetSignedIn(
                        current,
                        currentBackups,
                        Snapshot.Invitations,
                        "自动同步检查完成：配置没有变化。",
                        false);
                    return false;
                }
                var uploaded = await apiClient.UploadBackupAsync(
                        current.Token,
                        temporary,
                        exported.ContentId,
                        token)
                    .ConfigureAwait(false);
                var backups = await apiClient.ListBackupsAsync(current.Token, token)
                    .ConfigureAwait(false);
                SetSignedIn(
                    current,
                    backups,
                    Snapshot.Invitations,
                    $"上传完成：{uploaded.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}。",
                    false);
                return true;
            }
            finally
            {
                TryDelete(temporary);
            }
        }, cancellationToken);

    public Task PreviewRestoreAsync(string backupId, CancellationToken cancellationToken)
        => RunExclusiveAsync("正在下载并检查恢复内容…", async token =>
        {
            var current = RequireCredentials();
            var backup = RequireBackup(backupId);
            var temporary = CreateTemporaryCloudPath();
            try
            {
                await apiClient.DownloadBackupAsync(current.Token, backup, temporary, token)
                    .ConfigureAwait(false);
                var preview = await backupService.PreviewRestoreAsync(
                        temporary,
                        paths.ConfigDirectory,
                        current.RecoveryKey,
                        token)
                    .ConfigureAwait(false);
                SetSnapshot(Snapshot with
                {
                    IsBusy = true,
                    RestorePreview = preview,
                    StatusMessage = "预览完成；确认后才会覆盖本机配置。",
                    StatusIsError = false,
                });
            }
            finally
            {
                TryDelete(temporary);
            }
        }, cancellationToken);

    public Task<bool> RestoreAsync(string backupId, CancellationToken cancellationToken)
        => RunExclusiveWithResultAsync("正在恢复配置…", async token =>
        {
            var current = RequireCredentials();
            var backup = RequireBackup(backupId);
            var download = CreateTemporaryCloudPath();
            var rollback = CreateRollbackPath("before-cloud-restore");
            try
            {
                await apiClient.DownloadBackupAsync(current.Token, backup, download, token)
                    .ConfigureAwait(false);
                var result = await backupService.RestoreEncryptedAsync(
                        download,
                        paths.ConfigDirectory,
                        rollback,
                        current.RecoveryKey,
                        token)
                    .ConfigureAwait(false);
                SetSnapshot(Snapshot with
                {
                    IsBusy = true,
                    RestorePreview = null,
                    LastRollbackPath = result.RollbackArchivePath,
                    StatusMessage = "恢复完成。请重载 DACT，使全部配置安全生效。",
                    StatusIsError = false,
                });
                return true;
            }
            finally
            {
                TryDelete(download);
            }
        }, cancellationToken);

    public Task<bool> RollbackAsync(CancellationToken cancellationToken)
        => RunExclusiveWithResultAsync("正在回滚上次恢复…", async token =>
        {
            var current = RequireCredentials();
            var source = Snapshot.LastRollbackPath ?? FindLatestRollbackPath();
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                throw new FileNotFoundException("没有可用的本地恢复快照。", source);
            }
            var rollbackOfRollback = CreateRollbackPath("before-manual-rollback");
            var result = await backupService.RestoreEncryptedAsync(
                    source,
                    paths.ConfigDirectory,
                    rollbackOfRollback,
                    current.RecoveryKey,
                    token)
                .ConfigureAwait(false);
            SetSnapshot(Snapshot with
            {
                IsBusy = true,
                RestorePreview = null,
                LastRollbackPath = result.RollbackArchivePath,
                StatusMessage = "已回滚。请重载 DACT，使全部配置安全生效。",
                StatusIsError = false,
            });
            return true;
        }, cancellationToken);

    public void ReportExternalFailure(Exception exception)
        => SetFailure(exception.GetBaseException().Message, true);

    public void Dispose()
    {
        monitorShutdown.Cancel();
        CancelSessionMonitor();
        Task[] tasks;
        lock (monitorLock)
        {
            tasks = monitorTasks.ToArray();
        }
        try
        {
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        sessionMonitorCancellation?.Dispose();
        monitorShutdown.Dispose();
        operationGate.Dispose();
        apiClient.Dispose();
    }

    private async Task RunExclusiveAsync(
        string operationMessage,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (!await TryBeginOperationAsync(operationMessage, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (ActiveBan is null)
            {
                SetFailure("操作已取消。", false);
            }
        }
        catch (Exception ex)
        {
            HandleOperationFailure(ex);
        }
        finally
        {
            FinishOperation();
        }
    }

    private async Task<bool> RunExclusiveWithResultAsync(
        string operationMessage,
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        if (!await TryBeginOperationAsync(operationMessage, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (ActiveBan is null)
            {
                SetFailure("操作已取消。", false);
            }
            return false;
        }
        catch (Exception ex)
        {
            HandleOperationFailure(ex);
            return false;
        }
        finally
        {
            FinishOperation();
        }
    }

    private async Task<bool> TryBeginOperationAsync(
        string message,
        CancellationToken cancellationToken)
    {
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        SetSnapshot(Snapshot with
        {
            IsBusy = true,
            StatusMessage = message,
            StatusIsError = false,
        });
        return true;
    }

    private void FinishOperation()
    {
        SetSnapshot(Snapshot with { IsBusy = false });
        operationGate.Release();
    }

    private void HandleOperationFailure(Exception exception)
    {
        if (exception is CloudApiException apiException &&
            apiException.ToBanNotice() is { } ban)
        {
            ApplyBan(ban);
            return;
        }
        if (exception is CloudApiException unauthorized &&
            unauthorized.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            InvalidateSessionPreservingRecoveryKey("登录已失效，请重新登录。", isError: true);
            return;
        }
        SetFailure(exception.GetBaseException().Message, true);
    }

    private Task<string?> TryEnableRecoveryResetAsync(
        CloudStoredCredentials current,
        CancellationToken cancellationToken)
        => TryEnableRecoveryResetAsync(
            current.Token,
            current.RecoveryKey,
            cancellationToken);

    private async Task<string?> TryEnableRecoveryResetAsync(
        string token,
        string recoveryKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await apiClient.UpdateRecoveryVerifierAsync(
                    token,
                    envelopeService.CreateRecoveryVerifier(recoveryKey),
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            // Login already succeeded remotely. A verifier-enrollment failure should
            // disable only self-service recovery, not hide the usable account session.
            return $"恢复密钥自助改密暂未启用：{ex.GetBaseException().Message}";
        }
    }

    private static string? CombineWarnings(params string?[] warnings)
    {
        var message = string.Join(
            "；",
            warnings.Where(static warning => !string.IsNullOrWhiteSpace(warning)));
        return message.Length == 0 ? null : message;
    }

    private (CloudStoredCredentials Credentials, string? PersistenceWarning) SaveAuthentication(
        CloudAuthenticationResponse response,
        string recoveryKey,
        bool rememberLogin)
    {
        var saved = new CloudStoredCredentials(
            response.User.Username,
            response.Token,
            response.ExpiresAt,
            recoveryKey);
        string? persistenceWarning = null;
        var persisted = false;
        try
        {
            if (rememberLogin)
            {
                credentialStore.Save(saved);
                persisted = true;
            }
            else
            {
                credentialStore.Clear();
            }
        }
        catch (Exception ex)
        {
            // Authentication has already committed remotely. Keep the usable in-memory
            // session and surface the local persistence failure instead of hiding success.
            credentialStore.TryClear();
            persistenceWarning = rememberLogin
                ? $"自动登录状态未能保存：{ex.GetBaseException().Message}"
                : $"旧的自动登录状态未能清除：{ex.GetBaseException().Message}";
        }
        lock (stateLock)
        {
            credentials = saved;
            storedAccount = persisted ? saved : null;
            persistCurrentAccount = persisted;
        }
        return (saved, persistenceWarning);
    }

    private void ValidateReturnedEnvelope(
        CloudAuthenticationResponse response,
        string password,
        string expectedRecoveryKey)
    {
        if (response.KeyEnvelope is null ||
            !string.Equals(
                envelopeService.Open(response.KeyEnvelope, password),
                expectedRecoveryKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("云服务返回了不匹配的账号加密密钥。");
        }
    }

    private string ResolveRecoveryKey(string username, string supplied)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            _ = PortableConfigurationEncryptionService.ParseRecoveryKey(supplied.Trim());
            return supplied.Trim();
        }
        lock (stateLock)
        {
            var candidate = credentials ?? storedAccount;
            if (candidate is not null &&
                candidate.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return candidate.RecoveryKey;
            }
        }
        throw new InvalidOperationException("请输入注册时保存的恢复密钥。旧备份无法由服务器解密。");
    }

    private CloudStoredCredentials RequireCredentials()
    {
        lock (stateLock)
        {
            return credentials
                   ?? throw new InvalidOperationException("请先登录云账号。");
        }
    }

    private CloudBackupVersion RequireBackup(string backupId)
        => Snapshot.Backups.FirstOrDefault(backup => backup.Id == backupId)
           ?? throw new InvalidOperationException("请选择仍存在的云端版本。");

    private CloudStoredCredentials? TryLoadStoredAccount()
    {
        try
        {
            return credentialStore.Load();
        }
        catch
        {
            credentialStore.TryClear();
            return null;
        }
    }

    private void ClearCredentials(string message, bool isError)
    {
        Exception? cleanupFailure = null;
        try
        {
            credentialStore.Clear();
        }
        catch (Exception ex)
        {
            cleanupFailure = ex;
        }
        lock (stateLock)
        {
            credentials = null;
            storedAccount = null;
            persistCurrentAccount = false;
            snapshot = CloudClientSnapshot.SignedOut(message) with
            {
                IsBusy = snapshot.IsBusy,
                StatusIsError = isError,
            };
        }
        CancelSessionMonitor();
        if (cleanupFailure is not null)
        {
            // The server session may already be revoked, so the in-memory state must still
            // become signed out even when Windows cannot remove the protected local file.
            throw new IOException("无法删除本机保存的云账号凭据。", cleanupFailure);
        }
    }

    private void InvalidateSessionPreservingRecoveryKey(string message, bool isError)
    {
        CloudStoredCredentials? recovery;
        bool shouldPersist;
        lock (stateLock)
        {
            recovery = credentials ?? storedAccount;
            shouldPersist = persistCurrentAccount;
            credentials = null;
            if (recovery is not null && shouldPersist)
            {
                storedAccount = recovery with
                {
                    Token = string.Empty,
                    ExpiresAt = DateTimeOffset.MinValue,
                };
            }
            snapshot = CloudClientSnapshot.SignedOut(message) with
            {
                StatusIsError = isError,
                ActiveBan = activeBan,
                HasSavedRecoveryKey = storedAccount is not null,
            };
        }
        if (recovery is not null && shouldPersist)
        {
            try
            {
                credentialStore.Save(recovery with
                {
                    Token = string.Empty,
                    ExpiresAt = DateTimeOffset.MinValue,
                });
            }
            catch (Exception ex)
            {
                // Authentication must still become invalid in memory when Windows cannot
                // refresh the optional recovery-only copy on disk.
                SetFailure($"{message}（无法保存本机恢复密钥：{ex.GetBaseException().Message}）", true);
            }
        }
        CancelSessionMonitor();
    }

    private void StartSessionMonitor(CloudStoredCredentials current)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource next;
        lock (monitorLock)
        {
            previous = sessionMonitorCancellation;
            next = CancellationTokenSource.CreateLinkedTokenSource(monitorShutdown.Token);
            sessionMonitorCancellation = next;
            monitorTasks.Add(Task.Run(
                () => RunSessionMonitorAsync(current, next.Token),
                CancellationToken.None));
        }
        previous?.Cancel();
    }

    private void CancelSessionMonitor()
    {
        lock (monitorLock)
        {
            sessionMonitorCancellation?.Cancel();
        }
    }

    private bool IsCurrentSession(CloudStoredCredentials candidate)
    {
        lock (stateLock)
        {
            // A late 401 from a cancelled monitor belongs to its original token and
            // must not sign out a newer session established on the same client object.
            return credentials is { } current &&
                   string.Equals(current.Token, candidate.Token, StringComparison.Ordinal);
        }
    }

    private bool ShouldApplyBan(
        CloudStoredCredentials monitoredSession,
        CloudBanNotice notice)
        => notice.BanType == "device" || IsCurrentSession(monitoredSession);

    private async Task RunSessionMonitorAsync(
        CloudStoredCredentials current,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(
                RunEventMonitorAsync(current, cancellationToken),
                RunHeartbeatMonitorAsync(current, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task RunEventMonitorAsync(
        CloudStoredCredentials current,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await apiClient.ListenForBanEventsAsync(
                        current.Token,
                        notice =>
                        {
                            if (ShouldApplyBan(current, notice))
                            {
                                ApplyBan(notice);
                            }
                            return Task.CompletedTask;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (CloudApiException ex) when (ex.ToBanNotice() is { } ban)
            {
                if (ShouldApplyBan(current, ban))
                {
                    ApplyBan(ban);
                }
                return;
            }
            catch (CloudApiException ex) when (
                ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                if (IsCurrentSession(current))
                {
                    InvalidateSessionPreservingRecoveryKey(
                        "登录已失效，请重新登录。",
                        isError: true);
                }
                return;
            }
            catch
            {
                // A dropped real-time connection is expected on network changes;
                // heartbeat validation remains the authoritative fallback.
            }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunHeartbeatMonitorAsync(
        CloudStoredCredentials current,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            try
            {
                await apiClient.ValidateSessionAsync(current.Token, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CloudApiException ex) when (ex.ToBanNotice() is { } ban)
            {
                if (ShouldApplyBan(current, ban))
                {
                    ApplyBan(ban);
                }
                return;
            }
            catch (CloudApiException ex) when (
                ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                if (IsCurrentSession(current))
                {
                    InvalidateSessionPreservingRecoveryKey(
                        "登录已失效，请重新登录。",
                        isError: true);
                }
                return;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Temporary server failures do not erase a valid local session. The
                // next heartbeat and SSE reconnect retry independently.
            }
        }
    }

    private async Task RunBanMarkerMonitorAsync(CancellationToken cancellationToken)
    {
        var nextServerCheck = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            CloudBanNotice? ban;
            CloudStoredCredentials? account;
            lock (stateLock)
            {
                ban = activeBan;
                account = credentials ?? storedAccount;
            }
            if (ban is not null)
            {
                try
                {
                    banStore.EnsurePresent(ban);
                }
                catch (Exception ex)
                {
                    SetFailure($"无法恢复本地封禁标记：{ex.GetBaseException().Message}", true);
                }

                if (account is not null && account.Token.Length > 0 &&
                    DateTimeOffset.UtcNow >= nextServerCheck)
                {
                    nextServerCheck = DateTimeOffset.UtcNow.AddSeconds(15);
                    try
                    {
                        var access = await apiClient.GetAccessStatusAsync(
                                account.Token,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (access.Banned)
                        {
                            ApplyBan(ToBanNotice(access));
                        }
                        else if (access.WasBanRevoked)
                        {
                            LiftBanAfterServerConfirmation();
                            InvalidateSessionPreservingRecoveryKey(
                                "封禁已解除。请重启游戏或重载 DACT，然后重新登录。",
                                isError: false);
                        }
                    }
                    catch (CloudApiException ex) when (
                        ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        // Only an authenticated server confirmation can remove a local
                        // ban marker; a missing token leaves it fail-closed.
                    }
                    catch when (!cancellationToken.IsCancellationRequested)
                    {
                    }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyBan(CloudBanNotice notice)
    {
        Exception? markerFailure = null;
        try
        {
            banStore.EnsurePresent(notice);
        }
        catch (Exception ex)
        {
            markerFailure = ex;
        }
        var changed = false;
        lock (stateLock)
        {
            changed = activeBan != notice;
            activeBan = notice;
            var message = FormatBanMessage(notice);
            if (markerFailure is not null)
            {
                // Server authority wins even if the redundant disk marker cannot be
                // persisted; the current process must still shut every DACT capability down.
                message += $"（本地封禁标记写入失败：{markerFailure.GetBaseException().Message}）";
            }
            snapshot = CloudClientSnapshot.SignedOut(message) with
            {
                StatusIsError = true,
                ActiveBan = notice,
                HasSavedRecoveryKey = storedAccount is not null,
            };
        }
        CancelSessionMonitor();
        if (changed)
        {
            BanReceived?.Invoke(notice);
        }
    }

    private void LiftBanAfterServerConfirmation(bool serverReportedRevocation = false)
    {
        CloudBanNotice? liftedBan = null;
        var shouldNotify = serverReportedRevocation;
        lock (stateLock)
        {
            if (activeBan is not null)
            {
                liftedBan = activeBan;
                banStore.Clear();
                activeBan = null;
                snapshot = snapshot with { ActiveBan = null };
                shouldNotify = true;
            }
        }
        if (shouldNotify)
        {
            BanLifted?.Invoke(liftedBan);
        }
    }

    private CloudBanNotice? TryLoadBan()
    {
        try
        {
            return banStore.Load();
        }
        catch
        {
            if (!File.Exists(paths.CloudBanFile) && !Directory.Exists(paths.CloudBanFile))
            {
                return null;
            }
            DateTimeOffset detectedAt;
            try
            {
                detectedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(paths.CloudBanFile));
            }
            catch
            {
                detectedAt = DateTimeOffset.UtcNow;
            }
            // Corruption is not proof that a server-issued marker is safe to ignore. Keep
            // the client fail-closed until a preserved authenticated session confirms unban.
            return new CloudBanNotice(
                "local_ban_marker",
                "unknown",
                detectedAt,
                null,
                null);
        }
    }

    private static CloudBanNotice ToBanNotice(CloudAccessStatus access)
        => access.BannedAt is { } bannedAt && !string.IsNullOrWhiteSpace(access.BanType)
            ? new CloudBanNotice(
                access.BanType == "device" ? "device_banned" : "account_banned",
                access.BanType,
                bannedAt,
                access.BanExpiresAt,
                access.BanReason)
            : throw new InvalidDataException("Cloud access status omitted ban details.");

    private static string FormatBanMessage(CloudBanNotice notice)
    {
        var subject = string.Equals(notice.BanType, "device", StringComparison.Ordinal)
            ? "您的账号及关联机器已经被封禁"
            : "您的账号已经被封禁";
        var message = $"{subject}（封禁时间：{notice.BannedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}）";
        return string.IsNullOrWhiteSpace(notice.BanReason)
            ? message
            : $"{message} 封禁原因：{notice.BanReason}";
    }

    private void SetSignedIn(
        CloudStoredCredentials current,
        IReadOnlyList<CloudBackupVersion> backups,
        CloudInvitationSummary? invitations,
        string message,
        bool isError,
        string? recoveryKeyToSave = null,
        string? invitationKeyToShare = null)
    {
        var rollbackPath = FindLatestRollbackPath();
        lock (stateLock)
        {
            // A response from an operation started before the live ban event must never
            // resurrect the signed-in UI after access has already been revoked.
            if (activeBan is not null)
            {
                return;
            }
            snapshot = new CloudClientSnapshot(
                true,
                true,
                current.Username,
                current.ExpiresAt,
                backups.ToArray(),
                message,
                isError,
                recoveryKeyToSave,
                null,
                rollbackPath,
                invitations,
                invitationKeyToShare,
                null,
                storedAccount is not null);
        }
    }

    private void SetFailure(string message, bool isError)
        => SetSnapshot(Snapshot with { StatusMessage = message, StatusIsError = isError });

    private void SetSnapshot(CloudClientSnapshot value)
    {
        lock (stateLock)
        {
            snapshot = value;
        }
    }

    private string? FindLatestRollbackPath()
    {
        try
        {
            return Directory.Exists(paths.CloudRollbackDirectory)
                ? Directory.EnumerateFiles(paths.CloudRollbackDirectory, "*.dactcloud")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private string CreateRollbackPath(string label)
    {
        Directory.CreateDirectory(paths.CloudRollbackDirectory);
        return Path.Combine(
            paths.CloudRollbackDirectory,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{label}-{Guid.NewGuid():N}.dactcloud");
    }

    private static string CreateTemporaryCloudPath()
        => Path.Combine(Path.GetTempPath(), $"dact-cloud-{Guid.NewGuid():N}.dactcloud");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The authenticated archive remains unreadable without the local key;
            // cleanup failure must not hide the real cloud operation result.
        }
    }
}
