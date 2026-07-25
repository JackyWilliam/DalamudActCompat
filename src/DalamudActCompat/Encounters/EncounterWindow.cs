using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.State;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.UI;

namespace DalamudActCompat.Encounters;

public sealed class EncounterWindow : Window
{
    private readonly EncounterStateStore stateStore;
    private readonly UiText text;
    private readonly Action openLogHistory;

    public EncounterWindow(EncounterStateStore stateStore, UiText text, Action openLogHistory)
        : base("战斗历史###DalamudActCompatHistory")
    {
        this.stateStore = stateStore;
        this.text = text;
        this.openLogHistory = openLogHistory;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(640, 260),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var snapshot = stateStore.GetSnapshot();
        WindowName = text.Get("战斗历史###DalamudActCompatHistory", "Encounter History###DalamudActCompatHistory");
        if (ImGui.Button(text.Get("查看战斗日志文件", "View encounter log files")))
        {
            openLogHistory();
        }
        if (snapshot.Recent.Count == 0)
        {
            ImGui.TextUnformatted(text.Get("没有已保存的战斗。", "No saved encounters."));
            return;
        }

        if (!ImGui.BeginTable("history-table", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn(text.Get("开始", "Start"));
        ImGui.TableSetupColumn(text.Get("结束", "End"));
        ImGui.TableSetupColumn(text.Get("区域", "Zone"));
        ImGui.TableSetupColumn(text.Get("敌人", "Enemy"));
        ImGui.TableSetupColumn(text.Get("时长", "Duration"));
        ImGui.TableSetupColumn(text.Get("人数", "Party"));
        ImGui.TableSetupColumn("DPS");
        ImGui.TableSetupColumn("HPS");
        ImGui.TableSetupColumn(text.Get("死亡", "Deaths"));
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
