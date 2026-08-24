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
    private readonly JobIconTextureSet jobIcons;
    private readonly Action saveConfiguration;
    private readonly RoleSplitGroup group;
    private bool locateOnNextDraw;
    private long locatePreviewExpiresAt;

    public RoleSplitMeterWindow(
        MeterService meterService,
        PluginConfiguration configuration,
        UiText text,
        JobIconTextureSet jobIcons,
        Action saveConfiguration,
        RoleSplitGroup group)
        : base(group == RoleSplitGroup.Healer
            ? "治疗 HPS 榜###DalamudActCompatRoleSplitHealerMeter"
            : "D / T 伤害榜###DalamudActCompatRoleSplitDamageMeter")
    {
        this.meterService = meterService;
        this.configuration = configuration;
        this.text = text;
        this.jobIcons = jobIcons;
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

        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.70f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Navy);
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
            var compactHeight = 58 + CalculateRowHeight() +
                                (Profile.Slots.Any(slot => slot.Visible && slot.Metric == summaryMetric)
                                    ? MeterSlotPresentation.TeamSummaryHeight
                                    : 0);
            ImGui.SetWindowSize(
                new Vector2(ImGui.GetWindowSize().X, compactHeight),
                ImGuiCond.Always);
        }
    }

    private void DrawHeader(Encounter? encounter)
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
            ? text.Get("治疗 HPS 榜", "Healer HPS")
            : text.Get("D / T 伤害榜", "D / T Damage");
        var titleSize = ImGui.CalcTextSize(title);
        var titleStart = start + new Vector2(6, 4);
        var titleEnd = start + new Vector2(titleSize.X + 18, 30);
        var titleHovered = ImGui.IsMouseHoveringRect(titleStart, titleEnd);
        ImGui.InvisibleButton("role-split-drag", size);
        if (!toggleHovered && !titleHovered && !Profile.IsLocked && ImGui.IsItemActive() &&
            ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, ImGuiCond.Always);
        }
        if ((toggleHovered || titleHovered) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Compact = !Compact;
            saveConfiguration();
        }

        var state = encounter is null
            ? text.Get("等待数据", "Waiting")
            : encounter.IsActive
                ? text.Get("战斗中", "Running")
                : text.Get("已结束 · 保留上一场", "Ended · retained");
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + size, ImGui.GetColorU32(NavyRaised), 6);
        drawList.AddRectFilled(
            titleStart,
            titleEnd,
            ImGui.GetColorU32(titleHovered ? NavyHover : Navy),
            5);
        drawList.AddRect(
            titleStart,
            titleEnd,
            ImGui.GetColorU32(group == RoleSplitGroup.Healer ? HealingGreen : Gold),
            5);
        drawList.AddText(start + new Vector2(12, 7), ImGui.GetColorU32(Gold), title);
        var stateSize = ImGui.CalcTextSize(state);
        drawList.AddText(
            start + new Vector2(size.X - stateSize.X - toggleSize - 15, 7),
            ImGui.GetColorU32(IceBlue),
            state);
        drawList.AddRectFilled(
            toggleStart,
            toggleEnd,
            ImGui.GetColorU32(toggleHovered ? NavyHover : Navy),
            4);
        drawList.AddRect(
            toggleStart,
            toggleEnd,
            ImGui.GetColorU32(group == RoleSplitGroup.Healer ? HealingGreen : Gold),
            4);
        DrawChevron(drawList, toggleStart, toggleEnd, Compact);
        if (toggleHovered || titleHovered)
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
        bool useHealing)
    {
        ImGui.Separator();
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(text.Get("暂无玩家", "No players"));
            return;
        }

        foreach (var row in rows)
        {
            DrawRow(row, encounter, useHealing);
        }
    }

    private void DrawRow(CombatantRow row, Encounter encounter, bool useHealing)
    {
        var slots = Profile.Slots.Where(static slot => slot.Visible).ToArray();
        var metrics = ResolveMetrics(slots, useHealing);
        const int columns = 3;
        var rowHeight = CalculateRowHeight(metrics.Count);
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"role-row-{useHealing}-{row.Id}-{row.Name}", new Vector2(width, rowHeight));
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + new Vector2(width, rowHeight),
            ImGui.GetColorU32(hovered ? NavyHover : NavyRaised),
            5);

        var displayName = MeterSlotPresentation.DisplayName(row, encounter, configuration.Meter, text);
        var cursorX = start.X + 8;
        if (slots.Any(static slot => slot.Metric == MeterSlotMetric.Rank))
        {
            var rank = row.Rank?.ToString() ?? "--";
            drawList.AddText(new Vector2(cursorX, start.Y + 6), ImGui.GetColorU32(IceBlue), rank);
            cursorX += ImGui.CalcTextSize(rank).X + 7;
        }
        if (slots.Any(static slot => slot.Metric == MeterSlotMetric.PlayerIdentity))
        {
            cursorX += DrawJob(row, new Vector2(cursorX, start.Y + 4), 22) + 6;
            drawList.AddText(
                new Vector2(cursorX, start.Y + 6),
                ImGui.GetColorU32(row.IsLocalPlayer ? Gold : Vector4.One),
                MeterSlotPresentation.TrimToWidth(displayName, Math.Max(20, width - (cursorX - start.X) - 8)));
        }

        var cellWidth = width / columns;
        for (var index = 0; index < metrics.Count; index++)
        {
            var column = index % columns;
            var metricRow = index / columns;
            var metricStart = start + new Vector2(8 + (column * cellWidth), 32 + (metricRow * 20));
            var metric = metrics[index].Metric;
            var label = MeterSlotPresentation.Label(metric, text);
            var value = MeterSlotPresentation.Value(metric, row, displayName);
            var combined = $"{label}  {value}";
            var color = metric == MeterSlotMetric.Hps
                ? HealingGreen
                : metric is MeterSlotMetric.Dps or MeterSlotMetric.Rdps
                    ? IceBlue
                    : new Vector4(0.82f, 0.84f, 0.87f, 1);
            drawList.AddText(
                metricStart,
                ImGui.GetColorU32(color),
                MeterSlotPresentation.TrimToWidth(combined, Math.Max(20, cellWidth - 12)));
        }

        if (hovered && row.HighestDamage > 0)
        {
            ImGui.SetTooltip(text.Get(
                $"最高单次：{row.HighestDamageAction} {row.HighestDamage:N0}",
                $"Highest hit: {row.HighestDamageAction} {row.HighestDamage:N0}"));
        }
    }

    private float DrawJob(CombatantRow row, Vector2 start, float size)
    {
        var texture = MeterService.IsLimitBreak(row.Id, row.Name)
            ? jobIcons.GetLimitBreak()
            : jobIcons.Get(configuration.Meter.JobDisplayStyle, row.Job);
        if (texture is not null)
        {
            ImGui.GetWindowDrawList().AddImage(
                texture.GetWrapOrEmpty().Handle,
                start,
                start + new Vector2(size));
            return size;
        }

        var job = JobDisplayFormatter.FormatText(row.Job, configuration.Meter.JobDisplayStyle);
        ImGui.GetWindowDrawList().AddText(start + new Vector2(0, 3), ImGui.GetColorU32(IceBlue), job);
        return ImGui.CalcTextSize(job).X;
    }

    private float CalculateRowHeight()
    {
        var metricCount = ResolveMetrics(
            Profile.Slots.Where(static slot => slot.Visible).ToArray(),
            group == RoleSplitGroup.Healer).Count;
        return CalculateRowHeight(metricCount);
    }

    private static IReadOnlyList<RoleMetric> ResolveMetrics(
        IReadOnlyList<MeterSlotDefinition> slots,
        bool useHealing)
    {
        var candidates = slots.Where(static slot =>
                slot.Metric is not MeterSlotMetric.Job and
                not MeterSlotMetric.PlayerName and
                not MeterSlotMetric.PlayerIdentity and
                not MeterSlotMetric.TotalDamage and
                not MeterSlotMetric.TotalHealing and
                not MeterSlotMetric.Fflogs and
                not MeterSlotMetric.Rank)
            .ToArray();
        if (!useHealing)
        {
            return candidates.Where(static slot => slot.Metric != MeterSlotMetric.Hps)
                .Select(static slot => new RoleMetric(slot, slot.Metric))
                .ToArray();
        }

        var leadingDamage = candidates.FirstOrDefault(static slot =>
            slot.Metric is MeterSlotMetric.Dps or MeterSlotMetric.Rdps);
        var resolved = new List<RoleMetric>();
        foreach (var slot in candidates)
        {
            if (ReferenceEquals(slot, leadingDamage))
            {
                // The healing window replaces only the earliest damage rate. A second
                // DPS/rDPS slot remains available exactly where the user placed it.
                resolved.Add(new RoleMetric(slot, MeterSlotMetric.Hps));
            }
            else if (leadingDamage is not null && slot.Metric == MeterSlotMetric.Hps)
            {
                // The replaced rate already supplies HPS; suppress the original slot
                // so a default layout cannot render the same HPS value twice.
                continue;
            }
            else
            {
                resolved.Add(new RoleMetric(slot, slot.Metric));
            }
        }
        return resolved;
    }

    private static float CalculateRowHeight(int metricCount)
    {
        const int columns = 3;
        var metricRows = Math.Max(1, (metricCount + columns - 1) / columns);
        return 31 + (metricRows * 20);
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

    private sealed record RoleMetric(MeterSlotDefinition Slot, MeterSlotMetric Metric);
}
