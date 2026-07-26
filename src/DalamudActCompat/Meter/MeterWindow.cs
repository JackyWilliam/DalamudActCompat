using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.State;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.Meter;

public sealed class MeterWindow : Window
{
    private readonly MeterService meterService;
    private readonly EncounterStateStore stateStore;
    private readonly PluginConfiguration configuration;
    private readonly UiText text;

    public MeterWindow(MeterService meterService, EncounterStateStore stateStore, PluginConfiguration configuration, UiText text)
        : base("ACT 兼容悬浮窗###DalamudActCompatMeter")
    {
        this.meterService = meterService;
        this.stateStore = stateStore;
        this.configuration = configuration;
        this.text = text;
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
    }

    public override void Draw()
    {
        var settings = configuration.Meter;
        var snapshot = meterService.Snapshot;
        using var fontScale = new FontScaleScope(settings.FontScale);

        if (snapshot.Current is null)
        {
            ImGui.TextUnformatted(text.Get("暂无战斗数据。", "No encounter data."));
            return;
        }

        var encounter = snapshot.Current;
        if (settings.ShowHeader)
        {
            ImGui.TextUnformatted($"{encounter.EnemyName} | {encounter.ZoneName} | {FormatDuration(encounter.Duration)} | {(encounter.IsActive ? text.Get("战斗中", "Running") : text.Get("已结束", "Ended"))}");
        }

        if (ImGui.BeginCombo(text.Get("排序", "Sort"), settings.SortMode.ToString()))
        {
            foreach (var mode in Enum.GetValues<MeterSortMode>())
            {
                if (ImGui.Selectable(mode.ToString(), settings.SortMode == mode))
                {
                    settings.SortMode = mode;
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
        if (ImGui.BeginTable("meter-table", columnCount, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
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
