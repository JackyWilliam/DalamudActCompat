using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Meter;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 战斗记录 (tools/ui-redesign/features.js recent / progress): the former stand-alone history
// window as a control-centre page with three tabs. 近期战斗 and 日志文件 keep every function of
// that window (pull folders, rDPS/HPS ranking, FFLogs estimates, action table, raw JSON);
// 开荒进度 is new and reads the pulls ProgressionTracker records in duties.
public sealed partial class ControlCenterWindow
{
    private int historyTab;
    private Guid? historySelectedId;
    private readonly HashSet<Guid> historyExpandedFolders = [];
    private string? historySelectedFile;
    private Encounter? historyFileEncounter;
    private string historyRawJson = string.Empty;
    private string historyLoadError = string.Empty;
    private bool historyShowRawJson;
    private bool historyActionsOpen;
    private uint? historySelectedDuty;
    private int historyProgressionVersion = -1;
    private IReadOnlyList<DutyProgress> historyDuties = [];
    private readonly Dictionary<Guid, ProgressionPull?> historyPullMatches = [];
    private string? historyMissingFightNote;
    private double historyMissingFightUntil;

    internal EncounterStateStore? EncounterHistory { get; set; }

    internal JobIconTextureSet? JobIcons { get; set; }

    internal Func<uint?, string, string>? LocalizeZone { get; set; }

    internal ProgressionStore? Progression { get; set; }

    // Opens the page from commands (/actcompat history, logs) and the home quick action.
    public void ShowMeterPage()
    {
        selectedPage = Page.Meter;
        ShowAnimated();
    }

    public void OpenHistory(int tab = 0)
    {
        selectedPage = Page.History;
        historyTab = tab;
        ShowAnimated();
    }

    private bool DrawHistoryPage()
    {
        var s = DactUi.S;
        var folderLabel = text.Get("打开日志文件夹", "Open log folder");
        DactUi.PageHeader(text.Get("战斗记录", "Encounter history"),
            text.Get("近期战斗、开荒进度和原始日志；完成一场战斗后自动保存。",
                "Recent fights, progression and raw logs, saved automatically after each fight."),
            DactUi.ButtonWidth(folderLabel, icon: "folder"),
            () =>
            {
                if (DactUi.Button(folderLabel + "##history-folder", icon: "folder")) OpenEncounterLogFolder();
            });
        DactUi.Tabs("history-tabs",
        [
            text.Get("近期战斗", "Recent encounters"),
            text.Get("开荒进度", "Progression"),
            text.Get("日志文件", "Log files"),
        ], ref historyTab);

        RefreshProgression();
        // Side by side when the details keep a usable width; narrow windows stack the list on top.
        var wide = ImGui.GetContentRegionAvail().X >= 560 * s;
        var height = Math.Max(330 * s, ImGui.GetContentRegionAvail().Y);
        switch (historyTab)
        {
            case 1: DrawProgressionTab(wide, height); break;
            case 2: DrawLogFilesTab(wide, height); break;
            default: DrawRecentTab(wide, height); break;
        }
        return false;
    }

