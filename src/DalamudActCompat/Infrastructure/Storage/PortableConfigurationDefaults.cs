using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using DalamudActCompat.Plugin;
using DalamudActCompat.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DalamudActCompat.Infrastructure.Storage;

internal static class PortableConfigurationDefaults
{
    public static bool IsDefaultArchive(string archivePath)
    {
        // Inspect the captured payload, not live files that may change between
        // classification and upload. Unknown extension data must remain portable.
        using var archive = ZipFile.OpenRead(archivePath);
        var main = archive.GetEntry("payload/DalamudActCompat.json");
        if (main is null) return false;
        using (var reader = new StreamReader(main.Open()))
        {
            if (!IsDefaultMain(reader.ReadToEnd())) return false;
        }

        foreach (var entry in archive.Entries)
        {
            if (entry == main || !entry.FullName.StartsWith("payload/", StringComparison.Ordinal)) continue;
            if (entry.FullName == "payload/DalamudActCompat/Config/ACT.FoxTTS.config.xml")
            {
                using var input = entry.Open();
                using var xml = XmlReader.Create(input, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    IgnoreWhitespace = true,
                });
                try
                {
                    // FoxTTS is seeded on first Host startup, before the user edits anything.
                    var expected = new XElement("Config", new XElement("SettingsSerializer",
                        new XElement("TTSEngine", FoxTtsConfigurationDefaults.DefaultEngine),
                        new XElement("PluginIntegration", "Auto")));
                    if (XNode.DeepEquals(XDocument.Load(xml).Root, expected)) continue;
                }
                catch (XmlException) { }
            }

            // Do not guess defaults for third-party schemas: even a default main
            // file can accompany valuable triggers, overlay settings or user scripts.
            return false;
        }
        return true;
    }

    private static bool IsDefaultMain(string json)
    {
        try
        {
            var configuration = JsonConvert.DeserializeObject<PluginConfiguration>(json, new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Error,
                ObjectCreationHandling = ObjectCreationHandling.Replace,
                TypeNameHandling = TypeNameHandling.None,
            });
            if (configuration is null) return false;
            var defaults = new PluginConfiguration();
            if (configuration.Version > defaults.Version) return false;
            var actual = Comparable(configuration);
            if (JToken.DeepEquals(actual, Comparable(defaults))) return true;
            defaults.ApplyMigrations();
            return JToken.DeepEquals(actual, Comparable(defaults));
        }
        catch (JsonException)
        {
            // An unfamiliar field is evidence of data to preserve, not of an empty account.
            return false;
        }
    }

    private static JObject Comparable(PluginConfiguration configuration)
    {
        var value = JObject.FromObject(configuration);
        // These paths are kept local during restore; slot identities are generated
        // on construction and carry no layout preference by themselves.
        value.Remove(nameof(PluginConfiguration.Version));
        value.Remove(nameof(PluginConfiguration.LogDirectory));
        value.Remove(nameof(PluginConfiguration.ActPluginDirectory));
        foreach (var id in value.SelectTokens("Meter..Slots[*].Id")
                     .Concat(value.SelectTokens("Meter.RoleSplitDamageSlots[*].Id"))
                     .Concat(value.SelectTokens("Meter.RoleSplitHealerSlots[*].Id")).ToArray())
            ((JProperty)id.Parent!).Remove();
        return value;
    }
}
