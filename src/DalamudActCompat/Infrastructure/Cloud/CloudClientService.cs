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
    string? LastRollbackPath)
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
    private readonly CloudMachineIdentity machineIdentity;
    private readonly CloudKeyEnvelopeService envelopeService;
    private readonly PortableConfigurationBackupService backupService;
    private readonly PluginPaths paths;
    private CloudStoredCredentials? credentials;
    private CloudClientSnapshot snapshot;

    public CloudClientService(PluginPaths paths)
        : this(
            paths,
            new CloudApiClient(),
            new CloudCredentialStore(paths.CloudCredentialFile),
            new CloudMachineIdentity(paths.CloudDeviceFile),
            new CloudKeyEnvelopeService(),
            new PortableConfigurationBackupService())
    {
    }

    internal CloudClientService(
        PluginPaths paths,
        CloudApiClient apiClient,
        CloudCredentialStore credentialStore,
        CloudMachineIdentity machineIdentity,
        CloudKeyEnvelopeService envelopeService,
        PortableConfigurationBackupService backupService)
    {
        this.paths = paths;
        this.apiClient = apiClient;
        this.credentialStore = credentialStore;
        this.machineIdentity = machineIdentity;
        this.envelopeService = envelopeService;
        this.backupService = backupService;
        credentials = TryLoadCredentials();
        snapshot = credentials is null
            ? CloudClientSnapshot.SignedOut()
            : new CloudClientSnapshot(
                true,
                false,
                credentials.Username,
                credentials.ExpiresAt,
                Array.Empty<CloudBackupVersion>(),
                "正在验证已保存的登录状态…",
                false,
                null,
                null,
                FindLatestRollbackPath());
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
            var backups = await apiClient.ListBackupsAsync(current.Token, token)
                .ConfigureAwait(false);
            SetSignedIn(current, backups, "云账号已连接。", false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task RegisterAsync(
        string username,
        string password,
        string activationKey,
        CancellationToken cancellationToken)
        => RunExclusiveAsync("正在注册账号…", async token =>
        {
            var recoveryKey = backupService.GenerateRecoveryKey();
            var envelope = envelopeService.Create(recoveryKey, password);
            var response = await apiClient.RegisterAsync(
                    username.Trim(),
                    password,
                    activationKey.Trim(),
                    machineIdentity.GetDeviceId(),
                    envelope,
                    token)
                .ConfigureAwait(false);
            ValidateReturnedEnvelope(response, password, recoveryKey);
            var saved = SaveAuthentication(response, recoveryKey);
            // Registration is already committed remotely. Publish the recovery key before
            // the optional version-list refresh so a transient read failure cannot hide it.
            SetSignedIn(
                saved,
                Array.Empty<CloudBackupVersion>(),
                "注册成功。请立即抄下恢复密钥；它只在本次注册后显示。",
                false,
                recoveryKey);
            var backups = await apiClient.ListBackupsAsync(saved.Token, token)
                .ConfigureAwait(false);
            SetSignedIn(
                saved,
                backups,
                "注册成功。请立即抄下恢复密钥；它只在本次注册后显示。",
                false,
                recoveryKey);
        }, cancellationToken);

    public Task LoginAsync(
        string username,
        string password,
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
            string recoveryKey;
            try
            {
                recoveryKey = envelopeService.Open(response.KeyEnvelope, password);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException("账号加密密钥校验失败，请联系管理员。", ex);
            }
            var saved = SaveAuthentication(response, recoveryKey);
            SetSignedIn(
                saved,
                Array.Empty<CloudBackupVersion>(),
                "登录成功，正在读取云端版本…",
                false);
            var backups = await apiClient.ListBackupsAsync(saved.Token, token)
                .ConfigureAwait(false);
            SetSignedIn(saved, backups, "登录成功。请选择版本后预览或恢复。", false);
        }, cancellationToken);

    public Task ResetPasswordAsync(
        string username,
        string resetCode,
        string newPassword,
        string recoveryKey,
        CancellationToken cancellationToken)
        => RunExclusiveAsync("正在重置密码…", async token =>
        {
            var effectiveRecoveryKey = ResolveRecoveryKey(username, recoveryKey);
            var envelope = envelopeService.Create(effectiveRecoveryKey, newPassword);
            var response = await apiClient.ResetPasswordAsync(
                    username.Trim(),
                    resetCode.Trim(),
                    newPassword,
                    machineIdentity.GetDeviceId(),
                    envelope,
                    token)
                .ConfigureAwait(false);
            ValidateReturnedEnvelope(response, newPassword, effectiveRecoveryKey);
            var saved = SaveAuthentication(response, effectiveRecoveryKey);
            SetSignedIn(
                saved,
                Array.Empty<CloudBackupVersion>(),
                "密码已重置，正在读取云端版本…",
                false);
            var backups = await apiClient.ListBackupsAsync(saved.Token, token)
                .ConfigureAwait(false);
            SetSignedIn(saved, backups, "密码已重置，其他设备的旧登录已失效。", false);
        }, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken)
        => RunExclusiveAsync("正在退出登录…", async token =>
        {
            var current = credentials;
            if (current is not null)
            {
                try
                {
                    await apiClient.LogoutAsync(current.Token, token).ConfigureAwait(false);
                }
                finally
                {
                    ClearCredentials("已退出登录。", isError: false);
                }
            }
        }, cancellationToken);

    public Task RefreshBackupsAsync(CancellationToken cancellationToken)
        => RunExclusiveAsync("正在刷新云端版本…", async token =>
        {
            var current = RequireCredentials();
            var backups = await apiClient.ListBackupsAsync(current.Token, token)
                .ConfigureAwait(false);
            SetSignedIn(current, backups, $"已刷新，共 {backups.Count} 个云端版本。", false);
        }, cancellationToken);

    public Task<bool> UploadAsync(
        string pluginConfigurationDirectory,
        CancellationToken cancellationToken)
        => RunExclusiveWithResultAsync("正在加密并上传配置…", async token =>
        {
            var current = RequireCredentials();
            var temporary = CreateTemporaryCloudPath();
            try
            {
                await backupService.ExportEncryptedAsync(
                        pluginConfigurationDirectory,
                        temporary,
                        current.RecoveryKey,
                        token)
                    .ConfigureAwait(false);
                var uploaded = await apiClient.UploadBackupAsync(current.Token, temporary, token)
                    .ConfigureAwait(false);
                var backups = await apiClient.ListBackupsAsync(current.Token, token)
                    .ConfigureAwait(false);
                SetSignedIn(
                    current,
                    backups,
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
            SetFailure("操作已取消。", false);
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
            SetFailure("操作已取消。", false);
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
        if (exception is CloudApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
        {
            ClearCredentials("登录已失效，请重新登录。", isError: true);
            return;
        }
        SetFailure(exception.GetBaseException().Message, true);
    }

    private CloudStoredCredentials SaveAuthentication(
        CloudAuthenticationResponse response,
        string recoveryKey)
    {
        var saved = new CloudStoredCredentials(
            response.User.Username,
            response.Token,
            response.ExpiresAt,
            recoveryKey);
        credentialStore.Save(saved);
        lock (stateLock)
        {
            credentials = saved;
        }
        return saved;
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
            if (credentials is not null &&
                credentials.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return credentials.RecoveryKey;
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

    private CloudStoredCredentials? TryLoadCredentials()
    {
        try
        {
            var loaded = credentialStore.Load();
            if (loaded?.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                credentialStore.TryClear();
                return null;
            }
            return loaded;
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
            snapshot = CloudClientSnapshot.SignedOut(message) with
            {
                IsBusy = snapshot.IsBusy,
                StatusIsError = isError,
            };
        }
        if (cleanupFailure is not null)
        {
            // The server session may already be revoked, so the in-memory state must still
            // become signed out even when Windows cannot remove the protected local file.
            throw new IOException("无法删除本机保存的云账号凭据。", cleanupFailure);
        }
    }

    private void SetSignedIn(
        CloudStoredCredentials current,
        IReadOnlyList<CloudBackupVersion> backups,
        string message,
        bool isError,
        string? recoveryKeyToSave = null)
        => SetSnapshot(new CloudClientSnapshot(
            true,
            true,
            current.Username,
            current.ExpiresAt,
            backups.ToArray(),
            message,
            isError,
            recoveryKeyToSave,
            null,
            FindLatestRollbackPath()));

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
