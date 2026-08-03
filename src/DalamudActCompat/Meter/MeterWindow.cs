using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.State;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace DalamudActCompat.Meter;

public sealed class MeterWindow : Window
{
    private readonly MeterService meterService;
    private readonly EncounterStateStore stateStore;
    private readonly PluginConfiguration configuration;
    private readonly UiText text;
    private readonly Action saveConfiguration;

    public MeterWindow(
        MeterService meterService,
        EncounterStateStore stateStore,
        PluginConfiguration configuration,
        UiText text,
        Action saveConfiguration)
        : base("ACT 兼容悬浮窗###DalamudActCompatMeter")
    {
        this.meterService = meterService;
        this.stateStore = stateStore;
        this.configuration = configuration;
        this.text = text;
        this.saveConfiguration = saveConfiguration;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(420, 180),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override bool DrawConditions()
    {
        var settings = configuration.Meter;
        WindowName = text.Get("ACT 兼容悬浮窗###DalamudActCompatMeter", "ACT Compat Meter###DalamudActCompatMeter");
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
        Flags = ImGuiWindowFlags.NoCollapse;
        if (settings.IsLocked)
        {
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        }

        if (settings.IsLocked && settings.ClickThroughWhenLocked)
        {
            Flags |= ImGuiWindowFlags.NoInputs;
        }

        ImGui.SetNextWindowBgAlpha(Math.Clamp(settings.BackgroundOpacity, 0.05f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.035f, 0.055f, 0.09f, 1));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.62f, 0.52f, 0.28f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.50f, 0.42f, 0.24f, 0.75f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.075f, 0.11f, 0.17f, 1));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.11f, 0.17f, 0.25f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.17f, 0.25f, 0.35f, 1));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.15f, 0.21f, 0.29f, 1));
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, new Vector4(0.10f, 0.14f, 0.21f, 1));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(8);
    }

    public override void Draw()
    {
        var settings = configuration.Meter;
        var snapshot = meterService.Snapshot;
        using var fontScale = new FontScaleScope(settings.FontScale);

        if (snapshot.Current is null)
        {
            ImGui.TextColored(
                new Vector4(0.38f, 0.72f, 0.90f, 1),
                text.Get("● 等待战斗数据", "● Waiting for encounter data"));
            ImGui.TextDisabled(text.Get("解析开始后数据会自动显示在这里。", "Data will appear here when parsing begins."));
            return;
        }

        var encounter = snapshot.Current;
        if (settings.ShowHeader)
        {
            ImGui.TextColored(
                encounter.IsActive
                    ? new Vector4(0.38f, 0.78f, 0.66f, 1)
                    : new Vector4(0.66f, 0.69f, 0.74f, 1),
                encounter.IsActive ? "●" : "○");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.90f, 0.81f, 0.55f, 1), encounter.EnemyName);
            ImGui.SameLine();
            ImGui.TextDisabled($"{encounter.ZoneName}  ·  {FormatDuration(encounter.Duration)}  ·  {(encounter.IsActive ? text.Get("战斗中", "Running") : text.Get("已结束", "Ended"))}");
            ImGui.Separator();
        }

        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo(text.Get("排序", "Sort"), settings.SortMode.ToString()))
        {
            foreach (var mode in Enum.GetValues<MeterSortMode>())
            {
                if (ImGui.Selectable(mode.ToString(), settings.SortMode == mode))
                {
                    settings.SortMode = mode;
                    saveConfiguration();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button(text.Get("重置", "Reset")))
        {
            stateStore.ResetCurrent();
        }

        var columnCount = 1 +
                          (settings.ShowJob ? 1 : 0) +
                          (settings.ShowDps ? 1 : 0) +
                          (settings.ShowDamage ? 1 : 0) +
                          (settings.ShowDamagePercent ? 1 : 0) +
                          (settings.ShowHps && settings.SortMode != MeterSortMode.Hps ? 1 : 0) +
                          (settings.ShowHealing ? 1 : 0) +
                          (settings.ShowDeaths ? 1 : 0);
        var tableFlags = ImGuiTableFlags.RowBg |
                         ImGuiTableFlags.BordersInnerH |
                         ImGuiTableFlags.BordersInnerV |
                         ImGuiTableFlags.PadOuterX |
                         ImGuiTableFlags.SizingStretchProp |
                         ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("meter-table", columnCount, tableFlags, new Vector2(0, -1)))
        {
            if (settings.ShowJob) ImGui.TableSetupColumn(text.Get("职业", "Job"), ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableSetupColumn(text.Get("名称", "Name"));
            if (settings.ShowDps) ImGui.TableSetupColumn(PrimaryRateLabel(settings));
            if (settings.ShowDamage) ImGui.TableSetupColumn(text.Get("伤害", "Damage"));
            if (settings.ShowDamagePercent) ImGui.TableSetupColumn("%");
            if (settings.ShowHps && settings.SortMode != MeterSortMode.Hps) ImGui.TableSetupColumn("HPS");
            if (settings.ShowHealing) ImGui.TableSetupColumn(text.Get("治疗量", "Healing"));
            if (settings.ShowDeaths) ImGui.TableSetupColumn(text.Get("死亡", "Deaths"));
            ImGui.TableHeadersRow();

            foreach (var row in meterService.GetRows())
            {
                ImGui.TableNextRow();
                if (row.IsLocalPlayer)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(settings.LocalPlayerColor));
                }

                if (settings.ShowJob)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(JobIconText(row.Job));
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Name);
                if (settings.ShowDps)
                {
                    ImGui.TableNextColumn();
                    var primaryRate = settings.SortMode == MeterSortMode.Hps ? row.Hps : row.Dps;
                    ImGui.TextUnformatted(primaryRate.ToString("N0"));
                }
                if (settings.ShowDamage) { ImGui.TableNextColumn(); ImGui.TextUnformatted(row.TotalDamage.ToString("N0")); }
                if (settings.ShowDamagePercent) { ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.DamagePercent:N1}%"); }
                if (settings.ShowHps && settings.SortMode != MeterSortMode.Hps)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Hps.ToString("N0"));
                }
                if (settings.ShowHealing) { ImGui.TableNextColumn(); ImGui.TextUnformatted(row.TotalHealing.ToString("N0")); }
                if (settings.ShowDeaths) { ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Deaths.ToString()); }
            }

            ImGui.EndTable();
        }
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

        public void Dispose() => ImGui.SetWindowFontScale(1.0f);
    }
}
