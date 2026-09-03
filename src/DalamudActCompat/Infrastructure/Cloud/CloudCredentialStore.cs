using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed record CloudStoredCredentials(
    string Username,
    string Token,
    DateTimeOffset ExpiresAt,
    string RecoveryKey);

internal sealed class CloudCredentialStore(string path)
{
    private const int CurrentFormat = 1;
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("DalamudActCompat.CloudAccount.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string path = Path.GetFullPath(path);

    public CloudStoredCredentials? Load()
    {
        if (!File.Exists(path))
        {
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
            var document = JsonSerializer.Deserialize<CredentialDocument>(plaintext, JsonOptions);
            if (document is null || document.Format != CurrentFormat ||
                string.IsNullOrWhiteSpace(document.Username) ||
                string.IsNullOrWhiteSpace(document.Token) ||
                string.IsNullOrWhiteSpace(document.RecoveryKey))
            {
                throw new InvalidDataException("Saved cloud credentials are invalid.");
            }
            var recoveryKeyBytes = Storage.PortableConfigurationEncryptionService.ParseRecoveryKey(
                document.RecoveryKey);
            CryptographicOperations.ZeroMemory(recoveryKeyBytes);
            return new CloudStoredCredentials(
                document.Username,
                document.Token,
                document.ExpiresAt,
                document.RecoveryKey);
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

    public void Save(CloudStoredCredentials credentials)
    {
        var document = new CredentialDocument(
            CurrentFormat,
            credentials.Username,
            credentials.Token,
            credentials.ExpiresAt,
            credentials.RecoveryKey);
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
                ?? throw new InvalidOperationException("Credential file has no parent directory."));
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

    public void Clear()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void TryClear() => TryDelete(path);

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
            // Corrupt/expired startup state is best-effort cleanup; explicit logout
            // uses Clear so the UI never claims a token was removed when it was not.
        }
    }

    private sealed record CredentialDocument(
        int Format,
        string Username,
        string Token,
        DateTimeOffset ExpiresAt,
        string RecoveryKey);
}
