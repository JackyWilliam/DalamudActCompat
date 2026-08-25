using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using System.Numerics;

namespace DalamudActCompat.Meter;

public enum RoleSplitGroup
{
    DamageTank,
    Healer,
}

public sealed class RoleSplitMeterWindow : Window
{
    private static readonly Vector4 Navy = new(0.035f, 0.055f, 0.09f, 1);
    private static readonly Vector4 NavyRaised = new(0.075f, 0.10f, 0.15f, 0.94f);
    private static readonly Vector4 Gold = new(0.90f, 0.81f, 0.55f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private static readonly Vector4 HealingGreen = new(0.48f, 0.88f, 0.62f, 1);
    private readonly MeterService meterService;
    private readonly PluginConfiguration configuration;
    private readonly UiText text;
    private readonly MeterWindow classicRenderer;
    private readonly RoleSplitGroup group;
    private bool locateOnNextDraw;
    private long locatePreviewExpiresAt;

    public RoleSplitMeterWindow(
        MeterService meterService,
        PluginConfiguration configuration,
        UiText text,
        MeterWindow classicRenderer,
        RoleSplitGroup group)
        : base(group == RoleSplitGroup.Healer
            ? "治疗 HPS 榜###DalamudActCompatRoleSplitHealerMeter"
            : "D / T 伤害榜###DalamudActCompatRoleSplitDamageMeter")
    {
        this.meterService = meterService;
        this.configuration = configuration;
        this.text = text;
        this.classicRenderer = classicRenderer;
        this.group = group;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Size = new Vector2(500, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 190),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    private MeterWindowProfile Profile => configuration.Meter.RoleSplitWindow;

    public override bool DrawConditions()
    {
        WindowName = text.Get(
            group == RoleSplitGroup.Healer
                ? "治疗 HPS 榜###DalamudActCompatRoleSplitHealerMeter"
                : "D / T 伤害榜###DalamudActCompatRoleSplitDamageMeter",
            group == RoleSplitGroup.Healer
                ? "Healer HPS###DalamudActCompatRoleSplitHealerMeter"
                : "D / T Damage###DalamudActCompatRoleSplitDamageMeter");
        if (!configuration.Meter.IsVisible || !Profile.IsEnabled)
        {
            return false;
        }

        var encounter = meterService.DisplayEncounter;
        return Environment.TickCount64 < locatePreviewExpiresAt ||
               !Profile.AutoHideOutOfCombat ||
               encounter?.IsActive == true;
    }

    public void LocateOnNextDraw()
    {
        locateOnNextDraw = true;
        locatePreviewExpiresAt = Environment.TickCount64 + 3_000;
    }

    public override void PreDraw()
    {
        if (locateOnNextDraw)
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                viewport.Pos + (viewport.Size *
                    (group == RoleSplitGroup.Healer ? 0.68f : 0.42f)),
                ImGuiCond.Always,
                new Vector2(0.5f));
            locateOnNextDraw = false;
        }

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
        if (Profile.IsLocked)
        {
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        }
        if (Profile.IsLocked && Profile.ClickThroughWhenLocked)
        {
            Flags |= ImGuiWindowFlags.NoInputs;
        }

        var backgroundOpacity = MeterWindow.NormalizeBackgroundOpacity(Profile.BackgroundOpacity);
        ImGui.SetNextWindowBgAlpha(backgroundOpacity);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Navy);
        ImGui.PushStyleColor(
            ImGuiCol.Border,
            MeterWindow.ApplyBackgroundOpacity(
                new Vector4(Gold.X, Gold.Y, Gold.Z, 0.70f),
                backgroundOpacity));
        ImGui.PushStyleColor(
            ImGuiCol.ScrollbarBg,
            MeterWindow.ApplyBackgroundOpacity(Navy, backgroundOpacity));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(3);
    }

    public override void Draw()
    {
        using var fontScale = new MeterFontScaleScope(Profile.FontScale);
        var encounter = meterService.DisplayEncounter;
        DrawHeader(encounter);
        if (encounter is null)
        {
            ImGui.TextColored(IceBlue, text.Get("等待战斗数据…", "Waiting for encounter data…"));
            return;
        }

        var rows = meterService.GetRows(encounter);
        var useHealing = group == RoleSplitGroup.Healer;
        var groupRows = MeterSlotPresentation.SortAndRank(
            rows.Where(row => JobRoleClassifier.IsHealer(row.Job) == useHealing),
            useHealing ? MeterSortMode.Hps : MeterSortMode.Dps);
        DrawSection(groupRows, encounter, useHealing);
        ImGui.Dummy(new Vector2(1, 4));
        MeterSlotPresentation.DrawTeamSummary(
            useHealing ? "role-split-healer" : "role-split-damage",
            encounter,
            Profile.Slots.Where(slot => slot.Metric ==
                (useHealing ? MeterSlotMetric.TotalHealing : MeterSlotMetric.TotalDamage)),
            text,
            IceBlue,
            Gold);
    }

    internal void DrawEditorPreview(
        Encounter encounter,
        IReadOnlyList<CombatantRow> rows)
    {
        using var fontScale = new MeterFontScaleScope(Profile.FontScale);
        DrawHeader(encounter, embeddedPreview: true);
        var useHealing = group == RoleSplitGroup.Healer;
        var groupRows = MeterSlotPresentation.SortAndRank(
            rows.Where(row => JobRoleClassifier.IsHealer(row.Job) == useHealing),
            useHealing ? MeterSortMode.Hps : MeterSortMode.Dps);
        DrawSection(groupRows, encounter, useHealing);
        ImGui.Dummy(new Vector2(1, 4));
        MeterSlotPresentation.DrawTeamSummary(
            useHealing ? "role-editor-healer" : "role-editor-damage",
            encounter,
            Profile.Slots.Where(slot => slot.Metric ==
                (useHealing ? MeterSlotMetric.TotalHealing : MeterSlotMetric.TotalDamage)),
            text,
            IceBlue,
            Gold);
    }

    private void DrawHeader(Encounter? encounter, bool embeddedPreview = false)
    {
        if (!Profile.ShowHeader)
        {
            return;
        }

        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 34);
        var title = group == RoleSplitGroup.Healer
            ? text.Get("治疗", "Healer")
            : text.Get("D / T", "D / T");
        ImGui.InvisibleButton("role-split-drag", size);
        if (!embeddedPreview && !Profile.IsLocked && ImGui.IsItemActive() &&
            ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, ImGuiCond.Always);
        }

        var state = encounter is null
            ? text.Get("等待数据", "Waiting")
            : encounter.IsActive
                ? text.Get("战斗中", "Running")
                : text.Get("已结束 · 保留上一场", "Ended · retained");
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + size,
            ImGui.GetColorU32(MeterWindow.ApplyBackgroundOpacity(
                NavyRaised,
                Profile.BackgroundOpacity)),
            6);
        drawList.AddText(
            start + new Vector2(12, 7),
            ImGui.GetColorU32(group == RoleSplitGroup.Healer ? HealingGreen : Gold),
            title);
        var stateSize = ImGui.CalcTextSize(state);
        drawList.AddText(
            start + new Vector2(size.X - stateSize.X - 12, 7),
            ImGui.GetColorU32(IceBlue),
            state);
        ImGui.Dummy(new Vector2(1, 3));
    }

    private void DrawSection(
        IReadOnlyList<CombatantRow> rows,
        Encounter encounter,
        bool useHealing)
    {
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(text.Get("暂无玩家", "No players"));
            return;
        }

        classicRenderer.DrawClassicTable(
            encounter,
            rows,
            CreateTableSettings(useHealing),
            useHealing ? "role-healer-table" : "role-damage-table");
    }

    private MeterSettings CreateTableSettings(bool useHealing)
    {
        var source = configuration.Meter;
        var visibleSlots = Profile.Slots.Where(static slot => slot.Visible).ToArray();
        // Both role windows use the classic column contract. The healer view only
        // substitutes its first damage-rate column with HPS and preserves later columns.
        var leadingDamage = visibleSlots.FirstOrDefault(static slot =>
            slot.Metric is MeterSlotMetric.Dps or MeterSlotMetric.Rdps or
                MeterSlotMetric.EncDps or MeterSlotMetric.ExtDps);
        var slots = new List<MeterSlotDefinition>();
        foreach (var slot in visibleSlots)
        {
            var metric = slot.Metric;
            if (useHealing && ReferenceEquals(slot, leadingDamage))
            {
                metric = MeterSlotMetric.Hps;
            }
            else if (useHealing && leadingDamage is not null && metric == MeterSlotMetric.Hps)
            {
                continue;
            }
            else if (!useHealing && metric == MeterSlotMetric.Hps)
            {
                continue;
            }

            var clone = slot.Clone();
            clone.Metric = metric;
            slots.Add(clone);
        }

        bool Has(MeterSlotMetric metric) => slots.Any(slot => slot.Metric == metric);
        return new MeterSettings
        {
            ClassicWindow = new MeterWindowProfile
            {
                IsLocked = Profile.IsLocked,
                ClickThroughWhenLocked = Profile.ClickThroughWhenLocked,
                BackgroundOpacity = Profile.BackgroundOpacity,
                FontScale = Profile.FontScale,
                SortMode = useHealing ? MeterSortMode.Hps : MeterSortMode.Dps,
                Slots = slots,
            },
            IsLocked = Profile.IsLocked,
            ClickThroughWhenLocked = Profile.ClickThroughWhenLocked,
            BackgroundOpacity = Profile.BackgroundOpacity,
            FontScale = Profile.FontScale,
            SortMode = useHealing ? MeterSortMode.Hps : MeterSortMode.Dps,
            PlayerIdentityMode = source.PlayerIdentityMode,
            LocalPlayerAlias = source.LocalPlayerAlias,
            JobDisplayStyle = source.JobDisplayStyle,
            LocalPlayerColor = source.LocalPlayerColor,
            ShowRank = Has(MeterSlotMetric.Rank),
            ShowPlayerName = Has(MeterSlotMetric.PlayerIdentity),
            ShowJob = Has(MeterSlotMetric.PlayerIdentity),
            ShowDps = Has(MeterSlotMetric.Dps),
            ShowRdps = Has(MeterSlotMetric.Rdps),
            ShowEncDps = Has(MeterSlotMetric.EncDps),
            ShowExtDps = Has(MeterSlotMetric.ExtDps),
            ShowHps = Has(MeterSlotMetric.Hps),
            ShowDamagePercent = Has(MeterSlotMetric.DamagePercent),
            ShowHighestDamage = Has(MeterSlotMetric.HighestDamageAction) ||
                                Has(MeterSlotMetric.HighestDamage),
            ShowDeaths = Has(MeterSlotMetric.Deaths),
            ShowCriticalHitRate = Has(MeterSlotMetric.CriticalHitPercent),
            ShowDirectHitRate = Has(MeterSlotMetric.DirectHitPercent),
            ShowCriticalDirectHitRate = Has(MeterSlotMetric.CriticalDirectHitPercent),
            ShowFflogs = false,
            ShowTotalDamage = false,
            ShowTotalHealing = false,
        };
    }
}
