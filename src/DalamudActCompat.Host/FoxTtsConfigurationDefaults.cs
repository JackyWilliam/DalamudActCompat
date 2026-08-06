using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DalamudActCompat.Host;

public static class FoxTtsConfigurationDefaults
{
    public const string DefaultEngine = "ttsEngineCafePro";

    public static bool Ensure(string configRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        var configDirectory = Path.Combine(Path.GetFullPath(configRoot), "Config");
        var configPath = Path.Combine(configDirectory, "ACT.FoxTTS.config.xml");
        Directory.CreateDirectory(configDirectory);
        if (File.Exists(configPath))
        {
            return false;
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-16", "yes"),
            new XElement(
                "Config",
                new XElement(
                    "SettingsSerializer",
                    new XElement("TTSEngine", DefaultEngine),
                    new XElement("PluginIntegration", "Auto"))));
        var writerSettings = new XmlWriterSettings
        {
            Encoding = Encoding.Unicode,
            Indent = true,
            OmitXmlDeclaration = false,
        };
        try
        {
            using var stream = new FileStream(
                configPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = XmlWriter.Create(stream, writerSettings);
            document.Save(writer);
            return true;
        }
        catch (IOException) when (File.Exists(configPath))
        {
            return false;
        }
    }
}
