using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed record CloudApiUser(string Id, string Username);

internal sealed record CloudAuthenticationResponse(
    string Token,
    string TokenType,
    DateTimeOffset ExpiresAt,
    CloudApiUser User,
    CloudKeyEnvelope? KeyEnvelope);

internal sealed record CloudBackupVersion(
    string Id,
    DateTimeOffset CreatedAt,
    long SizeBytes,
    string Sha256);

internal sealed record CloudAccessStatus(
    bool Banned,
    bool SessionActive,
    bool WasBanRevoked,
    string? BanType,
    DateTimeOffset? BannedAt,
    DateTimeOffset? BanExpiresAt,
    string? BanReason);

internal sealed record CloudInvitation(
    string Id,
    string CodeHint,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? UsedAt);

internal sealed record CloudInvitationSummary(
    int Quota,
    int Used,
    int Remaining,
    IReadOnlyList<CloudInvitation> Invitations);

internal sealed record CloudCreatedInvitation(
    string Id,
    string ActivationKey,
    string CodeHint,
    string Name,
    string Status,
    DateTimeOffset CreatedAt);

internal sealed class CloudApiException(
    HttpStatusCode statusCode,
    string code,
    string message,
    string? banType = null,
    DateTimeOffset? bannedAt = null,
    DateTimeOffset? banExpiresAt = null,
    string? banReason = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public string? BanType { get; } = banType;

    public DateTimeOffset? BannedAt { get; } = bannedAt;

    public DateTimeOffset? BanExpiresAt { get; } = banExpiresAt;

    public string? BanReason { get; } = banReason;

    public CloudBanNotice? ToBanNotice()
        => Code is "account_banned" or "device_banned" &&
           !string.IsNullOrWhiteSpace(BanType) &&
           BannedAt is { } effectiveBannedAt
            ? new CloudBanNotice(
                Code,
                BanType,
                effectiveBannedAt,
                BanExpiresAt,
                BanReason)
            : null;
}

internal sealed class CloudApiClient : IDisposable
{
    internal static readonly Uri DefaultBaseAddress =
        new("https://admin.localhost2019.com/");
    private const long MaximumBackupBytes = 65L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;

    public CloudApiClient()
        : this(new HttpClient { BaseAddress = DefaultBaseAddress }, ownsClient: true)
    {
    }

    internal CloudApiClient(HttpClient httpClient, bool ownsClient = false)
    {
        this.httpClient = httpClient;
        this.ownsClient = ownsClient;
        this.httpClient.Timeout = TimeSpan.FromSeconds(90);
    }

    public Task<CloudAuthenticationResponse> RegisterAsync(
        string username,
        string password,
        string activationKey,
        string deviceId,
        CloudKeyEnvelope keyEnvelope,
        CancellationToken cancellationToken)
        => SendJsonAsync<CloudAuthenticationResponse>(
            HttpMethod.Post,
            "api/v1/auth/register",
            new { username, password, activationKey, deviceId, keyEnvelope },
            null,
            cancellationToken);

    public Task<CloudAuthenticationResponse> LoginAsync(
        string username,
        string password,
        string deviceId,
        CancellationToken cancellationToken)
        => SendJsonAsync<CloudAuthenticationResponse>(
            HttpMethod.Post,
            "api/v1/auth/login",
            new { username, password, deviceId },
            null,
            cancellationToken);

    public Task<CloudAuthenticationResponse> ResetPasswordAsync(
        string username,
        string resetCode,
        string newPassword,
        string deviceId,
        CloudKeyEnvelope keyEnvelope,
        CancellationToken cancellationToken)
        => SendJsonAsync<CloudAuthenticationResponse>(
            HttpMethod.Post,
            "api/v1/auth/reset-password",
            new { username, resetCode, newPassword, deviceId, keyEnvelope },
            null,
            cancellationToken);

