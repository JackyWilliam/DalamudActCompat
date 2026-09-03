using System.Security.Cryptography;
using System.Text;
using DalamudActCompat.Infrastructure.Storage;

namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed record CloudKeyEnvelope(
    string Format,
    string KeyId,
    int Iterations,
    string Salt,
    string Nonce,
    string Tag,
    string Ciphertext);

internal sealed class CloudKeyEnvelopeService
{
    internal const string EnvelopeFormat = "dact-cloud-key-envelope-v1";
    private const string RecoveryVerifierContext = "dact-password-recovery-verifier-v1";
    internal const int PasswordIterations = 310_000;
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public CloudKeyEnvelope Create(string recoveryKey, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var accountKey = PortableConfigurationEncryptionService.ParseRecoveryKey(recoveryKey);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[KeySize];
        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            KeySize);
        try
        {
            var keyIdBytes = SHA256.HashData(accountKey);
            var keyId = PortableConfigurationEncryptionService.ToBase64Url(keyIdBytes);
            using (var aes = new AesGcm(wrappingKey, TagSize))
            {
                // The key identifier is authenticated so a server-side envelope mix-up
                // cannot silently associate one account with another account's data key.
                aes.Encrypt(
                    nonce,
                    accountKey,
                    ciphertext,
                    tag,
                    BuildAuthenticatedData(keyId));
            }
            return new CloudKeyEnvelope(
                EnvelopeFormat,
                keyId,
                PasswordIterations,
                PortableConfigurationEncryptionService.ToBase64Url(salt),
                PortableConfigurationEncryptionService.ToBase64Url(nonce),
                PortableConfigurationEncryptionService.ToBase64Url(tag),
                PortableConfigurationEncryptionService.ToBase64Url(ciphertext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accountKey);
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public string Open(CloudKeyEnvelope envelope, string password)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ValidateEnvelope(envelope);
        var salt = PortableConfigurationEncryptionService.FromBase64Url(envelope.Salt);
        var nonce = PortableConfigurationEncryptionService.FromBase64Url(envelope.Nonce);
        var tag = PortableConfigurationEncryptionService.FromBase64Url(envelope.Tag);
        var ciphertext = PortableConfigurationEncryptionService.FromBase64Url(envelope.Ciphertext);
        var accountKey = new byte[KeySize];
        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            envelope.Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
        try
        {
            using (var aes = new AesGcm(wrappingKey, TagSize))
            {
                aes.Decrypt(
                    nonce,
                    ciphertext,
                    tag,
                    accountKey,
                    BuildAuthenticatedData(envelope.KeyId));
            }
            var actualKeyId = PortableConfigurationEncryptionService.ToBase64Url(
                SHA256.HashData(accountKey));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualKeyId),
                    Encoding.ASCII.GetBytes(envelope.KeyId)))
            {
                throw new CryptographicException("Cloud account key identifier mismatch.");
            }
            return "dact1_" + PortableConfigurationEncryptionService.ToBase64Url(accountKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accountKey);
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public string CreateRecoveryVerifier(string recoveryKey)
    {
        var accountKey = PortableConfigurationEncryptionService.ParseRecoveryKey(recoveryKey);
        try
        {
            using var hmac = new HMACSHA256(accountKey);
            // Domain separation prevents the verifier from being reused as either
            // the cloud decryption key or the public account-key identifier.
            var verifier = hmac.ComputeHash(Encoding.UTF8.GetBytes(RecoveryVerifierContext));
            return PortableConfigurationEncryptionService.ToBase64Url(verifier);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accountKey);
        }
    }

    private static byte[] BuildAuthenticatedData(string keyId)
        => Encoding.UTF8.GetBytes($"{EnvelopeFormat}\n{keyId}");

    private static void ValidateEnvelope(CloudKeyEnvelope envelope)
    {
        if (!string.Equals(envelope.Format, EnvelopeFormat, StringComparison.Ordinal) ||
            envelope.Iterations is < 100_000 or > 1_000_000 ||
            !IsEncodedLength(envelope.KeyId, 43) ||
            !IsEncodedLength(envelope.Salt, 22) ||
            !IsEncodedLength(envelope.Nonce, 16) ||
            !IsEncodedLength(envelope.Tag, 22) ||
            !IsEncodedLength(envelope.Ciphertext, 43))
        {
            throw new InvalidDataException("Cloud account key envelope is invalid.");
        }
    }

    private static bool IsEncodedLength(string value, int expectedLength)
        => value.Length == expectedLength &&
           value.All(static character =>
               char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
