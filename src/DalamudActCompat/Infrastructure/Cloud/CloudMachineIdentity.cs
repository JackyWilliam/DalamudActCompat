using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed class CloudMachineIdentity(string fallbackPath)
{
    private const string Prefix = "dact-device-v1_";
    private readonly string fallbackPath = Path.GetFullPath(fallbackPath);

    public string GetDeviceId()
    {
        var machineGuid = ReadMachineGuid();
        var volumeSerial = ReadSystemVolumeSerial();
        var source = !string.IsNullOrWhiteSpace(machineGuid) || volumeSerial.HasValue
            ? $"windows\0{machineGuid}\0{volumeSerial:X8}"
            : $"fallback\0{LoadOrCreateFallback()}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"DACT strict device v1\0{source}"));
        return Prefix + Storage.PortableConfigurationEncryptionService.ToBase64Url(digest);
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }

    private static uint? ReadSystemVolumeSerial()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }
            return GetVolumeInformationW(
                root,
                null,
                0,
                out var serial,
                out _,
                out _,
                null,
                0)
                ? serial
                : null;
        }
        catch
        {
            return null;
        }
    }

    private string LoadOrCreateFallback()
    {
        try
        {
            if (File.Exists(fallbackPath))
            {
                var existing = File.ReadAllText(fallbackPath).Trim();
                if (Guid.TryParseExact(existing, "N", out _))
                {
                    return existing;
                }
            }
            var created = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(
                Path.GetDirectoryName(fallbackPath)
                ?? throw new InvalidOperationException("Device file has no parent directory."));
            var temporaryPath = $"{fallbackPath}.tmp-{Guid.NewGuid():N}";
            File.WriteAllText(temporaryPath, created);
            File.Move(temporaryPath, fallbackPath, true);
            return created;
        }
        catch
        {
            // An ephemeral fallback is less persistent but still prevents raw machine
            // properties from ever leaving the client when Windows APIs are unavailable.
            return Guid.NewGuid().ToString("N");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        uint fileSystemNameSize);
}
