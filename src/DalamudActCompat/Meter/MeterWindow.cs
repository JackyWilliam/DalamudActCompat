using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.State;
using DalamudActCompat.Plugin;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.Meter;

public sealed class MeterWindow : Window
{
    private readonly MeterService meterService;
    private readonly EncounterStateStore stateStore;
    private readonly PluginConfiguration configuration;

    public MeterWindow(MeterService meterService, EncounterStateStore stateStore, PluginConfiguration configuration)
        : base("ACT Compat Meter###DalamudActCompatMeter")
    {
        this.meterService = meterService;
        this.stateStore = stateStore;
        this.configuration = configuration;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(420, 180),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override bool DrawConditions()
    {
        var settings = configuration.Meter;
        if (!settings.IsVisible)
        {
            return false;
        }

        var snapshot = meterService.Snapshot;
        return !settings.AutoHideOutOfCombat || snapshot.Current?.IsActive == true || snapshot.Current is not null;
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
            ImGui.TextUnformatted("No encounter data.");
            return;
        }

        var encounter = snapshot.Current;
        ImGui.TextUnformatted($"{encounter.EnemyName} | {encounter.ZoneName} | {FormatDuration(encounter.Duration)} | {(encounter.IsActive ? "Running" : "Ended")}");

        if (ImGui.BeginCombo("Sort", settings.SortMode.ToString()))
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
        if (ImGui.Button("Reset"))
        {
            stateStore.ResetCurrent();
        }

        if (ImGui.BeginTable("meter-table", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("DPS");
            ImGui.TableSetupColumn("Damage");
            ImGui.TableSetupColumn("%");
            ImGui.TableSetupColumn("HPS");
            ImGui.TableSetupColumn("Healing");
            ImGui.TableSetupColumn("Deaths");
            ImGui.TableHeadersRow();

            foreach (var row in meterService.GetRows())
            {
                ImGui.TableNextRow();
                if (row.IsLocalPlayer)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new System.Numerics.Vector4(0.25f, 0.42f, 0.55f, 0.45f)));
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(JobIconText(row.Job));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Dps.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.TotalDamage.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{row.DamagePercent:N1}%");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Hps.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.TotalHealing.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Deaths.ToString());
            }

            ImGui.EndTable();
        }
    }

    private static string FormatDuration(TimeSpan duration)
        => $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

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
