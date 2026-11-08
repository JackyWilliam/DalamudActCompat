using System.Security.Cryptography;
using System.Text;

namespace DalamudActCompat.Ai;

// The player's own AI API keys, one per provider, encrypted with Windows DPAPI for the current
// user. They live in their own folder (not PluginConfiguration) so cloud sync, which only backs
// up listed files, never uploads them and diagnostic reports never copy them; another Windows
// account on the same PC cannot read them. One file per provider lets the player switch services
// without pasting a key again.
internal sealed class AiKeyStore(string directory, string legacyFile)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DalamudActCompat.AiKey.v1");
    private readonly string directory = Path.GetFullPath(directory);
    private readonly string legacyFile = Path.GetFullPath(legacyFile);

    public bool Has(string provider) => File.Exists(PathFor(provider));

    public string? Load(string provider)
    {
        var path = PathFor(provider);
        if (!File.Exists(path)) return null;
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plaintext);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (CryptographicException)
        {
            // Copied from another PC or Windows account: unreadable here, so treat it as absent.
            return null;
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Save(string provider, string key)
    {
        var path = PathFor(provider);
        var plaintext = Encoding.UTF8.GetBytes(key.Trim());
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear(string provider)
    {
        var path = PathFor(provider);
        if (File.Exists(path)) File.Delete(path);
    }

    // Earlier builds kept a single ai-key.dat; it belongs to the provider configured back then.
    // The encrypted bytes move as they are, so the key is never decrypted for the migration.
    public void AdoptLegacy(string provider)
    {
        if (!File.Exists(legacyFile)) return;
        var path = PathFor(provider);
        Directory.CreateDirectory(directory);
        if (File.Exists(path)) File.Delete(legacyFile);
        else File.Move(legacyFile, path);
    }

    // Provider ids come from AiProviders (lowercase letters and dashes), never from user input.
    private string PathFor(string provider) => Path.Combine(directory, AiProviders.Normalize(provider) + ".dat");
}
