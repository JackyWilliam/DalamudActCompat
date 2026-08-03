using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

namespace DalamudActCompat.Encounters;

public sealed class EncounterWindow : Window
{
    private enum HistoryPage
    {
        Recent,
        LogFiles,
    }

    private static readonly Vector4 Navy = new(0.045f, 0.065f, 0.105f, 1);
    private static readonly Vector4 NavyRaised = new(0.075f, 0.10f, 0.15f, 1);
    private static readonly Vector4 NavyHover = new(0.11f, 0.16f, 0.23f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.38f, 0.72f, 0.90f, 1);

    private readonly EncounterStateStore stateStore;
    private readonly PluginPaths paths;
    private readonly PluginConfiguration configuration;
    private readonly UiText text;
    private HistoryPage selectedPage;
    private Guid? selectedRecentId;
    private string? selectedFile;
    private Encounter? selectedFileEncounter;
    private string rawJson = string.Empty;
    private string loadError = string.Empty;
    private bool showRawJson;

    public EncounterWindow(
        EncounterStateStore stateStore,
        PluginPaths paths,
        PluginConfiguration configuration,
        UiText text)
        : base("战斗记录###DalamudActCompatHistory")
    {
        this.stateStore = stateStore;
        this.paths = paths;
        this.configuration = configuration;
        this.text = text;
        Size = new Vector2(980, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(780, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void OpenRecent()
    {
        selectedPage = HistoryPage.Recent;
        IsOpen = true;
    }

    public void OpenLogFiles()
    {
        selectedPage = HistoryPage.LogFiles;
        IsOpen = true;
    }

    public override void Draw()
    {
        WindowName = text.Get("战斗记录###DalamudActCompatHistory", "Combat History###DalamudActCompatHistory");
        PushTheme();
        try
        {
            ImGui.TextColored(Gold, text.Get("战斗记录", "Combat History"));
            ImGui.SameLine();
            ImGui.TextDisabled(text.Get("近期战斗与逐场日志使用同一个详情视图", "Recent encounters and log files share one detail view"));
            ImGui.Spacing();
            PageButton(HistoryPage.Recent, text.Get("近期战斗", "Recent encounters"));
            ImGui.SameLine();
            PageButton(HistoryPage.LogFiles, text.Get("日志文件", "Log files"));
            ImGui.Separator();
            ImGui.Spacing();

            if (selectedPage == HistoryPage.Recent)
            {
                DrawRecentPage();
            }
            else
            {
                DrawLogFilesPage();
            }
        }
        finally
        {
            PopTheme();
        }
    }

    private void DrawRecentPage()
    {
        var recent = stateStore.GetSnapshot().Recent;
        if (recent.Count == 0)
        {
            ImGui.TextDisabled(text.Get("没有已保存的战斗。", "No saved encounters."));
            return;
        }

        if (selectedRecentId is null || recent.All(encounter => encounter.Id != selectedRecentId))
        {
            selectedRecentId = recent[0].Id;
        }

        var listWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.30f, 240, 320);
        if (ImGui.BeginChild("recent-encounter-list", new Vector2(listWidth, -1), true))
        {
            ImGui.TextColored(Gold, text.Get("近期战斗", "Recent encounters"));
            ImGui.Separator();
            foreach (var encounter in recent)
            {
                var selected = encounter.Id == selectedRecentId;
                var label = $"{encounter.EnemyName}##recent-{encounter.Id:N}";
                if (ImGui.Selectable(label, selected))
                {
                    selectedRecentId = encounter.Id;
                }
                ImGui.TextDisabled(
                    $"{encounter.StartTime.LocalDateTime:MM-dd HH:mm}  ·  " +
                    $"{FormatDuration(encounter.Duration)}  ·  {encounter.ZoneName}");
                ImGui.Spacing();
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("recent-encounter-details", new Vector2(-1, -1), true))
        {
            DrawEncounterDetails(recent.First(encounter => encounter.Id == selectedRecentId.Value));
        }
        ImGui.EndChild();
    }

    private void DrawLogFilesPage()
    {
        Directory.CreateDirectory(paths.EncounterLogDirectory);
        if (ImGui.Button(text.Get("打开日志文件夹", "Open log folder")))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.EncounterLogDirectory)
            {
                UseShellExecute = true,
            });
        }
        ImGui.SameLine();
        if (configuration.Meter.PlayerIdentityMode == PlayerIdentityMode.Original)
        {
            ImGui.Checkbox(text.Get("查看原始 JSON", "Show raw JSON"), ref showRawJson);
        }
        else
        {
            showRawJson = false;
            ImGui.TextDisabled(text.Get(
                "已启用 ID 遮盖，原始 JSON 不在界面中显示",
                "Player ID masking is enabled; raw JSON is hidden in the UI"));
        }
        ImGui.Spacing();

        var files = Directory.EnumerateFiles(paths.EncounterLogDirectory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        if (files.Length == 0)
        {
            ImGui.TextDisabled(text.Get("暂无战斗日志。完成一场战斗后会自动生成。", "No encounter logs yet."));
            return;
        }

        var listWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.30f, 240, 320);
        if (ImGui.BeginChild("log-file-list", new Vector2(listWidth, -1), true))
        {
            ImGui.TextColored(Gold, text.Get("日志文件", "Log files"));
            ImGui.Separator();
            foreach (var file in files)
            {
                var label = Path.GetFileNameWithoutExtension(file);
                if (ImGui.Selectable($"{label}##log-file-{label}", string.Equals(file, selectedFile, StringComparison.OrdinalIgnoreCase)))
                {
                    Load(file);
                }
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("log-file-details", new Vector2(-1, -1), true))
        {
            DrawSelectedFile();
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
            selectedFileEncounter = JsonSerializer.Deserialize<Encounter>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (selectedFileEncounter is null)
            {
                loadError = text.Get("日志内容为空。", "The log is empty.");
            }
        }
        catch (Exception ex)
        {
            selectedFileEncounter = null;
            loadError = ex.Message;
        }
    }

    private void DrawSelectedFile()
    {
        if (!string.IsNullOrWhiteSpace(loadError))
        {
            ImGui.TextWrapped($"{text.Get("读取失败", "Load failed")}: {loadError}");
            return;
        }

        if (selectedFileEncounter is null)
        {
            ImGui.TextDisabled(text.Get("请从左侧选择一场战斗。", "Select an encounter on the left."));
            return;
        }

        if (showRawJson)
        {
            ImGui.TextWrapped(rawJson);
            return;
        }

        DrawEncounterDetails(selectedFileEncounter);
    }

    private void DrawEncounterDetails(Encounter encounter)
    {
        var durationSeconds = Math.Max(1, encounter.Duration.TotalSeconds);
        ImGui.TextColored(Gold, encounter.EnemyName);
        ImGui.TextDisabled(
            $"{encounter.ZoneName}  ·  {encounter.StartTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}  ·  " +
            $"{FormatDuration(encounter.Duration)}");
        ImGui.Spacing();

        if (ImGui.BeginTable("encounter-summary", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            SummaryCell(text.Get("队伍人数", "Party"), encounter.Combatants.Count.ToString());
            SummaryCell(text.Get("总伤害", "Damage"), encounter.TotalDamage.ToString("N0"));
            SummaryCell("DPS", (encounter.TotalDamage / durationSeconds).ToString("N0"));
            SummaryCell(text.Get("死亡", "Deaths"), encounter.TotalDeaths.ToString());
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("队伍表现", "Party performance"));
        if (ImGui.BeginTable(
                "combatants",
                9,
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.SizingStretchProp))
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
                if (combatant.IsLocalPlayer)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(0.16f, 0.32f, 0.45f, 0.65f)));
                }
                Cell(combatant.Job);
                Cell(PlayerIdentityFormatter.Format(combatant, encounter.Combatants, configuration.Meter, text));
                Cell((combatant.Dps > 0 ? combatant.Dps : combatant.TotalDamage / durationSeconds).ToString("N0"));
                Cell((combatant.EncDps > 0 ? combatant.EncDps : combatant.TotalDamage / durationSeconds).ToString("N0"));
                Cell((combatant.ExtDps > 0 ? combatant.ExtDps : combatant.TotalDamage / durationSeconds).ToString("N0"));
                Cell(combatant.TotalDamage.ToString("N0"));
                Cell((combatant.TotalHealing / durationSeconds).ToString("N0"));
                Cell(combatant.TotalHealing.ToString("N0"));
                Cell(combatant.Deaths.ToString());
            }
            ImGui.EndTable();
        }

        if (encounter.ActionSummaries.Count > 0 && ImGui.CollapsingHeader(text.Get("技能统计", "Action summaries")))
        {
            if (ImGui.BeginTable("actions", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn(text.Get("施放职业", "Source job"));
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
                    Cell(PlayerIdentityFormatter.FormatActionOwner(
                        action.CombatantId,
                        encounter.Combatants,
                        configuration.Meter,
                        text));
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
            $"{text.Get("事件", "Events")}: {encounter.DamageEvents.Count} {text.Get("伤害", "damage")}, " +
            $"{encounter.HealEvents.Count} {text.Get("治疗", "healing")}, " +
            $"{encounter.DeathEvents.Count} {text.Get("死亡", "deaths")}");
    }

    private void PageButton(HistoryPage page, string label)
    {
        var selected = selectedPage == page;
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? new Vector4(0.26f, 0.23f, 0.14f, 1) : new Vector4(0.12f, 0.17f, 0.24f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? new Vector4(0.95f, 0.86f, 0.60f, 1) : new Vector4(0.84f, 0.87f, 0.91f, 1));
        if (ImGui.Button($"{label}##history-page-{page}", new Vector2(150, 34)))
        {
            selectedPage = page;
        }
        ImGui.PopStyleColor(2);
    }

    private static void SummaryCell(string label, string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TextUnformatted(value);
    }

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private static string FormatDuration(TimeSpan duration)
        => $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

    private static void PushTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.34f, 0.29f, 0.18f, 0.70f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, NavyRaised);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, NavyHover);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.17f, 0.24f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.25f, 0.34f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.30f, 0.37f, 1));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, IceBlue);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.22f, 0.25f, 0.28f, 1));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, NavyHover);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
    }

    private static void PopTheme()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(11);
    }
}
