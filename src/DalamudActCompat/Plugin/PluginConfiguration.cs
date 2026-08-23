using Dalamud.Configuration;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Meter;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Fflogs;
using Newtonsoft.Json;

namespace DalamudActCompat.Plugin;

public sealed class PluginConfiguration : IPluginConfiguration
{
    private const int CurrentVersion = 9;

    public int Version { get; set; } = CurrentVersion;

    public bool EnableParsing { get; set; } = true;

    public bool AutoStartParser { get; set; } = true;

    public bool DebugMode { get; set; }

    public bool EnableFflogsParityRecorder { get; set; }

    public bool AutoCheckBundledPluginUpdates { get; set; } = true;

    public bool SuppressFoxTtsProPrompt { get; set; }

    public int HistoryLimit { get; set; } = 20;

    public string LogDirectory { get; set; } = string.Empty;

    public string ActPluginDirectory { get; set; } = string.Empty;

    public string UiLanguage { get; set; } = "zh-CN";

    public bool ShowLauncherButton { get; set; } = true;

    public int LauncherButtonSize { get; set; } = 80;

    public float LauncherPositionX { get; set; } = 80;

    public float LauncherPositionY { get; set; } = 160;

    public string SelectedOverlayTemplate { get; set; } = "Kagerou";

    public string SelectedCactbotOverlay { get; set; } =
        SelfHostedActRuntime.CactbotOverlayName;

    public Dictionary<string, HtmlOverlayWindowSettings> OverlayWindows { get; set; } =
        CreateDefaultOverlayWindows();

    public MeterSettings Meter { get; set; } = new();

    public FflogsSettings Fflogs { get; set; } = new();

    public EmbeddedPluginSettings EmbeddedPlugins { get; set; } = new();

    // Json.NET otherwise reuses this default set, so a saved [] cannot preserve
    // the user's explicit choice to enable SilverDasher across a cold start.
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public HashSet<string> DisabledActPluginIds { get; set; } =
        CreateDefaultDisabledActPluginIds();

