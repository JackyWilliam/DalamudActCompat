using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using System.Numerics;

namespace DalamudActCompat.Meter;

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
    private bool locateOnNextDraw;
    private long locatePreviewExpiresAt;

    public RoleSplitMeterWindow(
        MeterService meterService,
        PluginConfiguration configuration,
        UiText text,
        JobIconTextureSet jobIcons,
        Action saveConfiguration)
        : base("职能分栏战斗统计###DalamudActCompatRoleSplitMeter")
    {
        this.meterService = meterService;
        this.configuration = configuration;
        this.text = text;
        this.jobIcons = jobIcons;
        this.saveConfiguration = saveConfiguration;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Size = new Vector2(560, 520);
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
            "职能分栏战斗统计###DalamudActCompatRoleSplitMeter",
            "Role Split Combat Meter###DalamudActCompatRoleSplitMeter");
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
                viewport.Pos + (viewport.Size * 0.58f),
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
        var damageRows = MeterSlotPresentation.SortAndRank(
            rows.Where(static row => !JobRoleClassifier.IsHealer(row.Job)),
            MeterSortMode.Dps);
        var healerRows = MeterSlotPresentation.SortAndRank(
            rows.Where(static row => JobRoleClassifier.IsHealer(row.Job)),
            MeterSortMode.Hps);
        DrawSection(text.Get("D / T 伤害榜", "D / T DAMAGE"), damageRows, encounter, useHealing: false);
        ImGui.Dummy(new Vector2(1, 5));
        DrawSection(text.Get("治疗 HPS 榜", "HEALER HPS"), healerRows, encounter, useHealing: true);
    }

    private void DrawHeader(Encounter? encounter)
    {
        if (!Profile.ShowHeader)
        {
            return;
        }

        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 34);
        ImGui.InvisibleButton("role-split-drag", size);
        if (!Profile.IsLocked && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, ImGuiCond.Always);
        }

        var state = encounter is null
            ? text.Get("等待数据", "Waiting")
            : encounter.IsActive
                ? text.Get("战斗中", "Running")
                : text.Get("已结束 · 保留上一场", "Ended · retained");
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + size, ImGui.GetColorU32(NavyRaised), 6);
        drawList.AddText(start + new Vector2(9, 7), ImGui.GetColorU32(Gold),
            text.Get("职能分栏", "Role split"));
        var stateSize = ImGui.CalcTextSize(state);
        drawList.AddText(
            start + new Vector2(size.X - stateSize.X - 9, 7),
            ImGui.GetColorU32(IceBlue),
            state);
        ImGui.Dummy(new Vector2(1, 3));
    }

    private void DrawSection(
        string title,
        IReadOnlyList<CombatantRow> rows,
        Encounter encounter,
        bool useHealing)
    {
        ImGui.TextColored(useHealing ? HealingGreen : Gold, title);
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
        var metrics = slots.Where(static slot =>
            slot.Metric is not MeterSlotMetric.Job and
            not MeterSlotMetric.PlayerName and
            not MeterSlotMetric.Rank).ToArray();
        const int columns = 3;
        var metricRows = Math.Max(1, (metrics.Length + columns - 1) / columns);
        var rowHeight = 31 + (metricRows * 20);
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
        if (slots.Any(static slot => slot.Metric == MeterSlotMetric.Job))
        {
            cursorX += DrawJob(row, new Vector2(cursorX, start.Y + 4), 22) + 6;
        }
        if (slots.Any(static slot => slot.Metric == MeterSlotMetric.PlayerName))
        {
            drawList.AddText(
                new Vector2(cursorX, start.Y + 6),
                ImGui.GetColorU32(row.IsLocalPlayer ? Gold : Vector4.One),
                MeterSlotPresentation.TrimToWidth(displayName, Math.Max(20, width - (cursorX - start.X) - 8)));
        }

        var cellWidth = width / columns;
        for (var index = 0; index < metrics.Length; index++)
        {
            var column = index % columns;
            var metricRow = index / columns;
            var metricStart = start + new Vector2(8 + (column * cellWidth), 32 + (metricRow * 20));
            var label = MeterSlotPresentation.Label(metrics[index].Metric, text);
            var value = MeterSlotPresentation.Value(metrics[index].Metric, row, displayName);
            var combined = $"{label}  {value}";
            var color = metrics[index].Metric == MeterSlotMetric.Hps || useHealing &&
                        metrics[index].Metric == MeterSlotMetric.TotalHealing
                ? HealingGreen
                : metrics[index].Metric is MeterSlotMetric.Dps or MeterSlotMetric.Rdps
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
}
