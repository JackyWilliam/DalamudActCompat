using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.Infrastructure.Cloud;

// A small outbox, separate from replaceable configuration backups. Each immutable
// account ID owns its file, so parallel game processes cannot overwrite other accounts.
internal sealed class CloudSkinUnlockStore(string directory)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DACT.UltimateSkinUnlock.v1");
    internal static bool IsUltimate(uint id) => id is 30057 or 30067 or 30086 or 30106 or 30124 or 30143;

    private string PathFor(string accountId)
        => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))) + ".dat");

    public uint? Read(string accountId)
    {
        var path = PathFor(accountId);
        if (!File.Exists(path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var value = JsonSerializer.Deserialize<Observation>(bytes);
            if (value is not { Version: 1 } || value.AccountId != accountId || !IsUltimate(value.InstanceContentId))
                throw new InvalidDataException("Invalid saved skin unlock.");
            return value.InstanceContentId;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Remember(string accountId, uint instanceContentId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || !IsUltimate(instanceContentId)) throw new ArgumentException("Invalid ultimate observation.");
        // A valid record is already durable. Never let repeated frames rewrite it.
        try { if (Read(accountId) is not null) return; }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException) { }
        Directory.CreateDirectory(directory);
        var path = PathFor(accountId); var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Observation(1, accountId, instanceContentId));
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
            File.Move(temporary, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record Observation(int Version, string AccountId, uint InstanceContentId);
}
