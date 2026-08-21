using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace DalamudActCompat.Overlay;

internal sealed class PictoActNativeVfxBackend : IDisposable
{
    private const string VfxPoolName = "Client.System.Scheduler.Instance.VfxObject";
    private const string StaticVfxRemoveSignature =
        "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 " +
        "33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";
    private static readonly nint DirtyFlagOffset =
        Marshal.OffsetOf<VfxObject>(nameof(VfxObject.ObjectFlags));
    private static readonly nint PositionOffset =
        Marshal.OffsetOf<VfxObject>(nameof(VfxObject.Position));
    private static readonly nint RotationOffset =
        Marshal.OffsetOf<VfxObject>(nameof(VfxObject.Rotation));
    private static readonly nint ScaleOffset =
        Marshal.OffsetOf<VfxObject>(nameof(VfxObject.Scale));
    private static readonly nint ColorOffset =
        Marshal.OffsetOf<VfxObject>(nameof(VfxObject.Color));
    private readonly object syncRoot = new();
    private readonly HashSet<nint> activeHandles = [];
    private readonly StaticVfxCreateDelegate create;
    private readonly StaticVfxUpdateDelegate update;
    private readonly Hook<StaticVfxRemoveDelegate> removeHook;
    private bool disposed;

    internal static bool UsesExpectedFieldLayout =>
        DirtyFlagOffset == 0x38 &&
        PositionOffset == 0x50 &&
        RotationOffset == 0x60 &&
        ScaleOffset == 0x70 &&
        ColorOffset == 0x260;

    private unsafe PictoActNativeVfxBackend(
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider)
    {
        var createAddress = VfxObject.Addresses.Create.Value;
        if (createAddress == nint.Zero)
        {
            throw new InvalidOperationException("FFXIVClientStructs did not resolve VfxObject.Create.");
        }

        var updateAddress = VfxObject.Addresses.Update.Value;
        if (updateAddress == nint.Zero)
        {
            throw new InvalidOperationException("FFXIVClientStructs did not resolve VfxObject.Update.");
        }

        var removeAddress = sigScanner.ScanText(StaticVfxRemoveSignature);
        create = Marshal.GetDelegateForFunctionPointer<StaticVfxCreateDelegate>(createAddress);
        // Update is a generated FFXIVClientStructs member address. Reusing that authoritative
        // binding avoids a second caller-pattern signature drifting independently each patch.
        update = Marshal.GetDelegateForFunctionPointer<StaticVfxUpdateDelegate>(updateAddress);
        removeHook = gameInteropProvider.HookFromAddress<StaticVfxRemoveDelegate>(
            removeAddress,
            OnGameRemove);
        removeHook.Enable();
    }

    internal static PictoActNativeVfxBackend? TryCreate(
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider,
        IPluginLog log)
    {
        try
        {
            var backend = new PictoActNativeVfxBackend(sigScanner, gameInteropProvider);
            log.Information("PictoACT native ground VFX backend initialized.");
            return backend;
        }
        catch (Exception ex)
        {
            // Signature drift must not take down ACT; recognizable shapes can still use
            // the ImGui fallback while the native path reports one actionable warning.
            log.Warning(ex, "PictoACT native ground VFX backend is unavailable.");
            return null;
        }
    }

    internal nint Create(PictoActShape shape)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateVfxPath(shape.VfxPath);
        nint path = nint.Zero;
        nint pool = nint.Zero;
        nint handle = nint.Zero;
        try
        {
            path = Marshal.StringToCoTaskMemUTF8(shape.VfxPath);
            pool = Marshal.StringToCoTaskMemUTF8(VfxPoolName);
            handle = create(path, pool);
            if (handle == nint.Zero)
            {
                throw new InvalidOperationException(
                    $"FFXIV did not create PictoACT VFX '{shape.VfxPath}'.");
            }

            lock (syncRoot)
            {
                activeHandles.Add(handle);
            }

            WriteState(handle, shape, markDirty: false);
            update(handle, 0, -1);
            return handle;
        }
        catch
        {
            if (handle != nint.Zero)
            {
                _ = Remove(handle);
            }

            throw;
        }
        finally
        {
            if (path != nint.Zero)
            {
                Marshal.FreeCoTaskMem(path);
            }

            if (pool != nint.Zero)
            {
                Marshal.FreeCoTaskMem(pool);
            }
        }
    }

    internal bool Update(nint handle, PictoActShape shape)
    {
        lock (syncRoot)
        {
            if (!activeHandles.Contains(handle))
            {
                return false;
            }
        }

        WriteState(handle, shape, markDirty: true);
        return true;
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

        _ = CallOriginalRemove(handle);
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
            _ = CallOriginalRemove(handle);
        }

        removeHook.Disable();
        removeHook.Dispose();
    }

    private unsafe nint OnGameRemove(VfxObject* handle)
    {
        // The client can delete static VFX during territory teardown. Forgetting the
        // pointer here prevents later Change/expiry work from touching freed memory.
        lock (syncRoot)
        {
            activeHandles.Remove((nint)handle);
        }

        return removeHook.Original(handle);
    }

    private unsafe nint CallOriginalRemove(nint handle)
        => removeHook.Original((VfxObject*)handle);

    private static void WriteState(nint handle, PictoActShape shape, bool markDirty)
    {
        Marshal.StructureToPtr(shape.Position, handle + PositionOffset, false);
        var quaternion = Quaternion.CreateFromYawPitchRoll(
            shape.Yaw,
            shape.Pitch,
            shape.Angle);
        Marshal.StructureToPtr(
            new Vector4(quaternion.X, quaternion.Z, quaternion.Y, quaternion.W),
            handle + RotationOffset,
            false);
        Marshal.StructureToPtr(
            new Vector3(shape.PrimaryScale, shape.TertiaryScale, shape.SecondaryScale),
            handle + ScaleOffset,
            false);
        if (shape.HasExplicitColor)
        {
            Marshal.StructureToPtr(shape.Color, handle + ColorOffset, false);
        }

        if (markDirty)
        {
            var dirtyFlagAddress = handle + DirtyFlagOffset;
            Marshal.WriteByte(
                dirtyFlagAddress,
                (byte)(Marshal.ReadByte(dirtyFlagAddress) | 0x2));
        }
    }

    private static void ValidateVfxPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains('\\') ||
            path.Contains('\0') ||
            path.Count(character => character == '.') != 1)
        {
            // Passing a malformed resource path to the client creator is crash-prone,
            // so reject it before any unmanaged call.
            throw new InvalidDataException($"PictoACT VFX path '{path}' is unsafe.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint StaticVfxCreateDelegate(nint path, nint poolName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StaticVfxUpdateDelegate(nint handle, float deltaSeconds, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint StaticVfxRemoveDelegate(VfxObject* handle);
}
