using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed record FriendsLocalState(
    Dictionary<string, CloudChatSendRequest> Outbox,
    Dictionary<string, long> ReadThrough)
{
    public static FriendsLocalState Empty() => new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
}

internal interface IFriendsLocalStateStore
{
    Task<FriendsLocalState> LoadAsync(string userId, CancellationToken cancellationToken);
    Task SaveAsync(string userId, FriendsLocalState state, CancellationToken cancellationToken);
}

internal sealed class FriendsLocalStateStore(string directory) : IFriendsLocalStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaximumBytes = 2 * 1024 * 1024;
    private string? loadedUser;
    private FriendsLocalState baseline = FriendsLocalState.Empty();

    public async Task<FriendsLocalState> LoadAsync(string userId, CancellationToken cancellationToken)
    {
        var result = await ReadAsync(userId, cancellationToken).ConfigureAwait(false);
        loadedUser = userId; baseline = Clone(result);
        return result;
    }
    private async Task<FriendsLocalState> ReadAsync(string userId, CancellationToken cancellationToken)
    {
        var path = GetPath(userId);
        if (!File.Exists(path)) return FriendsLocalState.Empty();
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("好友本机状态文件过大。");
        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var plain = ProtectedData.Unprotect(protectedBytes, Entropy(userId), DataProtectionScope.CurrentUser);
        try
        {
            var state = JsonSerializer.Deserialize<FriendsLocalState>(plain, Json)
                ?? throw new InvalidDataException("好友本机状态无法读取。");
            Validate(state);
            return state;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveAsync(string userId, FriendsLocalState state, CancellationToken cancellationToken)
    {
        Validate(state);
        var path = GetPath(userId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (loadedUser != userId) throw new InvalidOperationException("请先读取当前账号的好友状态。");
        // Different game processes may share a config directory. Merge untouched
        // operations and read watermarks under an exclusive file lease; a conflicting
        // operation must fail before HTTP, never overwrite another unknown send.
        await using var lease = await AcquireAsync(path + ".lock", cancellationToken).ConfigureAwait(false);
        var merged = await ReadAsync(userId, cancellationToken).ConfigureAwait(false);
        foreach (var id in baseline.Outbox.Keys.Concat(state.Outbox.Keys).Distinct())
        {
            var before = baseline.Outbox.GetValueOrDefault(id);
            var after = state.Outbox.GetValueOrDefault(id);
            var current = merged.Outbox.GetValueOrDefault(id);
            if (before == after) continue;
            if (current != before && current != after)
                throw new IOException("另一游戏窗口已更新此会话的待发送消息；本次未覆盖，请先在原窗口处理。");
            if (after is null) merged.Outbox.Remove(id); else merged.Outbox[id] = after;
        }
        foreach (var (id, value) in state.ReadThrough)
            merged.ReadThrough[id] = Math.Max(value, merged.ReadThrough.GetValueOrDefault(id));
        foreach (var id in baseline.ReadThrough.Keys.Except(state.ReadThrough.Keys)) merged.ReadThrough.Remove(id);
        Validate(merged);
        var plain = JsonSerializer.SerializeToUtf8Bytes(merged, Json);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy(userId), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length > MaximumBytes) throw new InvalidDataException("好友本机状态超过容量限制。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            // Persist the exact operation before HTTP; atomic replacement survives
            // interrupted saves without turning an unknown send into a new operation.
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            baseline = Clone(state);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FriendsLocalState Clone(FriendsLocalState value) => new(new(value.Outbox), new(value.ReadThrough));
    private static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (attempt < 20) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
    }

    private string GetPath(string userId)
    {
        if (!Guid.TryParse(userId, out _)) throw new InvalidDataException("好友账号标识无效。");
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))) + ".dat");
    }
    private static byte[] Entropy(string userId) => Encoding.UTF8.GetBytes("DACT.Friends.LocalState.v1:" + userId);
    private static void Validate(FriendsLocalState state)
    {
        if (state.Outbox is null || state.ReadThrough is null || state.Outbox.Count > 201 || state.ReadThrough.Count > 201 ||
            state.Outbox.Any(pair => !Guid.TryParse(pair.Key, out _) || pair.Value is null || pair.Value.Sequence < 1 ||
                pair.Value.OperationId == Guid.Empty ||
                (pair.Value.QuickMessageId is null ? string.IsNullOrWhiteSpace(pair.Value.Text) || pair.Value.Text.EnumerateRunes().Count() > 2000 :
                    pair.Value.Text is not null || pair.Value.QuickMessageId is not CloudChatPolicy.InviteNext and not CloudChatPolicy.WhenFinished)) ||
            state.ReadThrough.Any(pair => !Guid.TryParse(pair.Key, out _) || pair.Value < 0))
            throw new InvalidDataException("好友本机状态格式无效。");
    }
}
