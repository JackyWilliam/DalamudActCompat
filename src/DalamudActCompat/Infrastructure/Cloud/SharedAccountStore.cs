using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Raynording.Accounts;

internal sealed record SharedAccount(string Username, string Token, DateTimeOffset ExpiresAt, string RecoveryKey);
internal sealed record SharedAccountState(long Revision, SharedAccount? Account, bool Remember, int ProcessId, long ProcessStarted)
{
    internal static SharedAccountState Empty => new(0, null, false, 0, 0);
    internal SharedAccount? UsableAccount => Account is { } a && a.ExpiresAt > DateTimeOffset.UtcNow &&
        (Remember || (ProcessId == Environment.ProcessId && ProcessStarted == SharedAccountStore.ProcessStarted)) ? a : null;
}

// This source is shared verbatim with RPets. A revisioned, encrypted tombstone prevents
// a late login response or an older plugin's cached token from undoing a logout/switch.
internal sealed class SharedAccountStore(string path)
{
    internal static readonly long ProcessStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Raynording.SharedAccount.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string path = Path.GetFullPath(path);

    internal static string PathForPlugin(string configDirectory) => Path.Combine(
        Directory.GetParent(Path.GetFullPath(configDirectory))!.FullName, "Raynording.Account", "session.dat");

    internal SharedAccountState Read() => Locked(ReadCore);

    internal SharedAccountState Publish(long expectedRevision, SharedAccount? account, bool remember)
        => Locked(() =>
        {
            var current = ReadCore();
            if (current.Revision != expectedRevision)
                throw new OperationCanceledException("账号状态已在另一插件变更，请重试。");
            var next = new SharedAccountState(checked(current.Revision + 1), account, remember,
                Environment.ProcessId, ProcessStarted);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(temporary, encrypted);
                File.Move(temporary, path, true);
                return next;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        });

    internal bool IfCurrent(long revision, Action apply) => Locked(() =>
    {
        if (ReadCore().Revision != revision) return false;
        apply();
        return true;
    });

    private SharedAccountState ReadCore()
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new InvalidDataException("共用登录文件被目录占用。");
            return SharedAccountState.Empty;
        }
        // A corrupt file must fail closed, not look like first installation and import
        // an obsolete saved DACT login over an explicit shared logout.
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("共用登录文件损坏。");
        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var state = JsonSerializer.Deserialize<SharedAccountState>(plaintext, JsonOptions);
            if (state is null || state.Revision < 1 || state.Account is { } a &&
                (string.IsNullOrWhiteSpace(a.Username) || string.IsNullOrWhiteSpace(a.Token) || string.IsNullOrWhiteSpace(a.RecoveryKey)))
                throw new InvalidDataException("共用登录文件损坏。");
            return state;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private T Locked<T>(Func<T> operation)
    {
        // Named mutexes work across plugin assembly load contexts and game processes.
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\Raynording.Account." + id);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("共用登录状态繁忙，请稍后重试。");
            return operation();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
