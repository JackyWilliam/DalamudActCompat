using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DalamudActCompat.Protocol;

public static class FoxTtsConfigurationDefaults
{
    public const string DefaultEngine = "ttsEngineCafePro";

    public static bool IsPro(string configRoot)
    {
        var configPath = GetConfigurationPath(configRoot);
        if (!File.Exists(configPath))
        {
            return false;
        }

        var document = Load(configPath, LoadOptions.None, out _);
        return string.Equals(
            document.Descendants("TTSEngine").FirstOrDefault()?.Value,
            DefaultEngine,
            StringComparison.Ordinal);
    }

    public static bool Ensure(string configRoot)
    {
        var configPath = GetConfigurationPath(configRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (File.Exists(configPath))
        {
            return false;
        }

        var document = CreateDefaultDocument();
        try
        {
            Save(document, configPath, FileMode.CreateNew);
            return true;
        }
        catch (IOException) when (File.Exists(configPath))
        {
            return false;
        }
    }

    public static bool SetPro(string configRoot)
    {
        var configPath = GetConfigurationPath(configRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (!File.Exists(configPath))
        {
            return Ensure(configRoot);
        }

        var document = Load(
            configPath,
            LoadOptions.PreserveWhitespace,
            out var sourceEncoding);
        var engine = document.Descendants("TTSEngine").FirstOrDefault();
        if (engine is not null &&
            string.Equals(engine.Value, DefaultEngine, StringComparison.Ordinal))
        {
            return false;
        }

        if (engine is null)
        {
            var serializer = document.Descendants("SettingsSerializer").FirstOrDefault();
            if (serializer is null)
            {
                document = CreateDefaultDocument();
            }
            else
            {
                serializer.AddFirst(new XElement("TTSEngine", DefaultEngine));
            }
        }
        else
        {
            engine.Value = DefaultEngine;
        }

        var temporaryPath = $"{configPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            Save(document, temporaryPath, FileMode.CreateNew, sourceEncoding);
            File.Move(temporaryPath, configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return true;
    }

    private static string GetConfigurationPath(string configRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        return Path.Combine(
            Path.GetFullPath(configRoot),
            "Config",
            "ACT.FoxTTS.config.xml");
    }

    private static XDocument CreateDefaultDocument()
        => new(
            new XDeclaration("1.0", "utf-16", "yes"),
            new XElement(
                "Config",
                new XElement(
                    "SettingsSerializer",
                    new XElement("TTSEngine", DefaultEngine),
                    new XElement("PluginIntegration", "Auto"))));

    private static XDocument Load(
        string path,
        LoadOptions options,
        out Encoding sourceEncoding)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        var xml = reader.ReadToEnd();
        sourceEncoding = reader.CurrentEncoding;
        return XDocument.Parse(xml, options);
    }

    private static void Save(
        XDocument document,
        string path,
        FileMode mode,
        Encoding? encoding = null)
    {
        var writerSettings = new XmlWriterSettings
        {
            Encoding = encoding ?? ResolveEncoding(document.Declaration?.Encoding),
            Indent = true,
            OmitXmlDeclaration = false,
        };
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        using var writer = XmlWriter.Create(stream, writerSettings);
        document.Save(writer);
    }

    private static Encoding ResolveEncoding(string? encoding)
        => string.Equals(encoding, "utf-16", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(encoding, "unicode", StringComparison.OrdinalIgnoreCase)
            ? Encoding.Unicode
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
