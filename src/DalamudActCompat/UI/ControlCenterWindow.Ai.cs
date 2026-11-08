using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Ai;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// AI 助手 (features.js aiDiagnose / aiReview / aiQA / aiSettings / aiOff): state, what is sent
// and what the replies' buttons do. Drawing lives in ControlCenterWindow.AiPages.cs.
//
// Requests go from this PC straight to the provider the player chose, with their own key.
// Before anything is sent, the character name, home world and (for reviews) teammates' names
// are replaced with placeholders when 去掉角色名和服务器名 is on, which it is by default.
public sealed partial class ControlCenterWindow
{
    // Set by the plugin.
    internal Func<string>? BuildDiagnosticReport { get; set; }
    internal Func<(string Name, string World)?>? LocalIdentity { get; set; }
    internal Action<string>? RestartHost { get; set; }
    internal AiKeyStore? AiKeys { get; set; }
    internal Func<IReadOnlyList<(string Topic, string Title, string Summary)>>? HelpOutline { get; set; }
    // Game data for reviews: action names, GCD flag, recast and charges from the player's client.
    internal Func<uint, AiActionInfo?>? ActionInfo { get; set; }
    internal Func<uint, string?>? StatusName { get; set; }
    // Where FFXIV_ACT_Plugin is writing its network log right now.
    internal Func<string>? ActiveCombatLogDirectory { get; set; }

    private sealed record AiEntry(bool FromUser, string Display, string ModelText, AiReply? Reply = null, string? Context = null);

    private sealed class AiConversation(AiMode mode)
    {
        public AiMode Mode { get; } = mode;
        public List<AiEntry> Entries { get; } = [];
        public bool Busy { get; set; }
        public double BusySince { get; set; }
        public string? Error { get; set; }
        public string Input = string.Empty;
        public CancellationTokenSource? Cancel { get; set; }
        // The user turn waiting for 每次发送前先让我确认.
        public AiEntry? PendingConfirm { get; set; }
        public int Revision { get; set; }
        // 战斗复盘: which fight this conversation is about, and the notes for the player's job in it.
        public Guid? EncounterId { get; set; }
        public string? JobKnowledge { get; set; }

        public void Reset()
        {
            Cancel?.Cancel();
            Cancel = null;
            Entries.Clear();
            Busy = false;
            Error = null;
            PendingConfirm = null;
            EncounterId = null;
            JobKnowledge = null;
            Revision++;
        }
    }

    private int aiTab;
    private bool aiSettingsPage;
    private readonly AiConversation[] aiConversations = [new(AiMode.Diagnose), new(AiMode.Review), new(AiMode.Ask)];
    // Results arrive on worker threads and are applied on the draw thread.
    private readonly ConcurrentQueue<Action> aiResults = new();
    private string? aiContextShown;

    private AiSettings AiConfig => configuration.Ai;

    // 战斗记录 entries: present only once AI is on and its connection was tested.
    private Action<Encounter>? ReviewEncounter => AiConfig.Ready ? StartAiReview : null;
    private Action<uint>? SummarizeProgression => AiConfig.Ready ? StartAiProgressionSummary : null;

    public void OpenAi(bool settings = false)
    {
        selectedPage = Page.Ai;
        aiSettingsPage = settings;
    }

    private void PumpAiResults()
    {
        while (aiResults.TryDequeue(out var apply)) apply();
    }

    // ---- Sending

