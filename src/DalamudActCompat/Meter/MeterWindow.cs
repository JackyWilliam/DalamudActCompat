using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using System.Numerics;

namespace DalamudActCompat.Meter;

public sealed class MeterWindow : Window
{
    internal const float CombatantRowSpacing = 3;
    internal const string LimitBreakDisplayName = "LB (Limit Break)";
    internal const float MinimumTableWidthWithFflogs = 563;
    internal const float MinimumTableWidthWithoutFflogs = 516;
    internal const float MinimumExpandedWindowWidth = 380;
    internal const float MinimumExpandedWindowHeight = 170;
    internal const float DefaultExpandedWindowWidth = 500;
    internal const float DefaultExpandedWindowHeight = 420;
    private const float CompactWindowMinimumHeight = 60;
    private const float EmptyStateHeight = 42;
    private const float EncounterHeaderHeight = 44;
    private const float CompactDragHandleHeight = 26;
    private const float CompactToggleSize = 26;
    private const float WindowResizeAnimationDurationSeconds = 0.18f;
    private const float TableRightPadding = 9;
    private const float ColumnSpacing = 3;
    private const float ColumnHeaderHorizontalPadding = 6;
    private const float FflogsColumnWidth = 44;
    private const float RateColumnWidth = 50;
    private const float HitRateColumnWidth = 43;
    private const float DamagePercentColumnWidth = 46;
    private const float TotalDamageColumnWidth = 76;
    private const float HighestDamageColumnWidth = 112;
    private const float DeathsColumnWidth = 28;
    private const float RankColumnWidth = 28;
    private const float IdentityColumnWidth = 150;
    private static readonly Vector4 NavyRaised = new(0.075f, 0.10f, 0.15f, 0.94f);
    private static readonly Vector4 NavyHover = new(0.11f, 0.16f, 0.23f, 0.96f);
    private static readonly Vector4 Gold = new(0.90f, 0.81f, 0.55f, 1);
    private static readonly Vector4 LocalRateBright = new(1.0f, 0.94f, 0.42f, 1);
    private static readonly Vector4 IceBlue = new(0.38f, 0.72f, 0.90f, 1);
    private static readonly Vector4 HealingGreen = new(0.48f, 0.88f, 0.62f, 1);

    private readonly MeterService meterService;
    private readonly FflogsEstimateService fflogsEstimateService;
    private readonly PluginConfiguration configuration;
    private readonly UiText text;
    private readonly JobIconTextureSet jobIcons;
    private readonly ISharedImmediateTexture runningStatusIcon;
    private readonly ISharedImmediateTexture transitionStatusIcon;
    private readonly ISharedImmediateTexture endedStatusIcon;
    private readonly Func<uint?, string, string> localizeZoneName;
    private readonly Action saveConfiguration;
    private bool isHeightAnimationActive;
    private bool isDragging;
    private bool observedCompactMode;
    private float heightAnimationElapsedSeconds;
    private float heightAnimationStart;
    private float heightAnimationTarget;
    private Vector2 dragOffset;
    private bool locateOnNextDraw;
    private long locatePreviewExpiresAt;

