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

    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);
    private static readonly Vector4 NavyHover = new(0.105f, 0.145f, 0.185f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);

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
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        outerFrameStylePushed = true;
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
                    "combat-history"))
            {
                IsOpen = false;
            }

            ImGui.TextDisabled(text.Get(
                "近期战斗与逐场日志使用同一个详情视图",
                "Recent encounters and log files share one detail view"));
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
            ImGui.TextColored(Gold, text.Get("近期战斗", "Recent encounters"));
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

        if (selectedFile is null || !files.Contains(selectedFile, StringComparer.OrdinalIgnoreCase))
        {
            Load(files[0]);
        }

        var listWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.30f, 240, 320);
        if (ImGui.BeginChild("log-file-list", new Vector2(listWidth, -1), true))
        {
            ImGui.TextColored(Gold, text.Get("日志文件", "Log files"));
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
        if (encounter.SegmentRecords.Count > 0)
        {
            ImGui.TextColored(Gold, LocalizeEncounterTitle(encounter));
            ImGui.TextDisabled(text.Get(
                $"本次副本包含 {encounter.SegmentRecords.Count} 把战斗，请在近期战斗中选择其中一把。",
                $"This duty contains {encounter.SegmentRecords.Count} pulls; select one under Recent encounters."));
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawEncounterDetails(encounter.SegmentRecords[^1]);
            return;
        }

        var damageDurationSeconds = Math.Max(1, encounter.EffectiveDuration.TotalSeconds);
        var healingDurationSeconds = Math.Max(1, encounter.Duration.TotalSeconds);
        ImGui.TextColored(Gold, LocalizeEncounterTitle(encounter));
        ImGui.TextDisabled(
            $"{localizeZoneName(encounter.TerritoryId, encounter.ZoneName)}  ·  " +
            $"{encounter.StartTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}  ·  " +
            $"{FormatDuration(encounter.EffectiveDuration)}");
        ImGui.Spacing();

        if (ImGui.BeginTable("encounter-summary", 4, ImGuiTableFlags.SizingStretchSame))
        {
            SummaryCell(
                "party",
                text.Get("队伍人数", "Party"),
                encounter.Combatants.Count(static combatant => !MeterService.IsLimitBreak(combatant)).ToString());
            SummaryCell("damage", text.Get("总伤害", "Damage"), encounter.TotalDamage.ToString("N0"));
            SummaryCell("dps", "DPS", (encounter.TotalDamage / damageDurationSeconds).ToString("N0"));
            SummaryCell("deaths", text.Get("死亡", "Deaths"), encounter.TotalDeaths.ToString());
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("队伍表现", "Party performance"));
        ImGui.SameLine();
        ImGui.TextDisabled(text.Get(
            $"职业显示：{JobDisplayFormatter.Label(configuration.Meter.JobDisplayStyle, text)}（跟随战斗统计）",
            $"Job display: {JobDisplayFormatter.Label(configuration.Meter.JobDisplayStyle, text)} (follows Combat Meter)"));

        var sortMode = MeterSortModeOptions.Normalize(configuration.Meter.SortMode);
        var rateDurationSeconds = sortMode == MeterSortMode.Hps
            ? healingDurationSeconds
            : damageDurationSeconds;
        DrawMetricButton(MeterSortMode.Dps, DpsRateLabel());
        ImGui.SameLine();
        DrawMetricButton(MeterSortMode.Hps, "HPS");
        ImGui.SameLine();
        ImGui.TextDisabled(text.Get("仅按 DPS 或 HPS 排序", "Sort by DPS or HPS only"));
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

        ImGui.TextDisabled(
            $"{text.Get("事件", "Events")}: {encounter.DamageEvents.Count} {text.Get("伤害", "damage")}, " +
            $"{encounter.HealEvents.Count} {text.Get("治疗", "healing")}, " +
            $"{encounter.DeathEvents.Count} {text.Get("死亡", "deaths")}");
    }

    private bool DrawEncounterListCard(
        Encounter encounter,
        bool selected,
        bool isFolder,
        bool expanded)
    {
        const float height = 58;
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
        var prefix = isFolder ? expanded ? "▼ " : "▶ " : string.Empty;
        drawList.AddText(
            start + new Vector2(10, 8),
            ImGui.GetColorU32(selected ? IceBlue : Vector4.One),
            TrimToWidth(prefix + EncounterTitle(encounter), width - 20));
        var recordCount = isFolder
            ? text.Get($"{encounter.SegmentRecords.Count} 条记录  ·  ", $"{encounter.SegmentRecords.Count} records  ·  ")
            : string.Empty;
        var displayDuration = isFolder ? encounter.Duration : encounter.EffectiveDuration;
        var deaths = isFolder
            ? encounter.SegmentRecords.Sum(static pull => pull.TotalDeaths)
            : encounter.TotalDeaths;
        var detail = $"{recordCount}{encounter.StartTime.LocalDateTime:MM-dd HH:mm}  ·  {FormatDuration(displayDuration)}  ·  {deaths} {text.Get("死亡", "KO")}";
        var localEstimate = encounter.Combatants
            .Where(static combatant => combatant.IsLocalPlayer)
            .Select(FflogsEstimateService.GetPersistedEstimate)
            .FirstOrDefault(static estimate => estimate is not null);
        if (localEstimate is not null)
        {
            detail += $"  ·  FFLogs {localEstimate.Score}";
        }
        drawList.AddText(start + new Vector2(10, 32), ImGui.GetColorU32(new Vector4(0.66f, 0.69f, 0.74f, 1)), TrimToWidth(detail, width - 20));
        ImGui.PopID();
        return clicked;
    }

    private bool DrawSegmentListCard(Encounter segment, int index, bool selected)
    {
        const float height = 48;
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
        var title = text.Get(
            $"记录 {index + 1} · {EncounterTitle(segment)}",
            $"Record {index + 1} · {EncounterTitle(segment)}");
        drawList.AddText(
            start + new Vector2(9, 6),
            ImGui.GetColorU32(selected ? IceBlue : Vector4.One),
            TrimToWidth(title, width - 18));
        var detail = $"{segment.StartTime.LocalDateTime:HH:mm:ss}  ·  {FormatDuration(segment.EffectiveDuration)}";
        drawList.AddText(
            start + new Vector2(9, 26),
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
        const float height = 54;
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
        drawList.AddText(start + new Vector2(9, 29), ImGui.GetColorU32(new Vector4(0.66f, 0.69f, 0.74f, 1)), File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm"));
        ImGui.PopID();
        return clicked;
    }

    private void DrawMetricButton(MeterSortMode mode, string label)
    {
        var selected = MeterSortModeOptions.Normalize(configuration.Meter.SortMode) == mode;
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? new Vector4(0.11f, 0.29f, 0.38f, 1) : new Vector4(0.12f, 0.17f, 0.24f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? IceBlue : new Vector4(0.84f, 0.87f, 0.91f, 1));
        if (ImGui.Button($"{label}##history-sort-{mode}", new Vector2(82, 30)) && !selected)
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
        const float rowHeight = 42;
        var combatant = performance.Combatant;
        var width = ImGui.GetContentRegionAvail().X;
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

        var lineY = start.Y + (rowHeight - ImGui.GetTextLineHeight()) * 0.5f;
        var x = start.X + 9;
        drawList.AddText(
            new Vector2(x, lineY),
            ImGui.GetColorU32(new Vector4(0.68f, 0.71f, 0.76f, 1)),
            rank is { } playerRank ? $"{playerRank,2}" : "--");
        x += 27;
        x = DrawJob(drawList, combatant, x, start.Y, lineY, rowHeight);

        var right = end.X - 9;
        void DrawRight(string value, Vector4 color, float gap = 12)
        {
            var size = ImGui.CalcTextSize(value);
            right -= size.X;
            drawList.AddText(new Vector2(right, lineY), ImGui.GetColorU32(color), value);
            right -= gap;
        }

        var fflogsEstimate = MeterSortModeOptions.Normalize(configuration.Meter.SortMode) == MeterSortMode.Dps
            ? FflogsEstimateService.GetPersistedEstimate(combatant)
            : null;
        if (fflogsEstimate is not null)
        {
            DrawRight($"FFLogs {fflogsEstimate.Score}", fflogsEstimate.Color);
        }

        DrawRight(text.Get($"死亡 {combatant.Deaths}", $"KO {combatant.Deaths}"), new Vector4(0.78f, 0.80f, 0.84f, 1));
        DrawRight($"{performance.Percent:N1}%", new Vector4(0.72f, 0.78f, 0.84f, 1));
        DrawRight($"{(MeterSortModeOptions.Normalize(configuration.Meter.SortMode) == MeterSortMode.Hps ? "HPS" : DpsRateLabel())} {performance.Rate:N0}", MeterWindow.PrimaryRateColor(combatant.IsLocalPlayer));

        var isLimitBreak = MeterService.IsLimitBreak(combatant);
        var name = isLimitBreak
            ? MeterWindow.LimitBreakDisplayName
            : PlayerIdentityFormatter.Format(combatant, encounter.Combatants, configuration.Meter, text);
        drawList.AddText(
            new Vector2(x, lineY),
            ImGui.GetColorU32(combatant.IsLocalPlayer || isLimitBreak ? Gold : Vector4.One),
            TrimToWidth(name, Math.Max(20, right - x - 6)));
        if (hovered && fflogsEstimate is not null)
        {
            ImGui.SetTooltip(text.Get(
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
        drawList.AddText(new Vector2(x + (badgeWidth - labelSize.X) * 0.5f, textY), ImGui.GetColorU32(Vector4.One), label);
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
        ImGui.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
        if (ImGui.BeginChild($"summary-{id}", new Vector2(-1, 62), true))
        {
            ImGui.TextDisabled(label);
            ImGui.TextColored(IceBlue, value);
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
