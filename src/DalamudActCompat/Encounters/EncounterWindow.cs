using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Fflogs;
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

    private static Vector4 Navy => DactTheme.Tone(new Vector4(0.035f, 0.048f, 0.068f, 1), DactTheme.Palette.Surface);
    private static Vector4 NavyRaised => DactTheme.Tone(new Vector4(0.070f, 0.095f, 0.125f, 1), DactTheme.Palette.Raised);
    private static Vector4 NavyHover => DactTheme.Tone(new Vector4(0.105f, 0.145f, 0.185f, 1), DactTheme.Palette.Hover);
    private static Vector4 Gold => DactTheme.Tone(new Vector4(0.78f, 0.66f, 0.36f, 1), DactTheme.Palette.Gold);
    private static Vector4 IceBlue => DactTheme.Tone(new Vector4(0.42f, 0.78f, 0.96f, 1), DactTheme.Palette.Accent);

    private readonly EncounterStateStore stateStore;
    private readonly PluginPaths paths;
    private readonly PluginConfiguration configuration;
    private readonly UiText text;
    private readonly WindowDragController headerDrag = new();
    private readonly JobIconTextureSet jobIcons;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly Func<uint?, string, string> localizeZoneName;
    private readonly Action saveConfiguration;
    private HistoryPage selectedPage;
    private Guid? selectedRecentId;
    private readonly HashSet<Guid> expandedRecentFolderIds = [];
    private string? selectedFile;
    private Encounter? selectedFileEncounter;
    private string rawJson = string.Empty;
    private string loadError = string.Empty;
    private bool showRawJson;
    private bool outerFrameStylePushed;

    public EncounterWindow(
        EncounterStateStore stateStore,
        PluginPaths paths,
        PluginConfiguration configuration,
        UiText text,
        JobIconTextureSet jobIcons,
        ISharedImmediateTexture logoTexture,
        Func<uint?, string, string> localizeZoneName,
        Action saveConfiguration)
        : base("战斗记录###DalamudActCompatHistory")
    {
        this.stateStore = stateStore;
        this.paths = paths;
        this.configuration = configuration;
        this.text = text;
        this.jobIcons = jobIcons;
        this.logoTexture = logoTexture;
        this.localizeZoneName = localizeZoneName;
        this.saveConfiguration = saveConfiguration;
        Size = new Vector2(980, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(780, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
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

    public override void PreDraw()
    {
        headerDrag.PrepareNextWindow();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        outerFrameStylePushed = true;
        Flags = DactTheme.WindowFlags(Flags);
    }

    public override void PostDraw()
    {
        if (!outerFrameStylePushed)
        {
            return;
        }

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
        outerFrameStylePushed = false;
    }

    public override void Draw()
    {
        WindowName = text.Get("战斗记录###DalamudActCompatHistory", "Combat History###DalamudActCompatHistory");
        PushTheme();
        try
        {
            var pageLabel = selectedPage == HistoryPage.Recent
                ? text.Get("近期战斗", "Recent encounters")
                : text.Get("日志文件", "Log files");
            if (BrandedWindowChrome.Draw(
                    headerDrag,
                    logoTexture,
                    text.Get("战斗记录", "Combat History"),
                    pageLabel,
                    IceBlue,
                    ControlCenterWindow.FormatVersionLabel(typeof(EncounterWindow).Assembly.GetName().Version),
                    "combat-history", text: text))
            {
                IsOpen = false;
            }

            ImGui.Spacing();
            var pages = new[]
            {
                text.Get("近期战斗", "Recent encounters"),
                text.Get("日志文件", "Log files"),
            };
            var nextPage = BrandedWindowChrome.DrawNavigationRail(
                "combat-history-navigation",
                pages,
                (int)selectedPage,
                34);
            selectedPage = (HistoryPage)nextPage;
            ImGui.Spacing();

            if (ImGui.BeginChild("combat-history-page-content", new Vector2(-1, -1), true))
            {
                if (selectedPage == HistoryPage.Recent)
                {
                    DrawRecentPage();
                }
                else
                {
                    DrawLogFilesPage();
                }
            }
            ImGui.EndChild();
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

        var selectedEncounter = selectedRecentId is { } selectedId
            ? FindRecentEncounter(recent, selectedId)
            : null;
        if (selectedEncounter is null)
        {
            if (recent[0].SegmentRecords.Count > 0)
            {
                expandedRecentFolderIds.Add(recent[0].Id);
                selectedEncounter = recent[0].SegmentRecords[^1];
                selectedRecentId = selectedEncounter.Id;
            }
            else
            {
                selectedRecentId = recent[0].Id;
                selectedEncounter = recent[0];
            }
        }

        var listWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.30f, 240, 320);
        if (ImGui.BeginChild("recent-encounter-list", new Vector2(listWidth, -1), true))
        {
            // The list column is capped at 320 px; translated headings wrap within it.
            ImGui.PushTextWrapPos(0);
            DactTheme.TextColored(Gold, text.Get("近期战斗", "Recent encounters"));
            ImGui.PopTextWrapPos();
            ImGui.Separator();
            foreach (var encounter in recent)
            {
                var isFolder = encounter.SegmentRecords.Count > 0;
                var selected = encounter.Id == selectedRecentId ||
                               isFolder && encounter.SegmentRecords.Any(
                                   item => item.Id == selectedRecentId);
                var expanded = isFolder && expandedRecentFolderIds.Contains(encounter.Id);
                if (DrawEncounterListCard(encounter, selected, isFolder, expanded))
                {
                    if (isFolder && !expandedRecentFolderIds.Remove(encounter.Id))
                    {
                        expandedRecentFolderIds.Add(encounter.Id);
                    }
                    selectedEncounter = isFolder
                        ? encounter.SegmentRecords[^1]
                        : encounter;
                    selectedRecentId = selectedEncounter.Id;
                }
                if (expanded)
                {
                    for (var index = 0; index < encounter.SegmentRecords.Count; index++)
                    {
                        var segment = encounter.SegmentRecords[index];
                        if (DrawSegmentListCard(
                                segment,
                                index,
                                segment.Id == selectedRecentId))
                        {
                            selectedRecentId = segment.Id;
                            selectedEncounter = segment;
                        }
                    }
                }
                ImGui.Spacing();
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("recent-encounter-details", new Vector2(-1, -1), true))
        {
            DrawEncounterDetails(selectedEncounter);
        }
        ImGui.EndChild();
    }

    private void DrawLogFilesPage()
    {
        Directory.CreateDirectory(paths.EncounterLogDirectory);
        if (DactTheme.Button(text.Get("打开日志文件夹", "Open log folder")))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.EncounterLogDirectory)
            {
                UseShellExecute = true,
            });
        }
        if (configuration.Meter.PlayerIdentityMode == PlayerIdentityMode.Original)
        {
            ImGui.SameLine();
            DactTheme.Checkbox(text.Get("查看原始 JSON", "Show raw JSON"), ref showRawJson);
        }
        else
        {
            showRawJson = false;
            SameLineHint(text.Get(
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

        if (selectedFile is null || !files.Contains(selectedFile, StringComparer.OrdinalIgnoreCase))
        {
            Load(files[0]);
        }

        var listWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.30f, 240, 320);
        if (ImGui.BeginChild("log-file-list", new Vector2(listWidth, -1), true))
        {
            ImGui.PushTextWrapPos(0);
            DactTheme.TextColored(Gold, text.Get("日志文件", "Log files"));
            ImGui.PopTextWrapPos();
            ImGui.Separator();
            foreach (var file in files)
            {
                var label = Path.GetFileNameWithoutExtension(file);
                if (DrawLogFileCard(
                        file,
                        label,
                        string.Equals(file, selectedFile, StringComparison.OrdinalIgnoreCase)))
                {
                    Load(file);
                }
                ImGui.Spacing();
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
            ImGui.TextWrapped(text.Format($"读取失败：{loadError}", $"Load failed: {loadError}"));
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
        if (encounter.SegmentRecords.Count > 0)
        {
            DactTheme.TextColored(Gold, LocalizeEncounterTitle(encounter));
            ImGui.PushTextWrapPos(0);
            ImGui.TextDisabled(text.Format(
                $"本次副本包含 {encounter.SegmentRecords.Count} 把战斗，请在近期战斗中选择其中一把。",
                $"This duty contains {encounter.SegmentRecords.Count} pulls; select one under Recent encounters."));
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawEncounterDetails(encounter.SegmentRecords[^1]);
            return;
        }

        var damageDurationSeconds = Math.Max(1, encounter.EffectiveDuration.TotalSeconds);
        var healingDurationSeconds = Math.Max(1, encounter.Duration.TotalSeconds);
        DactTheme.TextColored(Gold, LocalizeEncounterTitle(encounter));
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(
            $"{localizeZoneName(encounter.TerritoryId, encounter.ZoneName)}  ·  " +
            $"{encounter.StartTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}  ·  " +
            $"{FormatDuration(encounter.EffectiveDuration)}");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var summaries = new (string Id, string Label, string Value)[]
        {
            ("party", text.Get("队伍人数", "Party"),
                encounter.Combatants.Count(static combatant => !MeterService.IsLimitBreak(combatant)).ToString()),
            ("damage", text.Get("总伤害", "Damage"), encounter.TotalDamage.ToString("N0")),
            ("dps", "DPS", (encounter.TotalDamage / damageDurationSeconds).ToString("N0")),
            ("deaths", text.Get("死亡", "Deaths"), encounter.TotalDeaths.ToString()),
        };
        // Four cards share a row only while every label and number fits; narrow or
        // enlarged layouts use two (or one) per row instead of clipping the values.
        var summaryStyle = ImGui.GetStyle();
        var summaryWidth = summaries.Max(item => Math.Max(ImGui.CalcTextSize(item.Label).X, ImGui.CalcTextSize(item.Value).X)) +
                           (summaryStyle.WindowPadding.X + summaryStyle.CellPadding.X) * 2 + 4;
        var available = ImGui.GetContentRegionAvail().X;
        var summaryColumns = available >= summaryWidth * 4 ? 4 : available >= summaryWidth * 2 ? 2 : 1;
        if (ImGui.BeginTable($"encounter-summary-{summaryColumns}", summaryColumns, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var (id, label, value) in summaries)
            {
                SummaryCell(id, label, value);
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DactTheme.TextColored(Gold, text.Get("队伍表现", "Party performance"));
        SameLineHint(text.Format(
            $"职业显示：{JobDisplayFormatter.Label(configuration.Meter.JobDisplayStyle, text)}（跟随战斗统计）",
            $"Job display: {JobDisplayFormatter.Label(configuration.Meter.JobDisplayStyle, text)} (follows Combat Meter)"));

        var sortMode = MeterSortModeOptions.Normalize(configuration.Meter.SortMode);
        var rateDurationSeconds = sortMode == MeterSortMode.Hps
            ? healingDurationSeconds
            : damageDurationSeconds;
        var dpsLabel = DpsRateLabel();
        DrawMetricButton(MeterSortMode.Dps, dpsLabel);
        ImGui.SameLine();
        DrawMetricButton(MeterSortMode.Hps, "HPS");
        SameLineHint(text.Format($"仅按 {dpsLabel} 或 HPS 排序", $"Sort by {dpsLabel} or HPS only"));
        ImGui.Spacing();

        var ordered = OrderCombatantsForDisplay(
                encounter,
                rateDurationSeconds,
                sortMode,
                configuration.Meter.DpsMetric)
            .Select(combatant => new CombatantPerformance(
                combatant,
                ResolveRate(
                    combatant,
                    rateDurationSeconds,
                    sortMode,
                    configuration.Meter.DpsMetric),
                ResolvePercent(combatant, encounter, sortMode)))
            .ToArray();
        var maximumRate = Math.Max(1, ordered.Select(item => item.Rate).DefaultIfEmpty(1).Max());
        var playerRank = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var performance = ordered[index];
            var rank = MeterService.NextPlayerRank(
                MeterService.IsLimitBreak(performance.Combatant),
                ref playerRank);
            DrawCombatantRow(performance, rank, maximumRate, encounter);
            ImGui.Spacing();
        }

        if (encounter.ActionSummaries.Count > 0 && ImGui.CollapsingHeader(text.Get("技能统计", "Action summaries")))
        {
            if (ImGui.BeginTable("actions", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            {
                ImGui.TableSetupColumn(text.Get("来源", "Source"));
                ImGui.TableSetupColumn(text.Get("技能", "Action"));
                ImGui.TableSetupColumn(text.Get("次数", "Uses"));
                ImGui.TableSetupColumn(text.Get("伤害", "Damage"));
                ImGui.TableSetupColumn(text.Get("治疗", "Healing"));
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
                }
                ImGui.EndTable();
            }
        }

        // Live ACT encounters keep totals, not per-event detail; "0 damage" beside a
        // non-zero total read as an error, so the counts appear only when retained.
        if (encounter.DamageEvents.Count + encounter.HealEvents.Count + encounter.DeathEvents.Count > 0)
        {
            ImGui.TextDisabled(text.Format(
                $"事件：{encounter.DamageEvents.Count} 伤害，{encounter.HealEvents.Count} 治疗，{encounter.DeathEvents.Count} 死亡",
                $"Events: {encounter.DamageEvents.Count} damage, {encounter.HealEvents.Count} healing, {encounter.DeathEvents.Count} deaths"));
        }
    }

    // Keep a hint beside its heading when it fits; longer translations start on the
    // next line and wrap instead of running past the panel edge.
    private static void SameLineHint(string hint)
    {
        ImGui.SameLine();
        if (ImGui.CalcTextSize(hint).X > ImGui.GetContentRegionAvail().X)
        {
            ImGui.NewLine();
        }
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(hint);
        ImGui.PopTextWrapPos();
    }

    private bool DrawEncounterListCard(
        Encounter encounter,
        bool selected,
        bool isFolder,
        bool expanded)
    {
        // Card geometry follows the font so enlarged text keeps both lines inside the card.
        var lineHeight = ImGui.GetTextLineHeight();
        var height = Math.Max(58, lineHeight * 2 + 24);
        var detailY = 14 + lineHeight;
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        ImGui.PushID($"recent-{encounter.Id:N}");
        ImGui.InvisibleButton("card", new Vector2(width, height));
        var clicked = ImGui.IsItemClicked();
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(hovered ? NavyHover : NavyRaised), 6);
        if (selected)
        {
            drawList.AddRect(start, start + new Vector2(width, height), ImGui.GetColorU32(IceBlue), 6, ImDrawFlags.None, 1.5f);
            drawList.AddRectFilled(start, start + new Vector2(3, height), ImGui.GetColorU32(IceBlue), 6);
        }
        var titleColor = ImGui.GetColorU32(selected ? IceBlue : Vector4.One);
        var titleX = 10f;
        if (isFolder)
        {
            // The game font has no ▶/▼ glyphs (they render as "="), so draw the arrow.
            var size = ImGui.GetFontSize() * 0.42f;
            var center = start + new Vector2(titleX + size * 0.5f, 8 + lineHeight * 0.5f);
            if (expanded)
            {
                drawList.AddTriangleFilled(center + new Vector2(-size * 0.5f, -size * 0.3f),
                    center + new Vector2(size * 0.5f, -size * 0.3f), center + new Vector2(0, size * 0.45f), titleColor);
            }
            else
            {
                drawList.AddTriangleFilled(center + new Vector2(-size * 0.3f, -size * 0.5f),
                    center + new Vector2(-size * 0.3f, size * 0.5f), center + new Vector2(size * 0.45f, 0), titleColor);
            }
            titleX += size + 6;
        }
        drawList.AddText(
            start + new Vector2(titleX, 8),
            titleColor,
            TrimToWidth(EncounterTitle(encounter), width - titleX - 10));
        var recordCount = isFolder
            ? text.Format($"{encounter.SegmentRecords.Count} 条记录  ·  ", $"{encounter.SegmentRecords.Count} records  ·  ")
            : string.Empty;
        var displayDuration = isFolder ? encounter.Duration : encounter.EffectiveDuration;
        var deaths = isFolder
            ? encounter.SegmentRecords.Sum(static pull => pull.TotalDeaths)
            : encounter.TotalDeaths;
        var detail = $"{recordCount}{encounter.StartTime.LocalDateTime:MM-dd HH:mm}  ·  {FormatDuration(displayDuration)}  ·  " +
                     text.Format($"死亡 {deaths}", $"KO {deaths}");
        var localEstimate = encounter.Combatants
            .Where(static combatant => combatant.IsLocalPlayer)
            .Select(FflogsEstimateService.GetPersistedEstimate)
            .FirstOrDefault(static estimate => estimate is not null);
        if (localEstimate is not null)
        {
            detail += $"  ·  FFLogs {localEstimate.Score}";
        }
        drawList.AddText(start + new Vector2(10, detailY), ImGui.GetColorU32(new Vector4(0.66f, 0.69f, 0.74f, 1)), TrimToWidth(detail, width - 20));
        ImGui.PopID();
        return clicked;
    }

    private bool DrawSegmentListCard(Encounter segment, int index, bool selected)
    {
        var lineHeight = ImGui.GetTextLineHeight();
        var height = Math.Max(48, lineHeight * 2 + 14);
        var restoreCursorX = ImGui.GetCursorPosX();
        var fullWidth = ImGui.GetContentRegionAvail().X;
        var width = Math.Max(40, fullWidth - 18);
        var start = ImGui.GetCursorScreenPos() + new Vector2(18, 0);
        ImGui.SetCursorScreenPos(start);
        ImGui.PushID($"segment-{segment.Id:N}");
        ImGui.InvisibleButton("card", new Vector2(width, height));
        var clicked = ImGui.IsItemClicked();
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + new Vector2(width, height),
            ImGui.GetColorU32(hovered ? NavyHover : new Vector4(NavyRaised.X, NavyRaised.Y, NavyRaised.Z, 0.72f)),
            5);
        if (selected)
        {
            drawList.AddRect(
                start,
                start + new Vector2(width, height),
                ImGui.GetColorU32(IceBlue),
                5,
                ImDrawFlags.None,
                1.25f);
        }
        var title = text.Format(
            $"记录 {index + 1} · {EncounterTitle(segment)}",
            $"Record {index + 1} · {EncounterTitle(segment)}");
        drawList.AddText(
            start + new Vector2(9, 6),
            ImGui.GetColorU32(selected ? IceBlue : Vector4.One),
            TrimToWidth(title, width - 18));
        var detail = $"{segment.StartTime.LocalDateTime:HH:mm:ss}  ·  {FormatDuration(segment.EffectiveDuration)}";
        drawList.AddText(
            start + new Vector2(9, 9 + lineHeight),
            ImGui.GetColorU32(new Vector4(0.62f, 0.66f, 0.72f, 1)),
            TrimToWidth(detail, width - 18));
        ImGui.PopID();
        ImGui.SetCursorPosX(restoreCursorX);
        return clicked;
    }

    internal static Encounter? FindRecentEncounter(
        IReadOnlyList<Encounter> recent,
        Guid encounterId)
    {
        foreach (var encounter in recent)
        {
            if (encounter.Id == encounterId)
            {
                return encounter;
            }

            var segment = encounter.SegmentRecords.FirstOrDefault(item => item.Id == encounterId);
            if (segment is not null)
            {
                return segment;
            }
        }

        return null;
    }

    private static bool DrawLogFileCard(string file, string label, bool selected)
    {
        var lineHeight = ImGui.GetTextLineHeight();
        var height = Math.Max(54, lineHeight * 2 + 20);
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        ImGui.PushID($"log-file-{label}");
        ImGui.InvisibleButton("card", new Vector2(width, height));
        var clicked = ImGui.IsItemClicked();
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(hovered ? NavyHover : NavyRaised), 6);
        if (selected)
        {
            drawList.AddRect(start, start + new Vector2(width, height), ImGui.GetColorU32(IceBlue), 6, ImDrawFlags.None, 1.5f);
        }
        drawList.AddText(start + new Vector2(9, 7), ImGui.GetColorU32(selected ? IceBlue : Vector4.One), TrimToWidth(label, width - 18));
        drawList.AddText(start + new Vector2(9, 12 + lineHeight), ImGui.GetColorU32(new Vector4(0.66f, 0.69f, 0.74f, 1)),
            TrimToWidth(File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm"), width - 18));
        ImGui.PopID();
        return clicked;
    }

    private void DrawMetricButton(MeterSortMode mode, string label)
    {
        var selected = MeterSortModeOptions.Normalize(configuration.Meter.SortMode) == mode;
        DactTheme.PushStyleColor(ImGuiCol.Button, selected ? new Vector4(0.11f, 0.29f, 0.38f, 1) : new Vector4(0.12f, 0.17f, 0.24f, 1));
        DactTheme.PushStyleColor(ImGuiCol.Text, selected ? IceBlue : new Vector4(0.84f, 0.87f, 0.91f, 1));
        var size = new Vector2(
            Math.Max(82, ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + 16),
            Math.Max(30, ImGui.GetFrameHeight()));
        if (DactTheme.Button($"{label}##history-sort-{mode}", size) && !selected)
        {
            configuration.Meter.SortMode = mode;
            saveConfiguration();
        }
        ImGui.PopStyleColor(2);
    }

    private void DrawCombatantRow(
        CombatantPerformance performance,
        int? rank,
        double maximumRate,
        Encounter encounter)
    {
        var combatant = performance.Combatant;
        var width = ImGui.GetContentRegionAvail().X;
        var lineHeight = ImGui.GetTextLineHeight();
        var rankWidth = Math.Max(27, ImGui.CalcTextSize("00").X + 10);
        var isHps = MeterSortModeOptions.Normalize(configuration.Meter.SortMode) == MeterSortMode.Hps;
        var fflogsEstimate = isHps ? null : FflogsEstimateService.GetPersistedEstimate(combatant);
        // Left-to-right display order; trimming from the end always keeps the rate.
        var stats = new List<(string Value, Vector4 Color)>
        {
            ($"{(isHps ? "HPS" : DpsRateLabel())} {performance.Rate:N0}", MeterWindow.PrimaryRateColor(combatant.IsLocalPlayer)),
            ($"{performance.Percent:N1}%", new Vector4(0.72f, 0.78f, 0.84f, 1)),
            (text.Format($"死亡 {combatant.Deaths}", $"KO {combatant.Deaths}"), new Vector4(0.78f, 0.80f, 0.84f, 1)),
        };
        if (fflogsEstimate is not null)
        {
            stats.Add(($"FFLogs {fflogsEstimate.Score}", fflogsEstimate.Color));
        }
        float StatsWidth() => stats.Sum(item => ImGui.CalcTextSize(item.Value).X + 12);
        // Large fonts and long translations move the numbers under the name instead of
        // drawing them over the job badge; a still-too-narrow line sheds the least
        // important figures rather than running past the row.
        var bandHeight = Math.Max(42, lineHeight + 24);
        var twoLines = 18 + rankWidth + lineHeight * 7.4f + StatsWidth() > width;
        while (twoLines && stats.Count > 1 && StatsWidth() > width - 6)
        {
            stats.RemoveAt(stats.Count - 1);
        }
        var rowHeight = twoLines ? bandHeight + lineHeight + 8 : bandHeight;
        var start = ImGui.GetCursorScreenPos();
        var end = start + new Vector2(width, rowHeight);
        ImGui.InvisibleButton($"history-row-{combatant.Id}", new Vector2(width, rowHeight));
        var drawList = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsItemHovered();
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(hovered ? NavyHover : NavyRaised), 6);
        var ratio = (float)Math.Clamp(performance.Rate / maximumRate, 0, 1);
        var localColor = configuration.Meter.LocalPlayerColor;
        var fill = combatant.IsLocalPlayer
            ? new Vector4(localColor.X, localColor.Y, localColor.Z, Math.Clamp(localColor.W, 0.18f, 0.58f))
            : new Vector4(IceBlue.X, IceBlue.Y, IceBlue.Z, 0.13f);
        drawList.AddRectFilled(start, new Vector2(start.X + width * ratio, end.Y), ImGui.GetColorU32(fill), 6);
        if (combatant.IsLocalPlayer)
        {
            drawList.AddRect(start, end, ImGui.GetColorU32(new Vector4(localColor.X, localColor.Y, localColor.Z, 0.95f)), 6, ImDrawFlags.None, 1.5f);
        }

        var lineY = start.Y + (bandHeight - lineHeight) * 0.5f;
        var x = start.X + 9;
        drawList.AddText(
            new Vector2(x, lineY),
            ImGui.GetColorU32(new Vector4(0.68f, 0.71f, 0.76f, 1)),
            rank is { } playerRank ? $"{playerRank,2}" : "--");
        x += rankWidth;
        x = DrawJob(drawList, combatant, x, start.Y, lineY, bandHeight);

        var right = end.X - 9;
        var statsY = twoLines ? start.Y + bandHeight - 4 : lineY;
        // Draw from the right edge inward so the rate keeps its usual outer position.
        for (var index = stats.Count - 1; index >= 0; index--)
        {
            var (value, color) = stats[index];
            right -= ImGui.CalcTextSize(value).X;
            drawList.AddText(new Vector2(right, statsY), ImGui.GetColorU32(color), value);
            right -= 12;
        }
        var nameRight = twoLines ? end.X - 9 : right;

        var isLimitBreak = MeterService.IsLimitBreak(combatant);
        var name = isLimitBreak
            ? MeterWindow.LimitBreakDisplayName
            : PlayerIdentityFormatter.Format(combatant, encounter.Combatants, configuration.Meter, text);
        drawList.AddText(
            new Vector2(x, lineY),
            ImGui.GetColorU32(combatant.IsLocalPlayer || isLimitBreak ? Gold : Vector4.One),
            TrimToWidth(name, Math.Max(20, nameRight - x - 6)));
        if (hovered && fflogsEstimate is not null)
        {
            ImGui.SetTooltip(text.Format(
                $"DPS Parse 预估：{fflogsEstimate.Score}\n根据本场实际 DPS 与当前 FFLogs 同职业、同副本、同分区的 DPS 分布估算。\nFFLogs 数据更新于：{fflogsEstimate.DataUpdatedAt.ToLocalTime():yyyy/MM/dd}",
                $"Estimated DPS Parse: {fflogsEstimate.Score}\nEstimated from this encounter's actual DPS and the current FFLogs DPS distribution for the same job, encounter, and partition.\nFFLogs data updated: {fflogsEstimate.DataUpdatedAt.ToLocalTime():yyyy/MM/dd}"));
        }
    }

    private float DrawJob(
        ImDrawListPtr drawList,
        Combatant combatant,
        float x,
        float rowTop,
        float textY,
        float rowHeight)
    {
        if (MeterService.IsLimitBreak(combatant))
        {
            var limitBreakTexture = jobIcons.GetLimitBreak();
            if (limitBreakTexture is not null)
            {
                var iconSize = MeterWindow.CalculateJobIconSize(rowHeight, ImGui.GetTextLineHeight());
                var iconTop = rowTop + (rowHeight - iconSize) * 0.5f;
                var wrap = limitBreakTexture.GetWrapOrEmpty();
                drawList.AddImage(
                    wrap.Handle,
                    new Vector2(x, iconTop),
                    new Vector2(x + iconSize, iconTop + iconSize));
                return x + iconSize + 7;
            }

            drawList.AddText(new Vector2(x, textY), ImGui.GetColorU32(Gold), "LB");
            return x + ImGui.CalcTextSize("LB").X + 7;
        }

        var job = combatant.Job;
        var texture = jobIcons.Get(configuration.Meter.JobDisplayStyle, job);
        if (texture is not null)
        {
            var iconSize = MeterWindow.CalculateJobIconSize(rowHeight, ImGui.GetTextLineHeight());
            var iconTop = rowTop + (rowHeight - iconSize) * 0.5f;
            var wrap = texture.GetWrapOrEmpty();
            drawList.AddImage(wrap.Handle, new Vector2(x, iconTop), new Vector2(x + iconSize, iconTop + iconSize));
            return x + iconSize + 7;
        }

        var label = JobDisplayFormatter.FormatText(job, configuration.Meter.JobDisplayStyle);
        var labelSize = ImGui.CalcTextSize(label);
        var badgeWidth = Math.Max(35, labelSize.X + 10);
        drawList.AddRectFilled(new Vector2(x, textY - 2), new Vector2(x + badgeWidth, textY + ImGui.GetTextLineHeight() + 2), ImGui.GetColorU32(new Vector4(0.24f, 0.36f, 0.46f, 0.9f)), 4);
        drawList.AddText(new Vector2(x + (badgeWidth - labelSize.X) * 0.5f, textY), ImGui.GetColorU32(DactTheme.Palette.Text), label);
        return x + badgeWidth + 8;
    }

    internal static IReadOnlyList<Combatant> OrderCombatantsForDisplay(
        Encounter encounter,
        double encounterDuration,
        MeterSortMode sortMode,
        DpsMetric dpsMetric)
        => encounter.Combatants
            .OrderBy(static combatant => MeterService.IsLimitBreak(combatant))
            .ThenByDescending(combatant => ResolveRate(
                combatant,
                encounterDuration,
                sortMode,
                dpsMetric))
            .ToArray();

    internal static double ResolveRate(
        Combatant combatant,
        double encounterDuration,
        MeterSortMode sortMode,
        DpsMetric dpsMetric)
    {
        if (MeterSortModeOptions.Normalize(sortMode) == MeterSortMode.Hps)
        {
            return combatant.TotalHealing / Math.Max(1, encounterDuration);
        }

        return dpsMetric switch
        {
            DpsMetric.Rdps when combatant.Rdps > 0 => combatant.Rdps,
            DpsMetric.Dps when combatant.Dps > 0 => combatant.Dps,
            DpsMetric.ExtDps when combatant.ExtDps > 0 => combatant.ExtDps,
            DpsMetric.EncDps when combatant.EncDps > 0 => combatant.EncDps,
            _ => combatant.TotalDamage / Math.Max(1, encounterDuration),
        };
    }

    private static double ResolvePercent(Combatant combatant, Encounter encounter, MeterSortMode sortMode)
    {
        if (MeterSortModeOptions.Normalize(sortMode) == MeterSortMode.Hps)
        {
            return combatant.TotalHealing * 100.0 / Math.Max(1, encounter.TotalHealing);
        }

        return combatant.TotalDamage * 100.0 / Math.Max(1, encounter.TotalDamage);
    }

    private string DpsRateLabel() => configuration.Meter.DpsMetric switch
    {
        DpsMetric.Rdps => "rDPS",
        DpsMetric.EncDps => "eDPS",
        DpsMetric.ExtDps => "ExtDPS",
        _ => "DPS",
    };

    private string EncounterTitle(Encounter encounter)
        => string.IsNullOrWhiteSpace(encounter.EnemyName) ||
           string.Equals(encounter.EnemyName, "Encounter", StringComparison.OrdinalIgnoreCase)
            ? encounter.IsActive
                ? text.Get("状态：战斗中", "Status: Running")
                : text.Get("状态：已结束", "Status: Ended")
            : LocalizeEncounterTitle(encounter);

    private string LocalizeEncounterTitle(Encounter encounter)
        => string.Equals(
            encounter.EnemyName,
            encounter.ZoneName,
            StringComparison.OrdinalIgnoreCase)
            ? localizeZoneName(encounter.TerritoryId, encounter.EnemyName)
            : encounter.EnemyName;

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

    private static void SummaryCell(string id, string label, string value)
    {
        ImGui.TableNextColumn();
        DactTheme.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
        var style = ImGui.GetStyle();
        var height = Math.Max(62, ImGui.GetTextLineHeight() * 2 + style.ItemSpacing.Y + style.WindowPadding.Y * 2 + 4);
        if (ImGui.BeginChild($"summary-{id}", new Vector2(-1, height), true))
        {
            ImGui.TextDisabled(label);
            DactTheme.TextColored(IceBlue, value);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private static string FormatDuration(TimeSpan duration)
        => $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

    private sealed record CombatantPerformance(Combatant Combatant, double Rate, double Percent);

    private static void PushTheme()
    {
        DactTheme.PushStyleColor(ImGuiCol.ChildBg, Navy);
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        DactTheme.PushStyleColor(ImGuiCol.Separator, new Vector4(0.34f, 0.29f, 0.18f, 0.70f));
        DactTheme.PushStyleColor(ImGuiCol.FrameBg, NavyRaised);
        DactTheme.PushStyleColor(ImGuiCol.FrameBgHovered, NavyHover);
        DactTheme.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.17f, 0.24f, 1));
        DactTheme.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.25f, 0.34f, 1));
        DactTheme.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.30f, 0.37f, 1));
        DactTheme.PushStyleColor(ImGuiCol.CheckMark, IceBlue);
        DactTheme.PushStyleColor(ImGuiCol.Header, new Vector4(0.22f, 0.25f, 0.28f, 1));
        DactTheme.PushStyleColor(ImGuiCol.HeaderHovered, NavyHover);
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