    private void OpenEncounterLogFolder()
    {
        if (Paths is null) return;
        Directory.CreateDirectory(Paths.EncounterLogDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.EncounterLogDirectory) { UseShellExecute = true });
    }

    private void RefreshProgression()
    {
        if (Progression is null || Progression.Version == historyProgressionVersion) return;
        historyProgressionVersion = Progression.Version;
        historyDuties = DutyProgress.Summarize(Progression.Pulls);
        historyPullMatches.Clear();
    }

    // ---- 近期战斗

    private void DrawRecentTab(bool wide, float height)
    {
        var s = DactUi.S;
        var recent = EncounterHistory?.GetSnapshot().Recent ?? [];
        if (recent.Count == 0)
        {
            DactUi.Wrapped(text.Get("还没有战斗记录。完成一场战斗后会自动保存在这里。", "No fights yet. Each fight is saved here when it ends."), DactTheme.Palette.Muted);
            return;
        }

        var selected = historySelectedId is { } id ? EncounterWindow.FindRecentEncounter(recent, id) : null;
        if (selected is null)
        {
            // The newest fight opens first; a duty folder opens on its latest pull.
            selected = recent[0].SegmentRecords.Count > 0 ? recent[0].SegmentRecords[^1] : recent[0];
            if (recent[0].SegmentRecords.Count > 0) historyExpandedFolders.Add(recent[0].Id);
            historySelectedId = selected.Id;
        }

        if (DactUi.BeginPanel("history-recent-list", new Vector2(wide ? 236 * s : -1, wide ? height : 230 * s), 8, 2))
        {
            foreach (var encounter in recent)
            {
                var isFolder = encounter.SegmentRecords.Count > 0;
                var expanded = isFolder && historyExpandedFolders.Contains(encounter.Id);
                // An open folder shows the selection on the pull itself, not on both rows.
                var active = encounter.Id == historySelectedId ||
                             isFolder && !expanded && encounter.SegmentRecords.Any(pull => pull.Id == historySelectedId);
                if (DrawFightItem(encounter, active, isFolder ? expanded : null, 0))
                {
                    if (isFolder && !historyExpandedFolders.Remove(encounter.Id)) historyExpandedFolders.Add(encounter.Id);
                    historySelectedId = (isFolder ? encounter.SegmentRecords[^1] : encounter).Id;
                }
                if (!expanded) continue;
                for (var index = 0; index < encounter.SegmentRecords.Count; index++)
                {
                    var pull = encounter.SegmentRecords[index];
                    if (DrawFightItem(pull, pull.Id == historySelectedId, null, index + 1)) historySelectedId = pull.Id;
                }
            }
        }
        DactUi.EndPanel();
        if (wide) ImGui.SameLine(0, 14 * s);
        DrawEncounterDetails(selected, height, "history-recent-details");
    }

    // One row of the fight list (dsl fight): title with a result pill, then time, length and
    // deaths. folderOpen marks a duty folder; segment > 0 is a pull inside an open folder.
    private bool DrawFightItem(Encounter encounter, bool selected, bool? folderOpen, int segment)
    {
        var s = DactUi.S;
        ImGui.PushID(encounter.Id.ToString("N"));
        var indent = segment > 0 ? 14 * s : 0;
        if (indent > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = (8 + 22 + 3 + 16 + 8) * s;
        var clicked = ImGui.InvisibleButton("fight", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var fill = DactMotion.Color(ImGuiP.GetItemID(), DactTheme.Palette.Hover with { W = selected ? 1 : hovered ? .5f : 0 }, DactMotion.Fast);
        if (fill.W > 0) draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(fill), 8 * s);
        if (selected)
            draw.AddRect(start, start + new Vector2(width, height), DactUi.U32(DactTheme.Palette.Accent with { W = .55f }), 8 * s, ImDrawFlags.None, Math.Max(1, s));

        var x = start.X + 10 * s;
        if (folderOpen is { } open)
        {
            DactIcons.Draw(draw, open ? "chevronDown" : "chevronRight", new Vector2(x, start.Y + 12 * s), 14 * s, DactTheme.Palette.Accent);
            x += 18 * s;
        }

        var pull = segment == 0 && folderOpen is null || segment > 0 ? MatchPull(encounter) : null;
        var (pillLabel, pillTone) = pull is null ? (null, PillTone.Ok)
            : pull.Cleared ? (text.Get("通关", "Cleared"), PillTone.Ok) : (text.Get("团灭", "Wipe"), PillTone.Error);
        var pillWidth = pillLabel is null ? 0 : DactUi.PillWidth(pillLabel, false);
        var right = start.X + width - 10 * s;
        if (pillLabel is not null) DactUi.DrawPill(draw, new Vector2(right - pillWidth, start.Y + 8 * s), pillLabel, pillTone, false);
        var titleText = segment > 0
            ? text.Format($"记录 {segment} · {FightTitle(encounter)}", $"Record {segment} · {FightTitle(encounter)}")
            : FightTitle(encounter);
        var title = DactUi.Fit(titleText, right - x - (pillWidth > 0 ? pillWidth + 6 * s : 0));
        DactUi.DrawText(draw, new Vector2(x, start.Y + 8 * s), 22 * s, title, selected ? DactTheme.Palette.Text : DactTheme.Palette.Muted);

        var isFolder = folderOpen is not null;
        var deaths = isFolder ? encounter.SegmentRecords.Sum(static item => item.TotalDeaths) : encounter.TotalDeaths;
        var detail = (isFolder ? text.Format($"{encounter.SegmentRecords.Count} 条记录 · ", $"{encounter.SegmentRecords.Count} records · ") : string.Empty) +
                     $"{HistoryTime(encounter.StartTime)} · {FormatFightDuration(isFolder ? encounter.Duration : encounter.EffectiveDuration)}" +
                     (deaths > 0 || !isFolder ? " · " + text.Format($"死亡 {deaths}", $"KO {deaths}") : string.Empty);
        if (!isFolder && encounter.Combatants.Where(static c => c.IsLocalPlayer).Select(FflogsEstimateService.GetPersistedEstimate)
                .FirstOrDefault(static estimate => estimate is not null) is { } estimate)
            detail += $" · FFLogs {estimate.Score}";
        string line;
        using (DactFonts.Push(DactFonts.Small)) line = DactUi.Fit(detail, start.X + width - 10 * s - (start.X + 10 * s));
        DactUi.DrawText(draw, new Vector2(start.X + 10 * s, start.Y + 33 * s), 16 * s, line, DactTheme.Palette.Muted, DactFonts.Small);
        if (hovered && (title != titleText || line != detail)) DactUi.Tooltip(titleText + "\n" + detail);
        ImGui.PopID();
        return clicked;
    }

    private string FightTitle(Encounter encounter)
        => string.IsNullOrWhiteSpace(encounter.EnemyName) || string.Equals(encounter.EnemyName, "Encounter", StringComparison.OrdinalIgnoreCase)
            ? encounter.IsActive ? text.Get("状态：战斗中", "Status: Running") : text.Get("状态：已结束", "Status: Ended")
            : string.Equals(encounter.EnemyName, encounter.ZoneName, StringComparison.OrdinalIgnoreCase)
                ? LocalizeZone?.Invoke(encounter.TerritoryId, encounter.EnemyName) ?? encounter.EnemyName
                : encounter.EnemyName;

    private string HistoryTime(DateTimeOffset at)
    {
        var local = at.LocalDateTime;
        return (DateTime.Today - local.Date).Days switch
        {
            0 => text.Format($"今天 {local:HH:mm}", $"Today {local:HH:mm}"),
            1 => text.Format($"昨天 {local:HH:mm}", $"Yesterday {local:HH:mm}"),
            _ => local.ToString("MM-dd HH:mm"),
        };
    }

    private static string FormatFightDuration(TimeSpan duration) => $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

    // The progression pull recorded for this fight, if the tracker saw it (same duty, start
    // within 45 s: ACT and the duty director start their clocks a moment apart).
    private ProgressionPull? MatchPull(Encounter encounter)
    {
        if (Progression is null || encounter.TerritoryId is not { } territory) return null;
        if (historyPullMatches.TryGetValue(encounter.Id, out var cached)) return cached;
        var match = Progression.Pulls.LastOrDefault(pull => pull.TerritoryId == territory &&
                                                            Math.Abs((pull.StartTime - encounter.StartTime).TotalSeconds) <= 45);
        historyPullMatches[encounter.Id] = match;
        return match;
    }

    private int PullNumber(ProgressionPull pull)
        => historyDuties.FirstOrDefault(duty => duty.TerritoryId == pull.TerritoryId) is { } duty
            ? duty.Pulls.ToList().IndexOf(pull) + 1
            : 0;

    // "P2 中段" for a sentence; PhaseValue gives the "P2 · 45%" figure form.
    private string PhaseWords(ProgressionPull pull)
    {
        if (pull.Cleared) return text.Get("通关", "cleared");
        var part = pull.PhaseFraction < 1 / 3d ? text.Get("前段", "early") : pull.PhaseFraction < 2 / 3d ? text.Get("中段", "mid") : text.Get("后段", "late");
        return text.Format($"P{pull.Phase} {part}", $"P{pull.Phase} {part}");
    }

    private static string PhaseValue(int phase, double fraction) => $"P{phase} · {fraction * 100:0}%";

    private void DrawEncounterDetails(Encounter encounter, float height, string id)
    {
        var s = DactUi.S;
        // The details scroll on their own but sit straight on the page (no panel behind them).
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.BeginChild(id, new Vector2(-1, height), false);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 12) * s);
        if (encounter.SegmentRecords.Count > 0)
        {
            DactUi.Hint(text.Format(
                $"本次副本包含 {encounter.SegmentRecords.Count} 把战斗，请在近期战斗中选择其中一把。",
                $"This duty contains {encounter.SegmentRecords.Count} pulls; select one under Recent encounters."));
            encounter = encounter.SegmentRecords[^1];
        }

        var pull = MatchPull(encounter);
        var title = FightTitle(encounter) + (pull is not null && PullNumber(pull) is > 0 and var number
            ? text.Format($" · 第 {number} 次", $" · pull {number}")
            : string.Empty);
        var zone = LocalizeZone?.Invoke(encounter.TerritoryId, encounter.ZoneName) ?? encounter.ZoneName;
        var sub = string.Join(" · ", new[]
        {
            title.StartsWith(zone, StringComparison.Ordinal) ? null : zone,
            HistoryTime(encounter.StartTime),
            FormatFightDuration(encounter.EffectiveDuration),
            pull is null ? null : pull.Cleared ? text.Get("通关", "Cleared") : text.Format($"打到 {PhaseWords(pull)}", $"Reached {PhaseWords(pull)}"),
        }.Where(static part => !string.IsNullOrEmpty(part)));
        var reviewLabel = text.Get("AI 复盘", "AI review");
        var reviewWidth = ReviewEncounter is null ? 0 : DactUi.ButtonWidth(reviewLabel, ButtonKind.Primary, "sparkle");
        var top = ImGui.GetCursorPos();
        var available = ImGui.GetContentRegionAvail().X;
        ImGui.BeginGroup();
        using (DactUi.Gap(2))
        {
            DactUi.Text(DactUi.Fit(title, available - reviewWidth - 12 * s), size: DactFonts.H2);
            DactUi.Wrapped(sub, DactUi.HintColor, DactFonts.Small, available - reviewWidth - 12 * s);
        }
        ImGui.EndGroup();
        if (ReviewEncounter is { } review)
        {
            var after = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(top.X + available - reviewWidth, top.Y + (ImGui.GetItemRectSize().Y - 30 * s) * .5f));
            if (DactUi.Button(reviewLabel + "##history-review", ButtonKind.Primary, "sparkle")) review(encounter);
            ImGui.SetCursorPos(after);
        }

        var players = encounter.Combatants.Count(static combatant => !MeterService.IsLimitBreak(combatant));
        var damageSeconds = Math.Max(1, encounter.EffectiveDuration.TotalSeconds);
        var palette = DactTheme.Palette;
        DactUi.StatTiles(
            new DactUi.StatTileData("users", text.Get("队伍人数", "Party"), players.ToString(), palette.Text,
                encounter.PartyCapacity > 0 && players < encounter.PartyCapacity
                    ? text.Format($"{players} / {encounter.PartyCapacity} 人", $"{players} / {encounter.PartyCapacity}")
                    : text.Get("全员在场", "Full party")),
            new DactUi.StatTileData("meter", text.Get("总伤害", "Damage"), FormatCompact(encounter.TotalDamage), palette.Text,
                text.Format($"队伍 DPS {FormatCompact(encounter.TotalDamage / damageSeconds)}", $"Party DPS {FormatCompact(encounter.TotalDamage / damageSeconds)}")),
            new DactUi.StatTileData("shield", text.Get("死亡", "Deaths"), encounter.TotalDeaths.ToString(),
                encounter.TotalDeaths > 0 ? palette.Gold : palette.Text,
                pull?.FirstDeathSeconds is { } firstDeath
                    ? text.Format($"首个死亡 {FormatFightDuration(TimeSpan.FromSeconds(firstDeath))}", $"First death {FormatFightDuration(TimeSpan.FromSeconds(firstDeath))}")
                    : encounter.TotalDeaths == 0 ? text.Get("无人倒下", "No one fell") : string.Empty));

        DrawPartyPerformance(encounter);

        if (encounter.ActionSummaries.Count > 0)
        {
            DactUi.Disclosure(text.Get("技能统计（来源、技能、次数、伤害、治疗）", "Action summaries (source, action, uses, damage, healing)"), ref historyActionsOpen);
            if (historyActionsOpen) DrawActionTable(encounter);
        }

        // Live ACT encounters keep totals, not per-event detail; "0 damage" beside a non-zero
        // total read as an error, so the counts appear only when retained.
        if (encounter.DamageEvents.Count + encounter.HealEvents.Count + encounter.DeathEvents.Count > 0)
            DactUi.Hint(text.Format(
                $"事件：{encounter.DamageEvents.Count} 伤害，{encounter.HealEvents.Count} 治疗，{encounter.DeathEvents.Count} 死亡",
                $"Events: {encounter.DamageEvents.Count} damage, {encounter.HealEvents.Count} healing, {encounter.DeathEvents.Count} deaths"));
        ImGui.PopStyleVar();
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private static string FormatCompact(double value)
        => value >= 1_000_000 ? $"{value / 1_000_000:0.0}m" : value >= 10_000 ? $"{value / 1_000:0.0}k" : value.ToString("N0");

    private string HistoryRateLabel() => configuration.Meter.DpsMetric switch
    {
        DpsMetric.Rdps => "rDPS",
        DpsMetric.EncDps => "eDPS",
        DpsMetric.ExtDps => "ExtDPS",
        _ => "DPS",
    };

    // 队伍表现: one row per member (dsl member) ranked by the meter's rate; the switch on the
    // title row changes the meter's own sort mode, as the old window did.
    private void DrawPartyPerformance(Encounter encounter)
    {
        var s = DactUi.S;
        var rateLabel = HistoryRateLabel();
        var options = new[] { rateLabel, "HPS" };
        var sortMode = MeterSortModeOptions.Normalize(configuration.Meter.SortMode);
        var active = sortMode == MeterSortMode.Hps ? 1 : 0;
        var segmentedWidth = options.Sum(option => DactUi.TextWidth(option) + 24 * s) + 2 * s + 6 * s;
        DactUi.BeginCard("history-party", null);
        DactUi.CardTitleRow(text.Get("队伍表现", "Party performance"), null, segmentedWidth, () =>
        {
            if (DactUi.Segmented("history-sort", options, ref active))
            {
                configuration.Meter.SortMode = active == 1 ? MeterSortMode.Hps : MeterSortMode.Dps;
                saveConfiguration();
            }
            if (ImGui.IsItemHovered())
                DactUi.Tooltip(text.Format(
                    $"仅按 {rateLabel} 或 HPS 排序；与战斗统计共用同一设置。\n职业显示：{JobDisplayFormatter.Label(configuration.Meter.JobDisplayStyle, text)}（跟随战斗统计）",
                    $"Sort by {rateLabel} or HPS only; shared with the Combat Meter.\nJob display: {JobDisplayFormatter.Label(configuration.Meter.JobDisplayStyle, text)} (follows Combat Meter)"));
        });

        var hps = MeterSortModeOptions.Normalize(configuration.Meter.SortMode) == MeterSortMode.Hps;
        var seconds = hps ? Math.Max(1, encounter.Duration.TotalSeconds) : Math.Max(1, encounter.EffectiveDuration.TotalSeconds);
        var total = Math.Max(1, hps ? encounter.TotalHealing : encounter.TotalDamage);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4) * s);
        foreach (var combatant in EncounterWindow.OrderCombatantsForDisplay(encounter, seconds, configuration.Meter.SortMode, configuration.Meter.DpsMetric))
        {
            var rate = EncounterWindow.ResolveRate(combatant, seconds, configuration.Meter.SortMode, configuration.Meter.DpsMetric);
            var share = (hps ? combatant.TotalHealing : combatant.TotalDamage) * 100.0 / total;
            DrawMemberRow(encounter, combatant, rate, share, hps);
        }
        ImGui.PopStyleVar();
        DactUi.EndCard();
    }

    private void DrawMemberRow(Encounter encounter, Combatant combatant, double rate, double share, bool hps)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 30 * s;
        ImGui.InvisibleButton($"member-{combatant.Id}", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var self = combatant.IsLocalPlayer;
        var limitBreak = MeterService.IsLimitBreak(combatant);
        var background = self ? palette.Gold with { W = .12f } : hovered ? palette.Hover : DactUi.Field;
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(background), 6 * s);

        // Job: the meter's icon set when it uses icons, otherwise the dsl tag in the role colour.
        var x = start.X + 10 * s;
        var icon = limitBreak ? JobIcons?.GetLimitBreak() : JobIcons?.Get(configuration.Meter.JobDisplayStyle, combatant.Job);
        if (icon is not null)
        {
            draw.AddImage(icon.GetWrapOrEmpty().Handle, new Vector2(x, start.Y + 5 * s), new Vector2(x + 20 * s, start.Y + 25 * s));
            x += 20 * s + 10 * s;
        }
        else
        {
            var label = limitBreak ? "LB" : JobDisplayFormatter.FormatText(combatant.Job, configuration.Meter.JobDisplayStyle);
            var tagWidth = Math.Max(40 * s, DactUi.TextWidth(label, DactFonts.Tiny) + 10 * s);
            var tag = limitBreak ? palette.Gold with { W = .55f } : MeterWindow.JobColor(combatant.Job) with { W = .85f };
            draw.AddRectFilled(new Vector2(x, start.Y + 5 * s), new Vector2(x + tagWidth, start.Y + 25 * s), DactUi.U32(tag), 4 * s);
            var labelWidth = DactUi.TextWidth(label, DactFonts.Tiny);
            DactUi.DrawText(draw, new Vector2(x + (tagWidth - labelWidth) * .5f, start.Y + 5 * s), 20 * s, label, palette.Text, DactFonts.Tiny);
            x += tagWidth + 10 * s;
        }

        var right = start.X + width - 10 * s;
        var value = rate.ToString("N0");
        var valueWidth = Math.Max(64 * s, DactUi.TextWidth(value));
        DactUi.DrawText(draw, new Vector2(right - DactUi.TextWidth(value), start.Y), height, value, palette.Accent);
        right -= valueWidth + 10 * s;
        var shareText = $"{share:0.0}%";
        DactUi.DrawText(draw, new Vector2(right - DactUi.TextWidth(shareText, DactFonts.Small), start.Y), height, shareText, palette.Muted, DactFonts.Small);
        right -= 44 * s + 10 * s;
        var estimate = hps ? null : FflogsEstimateService.GetPersistedEstimate(combatant);
        if (estimate is not null)
        {
            var score = $"FFLogs {estimate.Score}";
            var scoreWidth = DactUi.TextWidth(score, DactFonts.Small);
            DactUi.DrawText(draw, new Vector2(right - scoreWidth, start.Y), height, score, estimate.Color, DactFonts.Small);
            right -= scoreWidth + 10 * s;
        }

        var name = limitBreak ? MeterWindow.LimitBreakDisplayName : PlayerIdentityFormatter.Format(combatant, encounter.Combatants, configuration.Meter, text);
        var fitted = DactUi.Fit(name, Math.Max(20 * s, right - x));
        DactUi.DrawText(draw, new Vector2(x, start.Y), height, fitted, self || limitBreak ? palette.Gold : palette.Text);
        if (!hovered) return;
        var tooltip = (fitted != name ? name + "\n" : string.Empty) +
                      text.Format($"{(hps ? "HPS" : HistoryRateLabel())} {rate:N0} · 占比 {share:0.0}% · 死亡 {combatant.Deaths}",
                          $"{(hps ? "HPS" : HistoryRateLabel())} {rate:N0} · share {share:0.0}% · KO {combatant.Deaths}");
        if (estimate is not null)
            tooltip += "\n" + text.Format(
                $"DPS Parse 预估：{estimate.Score}\n根据本场实际 DPS 与当前 FFLogs 同职业、同副本、同分区的 DPS 分布估算。\nFFLogs 数据更新于：{estimate.DataUpdatedAt.ToLocalTime():yyyy/MM/dd}",
                $"Estimated DPS Parse: {estimate.Score}\nEstimated from this encounter's actual DPS and the current FFLogs DPS distribution for the same job, encounter, and partition.\nFFLogs data updated: {estimate.DataUpdatedAt.ToLocalTime():yyyy/MM/dd}");
        DactUi.Tooltip(tooltip);
    }

    private void DrawActionTable(Encounter encounter)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, palette.Hover);
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, palette.Hover with { W = .35f });
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, DactUi.DividerColor);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(8, 5) * s);
        if (ImGui.BeginTable("history-actions", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(text.Get("来源", "Source"));
            ImGui.TableSetupColumn(text.Get("技能", "Action"));
            ImGui.TableSetupColumn(text.Get("次数", "Uses"), ImGuiTableColumnFlags.WidthFixed, 56 * s);
            ImGui.TableSetupColumn(text.Get("伤害", "Damage"), ImGuiTableColumnFlags.WidthFixed, 96 * s);
            ImGui.TableSetupColumn(text.Get("治疗", "Healing"), ImGuiTableColumnFlags.WidthFixed, 96 * s);
            using (DactFonts.Push(DactFonts.Small)) ImGui.TableHeadersRow();
            foreach (var action in encounter.ActionSummaries.OrderByDescending(static item => item.TotalDamage))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(PlayerIdentityFormatter.FormatActionOwner(action.CombatantId, encounter.Combatants, configuration.Meter, text));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(action.ActionName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(action.Uses.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(action.TotalDamage.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(action.TotalHealing.ToString("N0"));
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
    }

    // ---- 日志文件

    private void DrawLogFilesTab(bool wide, float height)
    {
        var s = DactUi.S;
        if (Paths is null) return;
        Directory.CreateDirectory(Paths.EncounterLogDirectory);
        var files = Directory.EnumerateFiles(Paths.EncounterLogDirectory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        if (files.Length == 0)
        {
            DactUi.Wrapped(text.Get("暂无战斗日志。完成一场战斗后会自动生成。", "No encounter logs yet."), DactTheme.Palette.Muted);
            return;
        }
        if (historySelectedFile is null || !files.Contains(historySelectedFile, StringComparer.OrdinalIgnoreCase)) LoadHistoryFile(files[0]);

        if (DactUi.BeginPanel("history-log-list", new Vector2(wide ? 236 * s : -1, wide ? height : 230 * s), 8, 2))
        {
            foreach (var file in files)
            {
                var selected = string.Equals(file, historySelectedFile, StringComparison.OrdinalIgnoreCase);
                ImGui.PushID(file);
                var start = ImGui.GetCursorScreenPos();
                var width = ImGui.GetContentRegionAvail().X;
                var rowHeight = (8 + 18 + 3 + 16 + 8) * s;
                if (ImGui.InvisibleButton("log", new Vector2(width, rowHeight))) LoadHistoryFile(file);
                var hovered = ImGui.IsItemHovered();
                var draw = ImGui.GetWindowDrawList();
                var fill = DactMotion.Color(ImGuiP.GetItemID(), DactTheme.Palette.Hover with { W = selected ? 1 : hovered ? .5f : 0 }, DactMotion.Fast);
                if (fill.W > 0) draw.AddRectFilled(start, start + new Vector2(width, rowHeight), DactUi.U32(fill), 8 * s);
                if (selected)
                    draw.AddRect(start, start + new Vector2(width, rowHeight), DactUi.U32(DactTheme.Palette.Accent with { W = .55f }), 8 * s, ImDrawFlags.None, Math.Max(1, s));
                var name = Path.GetFileNameWithoutExtension(file);
                var title = DactUi.Fit(name, width - 20 * s);
                DactUi.DrawText(draw, start + new Vector2(10 * s, 8 * s), 18 * s, title, selected ? DactTheme.Palette.Text : DactTheme.Palette.Muted);
                DactUi.DrawText(draw, start + new Vector2(10 * s, 29 * s), 16 * s, File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm"), DactTheme.Palette.Muted, DactFonts.Small);
                if (hovered && title != name) DactUi.Tooltip(name);
                ImGui.PopID();
            }
        }
        DactUi.EndPanel();
        if (wide) ImGui.SameLine(0, 14 * s);

        ImGui.BeginGroup();
        // Raw JSON exposes player names and IDs, so it stays hidden while ID masking is on.
        if (configuration.Meter.PlayerIdentityMode == PlayerIdentityMode.Original)
            DactUi.InlineToggle(text.Get("查看原始 JSON", "Show raw JSON"), ref historyShowRawJson);
        else
        {
            historyShowRawJson = false;
            DactUi.Hint(text.Get("已启用 ID 遮盖，原始 JSON 不在界面中显示", "Player ID masking is enabled; raw JSON is hidden in the UI"));
        }
        // The toggle row and the gap below it come out of the column, so the page never scrolls.
        var detailHeight = Math.Max(120 * s, height - ImGui.GetItemRectSize().Y - ImGui.GetStyle().ItemSpacing.Y);
        if (!string.IsNullOrWhiteSpace(historyLoadError))
            DactUi.Wrapped(text.Format($"读取失败：{historyLoadError}", $"Load failed: {historyLoadError}"), DactTheme.Palette.Muted);
        else if (historyFileEncounter is null)
            DactUi.Wrapped(text.Get("请从左侧选择一场战斗。", "Select an encounter on the left."), DactTheme.Palette.Muted);
        else if (historyShowRawJson)
        {
            if (DactUi.BeginPanel("history-raw-json", new Vector2(-1, detailHeight)))
            {
                using (DactFonts.Push(DactFonts.Small)) ImGui.TextWrapped(historyRawJson);
            }
            DactUi.EndPanel();
        }
        else DrawEncounterDetails(historyFileEncounter, detailHeight, "history-log-details");
        ImGui.EndGroup();
    }

    private void LoadHistoryFile(string file)
    {
        historySelectedFile = file;
        historyLoadError = string.Empty;
        try
        {
            historyRawJson = File.ReadAllText(file);
            historyFileEncounter = JsonSerializer.Deserialize<Encounter>(historyRawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (historyFileEncounter is null) historyLoadError = text.Get("日志内容为空。", "The log is empty.");
        }
        catch (Exception error)
        {
            historyFileEncounter = null;
            historyLoadError = error.Message;
        }
    }

    // ---- 开荒进度

    private void DrawProgressionTab(bool wide, float height)
    {
        var s = DactUi.S;
        if (historyDuties.Count == 0)
        {
            DactUi.Wrapped(text.Get(
                "还没有开荒记录。在副本里和 Boss 战斗后，每次团灭或通关都会记在这里：打到哪个阶段、Boss 还剩多少血、团灭前 Boss 读的是哪个技能。",
                "No progression yet. After you fight a boss in a duty, each wipe or clear is kept here: the phase reached, the boss HP left and what the boss was casting before the wipe."),
                DactTheme.Palette.Muted);
            return;
        }
        var duty = historyDuties.FirstOrDefault(item => item.TerritoryId == historySelectedDuty) ?? historyDuties[0];
        historySelectedDuty = duty.TerritoryId;

        if (DactUi.BeginPanel("history-duty-list", new Vector2(wide ? 236 * s : -1, wide ? height : 200 * s), 8, 2))
        {
            foreach (var item in historyDuties)
            {
                ImGui.PushID((int)item.TerritoryId);
                var selected = item.TerritoryId == duty.TerritoryId;
                var start = ImGui.GetCursorScreenPos();
                var width = ImGui.GetContentRegionAvail().X;
                var rowHeight = (8 + 22 + 3 + 16 + 8) * s;
                if (ImGui.InvisibleButton("duty", new Vector2(width, rowHeight))) historySelectedDuty = item.TerritoryId;
                var hovered = ImGui.IsItemHovered();
                var draw = ImGui.GetWindowDrawList();
                var fill = DactMotion.Color(ImGuiP.GetItemID(), DactTheme.Palette.Hover with { W = selected ? 1 : hovered ? .5f : 0 }, DactMotion.Fast);
                if (fill.W > 0) draw.AddRectFilled(start, start + new Vector2(width, rowHeight), DactUi.U32(fill), 8 * s);
                if (selected)
                    draw.AddRect(start, start + new Vector2(width, rowHeight), DactUi.U32(DactTheme.Palette.Accent with { W = .55f }), 8 * s, ImDrawFlags.None, Math.Max(1, s));
                var (pill, tone) = item.Cleared ? (text.Get("已通关", "Cleared"), PillTone.Ok) : (text.Get("开荒中", "In progress"), PillTone.Warn);
                var pillWidth = DactUi.PillWidth(pill, false);
                var right = start.X + width - 10 * s;
                DactUi.DrawPill(draw, new Vector2(right - pillWidth, start.Y + 8 * s), pill, tone, false);
                var title = DactUi.Fit(item.Name, right - pillWidth - 6 * s - (start.X + 10 * s));
                DactUi.DrawText(draw, start + new Vector2(10 * s, 8 * s), 22 * s, title, selected ? DactTheme.Palette.Text : DactTheme.Palette.Muted);
                var detail = item.Cleared
                    ? text.Format($"{item.Pulls.Count} 次 · 已通关", $"{item.Pulls.Count} pulls · cleared")
                    : text.Format($"{item.Pulls.Count} 次 · 最远 {PhaseValue(item.Best.Phase, item.Best.PhaseFraction)}",
                        $"{item.Pulls.Count} pulls · best {PhaseValue(item.Best.Phase, item.Best.PhaseFraction)}");
                DactUi.DrawText(draw, start + new Vector2(10 * s, 33 * s), 16 * s, detail, DactTheme.Palette.Muted, DactFonts.Small);
                if (hovered && title != item.Name) DactUi.Tooltip(item.Name);
                ImGui.PopID();
            }
        }
        DactUi.EndPanel();
        if (wide) ImGui.SameLine(0, 14 * s);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.BeginChild("history-progress", new Vector2(-1, height), false);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 12) * s);
        DrawProgressTiles(duty);
        DrawProgressChart(duty);
        DrawWipePoints(duty);
        ImGui.PopStyleVar();
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawProgressTiles(DutyProgress duty)
    {
        var palette = DactTheme.Palette;
        var now = DateTimeOffset.UtcNow;
        var thisWeek = duty.Pulls.Where(pull => now - pull.StartTime <= TimeSpan.FromDays(7)).ToArray();
        var lastWeek = duty.Pulls.Where(pull => now - pull.StartTime is var age && age > TimeSpan.FromDays(7) && age <= TimeSpan.FromDays(14)).ToArray();
        var recent = duty.Pulls.TakeLast(10).ToArray();
        var average = duty.AverageProgress(recent);
        var best = duty.Best;
        string Phase(double progress)
        {
            var phase = Math.Min(duty.MaxPhase, (int)Math.Floor(progress) + 1);
            return PhaseValue(phase, Math.Clamp(progress - (phase - 1), 0, 1));
        }

        var trend = string.Empty;
        if (thisWeek.Length > 0 && lastWeek.Length > 0)
        {
            var delta = (int)Math.Floor(duty.AverageProgress(thisWeek)) - (int)Math.Floor(duty.AverageProgress(lastWeek));
            trend = delta > 0 ? text.Format($"比上周推进 {delta} 个阶段", $"{delta} phase(s) further than last week")
                : delta < 0 ? text.Format($"比上周少 {-delta} 个阶段", $"{-delta} phase(s) behind last week")
                : text.Get("与上周在同一阶段", "Same phase as last week");
        }

        DactUi.StatTiles(
            new DactUi.StatTileData("history", text.Get("尝试", "Pulls"), text.Format($"{duty.Pulls.Count} 次", $"{duty.Pulls.Count}"), palette.Text,
                text.Format($"本周 +{thisWeek.Length}", $"This week +{thisWeek.Length}")),
            new DactUi.StatTileData("target", text.Get("最远", "Best"), best.Cleared ? text.Get("已通关", "Cleared") : PhaseValue(best.Phase, best.PhaseFraction),
                palette.Accent, text.Format($"第 {duty.BestIndex + 1} 次", $"Pull {duty.BestIndex + 1}")),
            new DactUi.StatTileData("pulse", text.Get("最近 10 次平均", "Last 10 average"), Phase(average), palette.Text, trend));
    }

    // 每次尝试打到哪里: one bar per pull, height = how far it got, colour = phase; the best pull
    // is solid gold (user edit in Figma). Clicking a bar opens that fight under 近期战斗.
    private void DrawProgressChart(DutyProgress duty)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        DactUi.BeginCard("history-progress-chart", text.Get("每次尝试打到哪里", "How far each pull got"));
        var chartHeight = 104 * s;
        var axisWidth = 22 * s;
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.Dummy(new Vector2(width, chartHeight));
        var draw = ImGui.GetWindowDrawList();
        var maxPhase = Math.Max(1, duty.MaxPhase);
        for (var phase = maxPhase; phase >= 1; phase--)
        {
            var y = origin.Y + (maxPhase - phase) * (chartHeight - 14 * s) / Math.Max(1, maxPhase - 1);
            DactUi.DrawText(draw, new Vector2(origin.X, maxPhase == 1 ? origin.Y + chartHeight - 14 * s : y), 14 * s, $"P{phase}", palette.Muted, DactFonts.Tiny);
        }

        var barWidth = 8 * s;
        var gap = 4 * s;
        var left = origin.X + axisWidth + 8 * s;
        var capacity = Math.Max(1, (int)((origin.X + width - left + gap) / (barWidth + gap)));
        var first = Math.Max(0, duty.Pulls.Count - capacity);
        for (var index = first; index < duty.Pulls.Count; index++)
        {
            var pull = duty.Pulls[index];
            var progress = pull.Cleared ? maxPhase : pull.Progress;
            var barHeight = 4 * s + (float)(progress / maxPhase) * (chartHeight - 4 * s);
            var min = new Vector2(left + (index - first) * (barWidth + gap), origin.Y + chartHeight - barHeight);
            var color = index == duty.BestIndex ? palette.Gold
                : pull.Cleared ? palette.Gold with { W = .55f }
                : PhaseColor(pull.Phase, maxPhase);
            ImGui.SetCursorScreenPos(new Vector2(min.X - gap * .5f, origin.Y));
            if (ImGui.InvisibleButton($"pull-{index}", new Vector2(barWidth + gap, chartHeight))) OpenPullFight(pull);
            var hovered = ImGui.IsItemHovered();
            draw.AddRectFilled(min, new Vector2(min.X + barWidth, origin.Y + chartHeight), DactUi.U32(hovered ? Vector4.Lerp(color, palette.Text, .25f) : color), 2 * s);
            if (!hovered) continue;
            var where = pull.Cleared ? text.Get("通关", "Cleared") : PhaseValue(pull.Phase, pull.PhaseFraction);
            var tooltip = text.Format($"第 {index + 1} 次 · {where}", $"Pull {index + 1} · {where}") + "\n" +
                          $"{HistoryTime(pull.StartTime)} · {FormatFightDuration(pull.EndTime - pull.StartTime)}";
            if (pull.LastBoss is { } boss && !pull.Cleared) tooltip += "\n" + text.Format($"{boss} 剩余 {pull.BossHpFraction * 100:0}%", $"{boss} at {pull.BossHpFraction * 100:0}%");
            if (historyMissingFightNote is not null && ImGui.GetTime() < historyMissingFightUntil) tooltip += "\n" + historyMissingFightNote;
            DactUi.Tooltip(tooltip);
        }
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + chartHeight));
        ImGui.Dummy(Vector2.Zero);
        DactUi.Hint(text.Get("金色那一条是最好的一次；点任意一根柱子查看那次战斗。", "The gold bar is your best pull; click any bar to open that fight."));
        DactUi.EndCard();
    }

    // Later phases move from the muted base towards the skin accent (dsl PHASE ramp).
    private static Vector4 PhaseColor(int phase, int maxPhase)
    {
        var palette = DactTheme.Palette;
        var t = maxPhase <= 1 ? 1 : (phase - 1) / (float)(maxPhase - 1);
        var low = Vector4.Lerp(palette.Raised, palette.Muted, .35f) with { W = 1 };
        return Vector4.Lerp(low, palette.Accent, t);
    }

    private void OpenPullFight(ProgressionPull pull)
    {
        var recent = EncounterHistory?.GetSnapshot().Recent ?? [];
        foreach (var encounter in recent)
        {
            foreach (var candidate in encounter.SegmentRecords.Count > 0 ? encounter.SegmentRecords : [encounter])
            {
                if (candidate.TerritoryId != pull.TerritoryId || Math.Abs((candidate.StartTime - pull.StartTime).TotalSeconds) > 45) continue;
                if (encounter.SegmentRecords.Count > 0) historyExpandedFolders.Add(encounter.Id);
                historySelectedId = candidate.Id;
                historyTab = 0;
                return;
            }
        }
        // Only the newest fights are kept (设置 · 历史记录上限); older pulls stay in the chart only.
        historyMissingFightNote = text.Get("这次战斗已超出近期战斗的保存上限。", "This fight is older than the saved recent fights.");
        historyMissingFightUntil = ImGui.GetTime() + 4;
    }

    private void DrawWipePoints(DutyProgress duty)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var summarizeLabel = text.Get("让 AI 总结开荒", "Summarize with AI");
        DactUi.BeginCard("history-wipes", null);
        DactUi.CardTitleRow(text.Get("最常见的团灭点", "Most common wipes"), null,
            SummarizeProgression is null ? 0 : DactUi.ButtonWidth(summarizeLabel, icon: "sparkle"),
            SummarizeProgression is not { } summarize ? null : () =>
            {
                if (DactUi.Button(summarizeLabel + "##history-ai-progress", icon: "sparkle")) summarize(duty.TerritoryId);
            });
        if (duty.WipePoints.Count == 0)
        {
            DactUi.Hint(text.Get("还没有团灭记录。", "No wipes recorded yet."));
            DactUi.EndCard();
            return;
        }

        var most = Math.Max(1, duty.WipePoints.Max(point => point.Count));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 10) * s);
        foreach (var point in duty.WipePoints)
        {
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var height = 22 * s;
            ImGui.Dummy(new Vector2(width, height));
            var hovered = ImGui.IsItemHovered();
            var draw = ImGui.GetWindowDrawList();
            var phase = $"P{point.Phase}";
            DactUi.DrawPill(draw, start, phase, PillTone.Off, false);
            var x = start.X + DactUi.PillWidth(phase, false) + 10 * s;
            var label = point.Cast ?? text.Format($"{point.Boss} 剩余约 {point.HpBucket}%", $"{point.Boss} at about {point.HpBucket}%");
            var fitted = DactUi.Fit(label, 128 * s);
            DactUi.DrawText(draw, new Vector2(x, start.Y), height, fitted, palette.Text);
            x += 128 * s + 10 * s;
            var count = text.Format($"{point.Count} 次", $"{point.Count}×");
            var countWidth = 36 * s;
            var barRight = start.X + width - countWidth - 10 * s;
            if (barRight > x)
            {
                var barY = start.Y + height * .5f - 3 * s;
                draw.AddRectFilled(new Vector2(x, barY), new Vector2(barRight, barY + 6 * s), DactUi.U32(palette.Hover), 3 * s);
                draw.AddRectFilled(new Vector2(x, barY), new Vector2(x + (barRight - x) * point.Count / most, barY + 6 * s), DactUi.U32(palette.Accent), 3 * s);
            }
            DactUi.DrawText(draw, new Vector2(start.X + width - DactUi.TextWidth(count, DactFonts.Small), start.Y), height, count, palette.Muted, DactFonts.Small);
            if (hovered)
                DactUi.Tooltip(point.Cast is null
                    ? text.Get("首个队员倒下时 Boss 没有在读条，按 Boss 剩余血量归类。", "The boss was not casting when the first member fell, so this groups by boss HP left.")
                    : text.Format($"首个队员倒下前 Boss 最近一次读条：{point.Cast}", $"The boss's last cast before the first member fell: {point.Cast}"));
        }
        ImGui.PopStyleVar();
        DactUi.EndCard();
    }
}
