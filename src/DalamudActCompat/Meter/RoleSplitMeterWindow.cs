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
    private static readonly Vector4 NavyHover = new(0.11f, 0.16f, 0.23f, 0.96f);
    private static readonly Vector4 Gold = new(0.90f, 0.81f, 0.55f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private static readonly Vector4 HealingGreen = new(0.48f, 0.88f, 0.62f, 1);
    private readonly MeterService meterService;
    private readonly PluginConfiguration configuration;
    private readonly UiText text;
    private readonly MeterWindow classicRenderer;
    private readonly Action saveConfiguration;
    private readonly RoleSplitGroup group;
    private bool locateOnNextDraw;
    private long locatePreviewExpiresAt;
    private float expandedHeight = 360;

    public RoleSplitMeterWindow(
        MeterService meterService,
        PluginConfiguration configuration,
        UiText text,
        MeterWindow classicRenderer,
        Action saveConfiguration,
        RoleSplitGroup group)
        : base(group == RoleSplitGroup.Healer
            ? "治疗 HPS 榜###DalamudActCompatRoleSplitHealerMeter"
            : "D / T 伤害榜###DalamudActCompatRoleSplitDamageMeter")
    {
        this.meterService = meterService;
        this.configuration = configuration;
        this.text = text;
        this.classicRenderer = classicRenderer;
        this.saveConfiguration = saveConfiguration;
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

    private bool Compact
    {
        get => group == RoleSplitGroup.Healer
            ? configuration.Meter.RoleSplitHealerCompact
            : configuration.Meter.RoleSplitDamageCompact;
        set
        {
            if (group == RoleSplitGroup.Healer)
            {
                configuration.Meter.RoleSplitHealerCompact = value;
            }
            else
            {
                configuration.Meter.RoleSplitDamageCompact = value;
            }
        }
    }

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
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, Compact ? 105 : 190),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
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
        if (Compact && groupRows.Count > 1)
        {
            var retained = groupRows.FirstOrDefault(static row => row.IsLocalPlayer) ?? groupRows[0];
            groupRows = [retained];
        }
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
        if (Compact)
        {
            var summaryMetric = useHealing
                ? MeterSlotMetric.TotalHealing
                : MeterSlotMetric.TotalDamage;
            var summaryHeight = Profile.Slots.Any(slot =>
                slot.Visible && slot.Metric == summaryMetric)
                    ? MeterSlotPresentation.TeamSummaryHeight + 4
                    : 0;
            var compactHeight = 42 +
                                (Profile.ShowHeader ? 37 : 0) +
                                MeterWindow.CalculateCombatantRowHeight(ImGui.GetTextLineHeightWithSpacing()) +
                                summaryHeight;
            ImGui.SetWindowSize(
                new Vector2(ImGui.GetWindowSize().X, compactHeight),
                ImGuiCond.Always);
        }
        else
        {
            expandedHeight = Math.Max(190, ImGui.GetWindowSize().Y);
        }
    }

    internal void DrawEditorPreview(
        Encounter encounter,
        IReadOnlyList<CombatantRow> rows,
        MeterPreviewInteraction previewInteraction)
    {
        using var fontScale = new MeterFontScaleScope(Profile.FontScale);
        DrawHeader(encounter, embeddedPreview: true);
        var useHealing = group == RoleSplitGroup.Healer;
        var groupRows = MeterSlotPresentation.SortAndRank(
            rows.Where(row => JobRoleClassifier.IsHealer(row.Job) == useHealing),
            useHealing ? MeterSortMode.Hps : MeterSortMode.Dps);
        if (Compact && groupRows.Count > 1)
        {
            var retained = groupRows.FirstOrDefault(static row => row.IsLocalPlayer) ?? groupRows[0];
            groupRows = [retained];
        }
        DrawSection(groupRows, encounter, useHealing, previewInteraction);
        ImGui.Dummy(new Vector2(1, 4));
        MeterSlotPresentation.DrawTeamSummary(
            useHealing ? "role-editor-healer" : "role-editor-damage",
            encounter,
            Profile.Slots.Where(slot => slot.Metric ==
                (useHealing ? MeterSlotMetric.TotalHealing : MeterSlotMetric.TotalDamage)),
            text,
            IceBlue,
            Gold,
            previewInteraction);
    }

    private void DrawHeader(Encounter? encounter, bool embeddedPreview = false)
    {
        if (!Profile.ShowHeader)
        {
            return;
        }

        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 34);
        const float toggleSize = 24;
        var toggleStart = start + new Vector2(size.X - toggleSize - 5, 5);
        var toggleEnd = toggleStart + new Vector2(toggleSize);
        var toggleHovered = ImGui.IsMouseHoveringRect(toggleStart, toggleEnd);
        var title = group == RoleSplitGroup.Healer
            ? text.Get("治疗", "Healer")
            : text.Get("D / T", "D / T");
        ImGui.InvisibleButton("role-split-drag", size);
        if (!embeddedPreview && !toggleHovered && !Profile.IsLocked && ImGui.IsItemActive() &&
            ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, ImGuiCond.Always);
        }
        if (!embeddedPreview && toggleHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (!Compact)
            {
                expandedHeight = Math.Max(190, ImGui.GetWindowSize().Y);
            }
            Compact = !Compact;
            if (!Compact)
            {
                ImGui.SetWindowSize(
                    new Vector2(ImGui.GetWindowSize().X, expandedHeight),
                    ImGuiCond.Always);
            }
            saveConfiguration();
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
            new Vector2(
                Math.Max(start.X + 80, toggleStart.X - stateSize.X - 8),
                start.Y + 7),
            ImGui.GetColorU32(IceBlue),
            state);
        drawList.AddRectFilled(
            toggleStart,
            toggleEnd,
            ImGui.GetColorU32(MeterWindow.ApplyBackgroundOpacity(
                toggleHovered && !embeddedPreview ? NavyHover : Navy,
                Profile.BackgroundOpacity)),
            4);
        drawList.AddRect(
            toggleStart,
            toggleEnd,
            ImGui.GetColorU32(group == RoleSplitGroup.Healer ? HealingGreen : Gold),
            4);
        DrawChevron(drawList, toggleStart, toggleEnd, Compact);
        if (!embeddedPreview && toggleHovered)
        {
            ImGui.SetTooltip(text.Get(
                Compact ? "展开榜单" : "收起榜单",
                Compact ? "Expand ranking" : "Collapse ranking"));
        }
        ImGui.Dummy(new Vector2(1, 3));
    }

    private void DrawSection(
        IReadOnlyList<CombatantRow> rows,
        Encounter encounter,
        bool useHealing,
        MeterPreviewInteraction? previewInteraction = null)
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
            useHealing ? "role-healer-table" : "role-damage-table",
            previewInteraction);
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
            // Role previews clone columns to substitute HPS, but retaining the source ID
            // is required for editor selection and drag ordering to target the real slot.
            clone.Id = slot.Id;
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

    private static void DrawChevron(
        ImDrawListPtr drawList,
        Vector2 start,
        Vector2 end,
        bool compact)
    {
        var center = (start + end) * 0.5f;
        var edgeY = center.Y + (compact ? -2.5f : 2.5f);
        var pointY = center.Y + (compact ? 2.5f : -2.5f);
        var color = ImGui.GetColorU32(Vector4.One);
        drawList.AddLine(new Vector2(center.X - 4.5f, edgeY), new Vector2(center.X, pointY), color, 2);
        drawList.AddLine(new Vector2(center.X, pointY), new Vector2(center.X + 4.5f, edgeY), color, 2);
    }
}
