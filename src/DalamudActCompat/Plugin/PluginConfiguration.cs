using Dalamud.Configuration;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Meter;
using DalamudActCompat.Compatibility.PluginHost;

namespace DalamudActCompat.Plugin;

public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool EnableParsing { get; set; }

    public bool AutoStartParser { get; set; }

    public bool DebugMode { get; set; }

    public int HistoryLimit { get; set; } = 20;

    public string LogDirectory { get; set; } = string.Empty;

    public string ActPluginDirectory { get; set; } = string.Empty;

    public string UiLanguage { get; set; } = "zh-CN";

    public string SelectedOverlayTemplate { get; set; } = "Kagerou";

    public Dictionary<string, HtmlOverlayWindowSettings> OverlayWindows { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public MeterSettings Meter { get; set; } = new();

    public EmbeddedPluginSettings EmbeddedPlugins { get; set; } = new();

    public HashSet<string> DisabledActPluginIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> BundledPluginDisclosureKeys { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, BundledActPluginUpdateRecord> BundledPluginUpdateRecords { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<ActCapability, bool>> ActPluginPermissions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void ResetToDefaults(string defaultLogDirectory)
    {
        EnableParsing = false;
        AutoStartParser = false;
        DebugMode = false;
        HistoryLimit = 20;
        LogDirectory = defaultLogDirectory;
        ActPluginDirectory = string.Empty;
        UiLanguage = "zh-CN";
        SelectedOverlayTemplate = "Kagerou";
        OverlayWindows = new Dictionary<string, HtmlOverlayWindowSettings>(
            StringComparer.OrdinalIgnoreCase);
        Meter = new MeterSettings();
        EmbeddedPlugins = new EmbeddedPluginSettings();
        DisabledActPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        BundledPluginDisclosureKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BundledPluginUpdateRecords =
            new Dictionary<string, BundledActPluginUpdateRecord>(StringComparer.OrdinalIgnoreCase);
        ActPluginPermissions =
            new Dictionary<string, Dictionary<ActCapability, bool>>(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsActCapabilityAllowed(string pluginId, ActCapability capability)
    {
        var permissionSnapshot = ActPluginPermissions;
        if (permissionSnapshot is not null &&
            permissionSnapshot.TryGetValue(pluginId, out var pluginPermissions) &&
            pluginPermissions.TryGetValue(capability, out var explicitDecision))
        {
            return explicitDecision;
        }

        return capability is
            ActCapability.ReadCombatLogs or
            ActCapability.ReadLocalConfiguration or
            ActCapability.TextToSpeech or
            ActCapability.Clipboard;
    }

    public void SetActCapability(string pluginId, ActCapability capability, bool allowed)
    {
        var updated = (ActPluginPermissions ?? [])
            .ToDictionary(
                pair => pair.Key,
                pair => new Dictionary<ActCapability, bool>(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        if (!updated.TryGetValue(pluginId, out var pluginPermissions))
        {
            pluginPermissions = new Dictionary<ActCapability, bool>();
            updated[pluginId] = pluginPermissions;
        }

        pluginPermissions[capability] = allowed;
        ActPluginPermissions = updated;
    }

    public HtmlOverlayWindowSettings GetOverlayWindowSettings(string name)
    {
        OverlayWindows ??= new Dictionary<string, HtmlOverlayWindowSettings>(
            StringComparer.OrdinalIgnoreCase);
        if (!OverlayWindows.TryGetValue(name, out var settings))
        {
            settings = new HtmlOverlayWindowSettings();
            OverlayWindows[name] = settings;
        }

        return settings;
    }
}