    public HashSet<string> TrustedGenericActPluginIds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> BundledPluginDisclosureKeys { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, BundledActPluginUpdateRecord> BundledPluginUpdateRecords { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<ActCapability, bool>> ActPluginPermissions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool ApplyMigrations()
    {
        var changed = false;
        DisabledActPluginIds = new HashSet<string>(
            DisabledActPluginIds ?? [],
            StringComparer.OrdinalIgnoreCase);
        TrustedGenericActPluginIds = new HashSet<string>(
            TrustedGenericActPluginIds ?? [],
            StringComparer.OrdinalIgnoreCase);
        ActPluginPermissions ??=
            new Dictionary<string, Dictionary<ActCapability, bool>>(
                StringComparer.OrdinalIgnoreCase);
        if (Meter is null)
        {
            Meter = new MeterSettings();
            changed = true;
        }
        if (Version < 2)
        {
            changed |= Meter.MigrateLegacyLocalPlayerColor();
            Version = 2;
            changed = true;
        }
        if (Version < 3)
        {
            EnableParsing = true;
            AutoStartParser = true;
            Version = 3;
            changed = true;
        }
        if (Version < 4)
        {
            if (Meter.DpsMetric == DpsMetric.EncDps)
            {
                Meter.DpsMetric = DpsMetric.Rdps;
            }
            Version = 4;
            changed = true;
        }
        if (Version < 5)
        {
            OverlayWindows ??= new Dictionary<string, HtmlOverlayWindowSettings>(
                StringComparer.OrdinalIgnoreCase);
            if (OverlayWindows.Remove(
                    SelfHostedActRuntime.CactbotCombinedTemplateName,
                    out var combinedTemplateSettings))
            {
                if (!OverlayWindows.TryGetValue(
                        SelfHostedActRuntime.CactbotOverlayName,
                        out var existingCombinedSettings) ||
                    combinedTemplateSettings.OpenOnStartup)
                {
                    OverlayWindows[SelfHostedActRuntime.CactbotOverlayName] =
                        combinedTemplateSettings;
                }
                else
                {
                    existingCombinedSettings.OpenOnStartup |=
                        combinedTemplateSettings.OpenOnStartup;
                }
            }

            var independentRaidbossOpen =
                OverlayWindows.TryGetValue(
                    SelfHostedActRuntime.CactbotAlertsOverlayName,
                    out var alertsSettings) && alertsSettings.OpenOnStartup ||
                OverlayWindows.TryGetValue(
                    SelfHostedActRuntime.CactbotTimelineOverlayName,
                    out var timelineSettings) && timelineSettings.OpenOnStartup;
            if (!OverlayWindows.TryGetValue(
                    SelfHostedActRuntime.CactbotOverlayName,
                    out var combinedSettings))
            {
                combinedSettings = new HtmlOverlayWindowSettings();
                OverlayWindows[SelfHostedActRuntime.CactbotOverlayName] = combinedSettings;
            }

            // The legacy built-in window always opened. Preserve that behavior unless the
            // user had explicitly opened one of the independent Raidboss templates.
            combinedSettings.OpenOnStartup = !independentRaidbossOpen;
            if (SelfHostedActRuntime.IsCactbotOverlayName(SelectedOverlayTemplate))
            {
                SelectedCactbotOverlay =
                    SelfHostedActRuntime.NormalizeCactbotOverlayName(SelectedOverlayTemplate);
                SelectedOverlayTemplate = "Kagerou";
            }

            Version = 5;
            changed = true;
        }
        if (Version < 6)
        {
            OverlayWindows ??= new Dictionary<string, HtmlOverlayWindowSettings>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (name, settings) in OverlayWindows)
            {
                if (!SelfHostedActRuntime.IsCactbotOverlayName(name))
                {
                    continue;
                }

                var normalizedName = SelfHostedActRuntime.NormalizeCactbotOverlayName(name);
                var isCombined = string.Equals(
                    normalizedName,
                    SelfHostedActRuntime.CactbotOverlayName,
                    StringComparison.OrdinalIgnoreCase);
                var hasUsageEvidence = settings.HasBeenOpened ||
                                       settings.OpenOnStartup ||
                                       !string.IsNullOrWhiteSpace(settings.SourceUrl) ||
                                       settings.Left is not null ||
                                       settings.Top is not null ||
                                       settings.Width is not null ||
                                       settings.Height is not null ||
                                       Math.Abs(settings.ZoomFactor - 1.0f) > 0.0001f ||
                                       !settings.IsClickThrough ||
                                       !settings.IsLocked;
                if (isCombined || hasUsageEvidence)
                {
                    settings.HasBeenOpened = true;
                }
            }

            Version = 6;
            changed = true;
        }
        if (Version < 7)
        {
            DisabledActPluginIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DisabledActPluginIds.Add("silverdasher");
            Version = 7;
            changed = true;
        }
        if (Version < 8)
        {
            // Existing unknown plugins remain disabled until their full-trust Host consent is explicit.
            TrustedGenericActPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pluginId in ActPluginPermissions.Keys.Where(
                         pluginId => !ActPluginPackageInstaller.IsSpecializedPluginId(pluginId)))
            {
                DisabledActPluginIds.Add(pluginId);
            }

            Version = 8;
            changed = true;
        }
        if (Version < 9)
        {
            // Debug mode historically implied parity capture. Make the expensive
            // research recorder an explicit opt-in without changing other diagnostics.
            EnableFflogsParityRecorder = false;
            Version = 9;
            changed = true;
        }

        return changed;
    }

