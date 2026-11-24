using System.Security.Cryptography;
using System.Text;

namespace DalamudActCompat.Fflogs.Upload;

internal sealed class FflogsUploadCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DalamudActCompat.FflogsUpload.v1");
    private readonly string path;

    public FflogsUploadCredentialStore(string path) => this.path = Path.GetFullPath(path);
    public bool HasSavedPassword => File.Exists(path);

    public string? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (CryptographicException) { return null; }
    }

    public void Save(string password)
    {
        var plaintext = Encoding.UTF8.GetBytes(password);
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser));
            File.Move(temporary, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
