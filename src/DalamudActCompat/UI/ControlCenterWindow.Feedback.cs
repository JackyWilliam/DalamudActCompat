using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 反馈 (features.js feedbackDialog): a centred dialog from the header's feedback button (or
// handed over by AI 助手 with the description filled in). It goes to DACT Cloud's 反馈 column;
// the developer replies in 好友 ·「官方通知」. 我的反馈 (设置 · 解析器与诊断) lists past ones.
public sealed partial class ControlCenterWindow
{
    private const string FeedbackDialog = "##dact-feedback";
    private const string MyFeedbackDialog = "##dact-my-feedback";
    private const int MaximumScreenshots = 3;
    private const long MaximumScreenshotBytes = 2 * 1024 * 1024;
    // Diagnostic reports are bounded on the server too; this keeps the request reasonable.
    private const int MaximumDiagnosticsLength = 400_000;

    private bool feedbackRequested;
    private int feedbackKind;
    private string feedbackText = string.Empty;
    private bool feedbackPrefilled;
    private bool feedbackAttachDiagnostics = true;
    private string? feedbackConversation;
    private int feedbackConversationMessages;
    private bool feedbackAttachConversation = true;
    private readonly List<string> feedbackImages = [];
    private Task<CloudFeedbackReceipt>? feedbackSending;
    private string? feedbackError;
    private string? feedbackSentNotice;
    private bool myFeedbackRequested;
    private Task<IReadOnlyList<CloudFeedbackItem>>? myFeedbackLoading;

    internal Func<CloudFeedbackRequest, Task<CloudFeedbackReceipt>>? SubmitFeedback { get; set; }

    internal Func<Task<IReadOnlyList<CloudFeedbackItem>>>? ListFeedback { get; set; }

    // Opens a file picker for screenshots; the callback receives the chosen image paths.
    internal Action<Action<IReadOnlyList<string>>>? PickScreenshots { get; set; }

    // prefill / conversation come from AI 助手's hand-over ("AI 转交").
    public void OpenFeedback(string? prefill = null, string? conversation = null, int conversationMessages = 0)
    {
        feedbackRequested = true;
        feedbackKind = 0;
        feedbackText = prefill ?? string.Empty;
        feedbackPrefilled = prefill is not null;
        feedbackConversation = conversation;
        feedbackConversationMessages = conversationMessages;
        feedbackAttachConversation = conversation is not null;
        feedbackAttachDiagnostics = true;
        feedbackImages.Clear();
        feedbackSending = null;
        feedbackError = null;
    }

