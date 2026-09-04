using System.Globalization;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DalamudActCompat.Overlay;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DalamudActCompat.Compatibility.PluginHost;

internal sealed class TriggernometryNativeBridgeService : IDisposable
{
    private readonly object syncRoot = new();
    private readonly Dictionary<nint, DateTimeOffset?> lockOns = [];
    private readonly List<nint> pendingRemovals = [];
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly TriggernometryActorVfxBackend? actorVfx;
    private bool disposed;

    internal TriggernometryNativeBridgeService(
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider,
        IObjectTable objectTable,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.log = log;
        actorVfx = TriggernometryActorVfxBackend.TryCreate(
            sigScanner,
            gameInteropProvider,
            log);
    }

    internal unsafe void ShowGimmickHint(string payload, bool isHint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var command = ParseHint(payload);
        var raptureAtkModule = RaptureAtkModule.Instance();
        if (raptureAtkModule is null)
        {
            throw new InvalidOperationException("FFXIV's RaptureAtkModule is unavailable.");
        }

        raptureAtkModule->ShowTextGimmickHint(
            command.Text,
            isHint
                ? RaptureAtkModule.TextGimmickHintStyle.Info
                : RaptureAtkModule.TextGimmickHintStyle.Warning,
            command.DurationIn100Milliseconds);
    }

    internal void CreateLockOn(string payload)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var backend = actorVfx ?? throw new InvalidOperationException(
            "Triggernometry's game-side ActorVfx backend is unavailable.");
        var command = ParseLockOn(payload);
        var target = objectTable.FirstOrDefault(candidate =>
            candidate.Address == command.TargetAddress);
        if (target is null || target.Address == nint.Zero)
        {
            // Legacy triggers supply a process pointer. Resolve it back to the live object table
            // so the IPC payload can never select arbitrary game memory.
            throw new InvalidDataException(
                $"Triggernometry LockOn target 0x{command.TargetAddress:X} is not a live game object.");
        }

        var path = $"vfx/lockon/eff/{command.VfxName}.avfx";
        var handle = backend.Create(path, target.Address);
        var removeImmediately = false;
        lock (syncRoot)
        {
            if (disposed)
            {
                removeImmediately = true;
            }
            else
            {
                lockOns[handle] = command.Duration is { } duration
                    ? DateTimeOffset.UtcNow.Add(duration)
                    : null;
            }
        }

        if (removeImmediately)
        {
            _ = backend.Remove(handle);
            throw new ObjectDisposedException(nameof(TriggernometryNativeBridgeService));
        }
    }

    internal void Update(DateTimeOffset now)
    {
        if (disposed)
        {
            return;
        }

        nint[] removals;
        lock (syncRoot)
        {
            if (actorVfx is not null)
            {
                foreach (var stale in lockOns.Keys
                             .Where(handle => !actorVfx.IsActive(handle))
                             .ToArray())
                {
                    lockOns.Remove(stale);
                }
            }

            foreach (var expired in lockOns
                         .Where(pair => pair.Value is { } expiresAt && expiresAt <= now)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                pendingRemovals.Add(expired);
                lockOns.Remove(expired);
            }

            removals = pendingRemovals.Distinct().ToArray();
            pendingRemovals.Clear();
        }

        RemoveHandles(removals);
    }

    internal void Clear()
    {
        lock (syncRoot)
        {
            pendingRemovals.AddRange(lockOns.Keys);
            lockOns.Clear();
        }
    }

    public void Dispose()
    {
        nint[] removals;
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            removals = lockOns.Keys.Concat(pendingRemovals).Distinct().ToArray();
            lockOns.Clear();
            pendingRemovals.Clear();
        }

        RemoveHandles(removals);
        actorVfx?.Dispose();
    }

    internal static TriggernometryHintCommand ParseHint(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var parts = payload.Split('\n', 2);
        var durationExpression = parts[0].Trim();
        var text = parts.Length > 1 ? parts[1] : string.Empty;
        if (durationExpression.Length == 0 || text.Length > 2000)
        {
            throw new InvalidDataException("Triggernometry Hint payload is invalid.");
        }

        double durationSeconds;
        try
        {
            durationSeconds = PictoActOverlayService.EvaluateNumericExpression(
                durationExpression);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidDataException(
                "Triggernometry Hint duration is not a valid number expression.",
                exception);
        }

        if (!double.IsFinite(durationSeconds) || Math.Abs(durationSeconds) > 3600)
        {
            throw new InvalidDataException(
                "Triggernometry Hint duration exceeds the safe 3600-second limit.");
        }

        return new TriggernometryHintCommand(
            text,
            Math.Max(0, checked((int)(durationSeconds * 10))));
    }

    internal static TriggernometryLockOnCommand ParseLockOn(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var parts = payload.Split(',', 3, StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                "Triggernometry LockOn requires target address, VFX name, and optional duration.");
        }

        var address = ParseAddress(parts[0]);
        var vfxName = parts[1];
        if (vfxName.Length is <= 8 or > 128 ||
            vfxName.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            // Keep BridgeNamazu's minimum basename length and accept no resource path. Excluding
            // separators/extensions prevents a Host payload from escaping the lock-on directory.
            throw new InvalidDataException(
                $"Triggernometry LockOn VFX name '{vfxName}' is unsafe.");
        }

        TimeSpan? duration = null;
        if (parts.Length == 3)
        {
            double seconds;
            try
            {
                seconds = PictoActOverlayService.EvaluateNumericExpression(parts[2]);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                throw new InvalidDataException(
                    "Triggernometry LockOn duration is not a valid number expression.",
                    exception);
            }

            if (!double.IsFinite(seconds) || Math.Abs(seconds) > 3600)
            {
                throw new InvalidDataException(
                    "Triggernometry LockOn duration exceeds the safe 3600-second limit.");
            }

            if (seconds >= 0)
            {
                duration = TimeSpan.FromSeconds(seconds);
            }
        }

        return new TriggernometryLockOnCommand(address, vfxName, duration);
    }

    private static nint ParseAddress(string value)
    {
        var normalized = value.Trim();
        ulong raw;
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(
                    normalized.AsSpan(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out raw))
            {
                throw new InvalidDataException(
                    $"Triggernometry LockOn address '{value}' is invalid.");
            }
        }
        else if (long.TryParse(
                     normalized,
                     NumberStyles.Integer,
                     CultureInfo.InvariantCulture,
                     out var decimalAddress) && decimalAddress > 0)
        {
            raw = checked((ulong)decimalAddress);
        }
        else if (!ulong.TryParse(
                     normalized,
                     NumberStyles.AllowHexSpecifier,
                     CultureInfo.InvariantCulture,
                     out raw))
        {
            throw new InvalidDataException(
                $"Triggernometry LockOn address '{value}' is invalid.");
        }

        if (raw <= ushort.MaxValue || raw > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Triggernometry LockOn address '{value}' is outside user memory.");
        }

        return checked((nint)(long)raw);
    }

    private void RemoveHandles(IEnumerable<nint> handles)
    {
        if (actorVfx is null)
        {
            return;
        }

        foreach (var handle in handles)
        {
            try
            {
                _ = actorVfx.Remove(handle);
            }
            catch (Exception exception)
            {
                log.Warning(
                    exception,
                    $"Triggernometry ActorVfx 0x{handle:X} could not be removed.");
            }
        }
    }
}

