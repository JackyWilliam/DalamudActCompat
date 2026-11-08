using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Ai;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// AI 助手 pages (features.js): 未开启, AI 设置, and the three tabs with their conversations.
public sealed partial class ControlCenterWindow
{
    private const string AiContextDialog = "##ai-context";
    private readonly Dictionary<AiMode, int> aiScrolledRevision = [];

    private bool DrawAiPage(CloudClientSnapshot snapshot)
    {
        PumpAiResults();
        if (aiSettingsPage) return DrawAiSettingsPage();
        if (!AiConfig.Enabled) return DrawAiOffPage();

        var s = DactUi.S;
        var settingsLabel = text.Get("AI 设置", "AI settings");
        var newLabel = text.Get("新对话", "New chat");
        DactUi.PageHeader(text.Get("AI 助手", "AI assistant"),
            text.Get("诊断问题、复盘你自己的战斗、回答使用问题。使用你自己的 AI 服务和 API Key，发出前会去掉角色名和服务器名。",
                "Troubleshoot, review your own fights and answer questions, using your own AI service and API key. Character and world names are removed before sending."),
            DactUi.ButtonWidth(settingsLabel, icon: "gear") + DactUi.ButtonWidth(newLabel, icon: "plus") + 8 * s,
            () =>
            {
                if (DactUi.Button(settingsLabel + "##ai-settings", icon: "gear")) aiSettingsPage = true;
                ImGui.SameLine(0, 8 * s);
                if (DactUi.Button(newLabel + "##ai-new", icon: "plus"))
                {
                    var conversation = aiConversations[aiTab];
                    var encounter = conversation.EncounterId;
                    conversation.Reset();
                    // A new review keeps the fight that was picked.
                    if (aiTab == 1) conversation.EncounterId = encounter;
                }
            });
        DactUi.Segmented("ai-tabs",
        [
            text.Get("诊断问题", "Diagnose"),
            text.Get("战斗复盘", "Fight review"),
            text.Get("使用问答", "Questions"),
        ], ref aiTab);

        switch (aiTab)
        {
            case 1: DrawAiReview(); break;
            case 2: DrawAiAsk(); break;
            default: DrawAiDiagnose(); break;
        }
        DrawAiContextDialog();
        return false;
    }

    // ---- 诊断问题

    private void DrawAiDiagnose()
    {
        var s = DactUi.S;
        var conversation = aiConversations[0];
        var inputHeight = 30 * s;
        DrawAiConversation(conversation, ImGui.GetContentRegionAvail().Y - inputHeight - ImGui.GetStyle().ItemSpacing.Y,
            text.Get("我会读取运行状态、扩展权限和最近 200 行诊断日志，找出问题并给出能直接点的修复按钮。描述你遇到的情况，或直接点「开始诊断」。",
                "I read the runtime status, extension permissions and the latest 200 log lines, then list what is wrong with buttons that fix it. Describe the problem, or just press Diagnose."));
        var start = text.Get("开始诊断", "Diagnose");
        if (DrawAiInput(conversation, text.Get("描述你遇到的问题，或直接点「开始诊断」", "Describe the problem, or just press Diagnose"), start, "sparkle", allowEmpty: true) is { } question)
        {
            var display = question.Length > 0 ? question : start;
            var asked = question.Length > 0 ? question : text.Get("请检查 DACT 现在有没有问题。", "Please check whether anything in DACT is wrong right now.");
            SendAi(conversation, display, asked, BuildDiagnoseContext);
        }
    }

    // ---- 使用问答

    private void DrawAiAsk()
    {
        var s = DactUi.S;
        var conversation = aiConversations[2];
        var reserved = 30 * s + 28 * s + 2 * ImGui.GetStyle().ItemSpacing.Y;
        DrawAiConversation(conversation, ImGui.GetContentRegionAvail().Y - reserved,
            text.Get("问任何关于 DACT 的问题。我会按帮助文档和你现在的设置回答，并能直接带你到对应的设置。",
                "Ask anything about DACT. I answer from the help and your current settings, and can take you straight to the setting."));
        {
            // 推荐问题 chips under the conversation.
            string[] chips =
            [
                text.Get("FFLogs 预估怎么开？", "How do I turn on FFLogs estimates?"),
                text.Get("触发器怎么导入？", "How do I import triggers?"),
                text.Get("为什么统计没有数据？", "Why is the meter empty?"),
            ];
            for (var index = 0; index < chips.Length; index++)
            {
                if (index > 0) DactUi.SameLineIfFits(DactUi.TextWidth(chips[index], DactFonts.Small) + 24 * s);
                if (DrawAiChip(chips[index], $"##ai-chip-{index}") && AiConfig.Ready && !conversation.Busy)
                    SendAi(conversation, chips[index], chips[index], BuildAskContext);
            }
        }
        if (DrawAiInput(conversation, text.Get("问一个关于 DACT 的问题", "Ask a question about DACT"), text.Get("发送", "Send"), "send", allowEmpty: false) is { } asked)
            SendAi(conversation, asked, asked, BuildAskContext);
    }

