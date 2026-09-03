using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed record CloudBanNotice(
    string Code,
    string BanType,
    DateTimeOffset BannedAt,
    DateTimeOffset? BanExpiresAt,
    string? BanReason);

internal sealed class CloudBanStore(string path)
{
    private const int CurrentFormat = 1;
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("DalamudActCompat.CloudBan.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string path = Path.GetFullPath(path);

    public CloudBanNotice? Load()
    {
        if (!File.Exists(path))
        {
            // A directory or another non-file entry at the marker path is not absence.
            // Treat it as tampering so replacing the marker cannot make startup fail open.
            if (Directory.Exists(path))
            {
                throw new InvalidDataException("Saved cloud ban state is not a file.");
            }
            return null;
        }

        var protectedBytes = File.ReadAllBytes(path);
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            var document = JsonSerializer.Deserialize<BanDocument>(plaintext, JsonOptions);
            if (document is null || document.Format != CurrentFormat ||
                string.IsNullOrWhiteSpace(document.Code) ||
                string.IsNullOrWhiteSpace(document.BanType) ||
                document.BannedAt == default)
            {
                throw new InvalidDataException("Saved cloud ban state is invalid.");
            }
            return new CloudBanNotice(
                document.Code,
                document.BanType,
                document.BannedAt,
                document.BanExpiresAt,
                string.IsNullOrWhiteSpace(document.BanReason) ? null : document.BanReason);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public void Save(CloudBanNotice notice)
    {
        var document = new BanDocument(
            CurrentFormat,
            notice.Code,
            notice.BanType,
            notice.BannedAt,
            notice.BanExpiresAt,
            notice.BanReason);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        byte[]? protectedBytes = null;
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            protectedBytes = ProtectedData.Protect(
                plaintext,
                Entropy,
                DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Ban file has no parent directory."));
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
            TryDelete(temporaryPath);
        }
    }

    public void EnsurePresent(CloudBanNotice notice)
    {
        // Existing state is left untouched. The rewrite exists only to repair
        // deletion while this process still holds authoritative server state.
        if (!File.Exists(path))
        {
            Save(notice);
        }
    }

    public void Clear()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryDelete(string target)
    {
        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch
        {
            // Temporary cleanup must not replace the persistence error.
        }
    }

    private sealed record BanDocument(
        int Format,
        string Code,
        string BanType,
        DateTimeOffset BannedAt,
        DateTimeOffset? BanExpiresAt,
        string? BanReason);
}
