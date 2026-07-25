using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.UI;

namespace DalamudActCompat.Encounters;

public sealed class LogHistoryWindow : Window
{
    private readonly PluginPaths paths;
    private readonly UiText text;
    private string? selectedFile;
    private Encounter? selectedEncounter;
    private string rawJson = string.Empty;
    private string loadError = string.Empty;
    private bool showRawJson;

    public LogHistoryWindow(PluginPaths paths, UiText text)
        : base("战斗日志历史###DalamudActCompatLogHistory")
    {
        this.paths = paths;
        this.text = text;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(820, 500),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        WindowName = text.Get("战斗日志历史###DalamudActCompatLogHistory", "Combat log history###DalamudActCompatLogHistory");
        Directory.CreateDirectory(paths.EncounterLogDirectory);
        if (ImGui.Button(text.Get("打开日志文件夹", "Open log folder")))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.EncounterLogDirectory)
            {
                UseShellExecute = true,
            });
        }

        ImGui.SameLine();
        ImGui.Checkbox(text.Get("查看原始 JSON", "Show raw JSON"), ref showRawJson);
        ImGui.Separator();
        var files = Directory.EnumerateFiles(paths.EncounterLogDirectory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        if (files.Length == 0)
        {
            ImGui.TextDisabled(text.Get("暂无战斗日志。完成一场战斗后会自动生成。", "No encounter logs yet."));
            return;
        }

        if (ImGui.BeginChild("log-list", new System.Numerics.Vector2(260, 0), true))
        {
            foreach (var file in files)
            {
                var label = Path.GetFileNameWithoutExtension(file);
                if (ImGui.Selectable(label, string.Equals(file, selectedFile, StringComparison.OrdinalIgnoreCase)))
                {
                    Load(file);
                }
            }
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("log-visualization", new System.Numerics.Vector2(0, 0), true))
        {
            DrawSelected();
        }
        ImGui.EndChild();
    }

    private void Load(string file)
    {
        selectedFile = file;
        rawJson = File.ReadAllText(file);
        loadError = string.Empty;
        try
        {
            selectedEncounter = JsonSerializer.Deserialize<Encounter>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (selectedEncounter is null)
            {
                loadError = text.Get("日志内容为空。", "The log is empty.");
            }
        }
        catch (Exception ex)
        {
            selectedEncounter = null;
            loadError = ex.Message;
        }
    }

    private void DrawSelected()
    {
        if (!string.IsNullOrWhiteSpace(loadError))
        {
            ImGui.TextWrapped($"{text.Get("读取失败", "Load failed")}: {loadError}");
            return;
        }

        var encounter = selectedEncounter;
        if (encounter is null)
        {
            ImGui.TextDisabled(text.Get("请从左侧选择一场战斗。", "Select an encounter on the left."));
            return;
        }

        if (showRawJson)
        {
            ImGui.TextWrapped(rawJson);
            return;
        }

        var duration = Math.Max(1, encounter.Duration.TotalSeconds);
        ImGui.TextUnformatted($"{encounter.EnemyName} — {encounter.ZoneName}");
        ImGui.TextUnformatted(
            $"{encounter.StartTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}  |  " +
            $"{text.Get("时长", "Duration")}: {(int)encounter.Duration.TotalMinutes:00}:{encounter.Duration.Seconds:00}  |  " +
            $"{text.Get("总伤害", "Damage")}: {encounter.TotalDamage:N0}  |  DPS: {encounter.TotalDamage / duration:N0}");
        ImGui.Separator();

        if (ImGui.BeginTable("combatants", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(text.Get("职业", "Job"));
            ImGui.TableSetupColumn(text.Get("玩家", "Player"));
            ImGui.TableSetupColumn("DPS");
            ImGui.TableSetupColumn("EncDPS");
            ImGui.TableSetupColumn("ExtDPS");
            ImGui.TableSetupColumn(text.Get("伤害", "Damage"));
            ImGui.TableSetupColumn("HPS");
            ImGui.TableSetupColumn(text.Get("治疗量", "Healing"));
            ImGui.TableSetupColumn(text.Get("死亡", "Deaths"));
            ImGui.TableHeadersRow();
            foreach (var combatant in encounter.Combatants.OrderByDescending(item => item.TotalDamage))
            {
                ImGui.TableNextRow();
                Cell(combatant.Job);
                Cell(combatant.Name);
                Cell((combatant.Dps > 0 ? combatant.Dps : combatant.TotalDamage / duration).ToString("N0"));
                Cell((combatant.EncDps > 0 ? combatant.EncDps : combatant.TotalDamage / duration).ToString("N0"));
                Cell((combatant.ExtDps > 0 ? combatant.ExtDps : combatant.TotalDamage / duration).ToString("N0"));
                Cell(combatant.TotalDamage.ToString("N0"));
                Cell((combatant.TotalHealing / duration).ToString("N0"));
                Cell(combatant.TotalHealing.ToString("N0"));
                Cell(combatant.Deaths.ToString());
            }
            ImGui.EndTable();
        }

        if (encounter.ActionSummaries.Count > 0 && ImGui.CollapsingHeader(text.Get("技能统计", "Action summaries")))
        {
            if (ImGui.BeginTable("actions", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn(text.Get("技能", "Action"));
                ImGui.TableSetupColumn(text.Get("次数", "Uses"));
                ImGui.TableSetupColumn(text.Get("伤害", "Damage"));
                ImGui.TableSetupColumn(text.Get("治疗", "Healing"));
                ImGui.TableSetupColumn(text.Get("暴击", "Crits"));
                ImGui.TableSetupColumn(text.Get("直击", "Direct hits"));
                ImGui.TableHeadersRow();
                foreach (var action in encounter.ActionSummaries.OrderByDescending(item => item.TotalDamage))
                {
                    ImGui.TableNextRow();
                    Cell(action.ActionName);
                    Cell(action.Uses.ToString());
                    Cell(action.TotalDamage.ToString("N0"));
                    Cell(action.TotalHealing.ToString("N0"));
                    Cell(action.Crits.ToString());
                    Cell(action.DirectHits.ToString());
                }
                ImGui.EndTable();
            }
        }

        ImGui.TextDisabled(
            $"{text.Get("事件", "Events")}: {encounter.DamageEvents.Count} " +
            $"{text.Get("伤害", "damage")}, {encounter.HealEvents.Count} " +
            $"{text.Get("治疗", "healing")}, {encounter.DeathEvents.Count} " +
            $"{text.Get("死亡", "deaths")}");
    }

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }
}