    private bool DrawAiChip(string label, string id)
    {
        var s = DactUi.S;
        var width = DactUi.TextWidth(label, DactFonts.Small) + 24 * s;
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, 28 * s));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var fill = DactMotion.Color(ImGuiP.GetItemID(), hovered ? Vector4.Lerp(DactUi.Field, DactTheme.Palette.Hover, .6f) : DactUi.Field, DactMotion.Fast);
        draw.AddRectFilled(min, min + new Vector2(width, 28 * s), DactUi.U32(fill), 14 * s);
        draw.AddRect(min, min + new Vector2(width, 28 * s), DactUi.U32(DactUi.FieldBorder), 14 * s, ImDrawFlags.None, Math.Max(1, s));
        DactUi.DrawText(draw, min + new Vector2(12 * s, 0), 28 * s, label, DactTheme.Palette.Accent, DactFonts.Small);
        return clicked;
    }

    // ---- 战斗复盘

    private void DrawAiReview()
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var conversation = aiConversations[1];
        var encounters = ReviewableEncounters();
        if (encounters.Count == 0)
        {
            DactUi.Wrapped(text.Get("还没有可以复盘的战斗。打完一场战斗后，它会出现在这里。", "No fights to review yet. Finish a fight and it shows up here."), palette.Muted);
            return;
        }
        var selectedIndex = Math.Max(0, encounters.ToList().FindIndex(encounter => encounter.Id == conversation.EncounterId));
        var encounter = encounters[selectedIndex];
        conversation.EncounterId ??= encounter.Id;

        var startLabel = text.Get("开始复盘", "Review");
        var startWidth = DactUi.ButtonWidth(startLabel, ButtonKind.Primary, "sparkle");
        var labels = encounters.Select(ReviewLabel).ToArray();
        if (DactUi.Select("ai-review-fight", labels, ref selectedIndex, ImGui.GetContentRegionAvail().X - startWidth - 8 * s))
        {
            conversation.Reset();
            conversation.EncounterId = encounters[selectedIndex].Id;
            encounter = encounters[selectedIndex];
        }
        ImGui.SameLine(0, 8 * s);
        var canStart = AiConfig.Ready && !conversation.Busy && conversation.PendingConfirm is null;
        if (DactUi.Button(startLabel + "##ai-review-start", ButtonKind.Primary, "sparkle", disabled: !canStart))
        {
            conversation.Reset();
            conversation.EncounterId = encounter.Id;
            StartReview(conversation, encounter);
        }

        DrawReviewTiles(encounter);

        // AI 建议 fills the rest of the page; the advice scrolls inside it.
        var copyLabel = text.Get("复制建议", "Copy advice");
        var reply = conversation.Entries.LastOrDefault(entry => !entry.FromUser)?.Reply;
        if (DactUi.BeginPanel("ai-review-advice", new Vector2(-1, Math.Max(160 * s, ImGui.GetContentRegionAvail().Y)), 14, 12))
        {
            DactUi.CardTitleRow(text.Get("AI 建议", "AI advice"), null, reply is null ? 0 : DactUi.ButtonWidth(copyLabel, ButtonKind.Ghost),
                reply is null ? null : () =>
                {
                    if (DactUi.Button(copyLabel + "##ai-review-copy", ButtonKind.Ghost)) ImGui.SetClipboardText(DescribeReply(reply));
                });
            var width = ImGui.GetContentRegionAvail().X;
            if (!AiConfig.Verified) DrawAiNotConnected();
            else if (conversation.PendingConfirm is not null) DrawAiConfirm(conversation);
            else if (conversation.Busy) DrawAiThinking(conversation, width);
            else if (conversation.Error is { } error) DrawAiError(conversation, error);
            else if (reply is null)
                DactUi.Hint(text.Get("选好一场战斗，点「开始复盘」。只分析你自己的数据：输出、暴击直击、死亡，以及开荒副本打到哪里、在哪个技能团灭。",
                    "Pick a fight and press Review. Only your own numbers are analysed: damage, crit and direct hits, deaths, and for progression how far the party got and which cast wiped it."));
            else
            {
                if (reply.Text.Length > 0) DactUi.Wrapped(reply.Text, palette.Text, DactFonts.Body, width);
                for (var index = 0; index < reply.Advice.Count; index++) DrawAdvice(index + 1, reply.Advice[index], width);
                DrawReplyActions(conversation, reply, "review");
                DactUi.Divider();
                var historyLabel = text.Get("在战斗记录里查看", "View in history");
                var basis = text.Get("只分析你自己的数据；职业建议参考当前版本的通用规律，仅供参考。", "Only your own data is analysed; job tips follow general rules of the current patch and are for reference.");
                var row = ImGui.GetCursorPosY();
                DactUi.Wrapped(basis, DactUi.HintColor, DactFonts.Small, width - DactUi.ButtonWidth(historyLabel, ButtonKind.Ghost) - 8 * s);
                var after = ImGui.GetCursorPos();
                ImGui.SetCursorPos(new Vector2(ImGui.GetCursorPosX() + width - DactUi.ButtonWidth(historyLabel, ButtonKind.Ghost), row - 7 * s));
                if (DactUi.Button(historyLabel + "##ai-review-history", ButtonKind.Ghost))
                {
                    historySelectedId = encounter.Id;
                    OpenHistory(0);
                }
                ImGui.SetCursorPos(after);
            }
        }
        DactUi.EndPanel();
    }

    private string ReviewLabel(Encounter encounter)
    {
        var pull = MatchPull(encounter);
        var parts = new List<string> { FightTitle(encounter) };
        if (pull is not null && PullNumber(pull) is > 0 and var number) parts.Add(text.Format($"第 {number} 次", $"pull {number}"));
        parts.Add(FormatFightDuration(encounter.EffectiveDuration));
        if (pull is not null) parts.Add(pull.Cleared ? text.Get("通关", "cleared") : text.Format($"{PhaseWords(pull)} 团灭", $"wipe at {PhaseWords(pull)}"));
        else parts.Add(HistoryTime(encounter.StartTime));
        return string.Join(" · ", parts);
    }

    // The tiles show only what DACT records for the fight (no cast-level data exists).
    private void DrawReviewTiles(Encounter encounter)
    {
        var palette = DactTheme.Palette;
        if (encounter.Combatants.FirstOrDefault(combatant => combatant.IsLocalPlayer) is not { } you) return;
        var party = encounter.Combatants.Where(combatant => !string.IsNullOrWhiteSpace(combatant.Job))
            .OrderByDescending(combatant => Rate(combatant, encounter)).ToList();
        var rank = party.IndexOf(you) + 1;
        var rate = Rate(you, encounter);
        double Percent(int part) => you.DamageHits > 0 ? part * 100d / you.DamageHits : 0;
        var pull = MatchPull(encounter);
        var deathSub = you.Deaths == 0
            ? text.Get("这场没有倒地", "You stayed up all fight")
            : pull?.FirstDeathSeconds is { } first
                ? text.Format($"全队首个死亡 {FormatFightDuration(TimeSpan.FromSeconds(first))}", $"Party's first death {FormatFightDuration(TimeSpan.FromSeconds(first))}")
                : text.Get("死亡损失通常比循环细节更大", "Deaths usually cost more than rotation details");
        DactUi.StatTiles(
            new("meter", you.Rdps > 0 ? "rDPS" : "DPS", rate.ToString("N0"), palette.Accent,
                text.Format($"DPS {Dps(you, encounter):N0} · 全队第 {rank}", $"DPS {Dps(you, encounter):N0} · #{rank} in party")),
            new("target", text.Get("暴击 / 直击", "Crit / direct hit"), $"{Percent(you.CriticalHits):0.0}% / {Percent(you.DirectHits):0.0}%", palette.Text,
                text.Format($"暴击直击 {Percent(you.CriticalDirectHits):0.0}%", $"Crit direct hits {Percent(you.CriticalDirectHits):0.0}%")),
            new("shield", text.Get("死亡", "Deaths"), text.Format($"{you.Deaths} 次", $"{you.Deaths}"), you.Deaths == 0 ? palette.Accent : palette.Gold, deathSub),
            you.FflogsPercentile is { } percentile
                ? new("pulse", text.Get("预估 Parse", "Estimated parse"), percentile.ToString("0"), FflogsEstimateService.ColorForPercentile(percentile),
                    text.Format($"本次 rDPS {you.Rdps:N0}", $"rDPS {you.Rdps:N0}"), Bold: true)
                : new("pulse", text.Get("预估 Parse", "Estimated parse"), "—", palette.Muted, text.Get("未开启 FFLogs 估算", "FFLogs estimates off")));
    }

    // Live encounters fill EncDps; older saved fights may only carry ACT's DPS or the total.
    private static double Dps(Combatant combatant, Encounter encounter)
        => combatant.EncDps > 0 ? combatant.EncDps
            : combatant.Dps > 0 ? combatant.Dps
            : combatant.TotalDamage / Math.Max(1, encounter.EffectiveDuration.TotalSeconds);

    private static double Rate(Combatant combatant, Encounter encounter) => combatant.Rdps > 0 ? combatant.Rdps : Dps(combatant, encounter);

    private void DrawAdvice(int number, AiAdvice advice, float width)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(start + new Vector2(10 * s, 10 * s + 1 * s), 10 * s, DactUi.U32(DactUi.AccentSoft), 20);
        var label = number.ToString();
        DactUi.DrawText(draw, new Vector2(start.X + 10 * s - DactUi.TextWidth(label, DactFonts.Tiny) * .5f, start.Y + 1 * s), 20 * s, label, palette.Accent, DactFonts.Tiny);
        ImGui.SetCursorScreenPos(start + new Vector2(30 * s, 0));
        ImGui.BeginGroup();
        using (DactUi.Gap(2))
        {
            DactUi.Wrapped(advice.Title, palette.Text, DactFonts.Body, width - 30 * s);
            if (advice.Detail.Length > 0) DactUi.Wrapped(advice.Detail, DactUi.HintColor, DactFonts.Small, width - 30 * s);
        }
        ImGui.EndGroup();
    }

    // ---- Conversation

    private void DrawAiConversation(AiConversation conversation, float height, string emptyHint)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        if (!DactUi.BeginPanel($"ai-chat-{conversation.Mode}", new Vector2(-1, Math.Max(120 * s, height)), 14, 14))
        {
            DactUi.EndPanel();
            return;
        }
        var width = ImGui.GetContentRegionAvail().X;
        if (!AiConfig.Verified) DrawAiNotConnected();
        else if (conversation.Entries.Count == 0 && !conversation.Busy && conversation.Error is null)
            DrawAiMessage(width, () => DactUi.Wrapped(emptyHint, palette.Muted, DactFonts.Body, width - 36 * s));

        AiEntry? previous = null;
        foreach (var entry in conversation.Entries)
        {
            if (entry == conversation.PendingConfirm) break;
            if (entry.FromUser) DrawUserBubble(entry.Display, width);
            else DrawAiMessage(width, () => DrawReply(conversation, entry.Reply ?? AiReply.PlainText(entry.Display), previous, width - 36 * s));
            previous = entry;
        }
        if (conversation.PendingConfirm is not null) DrawAiConfirm(conversation);
        else if (conversation.Busy) DrawAiThinking(conversation, width);
        else if (conversation.Error is { } error) DrawAiError(conversation, error);

        // New entries scroll into view once; scrolling back up afterwards stays where the player put it.
        if (!aiScrolledRevision.TryGetValue(conversation.Mode, out var seen) || seen != conversation.Revision)
        {
            aiScrolledRevision[conversation.Mode] = conversation.Revision;
            ImGui.SetScrollHereY(1);
        }
        DactUi.EndPanel();
    }

    private void DrawUserBubble(string message, float width)
    {
        var s = DactUi.S;
        var maxText = width * .72f - 24 * s;
        var lines = DactUi.WrapLines(message, maxText);
        var textSize = ImGui.CalcTextSize(string.Join("\n", lines));
        var size = textSize + new Vector2(24 * s, 16 * s);
        var origin = ImGui.GetCursorScreenPos();
        var min = new Vector2(origin.X + width - size.X, origin.Y);
        ImGui.Dummy(new Vector2(width, size.Y));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, DactUi.U32(Vector4.Lerp(DactTheme.Palette.Hover, DactTheme.Palette.Accent, .35f)), 10 * s);
        draw.AddText(min + new Vector2(12 * s, 8 * s), DactUi.U32(DactTheme.Foreground(DactTheme.Palette.Text)), string.Join("\n", lines));
    }

    // AI avatar on the left, the content column next to it.
    private void DrawAiMessage(float width, Action content)
    {
        var s = DactUi.S;
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(origin + new Vector2(13 * s), 13 * s, DactUi.U32(DactUi.AccentSoft), 24);
        DactIcons.Draw(draw, "sparkle", origin + new Vector2(6 * s), 14 * s, DactTheme.Palette.Accent);
        ImGui.SetCursorScreenPos(origin + new Vector2(36 * s, 3 * s));
        ImGui.BeginGroup();
        using (DactUi.Gap(8)) content();
        ImGui.EndGroup();
        // Keeps the avatar's height when the content is a single short line.
        var bottom = Math.Max(ImGui.GetItemRectMax().Y, origin.Y + 26 * s);
        ImGui.SetCursorScreenPos(new Vector2(origin.X, bottom));
        ImGui.Dummy(new Vector2(width, 0));
    }

    private void DrawReply(AiConversation conversation, AiReply reply, AiEntry? question, float width)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        // 已读取: what this answer was based on, with the exact text that was sent.
        if (conversation.Mode == AiMode.Diagnose && question?.Context is { } context)
        {
            var view = text.Get("查看内容", "View");
            var viewWidth = DactUi.ButtonWidth(view, ButtonKind.Ghost);
            var row = ImGui.GetCursorScreenPos();
            DactIcons.Draw(ImGui.GetWindowDrawList(), "eye", row + new Vector2(0, 8 * s), 13 * s, palette.Muted);
            ImGui.SetCursorScreenPos(row + new Vector2(19 * s, 6 * s));
            DactUi.Wrapped(ContextNote(conversation.Mode), DactUi.HintColor, DactFonts.Small, width - 19 * s - viewWidth - 8 * s);
            var after = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(new Vector2(row.X + width - viewWidth, row.Y));
            if (DactUi.Button(view + $"##ai-view-{conversation.Mode}-{conversation.Entries.IndexOf(question)}", ButtonKind.Ghost))
            {
                aiContextShown = context;
                DactUi.OpenDialog(AiContextDialog);
            }
            ImGui.SetCursorScreenPos(new Vector2(row.X, Math.Max(after.Y, row.Y + 30 * s + ImGui.GetStyle().ItemSpacing.Y)));
        }
        if (reply.Text.Length > 0) DactUi.Wrapped(reply.Text, palette.Text, DactFonts.Body, width);
        for (var index = 0; index < reply.Findings.Count; index++) DrawFinding(conversation, reply.Findings[index], width, index);
        for (var index = 0; index < reply.Advice.Count; index++) DrawAdvice(index + 1, reply.Advice[index], width);
        DrawReplyActions(conversation, reply, conversation.Entries.Count.ToString());
        if (reply.Sources.Count > 0)
        {
            var row = ImGui.GetCursorScreenPos();
            DactIcons.Draw(ImGui.GetWindowDrawList(), "help", row + new Vector2(0, 1 * s), 13 * s, palette.Muted);
            ImGui.SetCursorScreenPos(row + new Vector2(19 * s, 0));
            DactUi.Wrapped(text.Get("依据：", "Based on: ") + string.Join("、", reply.Sources), DactUi.HintColor, DactFonts.Small, width - 19 * s);
        }
    }

    private string ContextNote(AiMode mode) => mode switch
    {
        AiMode.Diagnose => AiConfig.AttachDiagnosticLog
            ? text.Get("已读取：运行状态、扩展权限、最近 200 行诊断日志", "Read: runtime status, extension permissions, latest 200 log lines")
            : text.Get("已读取：运行状态、扩展权限", "Read: runtime status, extension permissions"),
        AiMode.Review => text.Get("已读取：这场战斗的统计数据", "Read: this fight's numbers"),
        _ => text.Get("已读取：设置目录和帮助主题", "Read: settings catalog and help topics"),
    } + (AiConfig.StripNames ? text.Get("（角色名已去除）", " (names removed)") : string.Empty);

    // The fix buttons the answer offers; 发给开发者 joins them when the problem needs the developer.
    private void DrawReplyActions(AiConversation conversation, AiReply reply, string scope)
    {
        var s = DactUi.S;
        var actions = reply.Actions.Take(3).ToList();
        var first = true;
        void Gap(float width)
        {
            if (!first) DactUi.SameLineIfFits(width);
            first = false;
        }
        if (reply.Escalate)
        {
            var send = text.Get("发给开发者", "Send to developer");
            Gap(DactUi.ButtonWidth(send, ButtonKind.Primary, "send"));
            if (DactUi.Button(send + $"##ai-escalate-{scope}", ButtonKind.Primary, "send")) SendAiToDeveloper(conversation);
            var again = text.Get("再试一次", "Try again");
            Gap(DactUi.ButtonWidth(again));
            if (DactUi.Button(again + $"##ai-again-{scope}", disabled: conversation.Busy)) RetryAi(conversation);
        }
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            var external = action.Id.StartsWith("setting:", StringComparison.Ordinal) || action.Id.StartsWith("open", StringComparison.Ordinal);
            // Jumps elsewhere are buttons with the external mark; help links are quieter ghost buttons.
            var kind = action.Id.StartsWith("help:", StringComparison.Ordinal) ? ButtonKind.Ghost : ButtonKind.Secondary;
            var icon = external ? "external" : null;
            Gap(DactUi.ButtonWidth(action.Label, kind, icon));
            if (DactUi.Button(action.Label + $"##ai-action-{scope}-{index}", kind, icon)) RunAiAction(action.Id, conversation);
        }
        _ = s;
    }

    private void DrawFinding(AiConversation conversation, AiFinding finding, float width, int index)
    {
        var s = DactUi.S;
        var (tone, label) = finding.Level switch
        {
            "error" => (PillTone.Error, text.Get("错误", "Error")),
            "warn" => (PillTone.Warn, text.Get("提醒", "Warning")),
            _ => (PillTone.Ok, text.Get("提示", "Note")),
        };
        var pillWidth = DactUi.PillWidth(label, dot: false);
        var buttonWidth = finding.Action is { } action ? DactUi.ButtonWidth(action.Label) : 0;
        var inner = width - 24 * s - pillWidth - 10 * s - (buttonWidth > 0 ? buttonWidth + 10 * s : 0);
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(12 * s + pillWidth + 10 * s, 10 * s));
        ImGui.BeginGroup();
        using (DactUi.Gap(2))
        {
            DactUi.Wrapped(finding.Title, DactTheme.Palette.Text, DactFonts.Body, inner);
            if (finding.Detail.Length > 0) DactUi.Wrapped(finding.Detail, DactUi.HintColor, DactFonts.Small, inner);
        }
        ImGui.EndGroup();
        var height = Math.Max(ImGui.GetItemRectMax().Y - start.Y + 10 * s, 50 * s);
        DactUi.DrawPill(draw, new Vector2(start.X + 12 * s, start.Y + (height - 22 * s) * .5f), label, tone, dot: false);
        if (finding.Action is { } fix)
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X + width - 12 * s - buttonWidth, start.Y + (height - 30 * s) * .5f));
            if (DactUi.Button(fix.Label + $"##ai-fix-{conversation.Entries.Count}-{index}")) RunAiAction(fix.Id, conversation);
        }
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(DactUi.Field), 8 * s);
        draw.AddRect(start, start + new Vector2(width, height), DactUi.U32(DactUi.FieldBorder), 8 * s, ImDrawFlags.None, Math.Max(1, s));
        draw.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        ImGui.Dummy(new Vector2(width, 0));
    }

    private void DrawAiThinking(AiConversation conversation, float width)
    {
        var s = DactUi.S;
        DrawAiMessage(width, () =>
        {
            var row = ImGui.GetCursorScreenPos();
            var label = text.Get("正在分析", "Thinking");
            var draw = ImGui.GetWindowDrawList();
            DactUi.DrawText(draw, row, 20 * s, label, DactTheme.Palette.Muted);
            // Three dots that pulse one after another while the request is out.
            var x = row.X + DactUi.TextWidth(label) + 8 * s;
            var time = (float)ImGui.GetTime();
            for (var dot = 0; dot < 3; dot++)
            {
                var phase = (MathF.Sin(time * 5 - dot * .9f) + 1) * .5f;
                draw.AddCircleFilled(new Vector2(x + dot * 9 * s, row.Y + 11 * s - phase * 2 * s), 2.5f * s,
                    DactUi.U32(DactTheme.Palette.Accent with { W = .35f + .65f * phase }), 12);
            }
            var waited = ImGui.GetTime() - conversation.BusySince;
            if (waited > 6)
                DactUi.DrawText(draw, new Vector2(x + 34 * s, row.Y), 20 * s,
                    text.Format($"已等待 {waited:0} 秒", $"{waited:0}s"), DactUi.HintColor, DactFonts.Small);
            ImGui.Dummy(new Vector2(width - 36 * s, 20 * s));
        });
    }

    private void DrawAiError(AiConversation conversation, string error)
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        DrawAiMessage(width, () =>
        {
            DactUi.Pill(text.Get("没有完成", "Not finished"), PillTone.Error, dot: false);
            DactUi.Wrapped(error, DactTheme.Palette.Text, DactFonts.Body, width - 36 * s);
            if (DactUi.Button(text.Get("再试一次", "Try again") + $"##ai-retry-{conversation.Mode}", icon: "refresh")) RetryAi(conversation);
            if (conversation.Mode == AiMode.Diagnose)
            {
                ImGui.SameLine(0, 8 * s);
                if (DactUi.Button(text.Get("AI 设置", "AI settings") + "##ai-error-settings", ButtonKind.Ghost)) aiSettingsPage = true;
            }
        });
    }

    // 每次发送前先让我确认: the turn waits here with what would be sent.
    private void DrawAiConfirm(AiConversation conversation)
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        var pending = conversation.PendingConfirm!;
        DrawAiMessage(width, () =>
        {
            DactUi.Wrapped(text.Get("下面的内容会发给你的 AI 服务，确认后再发送。", "The content below goes to your AI service once you confirm."),
                DactTheme.Palette.Text, DactFonts.Body, width - 36 * s);
            if (DactUi.Button(text.Get("发送", "Send") + $"##ai-confirm-{conversation.Mode}", ButtonKind.Primary, "send")) ConfirmAiSend(conversation);
            ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(text.Get("查看内容", "View") + $"##ai-confirm-view-{conversation.Mode}"))
            {
                aiContextShown = pending.ModelText;
                DactUi.OpenDialog(AiContextDialog);
            }
            ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(text.Get("取消", "Cancel") + $"##ai-confirm-cancel-{conversation.Mode}", ButtonKind.Ghost)) CancelAiSend(conversation);
        });
    }

    private void DrawAiNotConnected()
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        DrawAiMessage(width, () =>
        {
            DactUi.Wrapped(text.Get("AI 服务还没有连接成功：先到 AI 设置里粘贴 API Key，连接会自动测试。", "Your AI service is not connected yet. Paste your API key in AI settings; the connection is tested automatically."),
                DactTheme.Palette.Text, DactFonts.Body, width - 36 * s);
            if (DactUi.Button(text.Get("去 AI 设置", "Open AI settings") + "##ai-goto-settings", ButtonKind.Primary, "gear")) aiSettingsPage = true;
        });
    }

    private AiMode? aiRefocusInput;

    // Input row: returns the question when sent (empty when allowEmpty and nothing was typed).
    private string? DrawAiInput(AiConversation conversation, string placeholder, string sendLabel, string icon, bool allowEmpty)
    {
        var s = DactUi.S;
        var buttonWidth = DactUi.ButtonWidth(sendLabel, ButtonKind.Primary, icon);
        var ready = AiConfig.Ready && !conversation.Busy && conversation.PendingConfirm is null;
        if (aiRefocusInput == conversation.Mode)
        {
            ImGui.SetKeyboardFocusHere();
            aiRefocusInput = null;
        }
        // Enter as the field reports it while typing (see DactUi.Input: ImGui's Enter can stay
        // stuck down after a send, and polling it re-focused the field over and over).
        var submitted = DactUi.Input($"ai-input-{conversation.Mode}", ref conversation.Input, placeholder, 2000,
            width: ImGui.GetContentRegionAvail().X - buttonWidth - 8 * s, submitOnEnter: true);
        ImGui.SameLine(0, 8 * s);
        var typed = conversation.Input.Trim();
        var clicked = DactUi.Button(sendLabel + $"##ai-send-{conversation.Mode}", ButtonKind.Primary, icon,
            disabled: !ready || (!allowEmpty && typed.Length == 0));
        // Enter ends ImGui's editing and a click on the button takes the focus; the field keeps
        // the keyboard either way, so a follow-up question is typed straight away.
        if (submitted || clicked) aiRefocusInput = conversation.Mode;
        if (!(clicked || submitted && (allowEmpty || typed.Length > 0)) || !ready) return null;
        conversation.Input = string.Empty;
        return typed;
    }

    private void DrawAiContextDialog()
    {
        var s = DactUi.S;
        if (!DactUi.BeginDialog(AiContextDialog, 560)) return;
        DactUi.Text(text.Get("发给 AI 的内容", "Sent to the AI"), size: DactFonts.H2);
        DactUi.Hint(text.Get("这是发出的原文；开启「去掉角色名和服务器名」时，名字已替换为 <角色>。",
            "This is the exact text sent; with name removal on, names are replaced by <角色>."));
        var content = aiContextShown ?? string.Empty;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, DactUi.Field);
        ImGui.InputTextMultiline("##ai-context-text", ref content, content.Length + 1, new Vector2(-1, 320 * s), ImGuiInputTextFlags.ReadOnly);
        ImGui.PopStyleColor();
        var close = text.Get("关闭", "Close");
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - DactUi.ButtonWidth(close));
        if (DactUi.Button(close + "##ai-context-close")) DactUi.CloseDialog(AiContextDialog);
        DactUi.EndDialog(AiContextDialog);
    }

    // ---- 未开启

    private bool DrawAiOffPage()
    {
        var s = DactUi.S;
        var settingsLabel = text.Get("AI 设置", "AI settings");
        DactUi.PageHeader(text.Get("AI 助手", "AI assistant"),
            text.Get("诊断问题、复盘你自己的战斗、回答使用问题。", "Troubleshoot, review your own fights and answer questions."),
            DactUi.ButtonWidth(settingsLabel, icon: "gear"),
            () =>
            {
                if (DactUi.Button(settingsLabel + "##ai-off-settings", icon: "gear")) aiSettingsPage = true;
            });
        DactUi.BeginPanel("ai-off", new Vector2(-1, Math.Max(300 * s, ImGui.GetContentRegionAvail().Y)), 14, 12);
        DactUi.Text(text.Get("AI 助手还没有开启", "The AI assistant is off"), DactTheme.Palette.Gold);
        DactUi.Hint(text.Get("DACT 不提供 AI 接口：开启后填入你自己的 API Key，请求从你的电脑直接发到你选择的服务商，Key 只保存在本机。",
            "DACT does not provide an AI service: turn it on and enter your own API key. Requests go straight from your PC to the provider you choose, and the key stays on this PC."));
        AiFeatureRow("wrench", text.Get("诊断问题", "Diagnose problems"),
            text.Get("读取运行状态、扩展权限和诊断日志，找出问题并给出能直接点的修复按钮。", "Reads runtime status, extension permissions and logs, finds the problem and offers buttons that fix it."));
        AiFeatureRow("meter", text.Get("战斗复盘", "Fight review"),
            text.Get("只分析你自己的战斗：输出、暴击直击、死亡，以及开荒打到哪里、在哪里团灭。", "Analyses only your own fights: damage, crits and direct hits, deaths, and how far progression got and where it wiped."));
        AiFeatureRow("help", text.Get("使用问答", "Questions"),
            text.Get("根据帮助文档和你当前的设置回答，并能直接跳到对应设置。", "Answers from the help and your current settings, and jumps straight to the setting."));
        var enable = text.Get("开启并设置", "Turn on and set up");
        if (DactUi.Button(enable + "##ai-enable", ButtonKind.Primary, "sparkle"))
        {
            AiConfig.Enabled = true;
            saveConfiguration();
            aiSettingsPage = true;
        }
        ImGui.SameLine(0, 10 * s);
        var hint = text.Get("第一次开启会一步步带你拿到 API Key。", "The first time, it walks you through getting an API key.");
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 7 * s);
        DactUi.Text(hint, DactUi.HintColor, DactFonts.Small);
        DactUi.EndPanel();
        return false;
    }

    private void AiFeatureRow(string icon, string title, string detail)
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        var inner = width - 24 * s - 32 * s - 12 * s;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(12 * s + 32 * s + 12 * s, 10 * s));
        ImGui.BeginGroup();
        using (DactUi.Gap(2))
        {
            DactUi.Text(title);
            DactUi.Wrapped(detail, DactUi.HintColor, DactFonts.Small, inner);
        }
        ImGui.EndGroup();
        var height = Math.Max(ImGui.GetItemRectMax().Y - start.Y + 10 * s, 52 * s);
        var badge = start + new Vector2(12 * s, (height - 32 * s) * .5f);
        draw.AddRectFilled(badge, badge + new Vector2(32 * s), DactUi.U32(DactUi.AccentSoft), 8 * s);
        DactIcons.Draw(draw, icon, badge + new Vector2(8 * s), 16 * s, DactTheme.Palette.Accent);
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(DactUi.Field), 8 * s);
        draw.AddRect(start, start + new Vector2(width, height), DactUi.U32(DactUi.FieldBorder), 8 * s, ImDrawFlags.None, Math.Max(1, s));
        draw.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        ImGui.Dummy(new Vector2(width, 0));
    }

    // ---- AI 设置

    private bool DrawAiSettingsPage()
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var settings = AiConfig;
        var changed = false;
        var back = text.Get("返回 AI 助手", "Back to AI assistant");
        DactUi.PageHeader(text.Get("AI 设置", "AI settings"),
            text.Get("AI 助手 / AI 设置 · 默认关闭；开启后使用你自己的 AI 服务和 API Key。", "AI assistant / AI settings · Off by default; uses your own AI service and API key."),
            DactUi.ButtonWidth(back),
            () =>
            {
                if (DactUi.Button(back + "##ai-settings-back")) aiSettingsPage = false;
            });

        var wide = ImGui.GetContentRegionAvail().X >= 640 * s;
        ImGui.BeginChild("ai-settings-scroll", new Vector2(-1, PageContentHeight()), false);
        if (wide)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(7 * s, 0));
            ImGui.BeginTable("ai-settings-columns", 2, ImGuiTableFlags.NoPadOuterX);
            ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("right", ImGuiTableColumnFlags.WidthFixed, 300 * s);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
        }
        using (DactUi.Gap(14))
        {
            DactUi.BeginCard("ai-switch", text.Get("开关", "Switch"));
            var enabled = settings.Enabled;
            if (DactUi.ToggleRow(text.Get("启用 AI 助手", "Enable AI assistant"), ref enabled,
                    text.Get("关闭后不读取任何日志、不发出请求，各页面里的「让 AI 诊断」「AI 复盘」入口也会隐藏；已保存的 Key 仍留在本机。",
                        "When off, nothing is read or sent and the AI buttons on other pages are hidden; a saved key stays on this PC.")))
            {
                settings.Enabled = enabled;
                changed = true;
            }
            DactUi.EndCard();

            DactUi.BeginCard("ai-connection", text.Get("连接你的 AI 服务", "Connect your AI service"));
            DactUi.Hint(text.Get("DACT 不提供 AI 接口，也不经手你的 Key：请求从你的电脑直接发到你选择的服务商，费用由服务商按你的账户计算。",
                "DACT provides no AI service and never handles your key: requests go straight from your PC to your provider, which bills your account."));
            var current = AiProviders.Get(settings.Provider);
            if (DactUi.BeginGrid("ai-provider-grid", 12))
            {
                DactUi.BeginField(text.Get("服务商", "Provider"));
                var providers = AiProviders.All.Select(item => text.Get(item.NameZh, item.NameEn)).ToArray();
                var provider = AiProviders.All.ToList().IndexOf(current);
                if (DactUi.Select("ai-provider", providers, ref provider))
                {
                    // Keys are kept per provider, so switching back later needs no new paste.
                    settings.Provider = AiProviders.All[provider].Id;
                    settings.Endpoint = string.Empty;
                    aiKeyDraft = string.Empty;
                    InvalidateAiConnection(clearModels: true);
                    current = AiProviders.All[provider];
                    if (AiKeys?.Has(current.Id) == true || current.KeyOptional) ScheduleAiTest(0);
                }
                DactUi.EndField();
                DactUi.NextColumn();
                DactUi.BeginField(text.Get("模型", "Model"));
                var recommended = AiClient.RecommendedModel(settings);
                var models = new List<string> { text.Get("推荐（自动选择）", "Recommended (automatic)") };
                models.AddRange(settings.KnownModels);
                if (settings.Model.Length > 0 && !settings.KnownModels.Contains(settings.Model)) models.Add(settings.Model);
                var model = settings.Model.Length == 0 ? 0 : models.IndexOf(settings.Model);
                if (settings.KnownModels.Count == 0 && current.Protocol != AiProtocol.Claude)
                {
                    // Until a test lists the service's models (and for services that list none), the model name is typed.
                    var typed = settings.Model;
                    var example = current.Fallback?.FirstOrDefault() ?? "deepseek-v4-flash";
                    if (DactUi.Input("ai-model-name", ref typed, text.Format($"模型名称，可留空；例如 {example}", $"Model name, optional; e.g. {example}"), 120))
                    {
                        settings.Model = typed.Trim();
                        changed = true;
                    }
                }
                else if (DactUi.Select("ai-model", models.ToArray(), ref model))
                {
                    settings.Model = model == 0 ? string.Empty : models[model];
                    changed = true;
                }
                // The recommended model shows on hover, keeping the two fields level as in the design.
                if (settings.Model.Length == 0 && recommended.Length > 0 && ImGui.IsItemHovered())
                    DactUi.Tooltip(text.Format($"推荐：{recommended}", $"Recommended: {recommended}"));
                DactUi.EndField();
                DactUi.EndGrid();
            }

            DactUi.BeginField("API Key");
            var keyHint = current.KeyHint;
            var placeholder = AiKeys?.Has(current.Id) == true
                ? text.Get("已保存在本机；粘贴新的 Key 可替换", "Saved on this PC; paste a new key to replace it")
                : current.KeyOptional
                    ? text.Get("本地模型不需要 Key，可留空", "Local models need no key; leave empty")
                    : keyHint.Length > 0
                        ? text.Format($"粘贴你的 API Key（{keyHint}）", $"Paste your API key ({keyHint})")
                        : text.Get("粘贴你的 API Key", "Paste your API key");
            if (DactUi.Input("ai-key", ref aiKeyDraft, placeholder, 400, secret: true))
            {
                InvalidateAiConnection(clearModels: false);
                ScheduleAiTest(.8);
            }
            DactUi.EndField(text.Get("用 Windows 账户加密保存在本机，每个服务商分开保存；不上传到 DACT 服务器，也不进云同步。",
                "Encrypted with your Windows account and kept on this PC, one per provider; never uploaded to DACT servers or cloud sync."));

            DactUi.BeginField(text.Get("接口地址", "Endpoint"));
            var endpoint = settings.Endpoint;
            var defaultEndpoint = current.Endpoint;
            var endpointHint = defaultEndpoint.Length > 0
                ? text.Format($"默认 {defaultEndpoint}", $"Default {defaultEndpoint}")
                : text.Get("例如 https://api.deepseek.com/v1", "e.g. https://api.deepseek.com/v1");
            if (DactUi.Input("ai-endpoint", ref endpoint, endpointHint, 300))
            {
                settings.Endpoint = endpoint.Trim();
                InvalidateAiConnection(clearModels: true);
                ScheduleAiTest(.8);
            }
            DactUi.EndField(text.Get("留空即用服务商的官方地址；使用中转或代理时填写它的地址。",
                "Leave empty for the provider's official address; enter a relay's or proxy's address to use one."));

            RunScheduledAiTest();
            var test = text.Get("重新测试", "Test again");
            if (DactUi.Button(test + "##ai-test", ButtonKind.Secondary, "refresh", disabled: aiTesting)) TestAiConnection();
            ImGui.SameLine(0, 8 * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4 * s);
            var (status, tone) = aiTesting || aiAutoTestAt >= 0
                ? (text.Get("正在测试…", "Testing…"), PillTone.Warn)
                : aiTestMessage is { } message
                    ? (message, aiTestFailed ? PillTone.Error : PillTone.Ok)
                    : settings.Verified
                        ? (text.Get("连接正常", "Connected"), PillTone.Ok)
                        : (text.Get("填入 Key 后会自动测试", "Tests automatically once a key is in"), PillTone.Off);
            DactUi.Pill(status, tone, maxWidth: ImGui.GetContentRegionAvail().X);
            DactUi.Hint(text.Get("测试通过后，侧栏和各页面才会出现 AI 入口。", "AI buttons appear on other pages once the test passes."));
            DactUi.EndCard();

            DactUi.BeginCard("ai-sent", text.Get("发给 AI 的内容", "What is sent to the AI"));
            var strip = settings.StripNames;
            if (DactUi.ToggleRow(text.Get("去掉角色名和服务器名", "Remove character and world names"), ref strip, text.Get("默认开启，建议保留。", "On by default; best left on.")))
            {
                settings.StripNames = strip;
                changed = true;
            }
            var log = settings.AttachDiagnosticLog;
            if (DactUi.ToggleRow(text.Get("诊断时附带最近 200 行诊断日志", "Include the latest 200 log lines when diagnosing"), ref log))
            {
                settings.AttachDiagnosticLog = log;
                changed = true;
            }
            // Job notes are no longer optional: a review without them can only guess at rotations.
            DactUi.Hint(text.Get("复盘时还会附带：这场战斗你的技能记录（从本机战斗日志读取，只有你自己的技能和队伍团辅时间）和你所玩职业的要点（参考 xivanalysis 警察网的检查规则）。",
                "Reviews also include your casts in that fight (read from the local combat log: only your own actions and the party's raid buff times) and notes for your job (based on xivanalysis checks)."));
            var confirm = settings.ConfirmBeforeSend;
            if (DactUi.ToggleRow(text.Get("每次发送前先让我确认", "Ask me before each request"), ref confirm))
            {
                settings.ConfirmBeforeSend = confirm;
                changed = true;
            }
            DactUi.EndCard();
        }

        if (wide) ImGui.TableSetColumnIndex(1);
        DrawAiKeyGuide();
        if (wide)
        {
            ImGui.EndTable();
            ImGui.PopStyleVar();
        }
        // Room under the last card, like the bottom padding of every other page; without it the
        // card's lower border sat on the window edge when scrolled to the end.
        ImGui.Dummy(new Vector2(1, 20 * s));
        ImGui.EndChild();
        _ = palette;
        return changed;
    }

    private void DrawAiKeyGuide()
    {
        var provider = AiProviders.Get(AiConfig.Provider);
        var name = text.Get(provider.NameZh, provider.NameEn);
        DactUi.BeginCard("ai-key-guide", text.Get("怎么获取 API Key", "Getting an API key"));
        string[] steps;
        switch (provider.Id)
        {
            case "local":
                steps =
                [
                    text.Get("安装并打开 Ollama 或 LM Studio，下载一个模型。", "Install Ollama or LM Studio and download a model."),
                    text.Get("确认本地服务已经启动；LM Studio 要在开发者页里启动服务器。", "Make sure the local server runs; in LM Studio, start it on the Developer page."),
                    text.Get("「接口地址」留空即为 Ollama；LM Studio 填 http://localhost:1234/v1。不需要 Key。", "Leave Endpoint empty for Ollama; for LM Studio enter http://localhost:1234/v1. No key needed."),
                    text.Get("连接会自动测试，成功后从「模型」里选一个已下载的模型。", "The connection is tested automatically; then pick a downloaded model under Model."),
                ];
                break;
            case AiProviders.Compatible:
                DactUi.Hint(text.Get("兼容接口的服务商各不相同，一般步骤如下。", "Compatible providers differ; the usual steps are:"));
                steps =
                [
                    text.Get("在服务商的控制台注册并登录。", "Sign in to the provider's console."),
                    text.Get("创建 API Key 并复制，记下它的接口地址和模型名称。", "Create and copy an API key; note its endpoint and model name."),
                    text.Get("回到左边填写「接口地址」，粘贴 Key。", "Fill in Endpoint on the left and paste the key."),
                    text.Get("连接会自动测试；没有模型列表的服务需要先填写模型名称。", "The connection is tested automatically; services without a model list need the model name first."),
                ];
                break;
            default:
                steps =
                [
                    text.Format($"打开 {name} 的控制台，注册并登录。", $"Open the {name} console and sign in."),
                    text.Get("创建一个新的 API Key 并复制（通常只显示一次）。", "Create a new API key and copy it (usually shown once)."),
                    text.Get("充值或开通计费，建议同时设置用量上限。", "Add credit or enable billing, and set a spending limit."),
                    text.Get("回到左边粘贴到「API Key」，连接会自动测试；模型列表会从服务商实时获取。", "Paste it into API Key on the left; the connection is tested automatically and the model list comes live from the provider."),
                ];
                break;
        }
        if (provider.NoteZh is { } note) DactUi.Hint(text.Get(note, provider.NoteEn ?? note));
        DactUi.StepsPanel(steps);
        if (provider.ConsoleUrl.Length > 0 &&
            DactUi.Button(text.Format($"打开 {name} 控制台", $"Open the {name} console") + "##ai-console", icon: "external", width: ImGui.GetContentRegionAvail().X))
            OpenUrl(provider.ConsoleUrl);
        DactUi.Divider();
        DactUi.Text(text.Get("常见问题", "Common problems"), DactTheme.Palette.Muted, DactFonts.Small);
        DactUi.Hint(text.Get("测试失败：多半是 Key 复制不完整，或账户余额为 0。", "Test fails: usually the key was copied incompletely or the balance is 0."));
        DactUi.Hint(text.Get("想换服务商：选另一个服务商即可，存过 Key 的会自动重新测试；每个服务商的 Key 分开保存，切回来不用重新粘贴。",
            "Switching provider: just pick another one; one with a saved key is tested again automatically. Keys are kept per provider, so switching back needs no new paste."));
        DactUi.EndCard();
    }
}