internal readonly record struct TriggernometryHintCommand(
    string Text,
    int DurationIn100Milliseconds);

internal readonly record struct TriggernometryLockOnCommand(
    nint TargetAddress,
    string VfxName,
    TimeSpan? Duration);

internal sealed class TriggernometryActorVfxBackend : IDisposable
{
    private const string CreateSignature =
        "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? " +
        "48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? " +
        "0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
    private const string DestroySignature =
        "48 89 5C 24 ?? 57 48 83 EC ?? 48 8D 05 ?? ?? ?? ?? 48 8B D9 " +
        "48 89 01 8B FA 48 8D 05 ?? ?? ?? ?? 48 89 81 ?? ?? ?? ?? " +
        "48 8B 89 ?? ?? ?? ?? 48 85 C9 74 ?? 48 8B 01 48 8B D3";
    private readonly object syncRoot = new();
    private readonly HashSet<nint> activeHandles = [];
    private readonly ActorVfxCreateDelegate create;
    private readonly Hook<ActorVfxDestroyDelegate> destroyHook;
    private bool disposed;

    private TriggernometryActorVfxBackend(
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider)
    {
        var createAddress = sigScanner.ScanText(CreateSignature);
        var destroyAddress = sigScanner.ScanText(DestroySignature);
        create = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(createAddress);
        destroyHook = gameInteropProvider.HookFromAddress<ActorVfxDestroyDelegate>(
            destroyAddress,
            OnGameDestroy);
        destroyHook.Enable();
    }

    internal static TriggernometryActorVfxBackend? TryCreate(
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider,
        IPluginLog log)
    {
        try
        {
            var backend = new TriggernometryActorVfxBackend(
                sigScanner,
                gameInteropProvider);
            log.Information("Triggernometry game-side ActorVfx backend initialized.");
            return backend;
        }
        catch (Exception exception)
        {
            // A game patch can move native functions. Keep the rest of ACT available and make
            // LockOn fail visibly instead of calling a stale address.
            log.Warning(
                exception,
                "Triggernometry game-side ActorVfx backend is unavailable.");
            return null;
        }
    }

    internal nint Create(string path, nint target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        nint pathPointer = nint.Zero;
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUTF8(path);
            var handle = create(pathPointer, target, target, -1f, 0, 0, 0);
            if (handle == nint.Zero)
            {
                throw new InvalidOperationException(
                    $"FFXIV did not create Triggernometry ActorVfx '{path}'.");
            }

            lock (syncRoot)
            {
                activeHandles.Add(handle);
            }

            return handle;
        }
        finally
        {
            if (pathPointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    internal bool IsActive(nint handle)
    {
        lock (syncRoot)
        {
            return activeHandles.Contains(handle);
        }
    }

    internal bool Remove(nint handle)
    {
        lock (syncRoot)
        {
            if (!activeHandles.Remove(handle))
            {
                return false;
            }
        }

        destroyHook.Original(handle, 1);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        nint[] handles;
        lock (syncRoot)
        {
            handles = activeHandles.ToArray();
            activeHandles.Clear();
        }

        foreach (var handle in handles)
        {
            destroyHook.Original(handle, 1);
        }

        destroyHook.Disable();
        destroyHook.Dispose();
    }

    private void OnGameDestroy(nint handle, byte freeFlags)
    {
        // Lock-on effects may expire inside the client before a trigger's requested timeout.
        // Forget the pointer before forwarding destruction so later cleanup cannot double-free it.
        lock (syncRoot)
        {
            activeHandles.Remove(handle);
        }

        destroyHook.Original(handle, freeFlags);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ActorVfxCreateDelegate(
        nint path,
        nint caster,
        nint target,
        float unknown,
        byte a5,
        int a6,
        byte a7);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ActorVfxDestroyDelegate(nint handle, byte freeFlags);
}
