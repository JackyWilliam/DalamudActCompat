using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using DalamudActCompat.Meter;
using FFXIVClientStructs.FFXIV.Client.Game;
using ContentFinderCondition = Lumina.Excel.Sheets.ContentFinderCondition;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace DalamudActCompat.Plugin;

// 场景方案 (Figma 设置 · 场景方案): entering a kind of content switches the combat meter and the
// HTML overlays to the set chosen for it. Off by default so an upgrade changes nothing until
// the player turns it on.
public enum SceneKind
{
    HighEnd,
    Normal,
    Field,
    PvP,
}

// What a scene does to one overlay. Unchanged is the default, so overlays added later are
// never closed by a scene switch (user decision, 2026-11-07).
public enum SceneOverlayAction
{
    Unchanged,
    Open,
    Close,
}

public sealed class SceneSettings
{
    public bool Enabled { get; set; } = true;

    // "keep" leaves the meter as it is, "hidden" closes it, otherwise a MeterWindowKind name.
    public string Meter { get; set; } = SceneMeter.Keep;

    public Dictionary<string, SceneOverlayAction> Overlays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class SceneMeter
{
    public const string Keep = "keep";
    public const string Hidden = "hidden";
}

public sealed class ScenePresetSettings
{
    public bool Enabled { get; set; }

    public Dictionary<SceneKind, SceneSettings> Scenes { get; set; } = new();

    public SceneSettings Get(SceneKind kind)
    {
        Scenes ??= new();
        if (!Scenes.TryGetValue(kind, out var scene)) Scenes[kind] = scene = new SceneSettings();
        scene.Overlays ??= new(StringComparer.OrdinalIgnoreCase);
        return scene;
    }
}

// Detects the current scene each frame and applies its preset when the scene changes (or the
// preset is edited). Applying is idempotent: it only opens what is closed and closes what is open.
internal sealed class ScenePresetController
{
    private readonly PluginConfiguration configuration;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IDataManager data;
    private readonly Action<bool> setMeterVisible;
    private readonly Func<string, bool> openOverlay;
    private readonly Action<string> closeOverlay;
    private readonly Action save;
    private SceneKind? applied;
    private uint appliedTerritory;
    private bool applyRequested;

    public ScenePresetController(PluginConfiguration configuration, IClientState clientState, ICondition condition, IDataManager data,
        Action<bool> setMeterVisible, Func<string, bool> openOverlay, Action<string> closeOverlay, Action save)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.condition = condition;
        this.data = data;
        this.setMeterVisible = setMeterVisible;
        this.openOverlay = openOverlay;
        this.closeOverlay = closeOverlay;
        this.save = save;
    }

    public SceneKind Current { get; private set; } = SceneKind.Field;

    public string CurrentName { get; private set; } = string.Empty;

    // The page calls this after an edit so the current scene picks up the change at once.
    public void RequestApply() => applyRequested = true;

    public unsafe void Update()
    {
        if (!clientState.IsLoggedIn || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]) return;
        var territory = clientState.TerritoryType;
        var inDuty = condition[ConditionFlag.BoundByDuty];
        var game = GameMain.Instance();
        var conditionId = game == null ? 0u : game->CurrentContentFinderConditionId;
        var (kind, name) = Detect(territory, inDuty, conditionId);
        Current = kind;
        CurrentName = name;
        var settings = configuration.Scenes;
        if (settings is null || !settings.Enabled)
        {
            applied = null;
            return;
        }
        if (!applyRequested && applied == kind && appliedTerritory == territory) return;
        applyRequested = false;
        applied = kind;
        appliedTerritory = territory;
        Apply(kind);
    }

    private (SceneKind Kind, string Name) Detect(uint territory, bool inDuty, uint conditionId)
    {
        var territoryRow = data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory);
        var placeName = territoryRow?.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
        if (territoryRow?.IsPvpZone == true) return (SceneKind.PvP, placeName);
        if (!inDuty) return (SceneKind.Field, placeName);
        var duty = conditionId > 0 ? data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(conditionId) : null;
        var dutyName = duty?.Name.ToString() is { Length: > 0 } text ? text : placeName;
        // The game marks savage, ultimate, extreme and other high-end duties itself.
        return (duty?.HighEndDuty == true ? SceneKind.HighEnd : SceneKind.Normal, dutyName);
    }

    private void Apply(SceneKind kind)
    {
        var scene = configuration.Scenes.Get(kind);
        if (!scene.Enabled) return;
        // PvP never shows the meter while scenes are on: no information advantage in PvP.
        var meter = kind == SceneKind.PvP ? SceneMeter.Hidden : scene.Meter;
        if (meter == SceneMeter.Hidden)
        {
            if (configuration.Meter.IsVisible) setMeterVisible(false);
        }
        else if (meter != SceneMeter.Keep && Enum.TryParse<MeterWindowKind>(meter, out var meterKind))
        {
            if (configuration.Meter.ActiveWindowKind != meterKind)
            {
                configuration.Meter.ActivateWindow(meterKind);
                save();
            }
            if (!configuration.Meter.IsVisible) setMeterVisible(true);
        }

        foreach (var (name, action) in scene.Overlays)
        {
            if (!configuration.OverlayWindows.TryGetValue(name, out var overlay)) continue;
            if (action == SceneOverlayAction.Open && !overlay.IsVisible) openOverlay(name);
            else if (action == SceneOverlayAction.Close && overlay.IsVisible) closeOverlay(name);
        }
    }
}
