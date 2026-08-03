using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using System.Numerics;

namespace DalamudActCompat.Meter;

public sealed class MeterWindow : Window
{
    private static readonly Vector4 NavyRaised = new(0.075f, 0.10f, 0.15f, 0.94f);
    private static readonly Vector4 NavyHover = new(0.11f, 0.16f, 0.23f, 0.96f);
    private static readonly Vector4 Gold = new(0.90f, 0.81f, 0.55f, 1);
    private static readonly Vector4 IceBlue = new(0.38f, 0.72f, 0.90f, 1);

    private readonly MeterService meterService;
    private readonly EncounterStateStore stateStore;
    private readonly FflogsEstimateService fflogsEstimateService;
    private readonly PluginConfiguration configuration;
    private readonly UiText text;
    private readonly Action saveConfiguration;
    private bool isDragging;
    private Vector2 dragOffset;

    public MeterWindow(
        MeterService meterService,
        EncounterStateStore stateStore,
        FflogsEstimateService fflogsEstimateService,
        PluginConfiguration configuration,
        UiText text,
        Action saveConfiguration)
        : base("战斗统计###DalamudActCompatMeter")
    {
        this.meterService = meterService;
        this.stateStore = stateStore;
        this.fflogsEstimateService = fflogsEstimateService;
        this.configuration = configuration;
        this.text = text;
        this.saveConfiguration = saveConfiguration;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Size = new Vector2(500, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 170),
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

        var snapshot = meterService.Snapshot;
        return !settings.AutoHideOutOfCombat || snapshot.Current?.IsActive == true;
    }

