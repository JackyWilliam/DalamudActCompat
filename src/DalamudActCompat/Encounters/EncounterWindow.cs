using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.State;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.Encounters;

public sealed class EncounterWindow : Window
{
    private readonly EncounterStateStore stateStore;

    public EncounterWindow(EncounterStateStore stateStore)
        : base("Encounter History###DalamudActCompatHistory")
    {
        this.stateStore = stateStore;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(640, 260),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var snapshot = stateStore.GetSnapshot();
        if (snapshot.Recent.Count == 0)
        {
            ImGui.TextUnformatted("No saved encounters.");
            return;
        }

        if (!ImGui.BeginTable("history-table", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Start");
        ImGui.TableSetupColumn("End");
        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Enemy");
        ImGui.TableSetupColumn("Duration");
        ImGui.TableSetupColumn("Party");
        ImGui.TableSetupColumn("DPS");
        ImGui.TableSetupColumn("HPS");
        ImGui.TableSetupColumn("Deaths");
        ImGui.TableHeadersRow();

        foreach (var encounter in snapshot.Recent)
        {
            var duration = Math.Max(1.0, encounter.Duration.TotalSeconds);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(encounter.StartTime.LocalDateTime.ToString("MM-dd HH:mm"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(encounter.EndTime?.LocalDateTime.ToString("HH:mm") ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(encounter.ZoneName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(encounter.EnemyName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{(int)encounter.Duration.TotalMinutes:00}:{encounter.Duration.Seconds:00}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(encounter.Combatants.Count.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((encounter.TotalDamage / duration).ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((encounter.TotalHealing / duration).ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(encounter.TotalDeaths.ToString());
        }

        ImGui.EndTable();
    }
}
