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
    internal const float MinimumTableWidthWithFflogs = 410;
    internal const float MinimumTableWidthWithoutFflogs = 350;
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
        return !settings.AutoHideOutOfCombat || encounter?.IsActive == true;
    }

    public override void PreDraw()
    {
        var settings = configuration.Meter;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(
                MinimumExpandedWindowWidth,
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

        if (settings.IsLocked && settings.ClickThroughWhenLocked)
        {
            Flags |= ImGuiWindowFlags.NoInputs;
        }

        ImGui.SetNextWindowBgAlpha(Math.Clamp(settings.BackgroundOpacity, 0.05f, 1));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.035f, 0.055f, 0.09f, 1));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.62f, 0.52f, 0.28f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.50f, 0.42f, 0.24f, 0.75f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, NavyRaised);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, NavyHover);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.11f, 0.17f, 0.25f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.17f, 0.25f, 0.35f, 1));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.15f, 0.21f, 0.29f, 1));
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

        var rows = SelectVisibleRows(allRows, settings.CompactMode);
        var sortMode = MeterSortModeOptions.Normalize(settings.SortMode);
        var showFflogs = configuration.Fflogs.Enabled && sortMode != MeterSortMode.Hps;
        var availableTableWidth = ImGui.GetContentRegionAvail().X;
        var minimumTableWidth = (showFflogs
            ? MinimumTableWidthWithFflogs
            : MinimumTableWidthWithoutFflogs) * settings.FontScale;
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
                useHorizontalScroll
                    ? ImGuiWindowFlags.HorizontalScrollbar
                    : ImGuiWindowFlags.None))
        {
            var layout = BuildColumnLayout(
                ImGui.GetContentRegionAvail().X,
                rows,
                settings,
                showFflogs);
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
            ImGui.GetColorU32(NavyRaised),
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
            ImGui.GetColorU32(NavyRaised),
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
        => !settings.IsLocked || !settings.ClickThroughWhenLocked;

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
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(fill), 4);
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
        IReadOnlyList<CombatantRow> rows,
        MeterSettings settings,
        bool showFflogs)
    {
        var sortMode = MeterSortModeOptions.Normalize(settings.SortMode);
        float Measure(string header, IEnumerable<string> values)
            => Math.Max(
                ImGui.CalcTextSize(header).X,
                values.Select(value => ImGui.CalcTextSize(value).X).DefaultIfEmpty(0).Max());

        var right = Math.Max(0, availableWidth - 9);
        MeterColumn Take(float width)
        {
            right -= width;
            var column = new MeterColumn(right, width);
            right -= 4;
            return column;
        }

        var deaths = Take(Measure(
            text.Get("死亡", "KO"),
            rows.Select(static row => row.Deaths.ToString())));
        var damagePercent = Take(Measure(
            text.Get("占比", "DMG%"),
            rows.Select(static row => $"{row.DamagePercent:N1}%")));
        var criticalDirectHit = Take(Measure(
            text.Get("直暴", "CDH"),
            rows.Select(static row => FormatHitRateValue(row.CriticalDirectHitPercent))));
        var criticalHit = Take(Measure(
            text.Get("暴击", "CRIT"),
            rows.Select(static row => FormatHitRateValue(row.CriticalHitPercent))));
        var primaryRate = Take(Measure(
            PrimaryRateLabel(sortMode, settings),
            rows.Select(row => $"{(sortMode == MeterSortMode.Hps ? row.Hps : row.Dps):N0}")));
        MeterColumn? fflogs = showFflogs
            ? Take(Measure("FFLogs", ["~100", "--"]))
            : null;

        return new MeterColumnLayout(
            Math.Max(20, right),
            fflogs,
            primaryRate,
            criticalHit,
            criticalDirectHit,
            damagePercent,
            deaths);
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
            ImGui.GetColorU32(new Vector4(NavyRaised.X, NavyRaised.Y, NavyRaised.Z, 0.78f)),
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
        DrawColumnHeader(
            PrimaryRateLabel(MeterSortModeOptions.Normalize(settings.SortMode), settings),
            layout.PrimaryRate);
        DrawColumnHeader(text.Get("暴击", "CRIT"), layout.CriticalHit);
        DrawColumnHeader(text.Get("直暴", "CDH"), layout.CriticalDirectHit);
        DrawColumnHeader(text.Get("占比", "DMG%"), layout.DamagePercent);
        DrawColumnHeader(text.Get("死亡", "KO"), layout.Deaths);
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
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(hovered ? NavyHover : NavyRaised), 5);

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
        drawList.AddRectFilled(start, new Vector2(start.X + width * ratio, end.Y), ImGui.GetColorU32(barColor), 5);
        if (row.IsLocalPlayer)
        {
            var borderColor = new Vector4(
                configuredLocalColor.X,
                configuredLocalColor.Y,
                configuredLocalColor.Z,
                0.95f);
            drawList.AddRect(start, end, ImGui.GetColorU32(borderColor), 5, ImDrawFlags.None, 1.5f);
        }

        var primary = sortMode == MeterSortMode.Hps ? row.Hps : row.Dps;
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
                ImGui.GetColorU32(new Vector4(jobColor.X, jobColor.Y, jobColor.Z, 0.55f)),
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

        DrawColumn(
            row.Deaths.ToString(),
            layout.Deaths,
            new Vector4(0.78f, 0.80f, 0.84f, 1));
        DrawColumn(
            $"{row.DamagePercent:N1}%",
            layout.DamagePercent,
            new Vector4(0.72f, 0.78f, 0.84f, 1));
        DrawColumn(
            FormatHitRateValue(row.CriticalHitPercent),
            layout.CriticalHit,
            new Vector4(0.82f, 0.68f, 0.92f, 1));
        DrawColumn(
            FormatHitRateValue(row.CriticalDirectHitPercent),
            layout.CriticalDirectHit,
            new Vector4(0.95f, 0.62f, 0.45f, 1));
        DrawColumn(
            $"{primary:N0}",
            layout.PrimaryRate,
            PrimaryRateColor(row.IsLocalPlayer));
        if (layout.Fflogs is { } fflogsColumn)
        {
            DrawColumn(
                isLimitBreak
                    ? string.Empty
                    : estimate is null ? "--" : $"~{estimate.Score}",
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
                $"FFLogs 公开排名样本估算：{estimate.Score}（基于本地 rDPS 归因，非官方实时成绩）",
                $"FFLogs public-ranking estimate: {estimate.Score} (based on local rDPS attribution; not an official live parse)"));
        }
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

    private sealed record MeterColumnLayout(
        float NameRight,
        MeterColumn? Fflogs,
        MeterColumn PrimaryRate,
        MeterColumn CriticalHit,
        MeterColumn CriticalDirectHit,
        MeterColumn DamagePercent,
        MeterColumn Deaths);

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

        const string ellipsis = "…";
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