    // buildContext runs for the first turn of a conversation, or for every turn with alwaysContext
    // (a diagnose re-check needs the runtime state as it is now, not as it was).
    private void SendAi(AiConversation conversation, string display, string question, Func<string>? buildContext, bool alwaysContext = false)
    {
        if (conversation.Busy || !AiConfig.Ready) return;
        conversation.Error = null;
        var names = PrivateNames(conversation.Mode == AiMode.Review ? conversation.EncounterId : null);
        var strip = AiConfig.StripNames;
        var first = conversation.Entries.Count == 0 || alwaysContext;
        conversation.Busy = true;
        conversation.BusySince = ImGui.GetTime();
        conversation.Revision++;
        var confirm = AiConfig.ConfirmBeforeSend;
        _ = Task.Run(() =>
        {
            // Context builders read log files, so they run here rather than on the draw thread.
            string modelText;
            string? context = null;
            try
            {
                context = first && buildContext is not null ? buildContext() : null;
                if (context is not null && strip) context = Redact(context, names);
                modelText = context is null ? question : $"{context}\n\n---\n{question}";
            }
            catch (Exception exception)
            {
                aiResults.Enqueue(() =>
                {
                    conversation.Busy = false;
                    conversation.Error = text.Format($"读取本机数据失败：{exception.Message}", $"Could not read local data: {exception.Message}");
                });
                return;
            }
            var entry = new AiEntry(true, display, modelText, Context: context);
            aiResults.Enqueue(() =>
            {
                conversation.Entries.Add(entry);
                conversation.Revision++;
                if (confirm)
                {
                    conversation.PendingConfirm = entry;
                    conversation.Busy = false;
                    return;
                }
                DispatchAi(conversation);
            });
        });
    }

    private void ConfirmAiSend(AiConversation conversation)
    {
        if (conversation.PendingConfirm is null) return;
        conversation.PendingConfirm = null;
        conversation.Busy = true;
        conversation.BusySince = ImGui.GetTime();
        DispatchAi(conversation);
    }

    private void CancelAiSend(AiConversation conversation)
    {
        if (conversation.PendingConfirm is { } pending) conversation.Entries.Remove(pending);
        conversation.PendingConfirm = null;
        conversation.Revision++;
    }

    // Sends the conversation as it stands; its last entry is the player's turn.
    private void DispatchAi(AiConversation conversation)
    {
        var settings = AiConfig;
        var key = AiKeys?.Load(settings.Provider) ?? string.Empty;
        if (key.Length == 0 && !AiProviders.Get(settings.Provider).KeyOptional)
        {
            conversation.Busy = false;
            conversation.Error = text.Get("没有找到 API Key，请在 AI 设置里重新粘贴并测试连接。", "No API key found. Paste it again in AI settings and test the connection.");
            return;
        }
        var call = new AiCall(
            AiPrompts.System(conversation.Mode, text.Language, conversation.Mode == AiMode.Review ? conversation.JobKnowledge : null),
            conversation.Entries.Select(entry => new AiTurn(entry.FromUser, entry.ModelText)).ToArray(),
            conversation.Mode == AiMode.Ask ? "low" : "medium");
        var cancel = new CancellationTokenSource();
        conversation.Cancel = cancel;
        conversation.Busy = true;
        var revision = conversation.Revision;
        _ = Task.Run(async () =>
        {
            try
            {
                var raw = await AiClient.CompleteAsync(settings, key, call, cancel.Token).ConfigureAwait(false);
                var parsed = AiReplyFormat.Parse(raw);
                aiResults.Enqueue(() =>
                {
                    // 新对话 while waiting: the old answer is dropped.
                    if (conversation.Cancel != cancel) return;
                    // Filtered here on the draw thread, where the settings catalog is built.
                    var reply = WithAllowedActions(parsed, conversation.Mode);
                    conversation.Entries.Add(new AiEntry(false, reply.Text, raw, reply));
                    conversation.Busy = false;
                    conversation.Cancel = null;
                    conversation.Revision++;
                });
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                var message = exception is AiServiceException ? exception.Message : text.Format($"请求失败：{exception.Message}", $"Request failed: {exception.Message}");
                aiResults.Enqueue(() =>
                {
                    if (conversation.Cancel != cancel) return;
                    conversation.Busy = false;
                    conversation.Cancel = null;
                    conversation.Error = message;
                    conversation.Revision++;
                });
            }
        });
    }