    public MeterWindow(
        MeterService meterService,
        FflogsEstimateService fflogsEstimateService,
        PluginConfiguration configuration,
        UiText text,
        JobIconTextureSet jobIcons,
        ISharedImmediateTexture runningStatusIcon,
        ISharedImmediateTexture transitionStatusIcon,
        ISharedImmediateTexture endedStatusIcon,
        Func<uint?, string, string> localizeZoneName,
        Action saveConfiguration)
        : base("战斗统计###DalamudActCompatMeter")
    {
        this.meterService = meterService;
        this.fflogsEstimateService = fflogsEstimateService;
        this.configuration = configuration;
        this.text = text;
        this.jobIcons = jobIcons;
        this.runningStatusIcon = runningStatusIcon;
        this.transitionStatusIcon = transitionStatusIcon;
        this.endedStatusIcon = endedStatusIcon;
        this.localizeZoneName = localizeZoneName;
        this.saveConfiguration = saveConfiguration;
        observedCompactMode = configuration.Meter.CompactMode;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Size = new Vector2(1180, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(120, 90),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override bool DrawConditions()
    {
        var settings = configuration.Meter;
        WindowName = text.Get("战斗统计###DalamudActCompatMeter", "Combat Meter###DalamudActCompatMeter");
        if (!settings.IsVisible || !settings.ClassicWindow.IsEnabled)
        {
            return false;
        }

        var encounter = meterService.DisplayEncounter;
        return Environment.TickCount64 < locatePreviewExpiresAt ||
               !settings.AutoHideOutOfCombat ||
               encounter?.IsActive == true;
    }

    public void LocateOnNextDraw()
    {
        locateOnNextDraw = true;
        // Locating must remain visible long enough to prove where the window was moved,
        // even when the user's normal out-of-combat auto-hide setting is enabled.
        locatePreviewExpiresAt = Environment.TickCount64 + 3_000;
    }

    public override void PreDraw()
    {
        if (locateOnNextDraw)
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                viewport.Pos + (viewport.Size * 0.5f),
                ImGuiCond.Always,
                new Vector2(0.5f, 0.5f));
            locateOnNextDraw = false;
        }

        var settings = configuration.Meter;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(
                120,
                settings.CompactMode || isHeightAnimationActive
                    ? CompactWindowMinimumHeight
                    : MinimumExpandedWindowHeight),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
        if (settings.IsLocked)
        {
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        }

        if (ShouldPassMouseInputThrough(settings))
        {
            Flags |= ImGuiWindowFlags.NoInputs;
        }

        var backgroundOpacity = NormalizeBackgroundOpacity(settings.ClassicWindow.BackgroundOpacity);
        ImGui.SetNextWindowBgAlpha(backgroundOpacity);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.035f, 0.055f, 0.09f, 1));
        ImGui.PushStyleColor(
            ImGuiCol.Border,
            ApplyBackgroundOpacity(new Vector4(0.62f, 0.52f, 0.28f, 0.85f), backgroundOpacity));
        ImGui.PushStyleColor(
            ImGuiCol.Separator,
            ApplyBackgroundOpacity(new Vector4(0.50f, 0.42f, 0.24f, 0.75f), backgroundOpacity));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ApplyBackgroundOpacity(NavyRaised, backgroundOpacity));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ApplyBackgroundOpacity(NavyHover, backgroundOpacity));
        ImGui.PushStyleColor(
            ImGuiCol.Button,
            ApplyBackgroundOpacity(new Vector4(0.11f, 0.17f, 0.25f, 1), backgroundOpacity));
        ImGui.PushStyleColor(
            ImGuiCol.ButtonHovered,
            ApplyBackgroundOpacity(new Vector4(0.17f, 0.25f, 0.35f, 1), backgroundOpacity));
        ImGui.PushStyleColor(
            ImGuiCol.Header,
            ApplyBackgroundOpacity(new Vector4(0.15f, 0.21f, 0.29f, 1), backgroundOpacity));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 9));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(7, CombatantRowSpacing));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(8);
    }

    public override void Draw()
    {
        var settings = configuration.Meter;
        var encounter = meterService.DisplayEncounter;
        using var fontScale = new FontScaleScope(settings.FontScale);

        AdvanceWindowHeightAnimation();
        SynchronizeCompactMode(settings);
        if (!settings.CompactMode)
        {
            CaptureExpandedWindowSize(settings, ImGui.GetWindowSize());
        }

        if (encounter is null)
        {
            DrawEmptyState(settings);
            return;
        }

        var allRows = meterService.GetRows(encounter);
        if (settings.ShowHeader)
        {
            DrawEncounterHeader(encounter, settings);
        }
        else
        {
            DrawCompactDragHandle(settings);
        }

        var rows = SelectClassicRows(allRows, settings);
        ApplyCompactWindowHeight(settings);
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(text.Get("等待玩家数据…", "Waiting for player data…"));
            return;
        }

        if (settings.ClassicAllianceView)
        {
            DrawAllianceCompactTiles(encounter, rows, settings);
        }
        else
        {
            DrawClassicTable(encounter, rows, settings, "classic-meter-rows");
        }
        if (ShouldDrawTeamSummary(settings))
        {
            MeterSlotPresentation.DrawTeamSummary(
                "classic",
                encounter,
                settings.ClassicWindow.Slots,
                text,
                new Vector4(0.70f, 0.74f, 0.80f, 1),
                Gold);
        }
    }

    private void DrawEmptyState(MeterSettings settings)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var toggleStart = new Vector2(
            start.X + width - CompactToggleSize - 6,
            start.Y + ((EmptyStateHeight - CompactToggleSize) * 0.5f));
        var toggleEnd = toggleStart + new Vector2(CompactToggleSize, CompactToggleSize);
        var toggleHovered = CanInteractWithCompactToggle(settings) &&
                            ImGui.IsMouseHoveringRect(toggleStart, toggleEnd);
        ImGui.InvisibleButton("empty-meter-drag", new Vector2(width, EmptyStateHeight));
        HandleHeaderDrag(settings, allowStart: !toggleHovered);
        HandleCompactModeToggle(settings, toggleHovered);
        ApplyEmptyStateCompactWindowHeight(settings);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + new Vector2(width, EmptyStateHeight),
            ImGui.GetColorU32(ApplyBackgroundOpacity(NavyRaised, settings.ClassicWindow.BackgroundOpacity)),
            6);
        var textWidth = Math.Max(20, toggleStart.X - start.X - 16);
        drawList.AddText(
            start + new Vector2(10, 6),
            ImGui.GetColorU32(IceBlue),
            TrimToWidth(
                text.Get("● 等待战斗数据", "● Waiting for encounter data"),
                textWidth));
        drawList.AddText(
            start + new Vector2(10, 23),
            ImGui.GetColorU32(new Vector4(0.66f, 0.69f, 0.74f, 1)),
            TrimToWidth(
                text.Get("解析开始后会自动显示排行", "Rankings appear automatically when parsing begins"),
                textWidth));
        DrawCompactModeToggle(drawList, settings, toggleStart, toggleEnd, toggleHovered);
    }

    private void DrawEncounterHeader(
        Encounter encounter,
        MeterSettings settings,
        bool embeddedPreview = false)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var toggleStart = new Vector2(start.X + width - CompactToggleSize - 6, start.Y + 9);
        var toggleEnd = toggleStart + new Vector2(CompactToggleSize, CompactToggleSize);
        var rankingButtonWidth = Math.Max(54, ImGui.CalcTextSize("HPS 榜").X + 14);
        var audienceButtonWidth = Math.Max(58, ImGui.CalcTextSize(text.Get("24 人本", "24-player")).X + 18);
        var audienceEnd = new Vector2(toggleStart.X - 5, toggleEnd.Y);
        var audienceStart = new Vector2(audienceEnd.X - audienceButtonWidth, toggleStart.Y);
        var rankingEnd = new Vector2(audienceStart.X - 5, toggleEnd.Y);
        var rankingStart = new Vector2(rankingEnd.X - rankingButtonWidth, toggleStart.Y);
        var toggleHovered = CanInteractWithCompactToggle(settings) &&
                            ImGui.IsMouseHoveringRect(toggleStart, toggleEnd);
        var rankingHovered = CanInteractWithCompactToggle(settings) &&
                             ImGui.IsMouseHoveringRect(rankingStart, rankingEnd);
        var audienceHovered = CanInteractWithCompactToggle(settings) &&
                              ImGui.IsMouseHoveringRect(audienceStart, audienceEnd);
        ImGui.InvisibleButton("meter-header-drag", new Vector2(width, EncounterHeaderHeight));
        if (!embeddedPreview)
        {
            HandleHeaderDrag(
                settings,
                allowStart: !toggleHovered && !rankingHovered && !audienceHovered);
            HandleCompactModeToggle(settings, toggleHovered);
        }
        HandleRankingModeToggle(settings, rankingHovered, persistChanges: !embeddedPreview);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + new Vector2(width, EncounterHeaderHeight),
            ImGui.GetColorU32(ApplyBackgroundOpacity(NavyRaised, settings.ClassicWindow.BackgroundOpacity)),
            6);
        DrawEncounterStateIcon(drawList, encounter, start + new Vector2(9, 5));
        var titleRight = Math.Max(
            start.X + 36,
            audienceStart.X - 6);
        drawList.AddText(
            start + new Vector2(36, 6),
            ImGui.GetColorU32(Gold),
            TrimToWidth(LocalizeEncounterTitle(encounter), titleRight - start.X - 36));
        var subtitle =
            $"{localizeZoneName(encounter.TerritoryId, encounter.ZoneName)}  ·  " +
            FormatDuration(ResolveHeaderDuration(encounter));
        if (!UsesStatusAsEncounterTitle(encounter))
        {
            subtitle += "  ·  " +
                        ResolveEncounterStateText(encounter);
        }
        drawList.AddText(
            start + new Vector2(36, 24),
            ImGui.GetColorU32(new Vector4(0.66f, 0.69f, 0.74f, 1)),
            TrimToWidth(subtitle, titleRight - start.X - 36));
        DrawCompactModeToggle(drawList, settings, toggleStart, toggleEnd, toggleHovered);
        DrawRankingModeIcon(drawList, settings, rankingStart, rankingEnd, rankingHovered);
        DrawAudienceModeDropdown(
            drawList,
            settings,
            audienceStart,
            audienceEnd,
            audienceHovered,
            persistChanges: !embeddedPreview);
    }

    internal void DrawEditorPreview(
        Encounter encounter,
        IReadOnlyList<CombatantRow> allRows,
        MeterPreviewInteraction previewInteraction)
    {
        var settings = configuration.Meter;
        using var fontScale = new FontScaleScope(settings.ClassicWindow.FontScale);
        DrawEncounterHeader(encounter, settings, embeddedPreview: true);
        var rows = SelectClassicRows(allRows, settings);
        if (settings.ClassicAllianceView)
        {
            DrawAllianceCompactTiles(encounter, rows, settings);
        }
        else
        {
            DrawClassicTable(
                encounter,
                rows,
                settings,
                "classic-editor-preview-rows",
                previewInteraction);
        }
        if (ShouldDrawTeamSummary(settings))
        {
            MeterSlotPresentation.DrawTeamSummary(
                "classic-editor-preview",
                encounter,
                settings.ClassicWindow.Slots,
                text,
                new Vector4(0.70f, 0.74f, 0.80f, 1),
                Gold,
                previewInteraction);
        }
    }

    private void HandleRankingModeToggle(
        MeterSettings settings,
        bool hovered,
        bool persistChanges = true)
    {
        if (!hovered || !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            return;
        }

        var mode = MeterSortModeOptions.Normalize(settings.ClassicWindow.SortMode) == MeterSortMode.Hps
            ? MeterSortMode.Dps
            : MeterSortMode.Hps;
        settings.SortMode = mode;
        settings.ClassicWindow.SortMode = mode;
        MeterSlotPresentation.ReplacePrimaryMetric(settings.ClassicWindow, mode);
        SynchronizeClassicRateVisibility(settings);
        if (persistChanges)
        {
            saveConfiguration();
        }
    }

    private void DrawRankingModeIcon(
        ImDrawListPtr drawList,
        MeterSettings settings,
        Vector2 start,
        Vector2 end,
        bool hovered)
    {
        var hps = MeterSortModeOptions.Normalize(settings.ClassicWindow.SortMode) == MeterSortMode.Hps;
        DrawHeaderIconFrame(drawList, settings, start, end, hovered, hps ? HealingGreen : IceBlue);
        var color = ImGui.GetColorU32(hovered ? Vector4.One : hps ? HealingGreen : IceBlue);
        var label = hps ? text.Get("HPS 榜", "HPS") : text.Get("DPS 榜", "DPS");
        var labelSize = ImGui.CalcTextSize(label);
        drawList.AddText(
            start + ((end - start - labelSize) * 0.5f),
            color,
            label);
        if (hovered)
        {
            ImGui.SetTooltip(text.Get(
                hps ? "切换到 DPS 榜" : "切换到 HPS 榜",
                hps ? "Switch to DPS ranking" : "Switch to HPS ranking"));
        }
    }

    private void DrawAudienceModeDropdown(
        ImDrawListPtr drawList,
        MeterSettings settings,
        Vector2 start,
        Vector2 end,
        bool hovered,
        bool persistChanges = true)
    {
        DrawHeaderIconFrame(drawList, settings, start, end, hovered, Gold);
        var label = settings.ClassicAllianceView
            ? text.Get("24 人本", "24-player")
            : text.Get("8 人本", "8-player");
        var size = ImGui.CalcTextSize(label);
        drawList.AddText(
            start + ((end - start - size) * 0.5f),
            ImGui.GetColorU32(hovered ? Vector4.One : Gold),
            label);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            ImGui.OpenPopup("classic-party-size-popup");
        }
        if (hovered)
        {
            ImGui.SetTooltip(text.Get("切换 8 人本 / 24 人本", "Switch 8-player / 24-player mode"));
        }

        if (ImGui.BeginPopup("classic-party-size-popup"))
        {
            if (ImGui.Selectable(text.Get("8 人本", "8-player duty"), !settings.ClassicAllianceView))
            {
                settings.ClassicAllianceView = false;
                if (persistChanges)
                {
                    saveConfiguration();
                }
            }
            if (ImGui.Selectable(text.Get("24 人本", "24-player duty"), settings.ClassicAllianceView))
            {
                settings.ClassicAllianceView = true;
                if (persistChanges)
                {
                    saveConfiguration();
                }
            }
            ImGui.EndPopup();
        }
    }

    private static void DrawHeaderIconFrame(
        ImDrawListPtr drawList,
        MeterSettings settings,
        Vector2 start,
        Vector2 end,
        bool hovered,
        Vector4 accent)
    {
        var fill = hovered ? NavyHover : new Vector4(0.10f, 0.14f, 0.20f, 0.96f);
        drawList.AddRectFilled(
            start,
            end,
            ImGui.GetColorU32(ApplyBackgroundOpacity(fill, settings.ClassicWindow.BackgroundOpacity)),
            4);
        drawList.AddRect(start, end, ImGui.GetColorU32(accent), 4);
    }

    private static void SynchronizeClassicRateVisibility(MeterSettings settings)
    {
        bool Has(MeterSlotMetric metric) => settings.ClassicWindow.Slots.Any(slot =>
            slot.Visible && slot.Metric == metric);
        settings.ShowDps = Has(MeterSlotMetric.Dps);
        settings.ShowRdps = Has(MeterSlotMetric.Rdps);
        settings.ShowEncDps = Has(MeterSlotMetric.EncDps);
        settings.ShowExtDps = Has(MeterSlotMetric.ExtDps);
        settings.ShowHps = Has(MeterSlotMetric.Hps);
    }

    private string LocalizeEncounterTitle(Encounter encounter)
    {
        if (UsesStatusAsEncounterTitle(encounter))
        {
            return text.Get("状态：", "Status: ") + ResolveEncounterStateText(encounter);
        }

        return string.Equals(
            encounter.EnemyName,
            encounter.ZoneName,
            StringComparison.OrdinalIgnoreCase)
            ? localizeZoneName(encounter.TerritoryId, encounter.EnemyName)
            : encounter.EnemyName;
    }

    private static bool UsesStatusAsEncounterTitle(Encounter encounter)
        => string.IsNullOrWhiteSpace(encounter.EnemyName) ||
           string.Equals(encounter.EnemyName, "Encounter", StringComparison.OrdinalIgnoreCase);

    private string ResolveEncounterStateText(Encounter encounter)
        => encounter.IsActive
            ? encounter.IsTransitioning
                ? text.Get("转阶段", "Transition")
                : text.Get("战斗中", "Running")
            : text.Get("已结束", "Ended");

    private void DrawCompactDragHandle(MeterSettings settings)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var toggleStart = new Vector2(start.X + width - CompactToggleSize, start.Y);
        var toggleEnd = toggleStart + new Vector2(CompactToggleSize, CompactToggleSize);
        var rankingStart = toggleStart - new Vector2(CompactToggleSize + 5, 0);
        var rankingEnd = rankingStart + new Vector2(CompactToggleSize);
        var toggleHovered = CanInteractWithCompactToggle(settings) &&
                            ImGui.IsMouseHoveringRect(toggleStart, toggleEnd);
        var rankingHovered = CanInteractWithCompactToggle(settings) &&
                             ImGui.IsMouseHoveringRect(rankingStart, rankingEnd);
        ImGui.InvisibleButton("meter-compact-drag", new Vector2(width, CompactDragHandleHeight));
        HandleHeaderDrag(settings, allowStart: !toggleHovered && !rankingHovered);
        HandleCompactModeToggle(settings, toggleHovered);
        HandleRankingModeToggle(settings, rankingHovered);
        var drawList = ImGui.GetWindowDrawList();
        if (!settings.IsLocked && ImGui.IsItemHovered() && !toggleHovered)
        {
            var centerY = start.Y + (CompactDragHandleHeight * 0.5f);
            drawList.AddLine(
                new Vector2(start.X + 8, centerY),
                new Vector2(toggleStart.X - 8, centerY),
                ImGui.GetColorU32(new Vector4(Gold.X, Gold.Y, Gold.Z, 0.65f)),
                2);
        }
        DrawCompactModeToggle(drawList, settings, toggleStart, toggleEnd, toggleHovered);
        DrawRankingModeIcon(drawList, settings, rankingStart, rankingEnd, rankingHovered);
    }

    private void HandleHeaderDrag(MeterSettings settings, bool allowStart = true)
    {
        if (settings.IsLocked)
        {
            return;
        }

        if (allowStart && ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            isDragging = true;
            dragOffset = ImGui.GetMousePos() - ImGui.GetWindowPos();
        }

        if (isDragging && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetMousePos() - dragOffset, ImGuiCond.Always);
        }

        if (isDragging && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            isDragging = false;
        }
    }

    private static bool CanInteractWithCompactToggle(MeterSettings settings)
        => !ShouldPassMouseInputThrough(settings);

    internal static bool ShouldPassMouseInputThrough(MeterSettings settings)
        => settings.IsLocked && settings.ClickThroughWhenLocked;

    internal static int NoInputsFlagMask => (int)ImGuiWindowFlags.NoInputs;

    internal static int HorizontalScrollbarFlagMask
        => (int)ImGuiWindowFlags.HorizontalScrollbar;

    internal static int BuildRowsChildFlags(
        MeterSettings settings,
        bool useHorizontalScroll)
    {
        var flags = useHorizontalScroll
            ? HorizontalScrollbarFlagMask
            : (int)ImGuiWindowFlags.None;
        if (ShouldPassMouseInputThrough(settings))
        {
            // ImGui child windows own their own hover/input state. Applying NoInputs only
            // to the parent can still make the expanded ranking capture clicks over the game.
            flags |= NoInputsFlagMask;
        }

        return flags;
    }

    private void HandleCompactModeToggle(
        MeterSettings settings,
        bool hovered)
    {
        if (!hovered || !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            return;
        }

        if (settings.CompactMode)
        {
            settings.CompactMode = false;
            observedCompactMode = false;
            RestoreExpandedWindowSize(settings);
        }
        else
        {
            CaptureExpandedWindowSize(settings, ImGui.GetWindowSize());
            settings.CompactMode = true;
            observedCompactMode = true;
        }
        isDragging = false;
        saveConfiguration();
    }

    private void SynchronizeCompactMode(MeterSettings settings)
    {
        if (settings.CompactMode == observedCompactMode)
        {
            return;
        }

        if (settings.CompactMode)
        {
            CaptureExpandedWindowSize(settings, ImGui.GetWindowSize());
        }
        else
        {
            RestoreExpandedWindowSize(settings);
        }

        observedCompactMode = settings.CompactMode;
        saveConfiguration();
    }

    private static void CaptureExpandedWindowSize(MeterSettings settings, Vector2 size)
    {
        if (!IsValidExpandedWindowSize(size))
        {
            return;
        }

        settings.ExpandedWindowWidth = size.X;
        settings.ExpandedWindowHeight = size.Y;
    }

    private static bool IsValidExpandedWindowSize(Vector2 size)
        => float.IsFinite(size.X) &&
           float.IsFinite(size.Y) &&
           size.X >= MinimumExpandedWindowWidth &&
           size.Y >= MinimumExpandedWindowHeight;

    internal static Vector2 NormalizeExpandedWindowSize(MeterSettings settings)
        => new(
            float.IsFinite(settings.ExpandedWindowWidth) &&
            settings.ExpandedWindowWidth >= MinimumExpandedWindowWidth
                ? settings.ExpandedWindowWidth
                : DefaultExpandedWindowWidth,
            float.IsFinite(settings.ExpandedWindowHeight) &&
            settings.ExpandedWindowHeight >= MinimumExpandedWindowHeight
                ? settings.ExpandedWindowHeight
                : DefaultExpandedWindowHeight);

    private void RestoreExpandedWindowSize(MeterSettings settings)
    {
        var targetSize = NormalizeExpandedWindowSize(settings);
        var currentSize = ImGui.GetWindowSize();
        if (Math.Abs(currentSize.X - targetSize.X) > 0.5f)
        {
            ImGui.SetWindowSize(
                new Vector2(targetSize.X, currentSize.Y),
                ImGuiCond.Always);
        }

        BeginWindowHeightAnimation(targetSize.Y);
    }

    private void ApplyCompactWindowHeight(MeterSettings settings)
    {
        if (!settings.CompactMode)
        {
            return;
        }

        var bodyHeight = settings.ClassicAllianceView
            ? 34
            : CalculateCombatantRowHeight(ImGui.GetTextLineHeightWithSpacing()) + 26;
        var targetHeight = MathF.Ceiling(
            (ImGui.GetStyle().WindowPadding.Y * 2) +
            (settings.ShowHeader ? EncounterHeaderHeight : CompactDragHandleHeight) +
            ImGui.GetStyle().ItemSpacing.Y +
            bodyHeight +
            2);
        BeginWindowHeightAnimation(targetHeight);
    }

    private void ApplyEmptyStateCompactWindowHeight(MeterSettings settings)
    {
        if (!settings.CompactMode)
        {
            return;
        }

        var targetHeight = CalculateEmptyStateWindowHeight(
            ImGui.GetStyle().WindowPadding.Y);
        BeginWindowHeightAnimation(targetHeight);
    }

    private void BeginWindowHeightAnimation(float targetHeight)
    {
        if (!float.IsFinite(targetHeight) || targetHeight <= 0)
        {
            return;
        }

        if (isHeightAnimationActive &&
            Math.Abs(heightAnimationTarget - targetHeight) <= 0.5f)
        {
            return;
        }

        var currentHeight = ImGui.GetWindowSize().Y;
        if (!float.IsFinite(currentHeight) ||
            Math.Abs(currentHeight - targetHeight) <= 0.5f)
        {
            isHeightAnimationActive = false;
            return;
        }

        heightAnimationStart = currentHeight;
        heightAnimationTarget = targetHeight;
        heightAnimationElapsedSeconds = 0;
        isHeightAnimationActive = true;
    }

    private void AdvanceWindowHeightAnimation()
    {
        if (!isHeightAnimationActive)
        {
            return;
        }

        var deltaTime = ImGui.GetIO().DeltaTime;
        if (!float.IsFinite(deltaTime) || deltaTime <= 0)
        {
            deltaTime = 1f / 60f;
        }

        heightAnimationElapsedSeconds += Math.Min(deltaTime, 0.05f);
        var progress = Math.Clamp(
            heightAnimationElapsedSeconds / WindowResizeAnimationDurationSeconds,
            0,
            1);
        var height = float.Lerp(
            heightAnimationStart,
            heightAnimationTarget,
            EaseOutCubic(progress));
        if (progress >= 1)
        {
            height = heightAnimationTarget;
            isHeightAnimationActive = false;
        }

        var currentSize = ImGui.GetWindowSize();
        ImGui.SetWindowSize(new Vector2(currentSize.X, height), ImGuiCond.Always);
    }

    private void DrawCompactModeToggle(
        ImDrawListPtr drawList,
        MeterSettings settings,
        Vector2 start,
        Vector2 end,
        bool hovered)
    {
        var pressed = hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var fill = pressed
            ? new Vector4(0.07f, 0.11f, 0.17f, 0.98f)
            : hovered
                ? settings.CompactMode
                    ? new Vector4(0.21f, 0.40f, 0.49f, 0.98f)
                    : NavyHover
                : settings.CompactMode
                    ? new Vector4(0.17f, 0.34f, 0.42f, 0.96f)
                    : new Vector4(0.10f, 0.14f, 0.20f, 0.96f);
        var accent = settings.CompactMode ? IceBlue : Gold;
        if (!hovered && !settings.CompactMode)
        {
            accent.W = 0.72f;
        }
        drawList.AddRectFilled(
            start,
            end,
            ImGui.GetColorU32(ApplyBackgroundOpacity(fill, settings.ClassicWindow.BackgroundOpacity)),
            4);
        drawList.AddRect(
            start,
            end,
            ImGui.GetColorU32(accent),
            4);
        DrawCompactChevron(
            drawList,
            start,
            end,
            pointsDown: settings.CompactMode,
            hovered ? Vector4.One : accent);
        if (hovered)
        {
            ImGui.SetTooltip(text.Get(
                settings.CompactMode ? "展开：显示完整队伍" : "收起：只显示自己",
                settings.CompactMode ? "Expand: show the full party" : "Collapse: show only yourself"));
        }
    }

    private static void DrawCompactChevron(
        ImDrawListPtr drawList,
        Vector2 start,
        Vector2 end,
        bool pointsDown,
        Vector4 color)
    {
        var center = (start + end) * 0.5f;
        const float halfWidth = 4.5f;
        const float halfHeight = 2.5f;
        var middle = new Vector2(center.X, center.Y + (pointsDown ? halfHeight : -halfHeight));
        var edgeY = center.Y + (pointsDown ? -halfHeight : halfHeight);
        var packedColor = ImGui.GetColorU32(color);
        drawList.AddLine(new Vector2(center.X - halfWidth, edgeY), middle, packedColor, 2.2f);
        drawList.AddLine(middle, new Vector2(center.X + halfWidth, edgeY), packedColor, 2.2f);
    }

    private void DrawEncounterStateIcon(ImDrawListPtr drawList, Encounter encounter, Vector2 start)
    {
        const float iconSize = 24;
        var texture = encounter.IsActive
            ? encounter.IsTransitioning
                ? transitionStatusIcon
                : runningStatusIcon
            : endedStatusIcon;
        var wrap = texture.GetWrapOrEmpty();
        if (wrap.Handle.Handle != 0)
        {
            drawList.AddImage(
                wrap.Handle,
                start,
                start + new Vector2(iconSize, iconSize));
            return;
        }

        var stateColor = encounter.IsActive
            ? encounter.IsTransitioning
                ? new Vector4(0.95f, 0.68f, 0.24f, 1)
                : new Vector4(0.38f, 0.78f, 0.66f, 1)
            : new Vector4(0.66f, 0.69f, 0.74f, 1);
        var fallbackGlyph = encounter.IsActive
            ? encounter.IsTransitioning ? "◆" : "●"
            : "○";
        drawList.AddText(
            start + new Vector2(4, 2),
            ImGui.GetColorU32(stateColor),
            fallbackGlyph);
    }

    private static IReadOnlyList<CombatantRow> SelectClassicRows(
        IReadOnlyList<CombatantRow> rows,
        MeterSettings settings)
    {
        var players = rows.Where(static row => !MeterService.IsLimitBreak(row.Id, row.Name))
            .ToArray();
        if (settings.CompactMode)
        {
            // Compact mode is a self card, not an empty header when the local row arrives late.
            var localPlayer = players.FirstOrDefault(static row => row.IsLocalPlayer) ??
                              players.FirstOrDefault();
            return localPlayer is null ? [] : [localPlayer];
        }

        var ranked = MeterSlotPresentation.SortAndRank(
            players,
            settings.ClassicWindow.SortMode,
            settings.ClassicWindow.DpsSortMetric);
        if (settings.ClassicAllianceView)
        {
            return ranked.Take(24).ToArray();
        }

        return MeterSlotPresentation.SelectParty(
            ranked,
            MeterSlotPresentation.ResolveLocalPartyGroup(ranked));
    }

    internal void DrawClassicTable(
        Encounter encounter,
        IReadOnlyList<CombatantRow> rows,
        MeterSettings settings,
        string childId,
        MeterPreviewInteraction? previewInteraction = null)
    {
        var sortMode = MeterSortModeOptions.Normalize(settings.ClassicWindow.SortMode);
        var showFflogs = ShouldShowFflogsColumn(configuration.Fflogs.Enabled, settings);
        var columnWidths = MeasureColumnWidths(rows, settings, showFflogs);
        var availableTableWidth = ImGui.GetContentRegionAvail().X;
        var minimumIdentityWidth = MeasureMinimumIdentityColumnWidth(settings);
        var minimumColumnWidths = columnWidths with
        {
            Identity = columnWidths.Identity is null ? null : minimumIdentityWidth,
        };
        var minimumTableWidth = CalculateRequiredTableWidth(minimumColumnWidths, settings.FontScale);
        var useHorizontalScroll = ShouldEnableHorizontalScroll(availableTableWidth, minimumTableWidth);
        var summaryReserve = ShouldDrawTeamSummary(settings) &&
                             MeterSlotPresentation.HasTeamSummary(settings.ClassicWindow.Slots)
            ? MeterSlotPresentation.TeamSummaryHeight + 4
            : 0;
        if (useHorizontalScroll)
        {
            // Long player IDs may be truncated, so scrolling begins only after the
            // identity column has reached its readable two-character minimum.
            ImGui.SetNextWindowContentSize(new Vector2(minimumTableWidth, 0));
        }

        var childHeight = Math.Max(
            CalculateCombatantRowHeight(ImGui.GetTextLineHeightWithSpacing()) + 26,
            ImGui.GetContentRegionAvail().Y - summaryReserve);
        if (ImGui.BeginChild(
                childId,
                new Vector2(-1, childHeight),
                false,
                (ImGuiWindowFlags)BuildRowsChildFlags(settings, useHorizontalScroll)))
        {
            var layout = BuildColumnLayout(
                ImGui.GetContentRegionAvail().X,
                columnWidths,
                settings,
                minimumIdentityWidth);
            DrawTableHeader(layout, settings, previewInteraction);
            var maximumScore = Math.Max(
                1,
                rows.Max(row => Score(row, sortMode, settings.ClassicWindow.DpsSortMetric)));
            foreach (var row in rows)
            {
                DrawCombatantRow(
                    row,
                    maximumScore,
                    encounter,
                    settings,
                    layout,
                    showFflogs && !MeterService.IsLimitBreak(row.Id, row.Name)
                        ? fflogsEstimateService.GetEstimate(encounter, row.Id, row.Name)
                        : null,
                    previewInteraction);
            }
        }
        ImGui.EndChild();
    }

    private void DrawAllianceCompactTiles(
        Encounter encounter,
        IReadOnlyList<CombatantRow> rows,
        MeterSettings settings)
    {
        // Alliance mode intentionally ignores editable slots: 24 players must remain
        // readable in one ungrouped view, so every row keeps the same two fixed fields.
        var availableWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
        const float spacing = 4;
        const float desiredWidth = 180;
        var columns = Math.Max(1, (int)MathF.Floor((availableWidth + spacing) / (desiredWidth + spacing)));
        var tileWidth = Math.Max(42, (availableWidth - ((columns - 1) * spacing)) / columns);
        const float tileHeight = 34;
        var origin = ImGui.GetCursorScreenPos();
        var maximumScore = Math.Max(
            1,
            rows.Max(row => Score(
                row,
                settings.ClassicWindow.SortMode,
                settings.ClassicWindow.DpsSortMetric)));
        for (var index = 0; index < rows.Count; index++)
        {
            var column = index % columns;
            var rowIndex = index / columns;
            var start = origin + new Vector2(
                column * (tileWidth + spacing),
                rowIndex * (tileHeight + spacing));
            ImGui.SetCursorScreenPos(start);
            ImGui.InvisibleButton(
                $"classic-tile-{rows[index].Id}-{rows[index].Name}",
                new Vector2(tileWidth, tileHeight));
            DrawAllianceCompactTile(
                encounter,
                rows[index],
                start,
                new Vector2(tileWidth, tileHeight),
                maximumScore,
                settings);
        }

        var usedRows = (rows.Count + columns - 1) / columns;
        ImGui.SetCursorScreenPos(origin + new Vector2(0, usedRows * (tileHeight + spacing)));
    }

    private void DrawAllianceCompactTile(
        Encounter encounter,
        CombatantRow row,
        Vector2 start,
        Vector2 size,
        double maximumScore,
        MeterSettings settings)
    {
        var drawList = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsItemHovered();
        drawList.AddRectFilled(
            start,
            start + size,
            ImGui.GetColorU32(ApplyBackgroundOpacity(
                hovered ? NavyHover : NavyRaised,
                settings.ClassicWindow.BackgroundOpacity)),
            6);
        var ratio = (float)Math.Clamp(
            Score(
                row,
                settings.ClassicWindow.SortMode,
                settings.ClassicWindow.DpsSortMetric) / maximumScore,
            0,
            1);
        var jobColor = JobColor(row.Job);
        drawList.AddRectFilled(
            start,
            start + new Vector2(size.X * ratio, size.Y),
            ImGui.GetColorU32(ApplyBackgroundOpacity(
                new Vector4(jobColor.X, jobColor.Y, jobColor.Z, row.IsLocalPlayer ? 0.32f : 0.17f),
                settings.ClassicWindow.BackgroundOpacity)),
            6);

        var displayName = MeterSlotPresentation.DisplayName(row, encounter, settings, text);
        var cursorX = start.X + 7;
        cursorX += DrawTileJob(row, new Vector2(cursorX, start.Y + 6), 21, settings) + 5;
        var value = MeterSortModeOptions.Normalize(settings.ClassicWindow.SortMode) == MeterSortMode.Hps
            ? $"{row.Hps:N0}"
            : $"{MeterSlotPresentation.DpsScore(row, settings.ClassicWindow.DpsSortMetric):N0}";
        var valueWidth = ImGui.CalcTextSize(value).X;
        drawList.AddText(
            new Vector2(cursorX, start.Y + 8),
            ImGui.GetColorU32(row.IsLocalPlayer ? Gold : Vector4.One),
            TrimToWidth(displayName, Math.Max(12, start.X + size.X - cursorX - valueWidth - 16)));
        drawList.AddText(
            new Vector2(start.X + size.X - valueWidth - 7, start.Y + 8),
            ImGui.GetColorU32(
                MeterSortModeOptions.Normalize(settings.ClassicWindow.SortMode) == MeterSortMode.Hps
                    ? HealingGreen
                    : PrimaryRateColor(row.IsLocalPlayer)),
            value);
    }

    private float DrawTileJob(
        CombatantRow row,
        Vector2 start,
        float size,
        MeterSettings settings)
    {
        var texture = jobIcons.Get(settings.JobDisplayStyle, row.Job);
        if (texture is not null)
        {
            ImGui.GetWindowDrawList().AddImage(
                texture.GetWrapOrEmpty().Handle,
                start,
                start + new Vector2(size));
            return size;
        }

        var job = JobDisplayFormatter.FormatText(row.Job, settings.JobDisplayStyle);
        ImGui.GetWindowDrawList().AddText(
            start + new Vector2(0, 2),
            ImGui.GetColorU32(IceBlue),
            job);
        return ImGui.CalcTextSize(job).X;
    }

    private MeterColumnLayout BuildColumnLayout(
        float availableWidth,
        MeterColumnWidths widths,
        MeterSettings settings,
        float minimumIdentityWidth)
    {
        var scale = Math.Clamp(settings.FontScale, 0.75f, 1.8f);
        var right = Math.Max(0, availableWidth - (TableRightPadding * scale));
        var preferredTableWidth = CalculateRequiredTableWidth(widths, settings.FontScale);
        var (resolvedIdentityWidth, resolvedHighestDamageWidth) =
            ResolveAdaptiveColumnWidths(
                availableWidth,
                preferredTableWidth,
                widths.Identity,
                minimumIdentityWidth,
                widths.HighestDamage);
        MeterColumn Take(float width, MeterSlotDefinition? slot = null)
        {
            right -= width;
            var column = new MeterColumn(right, width, slot);
            right -= ColumnSpacing * scale;
            return column;
        }

        MeterColumn? rank = null;
        MeterColumn? identity = null;
        MeterColumn? fflogs = null;
        MeterColumn? deaths = null;
        MeterColumn? damagePercent = null;
        MeterColumn? totalDamage = null;
        MeterColumn? totalHealing = null;
        MeterColumn? highestDamage = null;
        MeterColumn? criticalDirectHit = null;
        MeterColumn? directHit = null;
        MeterColumn? criticalHit = null;
        MeterColumn? hps = null;
        MeterColumn? dps = null;
        MeterColumn? rdps = null;
        MeterColumn? encDps = null;
        MeterColumn? extDps = null;

        // Slots are stored left-to-right. Building from the right preserves that order
        // while retaining the compact fixed-column renderer and its stable widths.
        foreach (var slot in settings.ClassicWindow.Slots
                     .Where(static slot => slot.Visible)
                     .Reverse())
        {
            switch (slot.Metric)
            {
                case MeterSlotMetric.Rank when rank is null && widths.Rank is { } value:
                    rank = Take(value, slot);
                    break;
                case MeterSlotMetric.PlayerIdentity when identity is null && widths.Identity is not null:
                    identity = Take(resolvedIdentityWidth, slot);
                    break;
                case MeterSlotMetric.Fflogs when fflogs is null && widths.Fflogs is { } value:
                    fflogs = Take(value, slot);
                    break;
                case MeterSlotMetric.Dps when dps is null && widths.Dps is { } value:
                    dps = Take(value, slot);
                    break;
                case MeterSlotMetric.Rdps when rdps is null && widths.Rdps is { } value:
                    rdps = Take(value, slot);
                    break;
                case MeterSlotMetric.EncDps when encDps is null && widths.EncDps is { } value:
                    encDps = Take(value, slot);
                    break;
                case MeterSlotMetric.ExtDps when extDps is null && widths.ExtDps is { } value:
                    extDps = Take(value, slot);
                    break;
                case MeterSlotMetric.Hps when hps is null && widths.Hps is { } value:
                    hps = Take(value, slot);
                    break;
                case MeterSlotMetric.CriticalHitPercent when criticalHit is null && widths.CriticalHit is { } value:
                    criticalHit = Take(value, slot);
                    break;
                case MeterSlotMetric.DirectHitPercent when directHit is null && widths.DirectHit is { } value:
                    directHit = Take(value, slot);
                    break;
                case MeterSlotMetric.CriticalDirectHitPercent when criticalDirectHit is null && widths.CriticalDirectHit is { } value:
                    criticalDirectHit = Take(value, slot);
                    break;
                case MeterSlotMetric.DamagePercent when damagePercent is null && widths.DamagePercent is { } value:
                    damagePercent = Take(value, slot);
                    break;
                case MeterSlotMetric.TotalDamage when totalDamage is null && widths.TotalDamage is { } value:
                    totalDamage = Take(value, slot);
                    break;
                case MeterSlotMetric.TotalHealing when totalHealing is null && widths.TotalHealing is { } value:
                    totalHealing = Take(value, slot);
                    break;
                case MeterSlotMetric.HighestDamageAction or MeterSlotMetric.HighestDamage
                    when highestDamage is null && widths.HighestDamage is not null:
                    highestDamage = Take(resolvedHighestDamageWidth, slot);
                    break;
                case MeterSlotMetric.Deaths when deaths is null && widths.Deaths is { } value:
                    deaths = Take(value, slot);
                    break;
            }
        }

        // A legacy quick setting may expose a column before its slot exists. Keep it
        // visible on the left until the editor next synchronizes the profile.
        rank ??= widths.Rank is { } rankWidth ? Take(rankWidth) : null;
        identity ??= widths.Identity is not null
            ? Take(resolvedIdentityWidth)
            : null;
        fflogs ??= widths.Fflogs is { } fflogsWidth ? Take(fflogsWidth) : null;
        dps ??= widths.Dps is { } dpsWidth ? Take(dpsWidth) : null;
        rdps ??= widths.Rdps is { } rdpsWidth ? Take(rdpsWidth) : null;
        encDps ??= widths.EncDps is { } encDpsWidth ? Take(encDpsWidth) : null;
        extDps ??= widths.ExtDps is { } extDpsWidth ? Take(extDpsWidth) : null;
        hps ??= widths.Hps is { } hpsWidth ? Take(hpsWidth) : null;
        criticalHit ??= widths.CriticalHit is { } criticalHitWidth ? Take(criticalHitWidth) : null;
        directHit ??= widths.DirectHit is { } directHitWidth ? Take(directHitWidth) : null;
        criticalDirectHit ??= widths.CriticalDirectHit is { } criticalDirectHitWidth ? Take(criticalDirectHitWidth) : null;
        damagePercent ??= widths.DamagePercent is { } damagePercentWidth ? Take(damagePercentWidth) : null;
        totalDamage ??= widths.TotalDamage is { } totalDamageWidth ? Take(totalDamageWidth) : null;
        totalHealing ??= widths.TotalHealing is { } totalHealingWidth ? Take(totalHealingWidth) : null;
        highestDamage ??= widths.HighestDamage is not null
            ? Take(resolvedHighestDamageWidth)
            : null;
        deaths ??= widths.Deaths is { } deathsWidth ? Take(deathsWidth) : null;
        return new MeterColumnLayout(
            rank,
            identity,
            fflogs,
            dps,
            rdps,
            encDps,
            extDps,
            hps,
            criticalHit,
            directHit,
            criticalDirectHit,
            damagePercent,
            totalDamage,
            totalHealing,
            highestDamage,
            deaths);
    }

    private MeterColumnWidths MeasureColumnWidths(
        IReadOnlyList<CombatantRow> rows,
        MeterSettings settings,
        bool showFflogs)
    {
        var scale = Math.Clamp(settings.FontScale, 0.75f, 1.8f);
        float StableWidth(float nominalWidth, string header, IEnumerable<string> values)
            => ResolveStableColumnWidth(
                nominalWidth,
                scale,
                ImGui.CalcTextSize(header).X,
                values.Select(value => ImGui.CalcTextSize(value).X)
                    .DefaultIfEmpty(0)
                    .Max());

        return new MeterColumnWidths(
            settings.ShowRank
                ? StableWidth(RankColumnWidth, "#", rows.Select(static row => row.Rank?.ToString() ?? "--"))
                : null,
            settings.ShowPlayerName || settings.ShowJob
                ? Math.Min(
                    240 * scale,
                    StableWidth(
                        IdentityColumnWidth,
                        text.Get("职业 / ID", "Job / ID"),
                        rows.Select(static row => $"{row.Job}  {row.Name}")))
                : null,
            showFflogs
                ? StableWidth(FflogsColumnWidth, "FFLogs", ["100", "--"])
                : null,
            settings.ShowDps
                ? StableWidth(
                    RateColumnWidth,
                    "DPS",
                    rows.Select(static row => $"{row.PersonalDps:N0}"))
                : null,
            settings.ShowRdps
                ? StableWidth(
                    RateColumnWidth,
                    "rDPS",
                    rows.Select(static row => $"{row.Rdps:N0}"))
                : null,
            settings.ShowEncDps
                ? StableWidth(
                    RateColumnWidth,
                    "EncDPS",
                    rows.Select(static row => $"{row.EncDps:N0}"))
                : null,
            settings.ShowExtDps
                ? StableWidth(
                    RateColumnWidth,
                    "ExtDPS",
                    rows.Select(static row => $"{row.ExtDps:N0}"))
                : null,
            settings.ShowHps
                ? StableWidth(RateColumnWidth, "HPS", rows.Select(static row => $"{row.Hps:N0}"))
                : null,
            settings.ShowCriticalHitRate
                ? StableWidth(
                    HitRateColumnWidth,
                    text.Get("暴击", "CRIT"),
                    rows.Select(static row => FormatHitRateValue(row.CriticalHitPercent)))
                : null,
            settings.ShowDirectHitRate
                ? StableWidth(
                    HitRateColumnWidth,
                    text.Get("直击", "DH"),
                    rows.Select(static row => FormatHitRateValue(row.DirectHitPercent)))
                : null,
            settings.ShowCriticalDirectHitRate
                ? StableWidth(
                    HitRateColumnWidth,
                    text.Get("直暴", "CDH"),
                    rows.Select(static row => FormatHitRateValue(row.CriticalDirectHitPercent)))
                : null,
            settings.ShowDamagePercent
                ? StableWidth(
                    DamagePercentColumnWidth,
                    text.Get("占比", "DMG%"),
                    rows.Select(static row => $"{row.DamagePercent:N1}%"))
                : null,
            null,
            null,
            settings.ShowHighestDamage
                // Full action names remain available through the row tooltip; reserving
                // them here would force a permanent horizontal scrollbar.
                ? StableWidth(
                    HighestDamageColumnWidth,
                    text.Get("最高伤害", "Max hit"),
                    [])
                : null,
            settings.ShowDeaths
                ? StableWidth(
                    DeathsColumnWidth,
                    text.Get("死亡", "KO"),
                    rows.Select(static row => row.Deaths.ToString()))
                : null);
    }

    private float MeasureMinimumIdentityColumnWidth(MeterSettings settings)
    {
        var width = 6f;
        if (settings.ShowJob)
        {
            if (JobDisplayFormatter.UsesIcon(settings.JobDisplayStyle))
            {
                width += CalculateJobIconSize(
                    CalculateCombatantRowHeight(ImGui.GetTextLineHeightWithSpacing()),
                    ImGui.GetTextLineHeight()) + 7;
            }
            else
            {
                var jobSample = settings.JobDisplayStyle == JobDisplayStyle.ChineseAbbreviation
                    ? "白魔"
                    : "WHM";
                width += Math.Max(35, ImGui.CalcTextSize(jobSample).X + 10) + 8;
            }
        }

        if (settings.ShowPlayerName)
        {
            width += ImGui.CalcTextSize(text.Get("杰克...", "Ja...")).X;
        }
        return width;
    }

    private void DrawTableHeader(
        MeterColumnLayout layout,
        MeterSettings settings,
        MeterPreviewInteraction? previewInteraction)
    {
        var headerHeight = Math.Max(22, ImGui.GetTextLineHeightWithSpacing() + 4);
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("meter-column-header", new Vector2(width, headerHeight));

        var drawList = ImGui.GetWindowDrawList();
        var end = start + new Vector2(width, headerHeight);
        drawList.AddRectFilled(
            start,
            end,
            ImGui.GetColorU32(ApplyBackgroundOpacity(
                new Vector4(NavyRaised.X, NavyRaised.Y, NavyRaised.Z, 0.78f),
                settings.ClassicWindow.BackgroundOpacity)),
            4);
        drawList.AddLine(
            new Vector2(start.X, end.Y),
            new Vector2(end.X, end.Y),
            ImGui.GetColorU32(new Vector4(Gold.X, Gold.Y, Gold.Z, 0.55f)));

        var color = ImGui.GetColorU32(new Vector4(0.70f, 0.74f, 0.80f, 1));
        var lineY = start.Y + (headerHeight - ImGui.GetTextLineHeight()) * 0.5f;
        void DrawColumnHeader(string label, MeterColumn column, bool alignLeft = false)
        {
            var fittedLabel = TrimToWidth(label, Math.Max(1, column.Width - 6));
            var size = ImGui.CalcTextSize(fittedLabel);
            var columnStart = new Vector2(start.X + column.Offset, start.Y);
            var columnEnd = new Vector2(columnStart.X + column.Width, end.Y);
            previewInteraction?.Observe(column.Slot, columnStart, columnEnd, drawList);
            var columnX = alignLeft
                ? columnStart.X + 3
                : columnStart.X + Math.Max(0, column.Width - size.X);
            drawList.AddText(new Vector2(columnX, lineY), color, fittedLabel);
        }

        if (layout.Rank is { } rank)
        {
            DrawColumnHeader("#", rank, alignLeft: true);
        }
        if (layout.Identity is { } identity)
        {
            DrawColumnHeader(text.Get("职业 / ID", "Job / ID"), identity, alignLeft: true);
        }
        if (layout.Fflogs is { } fflogs)
        {
            DrawColumnHeader("FFLogs", fflogs);
        }
        if (layout.Dps is { } dps)
        {
            DrawColumnHeader("DPS", dps);
        }
        if (layout.Rdps is { } rdps)
        {
            DrawColumnHeader("rDPS", rdps);
        }
        if (layout.EncDps is { } encDps)
        {
            DrawColumnHeader("EncDPS", encDps);
        }
        if (layout.ExtDps is { } extDps)
        {
            DrawColumnHeader("ExtDPS", extDps);
        }
        if (layout.Hps is { } hps)
        {
            DrawColumnHeader("HPS", hps);
        }
        if (layout.CriticalHit is { } criticalHit)
        {
            DrawColumnHeader(text.Get("暴击", "CRIT"), criticalHit);
        }
        if (layout.DirectHit is { } directHit)
        {
            DrawColumnHeader(text.Get("直击", "DH"), directHit);
        }
        if (layout.CriticalDirectHit is { } criticalDirectHit)
        {
            DrawColumnHeader(text.Get("直暴", "CDH"), criticalDirectHit);
        }
        if (layout.DamagePercent is { } damagePercent)
        {
            DrawColumnHeader(text.Get("占比", "DMG%"), damagePercent);
        }
        if (layout.TotalDamage is { } totalDamage)
        {
            DrawColumnHeader(text.Get("总伤害", "Damage"), totalDamage);
        }
        if (layout.TotalHealing is { } totalHealing)
        {
            DrawColumnHeader(text.Get("总治疗", "Healing"), totalHealing);
        }
        if (layout.HighestDamage is { } highestDamage)
        {
            DrawColumnHeader(text.Get("最高伤害", "Max hit"), highestDamage);
        }
        if (layout.Deaths is { } deaths)
        {
            DrawColumnHeader(text.Get("死亡", "KO"), deaths);
        }

        var rdpsHovered = layout.Rdps is { } rdpsColumn && ImGui.IsMouseHoveringRect(
            new Vector2(start.X + rdpsColumn.Offset, start.Y),
            new Vector2(start.X + rdpsColumn.Offset + rdpsColumn.Width, end.Y));
        if (rdpsHovered)
        {
            ImGui.SetTooltip(text.Get(
                "rDPS（预估）\n基于本地战斗事件与团队增益归因实时估算的团队贡献伤害。结果仅供参考，可能因游戏版本、战斗事件状态及统计口径产生少量差异。",
                "rDPS (estimated)\nReal-time raid-contribution damage estimated from local combat events and party buffs. Results are informational and may vary slightly with game versions, event state, and statistical conventions."));
        }
        else if (layout.Fflogs is { } fflogsColumn && ImGui.IsMouseHoveringRect(
                     new Vector2(start.X + fflogsColumn.Offset, start.Y),
                     new Vector2(start.X + fflogsColumn.Offset + fflogsColumn.Width, end.Y)))
        {
            var reference = fflogsEstimateService.ReferenceSnapshot;
            var updated = reference.LatestDataUpdatedAt is { } date
                ? date.ToLocalTime().ToString("yyyy/MM/dd")
                : "--";
            ImGui.SetTooltip(text.Get(
                $"DPS Parse 预估\n根据本场实际 DPS 与当前 FFLogs 同职业、同副本、同分区的 DPS 分布估算。\nFFLogs 数据更新于：{updated}",
                $"Estimated DPS Parse\nEstimated from this encounter's actual DPS and the current FFLogs DPS distribution for the same job, encounter, and partition.\nFFLogs data updated: {updated}"));
        }
    }

    private void DrawCombatantRow(
        CombatantRow row,
        double maximumScore,
        Encounter encounter,
        MeterSettings settings,
        MeterColumnLayout layout,
        FflogsEstimate? estimate,
        MeterPreviewInteraction? previewInteraction)
    {
        var rowHeight = CalculateCombatantRowHeight(ImGui.GetTextLineHeightWithSpacing());
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var end = start + new Vector2(width, rowHeight);
        ImGui.InvisibleButton($"meter-row-{row.Id}-{row.Name}", new Vector2(width, rowHeight));

        var drawList = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsItemHovered();
        drawList.AddRectFilled(
            start,
            end,
            ImGui.GetColorU32(ApplyBackgroundOpacity(
                hovered ? NavyHover : NavyRaised,
                settings.ClassicWindow.BackgroundOpacity)),
            5);

        var sortMode = MeterSortModeOptions.Normalize(settings.ClassicWindow.SortMode);
        var jobColor = JobColor(row.Job);
        var ratio = (float)Math.Clamp(
            Score(row, sortMode, settings.ClassicWindow.DpsSortMetric) / maximumScore,
            0,
            1);
        var configuredLocalColor = settings.LocalPlayerColor;
        var barColor = row.IsLocalPlayer
            ? new Vector4(
                configuredLocalColor.X,
                configuredLocalColor.Y,
                configuredLocalColor.Z,
                Math.Clamp(configuredLocalColor.W, 0.12f, 0.65f))
            : new Vector4(jobColor.X, jobColor.Y, jobColor.Z, 0.20f);
        drawList.AddRectFilled(
            start,
            new Vector2(start.X + width * ratio, end.Y),
            ImGui.GetColorU32(ApplyBackgroundOpacity(barColor, settings.ClassicWindow.BackgroundOpacity)),
            5);
        if (row.IsLocalPlayer)
        {
            var borderColor = new Vector4(
                configuredLocalColor.X,
                configuredLocalColor.Y,
                configuredLocalColor.Z,
                0.95f);
            drawList.AddRect(start, end, ImGui.GetColorU32(borderColor), 5, ImDrawFlags.None, 1.5f);
        }

        var isLimitBreak = MeterService.IsLimitBreak(row.Id, row.Name);
        var combatant = encounter.Combatants.FirstOrDefault(item =>
            string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase));
        var displayName = isLimitBreak
            ? LimitBreakDisplayName
            : combatant is null
                ? row.Name
                : PlayerIdentityFormatter.Format(combatant, encounter.Combatants, settings, text);

        float DrawJob(float currentX, float textY)
        {
            if (!settings.ShowJob)
            {
                return currentX;
            }

            if (isLimitBreak)
            {
                var limitBreakTexture = jobIcons.GetLimitBreak();
                if (limitBreakTexture is not null)
                {
                    var iconSize = CalculateJobIconSize(rowHeight, ImGui.GetTextLineHeight());
                    var iconTop = start.Y + (rowHeight - iconSize) * 0.5f;
                    var wrap = limitBreakTexture.GetWrapOrEmpty();
                    drawList.AddImage(
                        wrap.Handle,
                        new Vector2(currentX, iconTop),
                        new Vector2(currentX + iconSize, iconTop + iconSize));
                    return currentX + iconSize + 7;
                }

                drawList.AddText(
                    new Vector2(currentX, textY),
                    ImGui.GetColorU32(Gold),
                    "LB");
                return currentX + ImGui.CalcTextSize("LB").X + 7;
            }

            var texture = jobIcons.Get(settings.JobDisplayStyle, row.Job);
            if (texture is not null)
            {
                var iconSize = CalculateJobIconSize(rowHeight, ImGui.GetTextLineHeight());
                var iconTop = start.Y + (rowHeight - iconSize) * 0.5f;
                var wrap = texture.GetWrapOrEmpty();
                drawList.AddImage(
                    wrap.Handle,
                    new Vector2(currentX, iconTop),
                    new Vector2(currentX + iconSize, iconTop + iconSize));
                return currentX + iconSize + 7;
            }

            var job = JobDisplayFormatter.FormatText(row.Job, settings.JobDisplayStyle);
            var jobSize = ImGui.CalcTextSize(job);
            var badgeSize = new Vector2(
                Math.Max(35, jobSize.X + 10),
                ImGui.GetTextLineHeight() + 4);
            drawList.AddRectFilled(
                new Vector2(currentX, textY - 1),
                new Vector2(currentX, textY - 1) + badgeSize,
                ImGui.GetColorU32(ApplyBackgroundOpacity(
                    new Vector4(jobColor.X, jobColor.Y, jobColor.Z, 0.55f),
                    settings.ClassicWindow.BackgroundOpacity)),
                4);
            drawList.AddText(
                new Vector2(currentX + (badgeSize.X - jobSize.X) * 0.5f, textY + 1),
                ImGui.GetColorU32(Vector4.One),
                job);
            return currentX + badgeSize.X + 8;
        }

        var lineY = start.Y + (rowHeight - ImGui.GetTextLineHeight()) * 0.5f;

        void DrawColumn(string value, MeterColumn column, Vector4 color)
        {
            var size = ImGui.CalcTextSize(value);
            var columnX = start.X + column.Offset + Math.Max(0, column.Width - size.X);
            drawList.AddText(new Vector2(columnX, lineY), ImGui.GetColorU32(color), value);
            previewInteraction?.Observe(
                column.Slot,
                new Vector2(start.X + column.Offset, start.Y),
                new Vector2(start.X + column.Offset + column.Width, end.Y),
                drawList,
                highlightSelection: false);
        }

        var highestDamageHovered = false;

        if (layout.Rank is { } rankColumn)
        {
            DrawColumn(
                row.Rank is { } rank ? rank.ToString() : "--",
                rankColumn,
                new Vector4(0.68f, 0.71f, 0.76f, 1));
        }
        if (layout.Identity is { } identity)
        {
            var identityStart = start.X + identity.Offset + 3;
            var identityTextX = DrawJob(identityStart, lineY);
            if (settings.ShowPlayerName)
            {
                var availableNameWidth = Math.Max(
                    12,
                    start.X + identity.Offset + identity.Width - identityTextX - 3);
                var fittedName = TrimToWidth(displayName, availableNameWidth);
                drawList.AddText(
                    new Vector2(identityTextX, lineY),
                    ImGui.GetColorU32(row.IsLocalPlayer || isLimitBreak ? Gold : Vector4.One),
                    fittedName);
                if (!string.Equals(fittedName, displayName, StringComparison.Ordinal) &&
                    ImGui.IsMouseHoveringRect(
                        new Vector2(start.X + identity.Offset, start.Y),
                        new Vector2(start.X + identity.Offset + identity.Width, end.Y)))
                {
                    ImGui.SetTooltip(displayName);
                }
            }
            previewInteraction?.Observe(
                identity.Slot,
                new Vector2(start.X + identity.Offset, start.Y),
                new Vector2(start.X + identity.Offset + identity.Width, end.Y),
                drawList,
                highlightSelection: false);
        }

        if (layout.Deaths is { } deaths)
        {
            DrawColumn(row.Deaths.ToString(), deaths, new Vector4(0.78f, 0.80f, 0.84f, 1));
        }
        if (layout.DamagePercent is { } damagePercent)
        {
            DrawColumn(
                $"{row.DamagePercent:N1}%",
                damagePercent,
                new Vector4(0.72f, 0.78f, 0.84f, 1));
        }
        if (layout.TotalDamage is { } totalDamage)
        {
            DrawColumn(
                FormatCompactNumber(row.TotalDamage),
                totalDamage,
                new Vector4(0.86f, 0.78f, 0.58f, 1));
        }
        if (layout.TotalHealing is { } totalHealing)
        {
            DrawColumn(
                FormatCompactNumber(row.TotalHealing),
                totalHealing,
                new Vector4(0.48f, 0.84f, 0.62f, 1));
        }
        if (layout.HighestDamage is { } highestDamage)
        {
            DrawColumn(
                TrimToWidth(FormatHighestDamage(row), highestDamage.Width),
                highestDamage,
                new Vector4(0.94f, 0.68f, 0.48f, 1));
            highestDamageHovered = ImGui.IsMouseHoveringRect(
                new Vector2(start.X + highestDamage.Offset, start.Y),
                new Vector2(start.X + highestDamage.Offset + highestDamage.Width, end.Y));
            if (highestDamageHovered && row.HighestDamage > 0)
            {
                ImGui.SetTooltip(text.Get(
                    $"最高单次：{row.HighestDamageAction} {row.HighestDamage:N0}",
                    $"Highest hit: {row.HighestDamageAction} {row.HighestDamage:N0}"));
            }
        }
        if (layout.CriticalHit is { } criticalHit)
        {
            DrawColumn(
                FormatHitRateValue(row.CriticalHitPercent),
                criticalHit,
                new Vector4(0.82f, 0.68f, 0.92f, 1));
        }
        if (layout.DirectHit is { } directHit)
        {
            DrawColumn(
                FormatHitRateValue(row.DirectHitPercent),
                directHit,
                new Vector4(0.52f, 0.78f, 0.92f, 1));
        }
        if (layout.CriticalDirectHit is { } criticalDirectHit)
        {
            DrawColumn(
                FormatHitRateValue(row.CriticalDirectHitPercent),
                criticalDirectHit,
                new Vector4(0.95f, 0.62f, 0.45f, 1));
        }
        if (layout.Hps is { } hps)
        {
            DrawColumn($"{row.Hps:N0}", hps, new Vector4(0.48f, 0.84f, 0.62f, 1));
        }
        if (layout.Dps is { } dps)
        {
            DrawColumn($"{row.PersonalDps:N0}", dps, PrimaryRateColor(row.IsLocalPlayer));
        }
        if (layout.Rdps is { } rdps)
        {
            DrawColumn($"{row.Rdps:N0}", rdps, PrimaryRateColor(row.IsLocalPlayer));
        }
        if (layout.EncDps is { } encDps)
        {
            DrawColumn($"{row.EncDps:N0}", encDps, PrimaryRateColor(row.IsLocalPlayer));
        }
        if (layout.ExtDps is { } extDps)
        {
            DrawColumn($"{row.ExtDps:N0}", extDps, PrimaryRateColor(row.IsLocalPlayer));
        }
        if (layout.Fflogs is { } fflogsColumn)
        {
            DrawColumn(
                isLimitBreak
                    ? string.Empty
                    : estimate is null ? "--" : estimate.Score.ToString(),
                fflogsColumn,
                estimate?.Color ?? new Vector4(0.66f, 0.69f, 0.74f, 1));
        }

        if (estimate is not null && hovered && !highestDamageHovered)
        {
            ImGui.SetTooltip(text.Get(
                $"DPS Parse 预估：{estimate.Score}\n根据本场实际 DPS 与当前 FFLogs 同职业、同副本、同分区的 DPS 分布估算。\nFFLogs 数据更新于：{estimate.DataUpdatedAt.ToLocalTime():yyyy/MM/dd}",
                $"Estimated DPS Parse: {estimate.Score}\nEstimated from this encounter's actual DPS and the current FFLogs DPS distribution for the same job, encounter, and partition.\nFFLogs data updated: {estimate.DataUpdatedAt.ToLocalTime():yyyy/MM/dd}"));
        }
    }

    internal static string FormatCompactNumber(long value)
        => Math.Abs(value) switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}b",
            >= 1_000_000 => $"{value / 1_000_000d:0.##}m",
            >= 1_000 => $"{value / 1_000d:0.#}k",
            _ => value.ToString("N0"),
        };

    internal static string FormatHighestDamage(CombatantRow row)
        => row.HighestDamage <= 0
            ? "--"
            : string.IsNullOrWhiteSpace(row.HighestDamageAction)
                ? FormatCompactNumber(row.HighestDamage)
                : $"{row.HighestDamageAction} {FormatCompactNumber(row.HighestDamage)}";

    internal static float NormalizeBackgroundOpacity(float opacity)
        => float.IsFinite(opacity)
            ? Math.Clamp(opacity, 0, 1)
            : 0.85f;

    internal static Vector4 ApplyBackgroundOpacity(Vector4 color, float opacity)
    {
        color.W *= NormalizeBackgroundOpacity(opacity);
        return color;
    }

    internal static string FormatHitRateValue(double? rate)
        => rate is { } value && double.IsFinite(value)
            ? $"{Math.Clamp(value, 0, 100):N1}%"
            : "--";

    internal static float CalculateCombatantRowHeight(float textLineHeightWithSpacing)
        => Math.Max(28, textLineHeightWithSpacing + 8);

    internal static float CalculateCompactWindowHeight(
        bool showHeader,
        float textLineHeightWithSpacing,
        float windowPaddingY,
        float itemSpacingY,
        float scrollbarSize,
        bool useHorizontalScroll)
    {
        var topSectionHeight = showHeader
            ? EncounterHeaderHeight
            : CompactDragHandleHeight;
        var tableHeaderHeight = Math.Max(22, textLineHeightWithSpacing + 4);
        var rowHeight = CalculateCombatantRowHeight(textLineHeightWithSpacing);
        var horizontalScrollbarHeight = useHorizontalScroll
            ? Math.Max(0, scrollbarSize)
            : 0;
        return MathF.Ceiling(
            (Math.Max(0, windowPaddingY) * 2) +
            topSectionHeight +
            (Math.Max(0, itemSpacingY) * 2) +
            tableHeaderHeight +
            rowHeight +
            horizontalScrollbarHeight +
            2);
    }

    internal static float CalculateEmptyStateWindowHeight(float windowPaddingY)
        => MathF.Ceiling(
            (Math.Max(0, windowPaddingY) * 2) +
            EmptyStateHeight +
            2);

    internal static float EaseOutCubic(float progress)
    {
        var normalized = Math.Clamp(progress, 0, 1);
        return 1 - MathF.Pow(1 - normalized, 3);
    }

    internal static float CalculateJobIconSize(float rowHeight, float textLineHeight)
        => Math.Min(rowHeight - 4, Math.Max(22, textLineHeight + 4));

    internal static float CalculateMinimumTableWidth(MeterSettings settings, bool showFflogs)
    {
        var width = TableRightPadding * 2;
        var columnCount = 0;

        void Add(bool visible, float columnWidth)
        {
            if (!visible)
            {
                return;
            }

            width += columnWidth;
            columnCount++;
        }

        Add(settings.ShowRank, RankColumnWidth);
        Add(settings.ShowPlayerName || settings.ShowJob, IdentityColumnWidth);
        Add(showFflogs, FflogsColumnWidth);
        Add(settings.ShowDps, RateColumnWidth);
        Add(settings.ShowRdps, RateColumnWidth);
        Add(settings.ShowEncDps, RateColumnWidth);
        Add(settings.ShowExtDps, RateColumnWidth);
        Add(settings.ShowHps, RateColumnWidth);
        Add(settings.ShowCriticalHitRate, HitRateColumnWidth);
        Add(settings.ShowDirectHitRate, HitRateColumnWidth);
        Add(settings.ShowCriticalDirectHitRate, HitRateColumnWidth);
        Add(settings.ShowDamagePercent, DamagePercentColumnWidth);
        Add(settings.ShowHighestDamage, HighestDamageColumnWidth);
        Add(settings.ShowDeaths, DeathsColumnWidth);
        return width + (columnCount * ColumnSpacing);
    }

    private static float CalculateRequiredTableWidth(
        MeterColumnWidths widths,
        float fontScale)
    {
        var scale = Math.Clamp(fontScale, 0.75f, 1.8f);
        var visibleWidths = new float?[]
        {
            widths.Rank,
            widths.Identity,
            widths.Fflogs,
            widths.Dps,
            widths.Rdps,
            widths.EncDps,
            widths.ExtDps,
            widths.Hps,
            widths.CriticalHit,
            widths.DirectHit,
            widths.CriticalDirectHit,
            widths.DamagePercent,
            widths.TotalDamage,
            widths.TotalHealing,
            widths.HighestDamage,
            widths.Deaths,
        }.Where(static width => width is not null)
            .Select(static width => width!.Value)
            .ToArray();
        return ((TableRightPadding * 2) * scale) +
               visibleWidths.Sum() +
               (visibleWidths.Length * ColumnSpacing * scale);
    }

    internal static bool ShouldShowFflogsColumn(bool integrationEnabled, MeterSettings settings)
        => integrationEnabled && settings.ShowFflogs;

    internal static bool ShouldDrawTeamSummary(MeterSettings settings)
        => !settings.ClassicAllianceView && !settings.CompactMode;

    internal static TimeSpan ResolveHeaderDuration(Encounter encounter)
    {
        // CombatDuration is the downtime-adjusted damage denominator. The header is a
        // fight clock, so it must keep the wall-clock duration used by HPS and history.
        return encounter.Duration;
    }

    internal static float ResolveStableColumnWidth(
        float nominalWidth,
        float fontScale,
        float headerTextWidth,
        float maximumValueTextWidth)
        => Math.Max(
            nominalWidth * Math.Clamp(fontScale, 0.75f, 1.8f),
            Math.Max(
                headerTextWidth + ColumnHeaderHorizontalPadding,
                maximumValueTextWidth));

    internal static float ResolveIdentityColumnWidth(
        float availableTableWidth,
        float preferredTableWidth,
        float preferredIdentityWidth,
        float minimumIdentityWidth)
        => Math.Max(
               minimumIdentityWidth,
               preferredIdentityWidth - Math.Max(0, preferredTableWidth - availableTableWidth)) +
           Math.Max(0, availableTableWidth - preferredTableWidth);

    internal static (float Identity, float HighestDamage) ResolveAdaptiveColumnWidths(
        float availableTableWidth,
        float preferredTableWidth,
        float? preferredIdentityWidth,
        float minimumIdentityWidth,
        float? preferredHighestDamageWidth)
    {
        var identity = preferredIdentityWidth is { } identityWidth
            ? Math.Max(
                minimumIdentityWidth,
                identityWidth - Math.Max(0, preferredTableWidth - availableTableWidth))
            : 0;
        var highestDamage = preferredHighestDamageWidth ?? 0;
        var extra = Math.Max(0, availableTableWidth - preferredTableWidth);
        if (preferredHighestDamageWidth is not null)
        {
            // Player IDs still benefit from a wide window, but the action column must also
            // gain readable space instead of remaining permanently fixed at its minimum.
            var highestDamageShare = preferredIdentityWidth is null ? extra : extra * 0.6f;
            highestDamage += highestDamageShare;
            identity += extra - highestDamageShare;
        }
        else
        {
            identity += extra;
        }
        return (identity, highestDamage);
    }

    internal static bool ShouldEnableHorizontalScroll(float availableWidth, float requiredWidth)
        => requiredWidth > availableWidth + 1;

    internal static IReadOnlyList<CombatantRow> SelectVisibleRows(
        IReadOnlyList<CombatantRow> rows,
        bool compactMode)
        => compactMode
            ? rows.Where(static row => row.IsLocalPlayer).ToArray()
            : rows;

    private static double Score(
        CombatantRow row,
        MeterSortMode mode,
        DpsMetric dpsMetric) => mode switch
    {
        MeterSortMode.Hps => row.Hps,
        _ => MeterSlotPresentation.DpsScore(row, dpsMetric),
    };

    internal static Vector4 PrimaryRateColor(bool isLocalPlayer)
        => isLocalPlayer ? LocalRateBright : IceBlue;

    private readonly record struct MeterColumn(
        float Offset,
        float Width,
        MeterSlotDefinition? Slot);

    private sealed record MeterColumnWidths(
        float? Rank,
        float? Identity,
        float? Fflogs,
        float? Dps,
        float? Rdps,
        float? EncDps,
        float? ExtDps,
        float? Hps,
        float? CriticalHit,
        float? DirectHit,
        float? CriticalDirectHit,
        float? DamagePercent,
        float? TotalDamage,
        float? TotalHealing,
        float? HighestDamage,
        float? Deaths);

    private sealed record MeterColumnLayout(
        MeterColumn? Rank,
        MeterColumn? Identity,
        MeterColumn? Fflogs,
        MeterColumn? Dps,
        MeterColumn? Rdps,
        MeterColumn? EncDps,
        MeterColumn? ExtDps,
        MeterColumn? Hps,
        MeterColumn? CriticalHit,
        MeterColumn? DirectHit,
        MeterColumn? CriticalDirectHit,
        MeterColumn? DamagePercent,
        MeterColumn? TotalDamage,
        MeterColumn? TotalHealing,
        MeterColumn? HighestDamage,
        MeterColumn? Deaths);

    private static Vector4 JobColor(string job)
    {
        var normalized = job.Trim().ToUpperInvariant();
        if (normalized is "PLD" or "WAR" or "DRK" or "GNB") return new Vector4(0.28f, 0.52f, 0.84f, 1);
        if (normalized is "WHM" or "SCH" or "AST" or "SGE") return new Vector4(0.32f, 0.70f, 0.48f, 1);
        if (normalized is "MNK" or "DRG" or "NIN" or "SAM" or "RPR" or "VPR") return new Vector4(0.82f, 0.34f, 0.34f, 1);
        if (normalized is "BRD" or "MCH" or "DNC") return new Vector4(0.76f, 0.58f, 0.28f, 1);
        if (normalized is "BLM" or "SMN" or "RDM" or "PCT" or "BLU") return new Vector4(0.60f, 0.42f, 0.78f, 1);
        return IceBlue;
    }

    private static string TrimToWidth(string value, float maximumWidth)
    {
        if (string.IsNullOrEmpty(value) || ImGui.CalcTextSize(value).X <= maximumWidth)
        {
            return value;
        }

        const string ellipsis = "...";
        var length = value.Length;
        while (length > 1 && ImGui.CalcTextSize(value[..length] + ellipsis).X > maximumWidth)
        {
            length--;
        }
        return value[..length] + ellipsis;
    }

    public override void OnClose()
    {
        configuration.Meter.IsVisible = false;
        saveConfiguration();
    }

    private static string FormatDuration(TimeSpan duration)
        => $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

    private static string PrimaryRateLabel(MeterSortMode sortMode, MeterSettings settings)
        => sortMode == MeterSortMode.Hps
            ? "HPS"
            : settings.DpsMetric switch
            {
                DpsMetric.Rdps => "rDPS",
                DpsMetric.EncDps => "eDPS",
                DpsMetric.ExtDps => "ExtDPS",
                _ => "DPS",
            };

    private readonly struct FontScaleScope : IDisposable
    {
        public FontScaleScope(float scale)
        {
            ImGui.SetWindowFontScale(Math.Clamp(scale, 0.75f, 1.8f));
        }

        public void Dispose() => ImGui.SetWindowFontScale(1);
    }
}