    private void DrawFeedbackDialog(CloudClientSnapshot snapshot)
    {
        if (feedbackRequested)
        {
            feedbackRequested = false;
            DactUi.OpenDialog(FeedbackDialog);
        }
        DrawMyFeedbackDialog();
        if (!DactUi.BeginDialog(FeedbackDialog, 500, padding: 18)) return;
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var width = ImGui.GetContentRegionAvail().X;

        var top = ImGui.GetCursorScreenPos();
        DactIcons.Draw(ImGui.GetWindowDrawList(), "feedback", top + new Vector2(0, 2 * s), 16 * s, palette.Gold);
        DactUi.DrawText(ImGui.GetWindowDrawList(), top + new Vector2(24 * s, 0), 20 * s, text.Get("反馈问题或建议", "Report a problem or idea"), palette.Text, DactFonts.H2);
        ImGui.SetCursorScreenPos(top + new Vector2(width - 28 * s, -4 * s));
        var close = DactUi.IconButton("##feedback-close", "close", text.Get("关闭", "Close"));
        ImGui.SetCursorScreenPos(top + new Vector2(0, 24 * s));
        ImGui.Dummy(Vector2.Zero);
        DactUi.Hint(text.Get("开发者会在 好友 ·「官方通知」里回复你。", "The developer replies in Friends · DACT announcements."));
        DactUi.Segmented("##feedback-kind", [text.Get("问题", "Problem"), text.Get("建议", "Idea"), text.Get("其他", "Other")], ref feedbackKind, width);

        DactUi.BeginField(text.Get("描述", "Description"));
        var focused = ImGui.IsItemFocused();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 8) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, Math.Max(1, s));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, DactUi.Field);
        ImGui.PushStyleColor(ImGuiCol.Border, palette.Accent with { W = .8f });
        ImGui.InputTextMultiline("##feedback-text", ref feedbackText, 4000, new Vector2(width, 96 * s), ImGuiInputTextFlags.None);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        DactUi.EndField(feedbackPrefilled
            ? text.Get("AI 已经帮你填好，可以修改后再发送。", "AI filled this in; edit it before sending if you like.")
            : text.Get("说清楚发生了什么、在哪个副本或页面，以及你希望怎样。", "Say what happened, where (duty or page), and what you expected."));

        // Attachments: the diagnostic report (redacted), the AI conversation, up to three screenshots.
        var attach = feedbackAttachDiagnostics;
        if (FeedbackCheck("##feedback-diag", text.Get("附上诊断日志", "Attach the diagnostic log"),
                text.Get("角色名、服务器名和常见凭据会被去除", "Character, world names and common credentials are removed"), ref attach,
                text.Get("预览", "Preview")))
            PreviewDiagnostics();
        feedbackAttachDiagnostics = attach;
        if (feedbackConversation is not null)
        {
            var conversation = feedbackAttachConversation;
            FeedbackCheck("##feedback-ai", text.Get("附上这次 AI 对话", "Attach this AI conversation"),
                text.Format($"{feedbackConversationMessages} 条消息", $"{feedbackConversationMessages} messages"), ref conversation, null);
            feedbackAttachConversation = conversation;
        }
        var addLabel = text.Get("添加截图", "Add screenshot");
        if (DactUi.Button(addLabel + "##feedback-shot", icon: "plus", disabled: feedbackImages.Count >= MaximumScreenshots || PickScreenshots is null))
        {
            // A modal blocks every window outside it, the file picker included, so the dialog
            // steps aside while a screenshot is picked and comes back with what was typed.
            DactUi.CloseDialog(FeedbackDialog);
            PickScreenshots?.Invoke(paths =>
            {
                foreach (var path in paths)
                    if (feedbackImages.Count < MaximumScreenshots && !feedbackImages.Contains(path, StringComparer.OrdinalIgnoreCase)) feedbackImages.Add(path);
                feedbackRequested = true;
            });
        }
        ImGui.SameLine(0, 8 * s);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 7 * s);
        DactUi.Hint(text.Format($"最多 {MaximumScreenshots} 张，每张 2 MB 以内", $"Up to {MaximumScreenshots}, 2 MB each"));
        for (var index = 0; index < feedbackImages.Count; index++)
        {
            ImGui.PushID(index);
            var name = Path.GetFileName(feedbackImages[index]);
            DactUi.Text(DactUi.Fit(name, width - 40 * s), palette.Muted, DactFonts.Small);
            ImGui.SameLine(width - 24 * s);
            if (DactUi.IconButton("##remove-shot", "close", text.Get("移除", "Remove"), size: 22)) feedbackImages.RemoveAt(index--);
            ImGui.PopID();
        }

        if (feedbackError is not null) DactUi.Wrapped(feedbackError, palette.Gold, DactFonts.Small);
        DactUi.Divider();

        var cancelLabel = text.Get("取消", "Cancel");
        var sendLabel = feedbackSending is { IsCompleted: false } ? text.Get("正在发送…", "Sending…") : text.Get("发送", "Send");
        var buttons = DactUi.ButtonWidth(cancelLabel) + 8 * s + DactUi.ButtonWidth(sendLabel, ButtonKind.Primary, "send");
        var footer = ImGui.GetCursorPos();
        DactUi.Wrapped(text.Get("发送后可在 设置 · 解析器与诊断 里查看处理进度。", "Track it later under Settings · Parser & diagnostics."),
            DactUi.HintColor, DactFonts.Small, width - buttons - 12 * s);
        var after = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(footer.X + width - buttons, footer.Y));
        if (DactUi.Button(cancelLabel + "##feedback-cancel") || close) DactUi.CloseDialog(FeedbackDialog);
        ImGui.SameLine(0, 8 * s);
        var busy = feedbackSending is { IsCompleted: false };
        if (DactUi.Button(sendLabel + "##feedback-send", ButtonKind.Primary, "send", disabled: busy || string.IsNullOrWhiteSpace(feedbackText) || !snapshot.CanUseDact))
            feedbackSending = SendFeedback();
        ImGui.SetCursorPos(new Vector2(after.X, Math.Max(after.Y, footer.Y + 34 * s)));
        ImGui.Dummy(Vector2.Zero);

        if (feedbackSending is { IsCompleted: true } done)
        {
            feedbackSending = null;
            if (done.IsCompletedSuccessfully)
            {
                feedbackSentNotice = text.Get("反馈已发送，开发者会在 好友 ·「官方通知」里回复你。", "Feedback sent. The developer replies in Friends · DACT announcements.");
                DactUi.CloseDialog(FeedbackDialog);
            }
            else feedbackError = FeedbackErrorText(done.Exception?.GetBaseException());
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !busy) DactUi.CloseDialog(FeedbackDialog);
        DactUi.EndDialog(FeedbackDialog);
    }

    // A checkbox row with a muted note and an optional ghost action; returns true for the action.
    private bool FeedbackCheck(string id, string label, string note, ref bool value, string? action)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var actionWidth = action is null ? 0 : DactUi.ButtonWidth(action, ButtonKind.Ghost) + 8 * s;
        if (ImGui.InvisibleButton(id, new Vector2(width - actionWidth, 24 * s))) value = !value;
        var draw = ImGui.GetWindowDrawList();
        var box = start + new Vector2(0, 3 * s);
        draw.AddRectFilled(box, box + new Vector2(18 * s), DactUi.U32(value ? palette.Accent : DactUi.Field), 4 * s);
        if (!value) draw.AddRect(box, box + new Vector2(18 * s), DactUi.U32(DactUi.FieldBorder), 4 * s, ImDrawFlags.None, Math.Max(1, s));
        else DactIcons.Draw(draw, "check", box + new Vector2(3 * s), 12 * s, palette.Surface);
        DactUi.DrawText(draw, start + new Vector2(26 * s, 0), 24 * s, label, palette.Text);
        var labelWidth = DactUi.TextWidth(label);
        string fitted;
        using (DactFonts.Push(DactFonts.Small)) fitted = DactUi.Fit(note, width - actionWidth - 26 * s - labelWidth - 8 * s);
        DactUi.DrawText(draw, start + new Vector2(26 * s + labelWidth + 8 * s, 0), 24 * s, fitted, DactUi.HintColor, DactFonts.Small);
        var clicked = false;
        if (action is not null)
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X + width - actionWidth + 8 * s, start.Y - 3 * s));
            clicked = DactUi.Button(action + id, ButtonKind.Ghost);
        }
        ImGui.SetCursorScreenPos(start + new Vector2(0, 24 * s));
        ImGui.Dummy(new Vector2(width, 0));
        return clicked;
    }

    private void PreviewDiagnostics()
    {
        try
        {
            var export = exportDiagnosticReport();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(export.FilePath) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            feedbackError = text.Format($"无法生成诊断日志：{error.Message}", $"Could not create the diagnostic log: {error.Message}");
        }
    }

    private async Task<CloudFeedbackReceipt> SendFeedback()
    {
        if (SubmitFeedback is not { } submit) throw new InvalidOperationException("feedback_unavailable");
        var kind = feedbackKind switch { 1 => CloudFeedbackKind.Idea, 2 => CloudFeedbackKind.Other, _ => CloudFeedbackKind.Bug };
        string? diagnostics = null;
        if (feedbackAttachDiagnostics)
        {
            var report = exportDiagnosticReport().Report;
            diagnostics = report.Length > MaximumDiagnosticsLength ? report[..MaximumDiagnosticsLength] : report;
        }
        var images = new List<CloudFeedbackImage>();
        foreach (var path in feedbackImages)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumScreenshotBytes) throw new InvalidDataException(Path.GetFileName(path));
            var type = info.Extension.ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
            images.Add(new CloudFeedbackImage(info.Name, type, Convert.ToBase64String(await File.ReadAllBytesAsync(path).ConfigureAwait(false))));
        }
        return await submit(new CloudFeedbackRequest(kind, feedbackText.Trim(), diagnostics,
            feedbackAttachConversation ? feedbackConversation : null, images)).ConfigureAwait(false);
    }

    private string FeedbackErrorText(Exception? error) => error switch
    {
        InvalidDataException bad => text.Format($"截图 {bad.Message} 不存在或超过 2 MB。", $"Screenshot {bad.Message} is missing or larger than 2 MB."),
        CloudApiException { StatusCode: System.Net.HttpStatusCode.NotFound } => text.Get("服务器暂不支持反馈，请稍后再试。", "The server does not accept feedback yet; please try again later."),
        CloudApiException api => text.SystemMessage(api.Message),
        _ => text.Format($"发送失败：{error?.Message}", $"Sending failed: {error?.Message}"),
    };

    // 我的反馈 row in 解析器与诊断 (Figma 设置).
    private void DrawMyFeedbackRow()
    {
        var s = DactUi.S;
        var label = text.Get("我的反馈", "My feedback");
        if (DactUi.Button(label + "##my-feedback", icon: "feedback"))
        {
            myFeedbackRequested = true;
            myFeedbackLoading = ListFeedback?.Invoke();
        }
        // 让 AI 诊断 joins the row only while the assistant is on and connected.
        if (AiConfig.Ready)
        {
            ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(text.Get("让 AI 诊断", "Ask AI to diagnose") + "##settings-ai-diagnose", icon: "sparkle"))
            {
                OpenAi();
                aiTab = 0;
            }
        }
        ImGui.SameLine(0, 10 * s);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 7 * s);
        DactUi.Wrapped(feedbackSentNotice ?? text.Get("开发者的回复也会出现在 好友 ·「官方通知」。", "Replies also appear in Friends · DACT announcements."),
            DactUi.HintColor, DactFonts.Small, ImGui.GetContentRegionAvail().X);
    }

    private void DrawMyFeedbackDialog()
    {
        if (myFeedbackRequested)
        {
            myFeedbackRequested = false;
            DactUi.OpenDialog(MyFeedbackDialog);
        }
        if (!DactUi.BeginDialog(MyFeedbackDialog, 520)) return;
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        DactUi.Text(text.Get("我的反馈", "My feedback"), size: DactFonts.H2);
        DactUi.Hint(text.Get("开发者的回复会出现在 好友 ·「官方通知」里。", "Replies appear in Friends · DACT announcements."));
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (ImGui.BeginChild("##my-feedback-list", new Vector2(-1, 300 * s), false))
        {
            if (myFeedbackLoading is null)
                DactUi.Wrapped(text.Get("登录后可以查看。", "Sign in to see it."), palette.Muted, DactFonts.Small);
            else if (!myFeedbackLoading.IsCompleted)
                DactUi.Wrapped(text.Get("正在读取…", "Loading…"), palette.Muted, DactFonts.Small);
            else if (!myFeedbackLoading.IsCompletedSuccessfully)
                DactUi.Wrapped(FeedbackErrorText(myFeedbackLoading.Exception?.GetBaseException()), palette.Gold, DactFonts.Small);
            else if (myFeedbackLoading.Result.Count == 0)
                DactUi.Wrapped(text.Get("还没有发过反馈。", "You have not sent any feedback yet."), palette.Muted, DactFonts.Small);
            else
                foreach (var item in myFeedbackLoading.Result.OrderByDescending(item => item.CreatedAt))
                {
                    ImGui.PushID(item.Id);
                    var start = ImGui.GetCursorScreenPos();
                    var height = 64 * s;
                    var draw = ImGui.GetWindowDrawList();
                    draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(DactUi.Field), 8 * s);
                    var kind = item.Kind switch { CloudFeedbackKind.Idea => text.Get("建议", "Idea"), CloudFeedbackKind.Other => text.Get("其他", "Other"), _ => text.Get("问题", "Problem") };
                    DactUi.DrawPill(draw, start + new Vector2(10 * s, 8 * s), kind, PillTone.Off, false);
                    var kindWidth = DactUi.PillWidth(kind, false);
                    DactUi.DrawText(draw, start + new Vector2(18 * s + kindWidth, 8 * s), 22 * s, item.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), palette.Muted, DactFonts.Small);
                    var (status, tone) = item.Status switch
                    {
                        "replied" => (text.Get("已回复", "Replied"), PillTone.Ok),
                        "closed" => (text.Get("已关闭", "Closed"), PillTone.Off),
                        _ => (text.Get("已收到", "Received"), PillTone.Warn),
                    };
                    DactUi.DrawPill(draw, start + new Vector2(width - 10 * s - DactUi.PillWidth(status), 8 * s), status, tone);
                    string excerpt;
                    using (DactFonts.Push(DactFonts.Small)) excerpt = DactUi.Fit(item.Text.ReplaceLineEndings(" "), width - 20 * s);
                    DactUi.DrawText(draw, start + new Vector2(10 * s, 36 * s), 20 * s, excerpt, palette.Text, DactFonts.Small);
                    ImGui.Dummy(new Vector2(width, height));
                    ImGui.PopID();
                }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        var newLabel = text.Get("新建反馈", "New feedback");
        var closeLabel = text.Get("关闭", "Close");
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + width - DactUi.ButtonWidth(closeLabel) - 8 * s - DactUi.ButtonWidth(newLabel, ButtonKind.Primary, "feedback"));
        if (DactUi.Button(closeLabel + "##my-feedback-close") || ImGui.IsKeyPressed(ImGuiKey.Escape)) DactUi.CloseDialog(MyFeedbackDialog);
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(newLabel + "##my-feedback-new", ButtonKind.Primary, "feedback"))
        {
            DactUi.CloseDialog(MyFeedbackDialog);
            OpenFeedback();
        }
        DactUi.EndDialog(MyFeedbackDialog);
    }
}
