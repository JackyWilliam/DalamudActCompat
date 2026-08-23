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
    internal const float MinimumTableWidthWithFflogs = 593;
    internal const float MinimumTableWidthWithoutFflogs = 546;
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
    private const float MinimumNameRegionWidth = 92;
    private const float TableRightPadding = 9;
    private const float ColumnSpacing = 3;
    private const float FflogsColumnWidth = 44;
    private const float RateColumnWidth = 50;
    private const float HitRateColumnWidth = 43;
    private const float DamagePercentColumnWidth = 46;
    private const float TotalDamageColumnWidth = 76;
    private const float HighestDamageColumnWidth = 138;
    private const float DeathsColumnWidth = 28;
    private static readonly Vector4 NavyRaised = new(0.075f, 0.10f, 0.15f, 0.94f);
    private static readonly Vector4 NavyHover = new(0.11f, 0.16f, 0.23f, 0.96f);
    private static readonly Vector4 Gold = new(0.90f, 0.81f, 0.55f, 1);
    private static readonly Vector4 LocalRateBright = new(1.0f, 0.94f, 0.42f, 1);
    private static readonly Vector4 IceBlue = new(0.38f, 0.72f, 0.90f, 1);

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
        Size = new Vector2(500, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 90),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override bool DrawConditions()
    {
        var settings = configuration.Meter;
        WindowName = text.Get("战斗统计###DalamudActCompatMeter", "Combat Meter###DalamudActCompatMeter");
        if (!settings.IsVisible)
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
        var usesHorizontalCards = settings.Preset is MeterPreset.HorizontalTransparent or MeterPreset.Custom;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(
                usesHorizontalCards ? 420 : MinimumExpandedWindowWidth,
                settings.CompactMode || isHeightAnimationActive
                    ? CompactWindowMinimumHeight
                    : usesHorizontalCards ? 90 : MinimumExpandedWindowHeight),
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

        var backgroundOpacity = usesHorizontalCards
            ? NormalizeBackgroundOpacity(settings.GetSelectedCustomStyle()?.BackgroundOpacity ?? 0)
            : NormalizeBackgroundOpacity(settings.BackgroundOpacity);
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
        var customScale = settings.GetSelectedCustomStyle()?.FontScale ?? 1;
        using var fontScale = new FontScaleScope(settings.FontScale * customScale);

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

        var rows = SelectVisibleRows(allRows, settings.CompactMode);
        if (settings.Preset is MeterPreset.HorizontalTransparent or MeterPreset.Custom)
        {
            DrawHorizontalMeter(encounter, rows, settings);
            return;
        }
        if (settings.Preset == MeterPreset.RoleSplit &&
            (encounter.PartyCapacity > 4 ||
             rows.Count(row => !MeterService.IsLimitBreak(row.Id, row.Name)) > 4))
        {
            DrawRoleSplitMeter(encounter, rows, settings);
            return;
        }

        var sortMode = MeterSortModeOptions.Normalize(settings.SortMode);
        var showFflogs = ShouldShowFflogsColumn(configuration.Fflogs.Enabled, settings);
        var columnWidths = MeasureColumnWidths(rows, settings, showFflogs);
        var availableTableWidth = ImGui.GetContentRegionAvail().X;
        var minimumTableWidth = CalculateRequiredTableWidth(columnWidths, settings.FontScale);
        var useHorizontalScroll = ShouldEnableHorizontalScroll(
            availableTableWidth,
            minimumTableWidth);
        ApplyCompactWindowHeight(settings, useHorizontalScroll);
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(text.Get("等待玩家数据…", "Waiting for player data…"));
            return;
        }

        var maximumScore = Math.Max(1, allRows.Max(row => Score(row, sortMode)));
        if (useHorizontalScroll)
        {
            ImGui.SetNextWindowContentSize(new Vector2(minimumTableWidth, 0));
        }
        if (ImGui.BeginChild(
                "meter-rows",
                new Vector2(-1, -1),
                false,
                (ImGuiWindowFlags)BuildRowsChildFlags(settings, useHorizontalScroll)))
        {
            var layout = BuildColumnLayout(
                ImGui.GetContentRegionAvail().X,
                columnWidths,
                settings.FontScale);
            DrawTableHeader(layout, settings);
            for (var index = 0; index < rows.Count; index++)
            {
                DrawCombatantRow(
                    rows[index],
                    maximumScore,
                    encounter,
                    settings,
                    layout,
                    showFflogs && !MeterService.IsLimitBreak(rows[index].Id, rows[index].Name)
                        ? fflogsEstimateService.GetEstimate(
                            encounter,
                            rows[index].Id,
                            rows[index].Name)
                        : null);
            }
        }
        ImGui.EndChild();
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
            ImGui.GetColorU32(ApplyBackgroundOpacity(NavyRaised, settings.BackgroundOpacity)),
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

    private void DrawEncounterHeader(Encounter encounter, MeterSettings settings)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var toggleStart = new Vector2(start.X + width - CompactToggleSize - 6, start.Y + 9);
        var toggleEnd = toggleStart + new Vector2(CompactToggleSize, CompactToggleSize);
        var toggleHovered = CanInteractWithCompactToggle(settings) &&
                            ImGui.IsMouseHoveringRect(toggleStart, toggleEnd);
        ImGui.InvisibleButton("meter-header-drag", new Vector2(width, EncounterHeaderHeight));
        HandleHeaderDrag(settings, allowStart: !toggleHovered);
        HandleCompactModeToggle(settings, toggleHovered);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + new Vector2(width, EncounterHeaderHeight),
            ImGui.GetColorU32(ApplyBackgroundOpacity(NavyRaised, settings.BackgroundOpacity)),
            6);
        DrawEncounterStateIcon(drawList, encounter, start + new Vector2(9, 5));
        var titleRight = Math.Max(start.X + 36, toggleStart.X - 6);
        drawList.AddText(
            start + new Vector2(36, 6),
            ImGui.GetColorU32(Gold),
            TrimToWidth(LocalizeEncounterTitle(encounter), titleRight - start.X - 36));
        var subtitle = $"{localizeZoneName(encounter.TerritoryId, encounter.ZoneName)}  ·  {FormatDuration(encounter.EffectiveDuration)}";
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
        var toggleHovered = CanInteractWithCompactToggle(settings) &&
                            ImGui.IsMouseHoveringRect(toggleStart, toggleEnd);
        ImGui.InvisibleButton("meter-compact-drag", new Vector2(width, CompactDragHandleHeight));
        HandleHeaderDrag(settings, allowStart: !toggleHovered);
        HandleCompactModeToggle(settings, toggleHovered);
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

    private void ApplyCompactWindowHeight(MeterSettings settings, bool useHorizontalScroll)
    {
        if (!settings.CompactMode)
        {
            return;
        }

        var targetHeight = CalculateCompactWindowHeight(
            settings.ShowHeader,
            ImGui.GetTextLineHeightWithSpacing(),
            ImGui.GetStyle().WindowPadding.Y,
            ImGui.GetStyle().ItemSpacing.Y,
            ImGui.GetStyle().ScrollbarSize,
            useHorizontalScroll);
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
            ImGui.GetColorU32(ApplyBackgroundOpacity(fill, settings.BackgroundOpacity)),
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

    private MeterColumnLayout BuildColumnLayout(
        float availableWidth,
        MeterColumnWidths widths,
        float fontScale)
    {
        var scale = Math.Clamp(fontScale, 0.75f, 1.8f);
        var right = Math.Max(0, availableWidth - (TableRightPadding * scale));
        MeterColumn Take(float width)
        {
            right -= width;
            var column = new MeterColumn(right, width);
            right -= ColumnSpacing * scale;
            return column;
        }

        MeterColumn? deaths = widths.Deaths is { } deathsWidth
            ? Take(deathsWidth)
            : null;
        MeterColumn? damagePercent = widths.DamagePercent is { } damagePercentWidth
            ? Take(damagePercentWidth)
            : null;
        MeterColumn? totalDamage = widths.TotalDamage is { } totalDamageWidth
            ? Take(totalDamageWidth)
            : null;
        MeterColumn? highestDamage = widths.HighestDamage is { } highestDamageWidth
            ? Take(highestDamageWidth)
            : null;
        MeterColumn? criticalDirectHit = widths.CriticalDirectHit is { } criticalDirectHitWidth
            ? Take(criticalDirectHitWidth)
            : null;
        MeterColumn? directHit = widths.DirectHit is { } directHitWidth
            ? Take(directHitWidth)
            : null;
        MeterColumn? criticalHit = widths.CriticalHit is { } criticalHitWidth
            ? Take(criticalHitWidth)
            : null;
        MeterColumn? hps = widths.Hps is { } hpsWidth
            ? Take(hpsWidth)
            : null;
        MeterColumn? dps = widths.Dps is { } dpsWidth
            ? Take(dpsWidth)
            : null;
        MeterColumn? fflogs = widths.Fflogs is { } fflogsWidth
            ? Take(fflogsWidth)
            : null;

        return new MeterColumnLayout(
            Math.Max(20, right),
            fflogs,
            dps,
            hps,
            criticalHit,
            directHit,
            criticalDirectHit,
            damagePercent,
            totalDamage,
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
            => Math.Max(
                nominalWidth * scale,
                Math.Max(
                    ImGui.CalcTextSize(header).X,
                    values.Select(value => ImGui.CalcTextSize(value).X)
                        .DefaultIfEmpty(0)
                        .Max()));

        return new MeterColumnWidths(
            showFflogs
                ? StableWidth(FflogsColumnWidth, "FFLogs", ["100", "--"])
                : null,
            settings.ShowDps
                ? StableWidth(
                    RateColumnWidth,
                    PrimaryRateLabel(MeterSortMode.Dps, settings),
                    rows.Select(static row => $"{row.Dps:N0}"))
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
            settings.ShowTotalDamage
                ? StableWidth(
                    TotalDamageColumnWidth,
                    text.Get("总伤害", "Damage"),
                    rows.Select(static row => FormatCompactNumber(row.TotalDamage)))
                : null,
            settings.ShowHighestDamage
                ? StableWidth(
                    HighestDamageColumnWidth,
                    text.Get("最高技能", "Max hit"),
                    rows.Select(static row => FormatHighestDamage(row)))
                : null,
            settings.ShowDeaths
                ? StableWidth(
                    DeathsColumnWidth,
                    text.Get("死亡", "KO"),
                    rows.Select(static row => row.Deaths.ToString()))
                : null);
    }

    private void DrawTableHeader(MeterColumnLayout layout, MeterSettings settings)
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
                settings.BackgroundOpacity)),
            4);
        drawList.AddLine(
            new Vector2(start.X, end.Y),
            new Vector2(end.X, end.Y),
            ImGui.GetColorU32(new Vector4(Gold.X, Gold.Y, Gold.Z, 0.55f)));

        var color = ImGui.GetColorU32(new Vector4(0.70f, 0.74f, 0.80f, 1));
        var lineY = start.Y + (headerHeight - ImGui.GetTextLineHeight()) * 0.5f;
        drawList.AddText(
            new Vector2(start.X + 8, lineY),
            color,
            text.Get("#  玩家", "#  Player"));

        void DrawColumnHeader(string label, MeterColumn column)
        {
            var size = ImGui.CalcTextSize(label);
            var columnX = start.X + column.Offset + Math.Max(0, column.Width - size.X);
            drawList.AddText(new Vector2(columnX, lineY), color, label);
        }

        if (layout.Fflogs is { } fflogs)
        {
            DrawColumnHeader("FFLogs", fflogs);
        }
        if (layout.Dps is { } dps)
        {
            DrawColumnHeader(PrimaryRateLabel(MeterSortMode.Dps, settings), dps);
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
        if (layout.HighestDamage is { } highestDamage)
        {
            DrawColumnHeader(text.Get("最高技能", "Max hit"), highestDamage);
        }
        if (layout.Deaths is { } deaths)
        {
            DrawColumnHeader(text.Get("死亡", "KO"), deaths);
        }

        var dpsHovered = layout.Dps is { } dpsColumn && ImGui.IsMouseHoveringRect(
            new Vector2(start.X + dpsColumn.Offset, start.Y),
            new Vector2(start.X + dpsColumn.Offset + dpsColumn.Width, end.Y));
        if (dpsHovered && settings.DpsMetric == DpsMetric.Rdps)
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
        FflogsEstimate? estimate)
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
                settings.BackgroundOpacity)),
            5);

        var sortMode = MeterSortModeOptions.Normalize(settings.SortMode);
        var jobColor = JobColor(row.Job);
        var ratio = (float)Math.Clamp(Score(row, sortMode) / maximumScore, 0, 1);
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
            ImGui.GetColorU32(ApplyBackgroundOpacity(barColor, settings.BackgroundOpacity)),
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

            if (!settings.ShowJob)
            {
                return currentX;
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
                    settings.BackgroundOpacity)),
                4);
            drawList.AddText(
                new Vector2(currentX + (badgeSize.X - jobSize.X) * 0.5f, textY + 1),
                ImGui.GetColorU32(Vector4.One),
                job);
            return currentX + badgeSize.X + 8;
        }

        var lineY = start.Y + (rowHeight - ImGui.GetTextLineHeight()) * 0.5f;
        var x = start.X + 8;
        drawList.AddText(
            new Vector2(x, lineY),
            ImGui.GetColorU32(new Vector4(0.68f, 0.71f, 0.76f, 1)),
            row.Rank is { } rank ? $"{rank,2}" : "--");
        x += 25;
        x = DrawJob(x, lineY);

        void DrawColumn(string value, MeterColumn column, Vector4 color)
        {
            var size = ImGui.CalcTextSize(value);
            var columnX = start.X + column.Offset + Math.Max(0, column.Width - size.X);
            drawList.AddText(new Vector2(columnX, lineY), ImGui.GetColorU32(color), value);
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
        if (layout.HighestDamage is { } highestDamage)
        {
            DrawColumn(
                FormatHighestDamage(row),
                highestDamage,
                new Vector4(0.94f, 0.68f, 0.48f, 1));
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
            DrawColumn($"{row.Dps:N0}", dps, PrimaryRateColor(row.IsLocalPlayer));
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

        var availableNameWidth = Math.Max(20, start.X + layout.NameRight - x - 6);
        drawList.AddText(
            new Vector2(x, lineY),
            ImGui.GetColorU32(row.IsLocalPlayer || isLimitBreak ? Gold : Vector4.One),
            TrimToWidth(displayName, availableNameWidth));

        if (estimate is not null && hovered)
        {
            ImGui.SetTooltip(text.Get(
                $"DPS Parse 预估：{estimate.Score}\n根据本场实际 DPS 与当前 FFLogs 同职业、同副本、同分区的 DPS 分布估算。\nFFLogs 数据更新于：{estimate.DataUpdatedAt.ToLocalTime():yyyy/MM/dd}",
                $"Estimated DPS Parse: {estimate.Score}\nEstimated from this encounter's actual DPS and the current FFLogs DPS distribution for the same job, encounter, and partition.\nFFLogs data updated: {estimate.DataUpdatedAt.ToLocalTime():yyyy/MM/dd}"));
        }
    }

    private void DrawHorizontalMeter(
        Encounter encounter,
        IReadOnlyList<CombatantRow> rows,
        MeterSettings settings)
    {
        var customStyle = settings.GetSelectedCustomStyle();
        var cardOpacity = customStyle?.CardOpacity ?? 0.34f;
        var cardSpacing = customStyle?.CardSpacing ?? 5;
        var cardRounding = customStyle?.CardRounding ?? 3;
        var slots = customStyle?.Slots ?? MeterCustomStyle.CreateHorizontalSlots();
        var groups = BuildAllianceGroups(rows);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        const float groupLabelWidth = 20;
        var cardWidth = Math.Max(
            96,
            (availableWidth - groupLabelWidth - (cardSpacing * 7)) / 8);
        var cardHeight = Math.Max(70, 72 * (customStyle?.FontScale ?? 1));
        var contentWidth = groupLabelWidth + (cardWidth * 8) + (cardSpacing * 7);
        var contentHeight = groups.Count * (cardHeight + cardSpacing);
        var childFlags = contentWidth > availableWidth
            ? ImGuiWindowFlags.HorizontalScrollbar
            : ImGuiWindowFlags.None;
        if (ShouldPassMouseInputThrough(settings))
        {
            childFlags |= ImGuiWindowFlags.NoInputs;
        }

        ImGui.SetNextWindowContentSize(new Vector2(contentWidth, contentHeight));
        if (!ImGui.BeginChild("horizontal-meter", new Vector2(-1, -1), false, childFlags))
        {
            ImGui.EndChild();
            return;
        }

        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            var y = origin.Y + (groupIndex * (cardHeight + cardSpacing));
            drawList.AddText(
                new Vector2(origin.X + 3, y + ((cardHeight - ImGui.GetTextLineHeight()) * 0.5f)),
                ImGui.GetColorU32(new Vector4(Gold.X, Gold.Y, Gold.Z, 0.88f)),
                group.Label);
            for (var cardIndex = 0; cardIndex < group.Rows.Count; cardIndex++)
            {
                var start = new Vector2(
                    origin.X + groupLabelWidth + (cardIndex * (cardWidth + cardSpacing)),
                    y);
                DrawHorizontalCard(
                    drawList,
                    encounter,
                    group.Rows[cardIndex],
                    settings,
                    slots,
                    start,
                    new Vector2(cardWidth, cardHeight),
                    cardOpacity,
                    cardRounding,
                    customStyle?.TextColor ?? Vector4.One);
            }
        }

        ImGui.Dummy(new Vector2(contentWidth, contentHeight));
        ImGui.EndChild();
    }

    private void DrawHorizontalCard(
        ImDrawListPtr drawList,
        Encounter encounter,
        CombatantRow row,
        MeterSettings settings,
        IReadOnlyList<MeterSlotDefinition> slots,
        Vector2 start,
        Vector2 size,
        float opacity,
        float rounding,
        Vector4 textColor)
    {
        var end = start + size;
        var hovered = ImGui.IsMouseHoveringRect(start, end);
        var jobColor = JobColor(row.Job);
        var fill = new Vector4(
            jobColor.X,
            jobColor.Y,
            jobColor.Z,
            Math.Clamp(opacity + (hovered ? 0.10f : 0), 0, 0.82f));
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(fill), rounding);
        if (row.IsLocalPlayer)
        {
            drawList.AddRect(start, end, ImGui.GetColorU32(settings.LocalPlayerColor), rounding);
        }

        var combatant = encounter.Combatants.FirstOrDefault(item =>
            string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase));
        var displayName = combatant is null
            ? row.Name
            : PlayerIdentityFormatter.Format(combatant, encounter.Combatants, settings, text);
        foreach (var slot in slots.Where(static slot => slot.Visible))
        {
            var slotStart = start + new Vector2(
                size.X * slot.Column / 24f,
                size.Y * slot.Row / 6f);
            var slotSize = new Vector2(
                size.X * slot.ColumnSpan / 24f,
                size.Y * slot.RowSpan / 6f);
            if (slot.Metric == MeterSlotMetric.Job)
            {
                DrawCardJobIcon(drawList, row, settings, slotStart, slotSize);
                continue;
            }

            var value = ResolveSlotText(slot.Metric, row, displayName);
            var rendered = TrimToWidth(value, Math.Max(4, slotSize.X - 3));
            var textSize = ImGui.CalcTextSize(rendered);
            var x = slot.Alignment switch
            {
                MeterSlotAlignment.Center => slotStart.X + ((slotSize.X - textSize.X) * 0.5f),
                MeterSlotAlignment.Right => slotStart.X + slotSize.X - textSize.X - 2,
                _ => slotStart.X + 2,
            };
            var y = slotStart.Y + Math.Max(0, (slotSize.Y - textSize.Y) * 0.5f);
            var color = row.IsLocalPlayer && slot.Metric == MeterSlotMetric.PlayerName
                ? Gold
                : textColor;
            drawList.AddText(new Vector2(x, y), ImGui.GetColorU32(color), rendered);
        }

        if (hovered && row.HighestDamage > 0)
        {
            ImGui.SetTooltip(text.Get(
                $"最高单次：{row.HighestDamageAction} {row.HighestDamage:N0}",
                $"Highest hit: {row.HighestDamageAction} {row.HighestDamage:N0}"));
        }
    }

    private void DrawCardJobIcon(
        ImDrawListPtr drawList,
        CombatantRow row,
        MeterSettings settings,
        Vector2 start,
        Vector2 size)
    {
        var texture = MeterService.IsLimitBreak(row.Id, row.Name)
            ? jobIcons.GetLimitBreak()
            : jobIcons.Get(settings.JobDisplayStyle, row.Job);
        if (texture is null)
        {
            var job = JobDisplayFormatter.FormatText(row.Job, settings.JobDisplayStyle);
            var jobSize = ImGui.CalcTextSize(job);
            drawList.AddText(
                start + ((size - jobSize) * 0.5f),
                ImGui.GetColorU32(Vector4.One),
                job);
            return;
        }

        var iconSize = Math.Max(8, Math.Min(size.X, size.Y) - 3);
        var iconStart = start + ((size - new Vector2(iconSize)) * 0.5f);
        var wrap = texture.GetWrapOrEmpty();
        drawList.AddImage(wrap.Handle, iconStart, iconStart + new Vector2(iconSize));
    }

    private void DrawRoleSplitMeter(
        Encounter encounter,
        IReadOnlyList<CombatantRow> rows,
        MeterSettings settings)
    {
        var damageRows = ReRank(rows
            .Where(static row => !JobRoleClassifier.IsHealer(row.Job))
            .OrderByDescending(static row => row.Dps));
        var healerRows = ReRank(rows
            .Where(static row => JobRoleClassifier.IsHealer(row.Job))
            .OrderByDescending(static row => row.Hps));
        var rowHeight = Math.Max(31, ImGui.GetTextLineHeightWithSpacing() + 10);
        if (!ImGui.BeginChild(
                "role-split-meter",
                new Vector2(-1, -1),
                false,
                ShouldPassMouseInputThrough(settings)
                    ? ImGuiWindowFlags.NoInputs
                    : ImGuiWindowFlags.None))
        {
            ImGui.EndChild();
            return;
        }

        DrawRoleSectionHeader(text.Get("D/T 伤害榜", "D/T DAMAGE"));
        foreach (var row in damageRows)
        {
            DrawRoleSplitRow(encounter, row, settings, rowHeight, useHealing: false);
        }
        ImGui.Dummy(new Vector2(1, 4));
        DrawRoleSectionHeader(text.Get("治疗 HPS 榜", "HEALER HPS"));
        foreach (var row in healerRows)
        {
            DrawRoleSplitRow(encounter, row, settings, rowHeight, useHealing: true);
        }
        ImGui.EndChild();
    }

    private void DrawRoleSectionHeader(string label)
    {
        ImGui.TextColored(Gold, label);
        ImGui.Separator();
    }

    private void DrawRoleSplitRow(
        Encounter encounter,
        CombatantRow row,
        MeterSettings settings,
        float height,
        bool useHealing)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var end = start + new Vector2(width, height);
        ImGui.InvisibleButton($"role-row-{useHealing}-{row.Id}-{row.Name}", new Vector2(width, height));
        var drawList = ImGui.GetWindowDrawList();
        var jobColor = JobColor(row.Job);
        drawList.AddRectFilled(
            start,
            end,
            ImGui.GetColorU32(new Vector4(jobColor.X, jobColor.Y, jobColor.Z, 0.22f)),
            4);
        var combatant = encounter.Combatants.FirstOrDefault(item =>
            string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase));
        var name = combatant is null
            ? row.Name
            : PlayerIdentityFormatter.Format(combatant, encounter.Combatants, settings, text);
        var group = row.PartyGroup is >= 1 and <= 3
            ? $"[{(char)('A' + row.PartyGroup - 1)}] "
            : string.Empty;
        var left = $"{row.Rank,2}  {group}{name}";
        var right = useHealing
            ? $"HPS {row.Hps:N0}   {text.Get("总治疗", "Healing")} {FormatCompactNumber(row.TotalHealing)}"
            : $"DPS {row.Dps:N0}   {text.Get("总伤害", "Damage")} {FormatCompactNumber(row.TotalDamage)}   {text.Get("最高", "Max")} {FormatHighestDamage(row)}";
        var lineY = start.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f);
        drawList.AddText(new Vector2(start.X + 7, lineY), ImGui.GetColorU32(Vector4.One), left);
        var rightSize = ImGui.CalcTextSize(right);
        drawList.AddText(
            new Vector2(Math.Max(start.X + 120, end.X - rightSize.X - 7), lineY),
            ImGui.GetColorU32(useHealing ? new Vector4(0.48f, 0.88f, 0.62f, 1) : IceBlue),
            right);
    }

    private static IReadOnlyList<CombatantRow> ReRank(IEnumerable<CombatantRow> rows)
    {
        var rank = 0;
        return rows.Select(row => row with
        {
            Rank = MeterService.IsLimitBreak(row.Id, row.Name) ? null : ++rank,
        }).ToArray();
    }

    private static IReadOnlyList<AllianceGroup> BuildAllianceGroups(IReadOnlyList<CombatantRow> rows)
    {
        if (rows.Any(static row => row.PartyGroup > 0))
        {
            return rows
                .GroupBy(static row => Math.Clamp(row.PartyGroup, 1, 3))
                .OrderBy(static group => group.Key)
                .Select(group => new AllianceGroup(
                    ((char)('A' + group.Key - 1)).ToString(),
                    group.Take(8).ToArray()))
                .ToArray();
        }

        return rows
            .Select((row, index) => new { row, index })
            .GroupBy(static item => item.index / 8)
            .Select(group => new AllianceGroup(
                ((char)('A' + group.Key)).ToString(),
                group.Select(static item => item.row).ToArray()))
            .ToArray();
    }

    private static string ResolveSlotText(
        MeterSlotMetric metric,
        CombatantRow row,
        string displayName)
        => metric switch
        {
            MeterSlotMetric.Rank => row.Rank?.ToString() ?? "LB",
            MeterSlotMetric.PlayerName => displayName,
            MeterSlotMetric.Dps => $"{row.Dps:N0}",
            MeterSlotMetric.Hps => $"{row.Hps:N0}",
            MeterSlotMetric.DamagePercent => $"{row.DamagePercent:N1}%",
            MeterSlotMetric.TotalDamage => FormatCompactNumber(row.TotalDamage),
            MeterSlotMetric.HighestDamageAction => row.HighestDamageAction,
            MeterSlotMetric.HighestDamage => FormatCompactNumber(row.HighestDamage),
            MeterSlotMetric.Deaths => row.Deaths.ToString(),
            MeterSlotMetric.CriticalHitPercent => FormatHitRateValue(row.CriticalHitPercent),
            MeterSlotMetric.DirectHitPercent => FormatHitRateValue(row.DirectHitPercent),
            MeterSlotMetric.CriticalDirectHitPercent => FormatHitRateValue(row.CriticalDirectHitPercent),
            _ => string.Empty,
        };

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

    private sealed record AllianceGroup(string Label, IReadOnlyList<CombatantRow> Rows);

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
        var width = MinimumNameRegionWidth + TableRightPadding;
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

        Add(showFflogs, FflogsColumnWidth);
        Add(settings.ShowDps, RateColumnWidth);
        Add(settings.ShowHps, RateColumnWidth);
        Add(settings.ShowCriticalHitRate, HitRateColumnWidth);
        Add(settings.ShowDirectHitRate, HitRateColumnWidth);
        Add(settings.ShowCriticalDirectHitRate, HitRateColumnWidth);
        Add(settings.ShowDamagePercent, DamagePercentColumnWidth);
        Add(settings.ShowTotalDamage, TotalDamageColumnWidth);
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
            widths.Fflogs,
            widths.Dps,
            widths.Hps,
            widths.CriticalHit,
            widths.DirectHit,
            widths.CriticalDirectHit,
            widths.DamagePercent,
            widths.TotalDamage,
            widths.HighestDamage,
            widths.Deaths,
        }.Where(static width => width is not null)
            .Select(static width => width!.Value)
            .ToArray();
        return ((MinimumNameRegionWidth + TableRightPadding) * scale) +
               visibleWidths.Sum() +
               (visibleWidths.Length * ColumnSpacing * scale);
    }

    internal static bool ShouldShowFflogsColumn(bool integrationEnabled, MeterSettings settings)
        => integrationEnabled && settings.ShowFflogs;

    internal static bool ShouldEnableHorizontalScroll(float availableWidth, float requiredWidth)
        => requiredWidth > availableWidth + 1;

    internal static IReadOnlyList<CombatantRow> SelectVisibleRows(
        IReadOnlyList<CombatantRow> rows,
        bool compactMode)
        => compactMode
            ? rows.Where(static row => row.IsLocalPlayer).ToArray()
            : rows;

    private static double Score(CombatantRow row, MeterSortMode mode) => mode switch
    {
        MeterSortMode.Hps => row.Hps,
        _ => row.Dps,
    };

    internal static Vector4 PrimaryRateColor(bool isLocalPlayer)
        => isLocalPlayer ? LocalRateBright : IceBlue;

    private readonly record struct MeterColumn(float Offset, float Width);

    private sealed record MeterColumnWidths(
        float? Fflogs,
        float? Dps,
        float? Hps,
        float? CriticalHit,
        float? DirectHit,
        float? CriticalDirectHit,
        float? DamagePercent,
        float? TotalDamage,
        float? HighestDamage,
        float? Deaths);

    private sealed record MeterColumnLayout(
        float NameRight,
        MeterColumn? Fflogs,
        MeterColumn? Dps,
        MeterColumn? Hps,
        MeterColumn? CriticalHit,
        MeterColumn? DirectHit,
        MeterColumn? CriticalDirectHit,
        MeterColumn? DamagePercent,
        MeterColumn? TotalDamage,
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
