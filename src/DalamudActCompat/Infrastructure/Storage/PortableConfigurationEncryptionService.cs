using System.Security.Cryptography;

namespace DalamudActCompat.Infrastructure.Storage;

internal sealed class PortableConfigurationEncryptionService
{
    private const string RecoveryKeyPrefix = "dact1_";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaximumPlaintextBytes = 64 * 1024 * 1024;
    private static readonly byte[] Magic = "DACTE2E1"u8.ToArray();
    private static readonly byte[] AuthenticatedHeader = [.. Magic, 1];

    public string GenerateRecoveryKey()
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            return RecoveryKeyPrefix + ToBase64Url(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task EncryptFileAsync(
        string plaintextPath,
        string encryptedPath,
        string recoveryKey,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(plaintextPath);
        var destination = Path.GetFullPath(encryptedPath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Configuration archive was not found.", source);
        }
        if (PathsEqual(source, destination))
        {
            throw new InvalidOperationException(
                "Plaintext and encrypted configuration archives must be different files.");
        }
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"Encrypted configuration archive already exists: {destination}");
        }

        var sourceLength = new FileInfo(source).Length;
        if (sourceLength <= 0 || sourceLength > MaximumPlaintextBytes)
        {
            throw new InvalidDataException(
                "Configuration archive exceeds the client-side encryption size limit.");
        }

        var key = ParseRecoveryKey(recoveryKey);
        var plaintext = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        var ciphertext = new byte[plaintext.Length];
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var temporaryPath = $"{destination}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AuthenticatedHeader);
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException(
                    "Encrypted configuration archive has no parent directory."));
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await output.WriteAsync(AuthenticatedHeader, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            TryDeleteFile(temporaryPath);
        }
    }

    public async Task DecryptFileAsync(
        string encryptedPath,
        string plaintextPath,
        string recoveryKey,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(encryptedPath);
        var destination = Path.GetFullPath(plaintextPath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Encrypted configuration archive was not found.", source);
        }
        if (PathsEqual(source, destination))
        {
            throw new InvalidOperationException(
                "Encrypted and plaintext configuration archives must be different files.");
        }
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"Plaintext configuration archive already exists: {destination}");
        }

        var minimumLength = AuthenticatedHeader.Length + NonceSize + TagSize + 1;
        var sourceLength = new FileInfo(source).Length;
        if (sourceLength < minimumLength ||
            sourceLength > MaximumPlaintextBytes + minimumLength)
        {
            throw new InvalidDataException("Encrypted configuration archive has an invalid size.");
        }

        var document = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        if (!document.AsSpan(0, AuthenticatedHeader.Length).SequenceEqual(AuthenticatedHeader))
        {
            throw new InvalidDataException(
                "Encrypted configuration archive has an unsupported format.");
        }

        var key = ParseRecoveryKey(recoveryKey);
        var nonceOffset = AuthenticatedHeader.Length;
        var tagOffset = nonceOffset + NonceSize;
        var ciphertextOffset = tagOffset + TagSize;
        var plaintext = new byte[document.Length - ciphertextOffset];
        var temporaryPath = $"{destination}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var aes = new AesGcm(key, TagSize))
            {
                // Authentication completes before a plaintext file is created. A wrong
                // account key or modified cloud blob therefore cannot reach restore logic.
                aes.Decrypt(
                    document.AsSpan(nonceOffset, NonceSize),
                    document.AsSpan(ciphertextOffset),
                    document.AsSpan(tagOffset, TagSize),
                    plaintext,
                    AuthenticatedHeader);
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException(
                    "Plaintext configuration archive has no parent directory."));
            await File.WriteAllBytesAsync(temporaryPath, plaintext, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            TryDeleteFile(temporaryPath);
        }
    }

    private static byte[] ParseRecoveryKey(string recoveryKey)
    {
        if (string.IsNullOrWhiteSpace(recoveryKey) ||
            !recoveryKey.StartsWith(RecoveryKeyPrefix, StringComparison.Ordinal))
        {
            throw new FormatException("DACT recovery key has an invalid format.");
        }

        try
        {
            var key = FromBase64Url(recoveryKey[RecoveryKeyPrefix.Length..]);
            if (key.Length != KeySize)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new FormatException("DACT recovery key has an invalid length.");
            }
            return key;
        }
        catch (FormatException)
        {
            throw new FormatException("DACT recovery key has an invalid format.");
        }
    }

    private static string ToBase64Url(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }

    private static bool PathsEqual(string left, string right)
        => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteFile(string path)
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
            // Cleanup failure must not hide an encryption or authentication failure.
        }
    }
}