    public async Task ValidateSessionAsync(string token, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/auth/me", token);
        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<CloudAccessStatus> GetAccessStatusAsync(
        string token,
        CancellationToken cancellationToken)
        => SendJsonAsync<CloudAccessStatus>(
            HttpMethod.Get,
            "api/v1/auth/access-status",
            null,
            token,
            cancellationToken);

    public Task UpdateKeyEnvelopeAsync(
        string token,
        CloudKeyEnvelope keyEnvelope,
        CancellationToken cancellationToken)
        => SendJsonAsync<KeyEnvelopeResponse>(
            HttpMethod.Put,
            "api/v1/auth/key-envelope",
            new { keyEnvelope },
            token,
            cancellationToken);

    public async Task ListenForBanEventsAsync(
        string token,
        Func<CloudBanNotice, Task> onBan,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/auth/events", token);
        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line[7..];
                continue;
            }
            if (eventName == "ban" && line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var notice = JsonSerializer.Deserialize<CloudBanNotice>(line[6..], JsonOptions)
                             ?? throw new InvalidDataException(
                                 "Cloud service returned an empty ban event.");
                await onBan(notice).ConfigureAwait(false);
                return;
            }
            if (line.Length == 0)
            {
                eventName = null;
            }
        }
    }

    public async Task LogoutAsync(string token, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/auth/logout", token);
        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudBackupVersion>> ListBackupsAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var response = await SendJsonAsync<BackupListResponse>(
                HttpMethod.Get,
                "api/v1/backups",
                null,
                token,
                cancellationToken)
            .ConfigureAwait(false);
        return response.Backups;
    }

    public Task<CloudInvitationSummary> ListInvitationsAsync(
        string token,
        CancellationToken cancellationToken)
        => SendJsonAsync<CloudInvitationSummary>(
            HttpMethod.Get,
            "api/v1/invitations",
            null,
            token,
            cancellationToken);

    public Task<CloudCreatedInvitation> CreateInvitationAsync(
        string token,
        CancellationToken cancellationToken)
        => SendJsonAsync<CloudCreatedInvitation>(
            HttpMethod.Post,
            "api/v1/invitations",
            null,
            token,
            cancellationToken);

    public async Task<CloudBackupVersion> UploadBackupAsync(
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        if (input.Length <= 0 || input.Length > MaximumBackupBytes)
        {
            throw new InvalidDataException("Encrypted cloud backup has an invalid size.");
        }
        using var content = new StreamContent(input);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = input.Length;
        using var request = CreateRequest(HttpMethod.Post, "api/v1/backups", token);
        request.Content = content;
        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<CloudBackupVersion>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DownloadBackupAsync(
        string token,
        CloudBackupVersion backup,
        string destination,
        CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(destination);
        var temporary = $"{target}.tmp-{Guid.NewGuid():N}";
        using var request = CreateRequest(
            HttpMethod.Get,
            $"api/v1/backups/{Uri.EscapeDataString(backup.Id)}",
            token);
        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is <= 0 or > MaximumBackupBytes ||
            declaredLength != backup.SizeBytes)
        {
            throw new InvalidDataException("Cloud backup length does not match its metadata.");
        }
        var expectedHash = response.Headers.TryGetValues("X-Content-SHA256", out var values)
            ? values.SingleOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(expectedHash) ||
            !expectedHash.Equals(backup.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Cloud backup hash header does not match its metadata.");
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("Download path has no parent directory."));
        long total = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaximumBackupBytes)
                    {
                        throw new InvalidDataException("Cloud backup exceeds the client size limit.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (total != backup.SizeBytes ||
                !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Downloaded cloud backup failed integrity checking.");
            }
            File.Move(temporary, target);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            httpClient.Dispose();
        }
    }

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string relativeUri,
        object? body,
        string? token,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, relativeUri, token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativeUri,
        string? token)
    {
        var request = new HttpRequestMessage(method, relativeUri);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        try
        {
            var error = await ReadJsonAsync<ApiError>(response, cancellationToken)
                .ConfigureAwait(false);
            throw new CloudApiException(
                response.StatusCode,
                error.Error,
                error.Message,
                error.BanType,
                error.BannedAt,
                error.BanExpiresAt,
                error.BanReason);
        }
        catch (CloudApiException)
        {
            throw;
        }
        catch
        {
            throw new CloudApiException(
                response.StatusCode,
                "http_error",
                $"云服务请求失败（HTTP {(int)response.StatusCode}）。");
        }
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        => await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
               .ConfigureAwait(false)
           ?? throw new InvalidDataException("Cloud service returned an empty JSON response.");

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
            // A failed temporary cleanup must not replace the integrity error.
        }
    }

    private sealed record BackupListResponse(IReadOnlyList<CloudBackupVersion> Backups);

    private sealed record KeyEnvelopeResponse(CloudKeyEnvelope KeyEnvelope);

    private sealed record ApiError(
        string Error,
        string Message,
        string? BanType,
        DateTimeOffset? BannedAt,
        DateTimeOffset? BanExpiresAt,
        string? BanReason);
}