    // 再试一次: resend the last turn (after an error), or ask again after an unhelpful answer.
    private void RetryAi(AiConversation conversation)
    {
        if (conversation.Busy) return;
        conversation.Error = null;
        if (conversation.Entries.Count == 0) return;
        if (!conversation.Entries[^1].FromUser)
        {
            var again = text.Get("我照做了，请再检查一次。", "I did that; please check again.");
            SendAi(conversation, again, again, conversation.Mode == AiMode.Diagnose ? BuildDiagnoseContext : null, conversation.Mode == AiMode.Diagnose);
            return;
        }
        conversation.Busy = true;
        conversation.BusySince = ImGui.GetTime();
        conversation.Revision++;
        DispatchAi(conversation);
    }

    // ---- What gets sent

    private HashSet<string> PrivateNames(Guid? encounterId)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            names.Add(name.Trim());
            // "First Last" names also leak through either half on its own.
            foreach (var part in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (part.Length >= 4) names.Add(part);
        }
        if (LocalIdentity?.Invoke() is { } identity)
        {
            Add(identity.Name);
            Add(identity.World);
        }
        if (encounterId is { } id && FindRecentEncounter(id) is { } encounter)
            foreach (var combatant in encounter.Combatants) Add(combatant.Name);
        return names;
    }

    private static string Redact(string value, IEnumerable<string> names)
    {
        // Longest first, so "First Last" is replaced before "First" alone.
        foreach (var name in names.OrderByDescending(name => name.Length))
            value = value.Replace(name, "<角色>", StringComparison.Ordinal);
        return value;
    }

    private string BuildDiagnoseContext()
    {
        var context = new StringBuilder();
        context.AppendLine("[DACT runtime snapshot]");
        if (HostSnapshots?.Invoke() is { } hosts)
        {
            string[] names = ["shared Host (共享 Host)", "Matcha Host (抹茶 Host)", "generic Host (通用 Host)"];
            for (var index = 0; index < hosts.Count && index < names.Length; index++)
                context.AppendLine(DescribeHost(names[index], hosts[index]));
        }
        context.AppendLine();
        context.AppendLine("[Extensions and permissions]");
        try
        {
            foreach (var plugin in discoverPlugins())
            {
                var requested = plugin.Manifest.RequestedCapabilities
                    .Select(name => Enum.TryParse<ActCapability>(name, true, out var capability) ? capability : (ActCapability?)null)
                    .OfType<ActCapability>()
                    .Select(capability => $"{ActCapabilityDisplay.Label(capability, text)}={(configuration.IsActCapabilityAllowed(plugin.Manifest.Id, capability) ? "granted" : "not granted")}");
                context.AppendLine($"- {plugin.Manifest.Name} ({plugin.Manifest.Id}) v{plugin.DisplayVersion}: {(plugin.Enabled ? "enabled" : "disabled")}; {string.Join(", ", requested)}");
            }
        }
        catch (Exception exception)
        {
            context.AppendLine($"(could not list extensions: {exception.GetType().Name})");
        }

        if (BuildDiagnosticReport?.Invoke() is { } report)
        {
            // The report's summary part as is, then only the newest log lines (the panel promises
            // 最近 200 行): older lines rarely explain a problem the player sees right now.
            var lines = report.ReplaceLineEndings("\n").Split('\n');
            var firstLog = Array.FindIndex(lines, line => line.StartsWith("[Current Dalamud plugin log]", StringComparison.Ordinal));
            var summary = firstLog < 0 ? lines : lines[..firstLog];
            context.AppendLine();
            context.AppendLine("[Diagnostic report summary]");
            foreach (var line in summary.Where(line => !line.StartsWith("Privacy:", StringComparison.Ordinal))) context.AppendLine(line);
            if (AiConfig.AttachDiagnosticLog && firstLog >= 0)
            {
                context.AppendLine("[Recent log lines, newest last]");
                foreach (var line in lines[firstLog..].Where(line => line.Length > 0).TakeLast(200)) context.AppendLine(line);
            }
        }
        return context.ToString();
    }

    private static string DescribeHost(string name, HostSupervisorSnapshot host)
        => $"{name}: state={host.State}; process={(host.ProcessRunning ? "running" : "not running")}; ipc={host.IpcStatus}; " +
           $"health={host.HealthState}{(string.IsNullOrWhiteSpace(host.HealthDetail) ? string.Empty : $" ({host.HealthDetail})")}; dropped={host.DroppedMessages}";

    private Encounter? FindRecentEncounter(Guid id)
        => EncounterHistory?.GetSnapshot().Recent is { } recent ? EncounterWindow.FindRecentEncounter(recent, id) : null;

    // Fights the review can pick from: single fights and every pull inside a duty folder.
    private IReadOnlyList<Encounter> ReviewableEncounters()
        => (EncounterHistory?.GetSnapshot().Recent ?? [])
            .SelectMany(encounter => encounter.SegmentRecords.Count > 0 ? encounter.SegmentRecords.Reverse() : [encounter])
            .Where(encounter => encounter.Combatants.Any(combatant => combatant.IsLocalPlayer))
            .Take(40)
            .ToArray();

    // labels receives each teammate's combat-log name → the label used here (队友N·JOB), so the
    // cast data can name the party's raid buffs without names.
    private string BuildReviewContext(Encounter encounter, Dictionary<string, string>? labels = null)
    {
        var context = new StringBuilder();
        var pull = MatchPull(encounter);
        var zone = LocalizeZone?.Invoke(encounter.TerritoryId, encounter.ZoneName) ?? encounter.ZoneName;
        context.AppendLine("[Fight]");
        context.AppendLine($"Duty: {zone}; enemy: {encounter.EnemyName}; duration {FormatFightDuration(encounter.EffectiveDuration)}");
        if (pull is not null)
        {
            context.AppendLine(pull.Cleared
                ? "Outcome: cleared"
                : $"Outcome: wipe in phase {pull.Phase} with the boss at {pull.BossHpFraction * 100:0}% HP; last boss cast before the wipe: {pull.WipeCast ?? "unknown"}");
            if (pull.FirstDeathSeconds is { } death) context.AppendLine($"First party death at {FormatFightDuration(TimeSpan.FromSeconds(death))}");
            if (Progression is not null)
            {
                var pulls = Progression.Pulls.Where(other => other.TerritoryId == pull.TerritoryId).ToArray();
                context.AppendLine($"This is pull {Array.IndexOf(pulls, pull) + 1} of {pulls.Length} recorded for this duty; best so far: phase {pulls.Max(other => other.Phase)}.");
            }
        }
        context.AppendLine();
        context.AppendLine("[Players: job, DPS, rDPS, crit %, direct hit %, crit+direct %, deaths, FFLogs estimate]");
        var players = encounter.Combatants.Where(combatant => !string.IsNullOrWhiteSpace(combatant.Job))
            .OrderByDescending(combatant => Rate(combatant, encounter)).ToArray();
        var teammate = 0;
        foreach (var player in players)
        {
            var label = player.IsLocalPlayer ? "你 (the player)" : $"队友{++teammate}";
            if (!player.IsLocalPlayer && labels is not null) labels[AiCombatLog.LogName(player.Name)] = $"{label}·{player.Job}";
            context.AppendLine($"- {label}: {player.Job}, DPS {Dps(player, encounter):0}, rDPS {(player.Rdps > 0 ? player.Rdps.ToString("0") : "n/a")}, " +
                               $"{Share(player.CriticalHits, player.DamageHits)}, {Share(player.DirectHits, player.DamageHits)}, {Share(player.CriticalDirectHits, player.DamageHits)}, " +
                               $"deaths {player.Deaths}, " +
                               (player.FflogsPercentile is { } percentile ? $"FFLogs {percentile:0} ({player.FflogsMetric ?? "rdps"})" : "FFLogs n/a") +
                               (string.IsNullOrWhiteSpace(player.HighestDamageAction) ? string.Empty : $", biggest hit {player.HighestDamageAction} {player.HighestDamage:N0}"));
        }
        return context.ToString();

        static string Share(int part, int total) => total <= 0 ? "n/a" : $"{part * 100d / total:0.0}%";
    }

    private string BuildAskContext()
    {
        var context = new StringBuilder();
        context.AppendLine("[DACT settings catalog: number. name — where (current value)]");
        var catalog = AskCatalog();
        for (var index = 0; index < catalog.Count; index++)
        {
            var entry = catalog[index];
            var value = entry.Get is { } get ? get() ? " (on)" : " (off)" : string.Empty;
            context.AppendLine($"{index + 1}. {entry.Title} — {entry.Where}{value}");
        }
        context.AppendLine();
        context.AppendLine("[Help topics: topic id — title: summary]");
        foreach (var (topic, title, summary) in HelpOutline?.Invoke() ?? [])
            context.AppendLine($"- {topic} — {title}: {summary}");
        return context.ToString();
    }

    // The settings and pages from 全局搜索, which also know how to open themselves.
    private List<SearchEntry> AskCatalog()
        => BuildSearchIndex(cloud.GetSnapshot()).Where(entry => entry.Group != 2).ToList();

    // ---- Entry points from other pages

    private void StartAiReview(Encounter encounter)
    {
        OpenAi();
        aiTab = 1;
        var conversation = aiConversations[1];
        if (conversation.EncounterId != encounter.Id) conversation.Reset();
        conversation.EncounterId = encounter.Id;
        if (conversation.Entries.Count == 0) StartReview(conversation, encounter);
    }

    private void StartReview(AiConversation conversation, Encounter encounter)
    {
        var question = text.Get("请复盘这场战斗，告诉我下次最该改进的地方。", "Review this fight and tell me what to improve first next time.");
        // Built here on the draw thread: it reads the history page's pull-match cache.
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var context = BuildReviewContext(encounter, labels);
        var player = encounter.Combatants.FirstOrDefault(combatant => combatant.IsLocalPlayer);
        conversation.JobKnowledge = player is null ? null : AiJobKnowledge.For(player.Job,
            id => ActionInfo?.Invoke(id)?.Name, id => StatusName?.Invoke(id));
        var playerName = LocalIdentity?.Invoke()?.Name ?? player?.Name ?? string.Empty;
        var directory = ActiveCombatLogDirectory?.Invoke() ?? string.Empty;
        var seconds = encounter.EffectiveDuration.TotalSeconds;
        var actionInfo = ActionInfo ?? (_ => null);
        // The log is read on the request's worker thread (SendAi), never while drawing.
        SendAi(conversation, question, question, () => context + Environment.NewLine + DescribeCasts(directory, encounter.StartTime, seconds, playerName, labels, actionInfo));
    }

    // The player's casts from the network log; a fight the log does not cover still gets a review
    // from the totals, so a missing or unreadable log is reported to the model, not to the player.
    private static string DescribeCasts(string directory, DateTimeOffset start, double seconds, string playerName,
        IReadOnlyDictionary<string, string> labels, Func<uint, AiActionInfo?> actionInfo)
    {
        try
        {
            var log = AiCombatLog.Read(directory, start, start.AddSeconds(seconds), playerName);
            return log is null
                ? "[Your casts] The combat log does not cover this fight (the log was cleared, moved, or the fight is older than the kept logs). Only the totals above are available."
                : AiReviewData.Describe(log, seconds, labels, actionInfo);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"[Your casts] The combat log could not be read ({exception.GetType().Name}). Only the totals above are available.";
        }
    }

    private void StartAiProgressionSummary(uint territory)
    {
        if (Progression is null) return;
        var pulls = Progression.Pulls.Where(pull => pull.TerritoryId == territory).ToArray();
        if (pulls.Length == 0) return;
        OpenAi();
        aiTab = 2;
        var conversation = aiConversations[2];
        conversation.Reset();
        var duty = DutyProgress.Summarize(pulls)[0];
        var summary = new StringBuilder();
        summary.AppendLine($"[Progression: {duty.Name}, {pulls.Length} pulls, best phase {duty.MaxPhase}, {(duty.Cleared ? "cleared" : "not cleared yet")}]");
        foreach (var pull in pulls.TakeLast(30))
            summary.AppendLine($"- {pull.StartTime.ToLocalTime():MM-dd HH:mm}: phase {pull.Phase}, boss HP {pull.BossHpFraction * 100:0}%, " +
                               $"{(pull.Cleared ? "cleared" : $"wipe cast {pull.WipeCast ?? "unknown"}")}, first death {(pull.FirstDeathSeconds is { } death ? FormatFightDuration(TimeSpan.FromSeconds(death)) : "none")}");
        summary.AppendLine("[Most common wipes]");
        foreach (var point in duty.WipePoints)
            summary.AppendLine($"- phase {point.Phase}: {point.Cast ?? $"{point.Boss} at ~{point.HpBucket}% HP"} × {point.Count}");
        var question = text.Get("总结一下这个副本的开荒进度：卡在哪里、有没有进步、下次重点练什么。",
            "Summarize this duty's progression: where we are stuck, whether we are improving and what to practise next.");
        SendAi(conversation, question, question, () => summary.ToString());
    }

    // ---- Reply buttons

    // Drops the buttons this mode never offered (see AiPrompts.IsAllowedAction).
    private AiReply WithAllowedActions(AiReply reply, AiMode mode)
    {
        var settingCount = mode == AiMode.Ask ? AskCatalog().Count : 0;
        var topics = mode == AiMode.Ask
            ? (HelpOutline?.Invoke() ?? []).Select(topic => topic.Topic).ToHashSet(StringComparer.Ordinal)
            : [];
        bool Allowed(AiAction action) => AiPrompts.IsAllowedAction(mode, action.Id, settingCount, topics.Contains);
        return reply with
        {
            Actions = reply.Actions.Where(Allowed).ToArray(),
            Findings = reply.Findings.Select(finding => finding.Action is { } action && !Allowed(action) ? finding with { Action = null } : finding).ToArray(),
        };
    }

    private void RunAiAction(string id, AiConversation conversation)
    {
        var (verb, argument) = id.IndexOf(':') is var colon and > 0 ? (id[..colon], id[(colon + 1)..]) : (id, string.Empty);
        switch (verb)
        {
            case "restart_parser":
                RestartParser();
                break;
            case "restart_host":
                RestartHost?.Invoke(argument);
                break;
            case "open_status":
                setStatusVisible(true);
                break;
            case "copy_diagnostics":
                CopyDiagnosticLog();
                break;
            case "help":
                if (OpenHelpTopic is { } open) open(argument);
                else openHelp();
                break;
            case "setting":
                var catalog = AskCatalog();
                if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= catalog.Count)
                    catalog[number - 1].Open();
                break;
            case "open":
                OpenAiTarget(argument);
                break;
        }
    }

    private void OpenAiTarget(string target)
    {
        switch (target)
        {
            case "home": selectedPage = Page.Overview; break;
            case "meter": selectedPage = Page.Meter; break;
            case "history": OpenHistory(0); break;
            case "progression": OpenHistory(1); break;
            case "logs": OpenHistory(2); break;
            case "overlays": selectedPage = Page.Overlays; break;
            case "extensions": selectedPage = Page.Extensions; break;
            case "friends": if (Friends is not null) OpenFriends(); break;
            case "cloud": selectedPage = Page.Cloud; break;
            case "settings": selectedPage = Page.Diagnostics; scenesPage = false; break;
            case "scenes": selectedPage = Page.Diagnostics; scenesPage = true; break;
            case "ai_settings": OpenAi(true); break;
        }
    }

    private void CopyDiagnosticLog()
    {
        if (BuildDiagnosticReport is not { } build) return;
        _ = Task.Run(() =>
        {
            var report = build();
            aiResults.Enqueue(() => ImGui.SetClipboardText(report));
        });
    }

    // 发给开发者: the feedback form, prefilled with this conversation; replies come back as 官方通知.
    private void SendAiToDeveloper(AiConversation conversation)
    {
        var transcript = new StringBuilder();
        foreach (var entry in conversation.Entries)
        {
            transcript.Append(entry.FromUser ? "我：" : "AI：");
            transcript.AppendLine(entry.FromUser ? entry.Display : DescribeReply(entry.Reply));
        }
        var firstQuestion = conversation.Entries.FirstOrDefault(entry => entry.FromUser)?.Display ?? string.Empty;
        OpenFeedback(firstQuestion, transcript.ToString(), conversation.Entries.Count);
    }

    private static string DescribeReply(AiReply? reply)
    {
        if (reply is null) return string.Empty;
        var lines = new List<string>();
        if (reply.Text.Length > 0) lines.Add(reply.Text);
        lines.AddRange(reply.Findings.Select(finding => $"[{finding.Level}] {finding.Title}：{finding.Detail}"));
        lines.AddRange(reply.Advice.Select((advice, index) => $"{index + 1}. {advice.Title}：{advice.Detail}"));
        return string.Join("\n", lines);
    }

    // ---- 测试连接

    private bool aiTesting;
    private string? aiTestMessage;
    private bool aiTestFailed;
    private string aiKeyDraft = string.Empty;

    private void TestAiConnection()
    {
        if (aiTesting) return;
        var settings = AiConfig;
        var provider = settings.Provider;
        var key = (string.IsNullOrWhiteSpace(aiKeyDraft) ? AiKeys?.Load(provider) : aiKeyDraft.Trim()) ?? string.Empty;
        if (key.Length == 0 && !AiProviders.Get(provider).KeyOptional)
        {
            aiTestFailed = true;
            aiTestMessage = text.Get("请先粘贴 API Key。", "Paste your API key first.");
            return;
        }
        aiTesting = true;
        aiTestMessage = null;
        var draft = aiKeyDraft.Trim();
        _ = Task.Run(async () =>
        {
            try
            {
                var models = await AiClient.ListModelsAsync(settings, key, CancellationToken.None).ConfigureAwait(false);
                aiResults.Enqueue(() =>
                {
                    aiTesting = false;
                    aiTestFailed = false;
                    // The key is stored only once it is known to work, under the provider it was tested with.
                    if (draft.Length > 0) AiKeys?.Save(provider, draft);
                    aiKeyDraft = string.Empty;
                    settings.KnownModels = models.ToList();
                    settings.Verified = true;
                    saveConfiguration();
                    aiTestMessage = models.Count > 0
                        ? text.Format($"连接正常 · {models.Count} 个模型", $"Connected · {models.Count} models")
                        : text.Get("连接正常", "Connected");
                });
            }
            catch (Exception exception)
            {
                var message = exception is AiServiceException ? exception.Message : exception.Message;
                aiResults.Enqueue(() =>
                {
                    aiTesting = false;
                    aiTestFailed = true;
                    aiTestMessage = message;
                    settings.Verified = false;
                    saveConfiguration();
                });
            }
        });
    }

    // The connection tests itself (user request): 0.8 s after the key or address stops changing,
    // and at once on switching to a provider whose key is already saved. Negative: none pending.
    private double aiAutoTestAt = -1;

    private void ScheduleAiTest(double delay) => aiAutoTestAt = ImGui.GetTime() + delay;

    private void RunScheduledAiTest()
    {
        if (aiAutoTestAt < 0 || ImGui.GetTime() < aiAutoTestAt || aiTesting) return;
        aiAutoTestAt = -1;
        var provider = AiProviders.Get(AiConfig.Provider);
        // Nothing to test yet: no key typed, none saved, and the provider needs one.
        if (aiKeyDraft.Trim().Length == 0 && !provider.KeyOptional && AiKeys?.Has(provider.Id) != true) return;
        TestAiConnection();
    }

    // Provider, address or key changed: the old test result no longer says anything.
    private void InvalidateAiConnection(bool clearModels)
    {
        AiConfig.Verified = false;
        if (clearModels)
        {
            AiConfig.KnownModels = [];
            AiConfig.Model = string.Empty;
        }
        aiTestMessage = null;
        aiTestFailed = false;
        saveConfiguration();
    }
}
