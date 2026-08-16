using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace DalamudActCompat.Host;

internal static class TriggernometryConfigurationRecovery
{
    private const long MaximumConfigurationBytes = 64L * 1024 * 1024;

    internal static string? TryRecover(string configRoot)
    {
        var configurationPath = Path.Combine(
            Path.GetFullPath(configRoot),
            "Config",
            "Triggernometry.config.xml");
        var previousPath = configurationPath + ".previous";
        if (IsValid(configurationPath) || !IsValid(previousPath))
        {
            return null;
        }

        var temporaryPath = configurationPath +
                            $".{Environment.ProcessId}.{Guid.NewGuid():N}.recovery.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
            if (File.Exists(configurationPath))
            {
                // Keep the damaged bytes for support instead of destroying the only evidence
                // while restoring the last configuration that Triggernometry already trusted.
                var corruptBackup = configurationPath +
                                    $".corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.xml";
                File.Copy(configurationPath, corruptBackup, overwrite: false);
            }

            File.Copy(previousPath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, configurationPath, overwrite: true);
            return "Recovered an incomplete Triggernometry configuration from " +
                   "Triggernometry.config.xml.previous before plugin initialization.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Triggernometry configuration recovery could not complete: {ex.Message}";
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stale uniquely named temporary file cannot affect a later recovery attempt.
            }
        }
    }

    private static bool IsValid(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumConfigurationBytes)
            {
                return false;
            }

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumConfigurationBytes,
            };
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            return string.Equals(
                document.Root?.Name.LocalName,
                "Configuration",
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }
}