    public override void PreDraw()
    {
        var settings = configuration.Meter;
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
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(7, 7));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(8);
    }

    public override void Draw()
    {
        var settings = configuration.Meter;
        var snapshot = meterService.Snapshot;
        using var fontScale = new FontScaleScope(settings.FontScale);

        if (snapshot.Current is null)
        {
            DrawEmptyState(settings);
            return;
        }

        var encounter = snapshot.Current;
        if (settings.ShowHeader)
        {
            DrawEncounterHeader(encounter, settings);
        }
        else if (!settings.IsLocked)
        {
            DrawCompactDragHandle(settings);
        }

        if (!settings.IsLocked)
        {
            DrawControls(settings);
        }

        var rows = meterService.GetRows();
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(text.Get("等待玩家数据…", "Waiting for player data…"));
            return;
        }

        var maximumScore = Math.Max(1, rows.Max(row => Score(row, settings.SortMode)));
        if (ImGui.BeginChild("meter-rows", new Vector2(-1, -1), false))
        {
            for (var index = 0; index < rows.Count; index++)
            {
                DrawCombatantRow(rows[index], index + 1, maximumScore, encounter, settings);
            }
        }
        ImGui.EndChild();
    }

    private void DrawEmptyState(MeterSettings settings)
    {
        const float height = 42;
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("empty-meter-drag", new Vector2(width, height));
        HandleHeaderDrag(settings);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(NavyRaised), 6);
        drawList.AddText(start + new Vector2(10, 6), ImGui.GetColorU32(IceBlue), text.Get("● 等待战斗数据", "● Waiting for encounter data"));
        drawList.AddText(start + new Vector2(10, 23), ImGui.GetColorU32(new Vector4(0.66f, 0.69f, 0.74f, 1)), text.Get("解析开始后会自动显示排行", "Rankings appear automatically when parsing begins"));
    }

    private void DrawEncounterHeader(Encounter encounter, MeterSettings settings)
    {
        const float headerHeight = 44;
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("meter-header-drag", new Vector2(width, headerHeight));
        HandleHeaderDrag(settings);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + new Vector2(width, headerHeight), ImGui.GetColorU32(NavyRaised), 6);
        var stateColor = encounter.IsActive
            ? new Vector4(0.38f, 0.78f, 0.66f, 1)
            : new Vector4(0.66f, 0.69f, 0.74f, 1);
        drawList.AddText(start + new Vector2(10, 6), ImGui.GetColorU32(stateColor), encounter.IsActive ? "●" : "○");
        drawList.AddText(start + new Vector2(28, 6), ImGui.GetColorU32(Gold), encounter.EnemyName);
        var subtitle = $"{encounter.ZoneName}  ·  {FormatDuration(encounter.Duration)}  ·  " +
                       (encounter.IsActive ? text.Get("战斗中", "Running") : text.Get("已结束", "Ended"));
        drawList.AddText(start + new Vector2(28, 24), ImGui.GetColorU32(new Vector4(0.66f, 0.69f, 0.74f, 1)), subtitle);
    }

    private void DrawCompactDragHandle(MeterSettings settings)
    {
        const float height = 8;
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("meter-compact-drag", new Vector2(width, height));
        HandleHeaderDrag(settings);
        if (ImGui.IsItemHovered())
        {
            var drawList = ImGui.GetWindowDrawList();
            var centerY = start.Y + (height * 0.5f);
            drawList.AddLine(
                new Vector2(start.X + 8, centerY),
                new Vector2(start.X + width - 8, centerY),
                ImGui.GetColorU32(new Vector4(Gold.X, Gold.Y, Gold.Z, 0.65f)),
                2);
        }
    }

    private void HandleHeaderDrag(MeterSettings settings)
    {
        if (settings.IsLocked)
        {
            return;
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
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

    private void DrawControls(MeterSettings settings)
    {
        ImGui.SetNextItemWidth(130);
        if (ImGui.BeginCombo("##meter-sort", SortModeLabel(settings.SortMode)))
        {
            foreach (var mode in Enum.GetValues<MeterSortMode>())
            {
                if (ImGui.Selectable(SortModeLabel(mode), settings.SortMode == mode))
                {
                    settings.SortMode = mode;
                    saveConfiguration();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(text.Get("排序", "Sort"));
        ImGui.SameLine();
        if (ImGui.SmallButton(text.Get("清空当前战斗", "Clear encounter")))
        {
            stateStore.ResetCurrent();
        }
    }

    private void DrawCombatantRow(
        CombatantRow row,
        int rank,
        double maximumScore,
        Encounter encounter,
        MeterSettings settings)
    {
        var rowHeight = Math.Max(42, ImGui.GetTextLineHeightWithSpacing() * 2.25f);
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var end = start + new Vector2(width, rowHeight);
        ImGui.InvisibleButton($"meter-row-{rank}-{row.Name}", new Vector2(width, rowHeight));

        var drawList = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsItemHovered();
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(hovered ? NavyHover : NavyRaised), 5);

        var jobColor = JobColor(row.Job);
        var ratio = (float)Math.Clamp(Score(row, settings.SortMode) / maximumScore, 0, 1);
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

        var firstLineY = start.Y + 5;
        var secondLineY = start.Y + rowHeight * 0.53f;
        var x = start.X + 8;
        drawList.AddText(new Vector2(x, firstLineY), ImGui.GetColorU32(new Vector4(0.68f, 0.71f, 0.76f, 1)), $"{rank,2}");
        x += 25;

        if (settings.ShowJob)
        {
            var badgeSize = new Vector2(35, ImGui.GetTextLineHeight() + 4);
            drawList.AddRectFilled(new Vector2(x, firstLineY - 1), new Vector2(x, firstLineY - 1) + badgeSize, ImGui.GetColorU32(new Vector4(jobColor.X, jobColor.Y, jobColor.Z, 0.55f)), 4);
            var job = JobIconText(row.Job);
            var jobSize = ImGui.CalcTextSize(job);
            drawList.AddText(new Vector2(x + (badgeSize.X - jobSize.X) * 0.5f, firstLineY + 1), ImGui.GetColorU32(Vector4.One), job);
            x += badgeSize.X + 8;
        }

        var estimate = row.IsLocalPlayer && settings.SortMode != MeterSortMode.Hps
            ? fflogsEstimateService.GetEstimate(encounter)
            : null;
        var primary = settings.SortMode == MeterSortMode.Hps ? row.Hps : row.Dps;
        var showPrimary = settings.SortMode == MeterSortMode.Hps ? settings.ShowHps : settings.ShowDps;
        var primaryText = showPrimary
            ? $"{(estimate is null ? string.Empty : $"~{estimate.Score}  ")}{PrimaryRateLabel(settings)}  {primary:N0}"
            : string.Empty;
        var primarySize = ImGui.CalcTextSize(primaryText);
        var rightX = end.X - primarySize.X - 9;
        var availableNameWidth = Math.Max(40, rightX - x - 10);
        var combatant = encounter.Combatants.FirstOrDefault(item =>
            string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase));
        var displayName = combatant is null
            ? row.Name
            : PlayerIdentityFormatter.Format(combatant, encounter.Combatants, settings, text);
        drawList.AddText(new Vector2(x, firstLineY), ImGui.GetColorU32(row.IsLocalPlayer ? Gold : Vector4.One), TrimToWidth(displayName, availableNameWidth));
        if (!string.IsNullOrEmpty(primaryText))
        {
            drawList.AddText(
                new Vector2(rightX, firstLineY),
                ImGui.GetColorU32(estimate?.Color ?? (row.IsLocalPlayer ? Gold : IceBlue)),
                primaryText);
        }

        if (estimate is not null && hovered)
        {
            ImGui.SetTooltip(text.Get(
                $"FFLogs 公开排名样本估算：{estimate.Score}（非官方实时成绩）",
                $"FFLogs public-ranking estimate: {estimate.Score} (not an official live parse)"));
        }

        var secondary = BuildSecondaryText(row, settings);
        drawList.AddText(new Vector2(x, secondLineY), ImGui.GetColorU32(new Vector4(0.70f, 0.73f, 0.78f, 1)), TrimToWidth(secondary, end.X - x - 9));
        ImGui.Spacing();
    }

    private string BuildSecondaryText(CombatantRow row, MeterSettings settings)
    {
        var parts = new List<string>();
        if (settings.ShowDamage)
        {
            parts.Add($"{text.Get("伤害", "DMG")} {row.TotalDamage:N0}");
        }
        if (settings.ShowDamagePercent)
        {
            parts.Add($"{row.DamagePercent:N1}%");
        }
        if (settings.ShowHps && settings.SortMode != MeterSortMode.Hps)
        {
            parts.Add($"HPS {row.Hps:N0}");
        }
        if (settings.ShowHealing)
        {
            parts.Add($"{text.Get("治疗", "HEAL")} {row.TotalHealing:N0}");
        }
        if (settings.ShowDeaths)
        {
            parts.Add($"{text.Get("死亡", "KO")} {row.Deaths}");
        }
        return string.Join("  ·  ", parts);
    }

    private static double Score(CombatantRow row, MeterSortMode mode) => mode switch
    {
        MeterSortMode.Hps => row.Hps,
        MeterSortMode.Damage => row.TotalDamage,
        MeterSortMode.Deaths => row.Deaths,
        _ => row.Dps,
    };

    private string SortModeLabel(MeterSortMode mode) => mode switch
    {
        MeterSortMode.Hps => "HPS",
        MeterSortMode.Damage => text.Get("总伤害", "Damage"),
        MeterSortMode.Deaths => text.Get("死亡", "Deaths"),
        _ => "DPS",
    };

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

    private static string PrimaryRateLabel(MeterSettings settings)
        => settings.SortMode == MeterSortMode.Hps
            ? "HPS"
            : settings.DpsMetric switch
            {
                DpsMetric.EncDps => "EncDPS",
                DpsMetric.ExtDps => "ExtDPS",
                _ => "DPS",
            };

    private static string JobIconText(string job)
        => string.IsNullOrWhiteSpace(job) ? "?" : job[..Math.Min(3, job.Length)].ToUpperInvariant();

    private readonly struct FontScaleScope : IDisposable
    {
        public FontScaleScope(float scale)
        {
            ImGui.SetWindowFontScale(Math.Clamp(scale, 0.75f, 1.8f));
        }

        public void Dispose() => ImGui.SetWindowFontScale(1);
    }
}