    public void ResetToDefaults(string defaultLogDirectory)
    {
        Version = CurrentVersion;
        EnableParsing = true;
        AutoStartParser = true;
        DebugMode = false;
        EnableFflogsParityRecorder = false;
        AutoCheckBundledPluginUpdates = true;
        SuppressFoxTtsProPrompt = false;
        HistoryLimit = 20;
        LogDirectory = defaultLogDirectory;
        ActPluginDirectory = string.Empty;
        UiLanguage = "zh-CN";
        ShowLauncherButton = true;
        LauncherButtonSize = 80;
        LauncherPositionX = 80;
        LauncherPositionY = 160;
        SelectedOverlayTemplate = "Kagerou";
        SelectedCactbotOverlay = SelfHostedActRuntime.CactbotOverlayName;
        OverlayWindows = CreateDefaultOverlayWindows();
        Meter = new MeterSettings();
        Fflogs = new FflogsSettings();
        EmbeddedPlugins = new EmbeddedPluginSettings();
        DisabledActPluginIds = CreateDefaultDisabledActPluginIds();
        TrustedGenericActPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        BundledPluginDisclosureKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BundledPluginUpdateRecords =
            new Dictionary<string, BundledActPluginUpdateRecord>(StringComparer.OrdinalIgnoreCase);
        ActPluginPermissions =
            new Dictionary<string, Dictionary<ActCapability, bool>>(StringComparer.OrdinalIgnoreCase);
    }

    internal PluginConfiguration CreateSnapshot()
        => JsonConvert.DeserializeObject<PluginConfiguration>(
               JsonConvert.SerializeObject(this))
           ?? throw new InvalidOperationException("Plugin configuration snapshot could not be created.");

    internal void RestoreFrom(PluginConfiguration snapshot)
    {
        Version = snapshot.Version;
        EnableParsing = snapshot.EnableParsing;
        AutoStartParser = snapshot.AutoStartParser;
        DebugMode = snapshot.DebugMode;
        EnableFflogsParityRecorder = snapshot.EnableFflogsParityRecorder;
        AutoCheckBundledPluginUpdates = snapshot.AutoCheckBundledPluginUpdates;
        SuppressFoxTtsProPrompt = snapshot.SuppressFoxTtsProPrompt;
        HistoryLimit = snapshot.HistoryLimit;
        LogDirectory = snapshot.LogDirectory;
        ActPluginDirectory = snapshot.ActPluginDirectory;
        UiLanguage = snapshot.UiLanguage;
        ShowLauncherButton = snapshot.ShowLauncherButton;
        LauncherButtonSize = snapshot.LauncherButtonSize;
        LauncherPositionX = snapshot.LauncherPositionX;
        LauncherPositionY = snapshot.LauncherPositionY;
        SelectedOverlayTemplate = snapshot.SelectedOverlayTemplate;
        SelectedCactbotOverlay = snapshot.SelectedCactbotOverlay;
        OverlayWindows = snapshot.OverlayWindows;
        Meter = snapshot.Meter;
        Fflogs = snapshot.Fflogs;
        EmbeddedPlugins = snapshot.EmbeddedPlugins;
        DisabledActPluginIds = snapshot.DisabledActPluginIds;
        TrustedGenericActPluginIds = snapshot.TrustedGenericActPluginIds;
        BundledPluginDisclosureKeys = snapshot.BundledPluginDisclosureKeys;
        BundledPluginUpdateRecords = snapshot.BundledPluginUpdateRecords;
        ActPluginPermissions = snapshot.ActPluginPermissions;
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

        return IsActCapabilityAllowedByDefault(capability);
    }

    internal static bool IsActCapabilityAllowedByDefault(ActCapability capability)
        => capability is
            ActCapability.ReadCombatLogs or
            ActCapability.ReadLocalConfiguration or
            ActCapability.TextToSpeech or
            ActCapability.Clipboard;

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

    public HtmlOverlayWindowSettings RegisterOverlayWindow(string name)
    {
        var settings = GetOverlayWindowSettings(name);
        settings.HasBeenOpened = true;
        return settings;
    }

    public IReadOnlyDictionary<string, HtmlOverlayWindowSettings> GetOverlayWindowSettingsSnapshot()
        => new Dictionary<string, HtmlOverlayWindowSettings>(
            OverlayWindows ?? new Dictionary<string, HtmlOverlayWindowSettings>(
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, HtmlOverlayWindowSettings> CreateDefaultOverlayWindows()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [SelfHostedActRuntime.CactbotOverlayName] = new HtmlOverlayWindowSettings
            {
                OpenOnStartup = true,
                HasBeenOpened = true,
            },
        };

    private static HashSet<string> CreateDefaultDisabledActPluginIds()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            "silverdasher",
        };
}
