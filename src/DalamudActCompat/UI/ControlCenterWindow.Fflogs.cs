using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Fflogs.Upload;
using DalamudActCompat.Protocol;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// The FFLogs page (tools/ui-redesign/fflogs.js): Parse 预估 on the left, 日志上传 on the right,
// 上传设置 as a sub-page, plus the per-pull upload in 战斗记录. Everything here only starts tasks on
// FflogsUploadService and shows their results; parsing and uploading run in the helper process,
// so nothing in a draw call may wait on them.
public sealed partial class ControlCenterWindow
{
    private bool fflogsUploadSettingsPage;
    private string fflogsUploadPassword = string.Empty;
    private bool fflogsUploadPasswordInitialized;

    // One sign-in, parse or upload from the card at a time; the helper would queue more, but the
    // card has a single progress block and a single cancel button.
    private Task<FflogsUploadResult?>? fflogsUploadTask;
    private Task<IReadOnlyList<FflogsUploadFight>>? fflogsPrepareTask;
    private Task? fflogsLiveTask;
    private CancellationTokenSource? fflogsUploadCancellation;
    private IReadOnlyList<FflogsUploadFight> fflogsPreparedFights = [];
    private string fflogsPreparedLogName = string.Empty;
    private readonly HashSet<int> fflogsSelectedFights = [];
    private FflogsUploadResult? fflogsLastResult;
    private string fflogsUploadMessage = string.Empty;
    private bool fflogsUploadMessageIsError;

    private Task<FflogsUploadResult>? historyUploadTask;
    private CancellationTokenSource? historyUploadCancellation;
    private Guid historyUploadEncounter;
    private FflogsUploadResult? historyUploadResult;
    private string historyUploadMessage = string.Empty;

    // Opened from the style editor's FFLogs column, which needs the online estimate switched on.
    public void ShowFflogsPage()
    {
        selectedPage = Page.Fflogs;
        fflogsUploadSettingsPage = false;
        ShowAnimated();
    }

    private bool DrawFflogsPage()
    {
        if (fflogsUploadSettingsPage) return DrawFflogsUploadSettingsPage();
        DactUi.PageHeader("FFLogs", text.Get(
            "Parse 预估和日志上传。预估用 API Client，上传用 FFLogs 网站账号，两者互不依赖。",
            "Parse estimate and log upload. The estimate uses an API client and upload uses your FFLogs website account; neither needs the other."));
        var changed = false;
        if (DactUi.BeginGrid("fflogs-grid", 14))
        {
            changed |= DrawFflogsSettings();
            DactUi.NextColumn();
            changed |= DrawFflogsUploadCard();
            DactUi.EndGrid();
        }
        return changed;
    }

    // ---- 日志上传

    private bool DrawFflogsUploadCard()
    {
        var s = DactUi.S;
        var changed = false;
        if (!fflogsUploadPasswordInitialized)
        {
            fflogsUploadPassword = fflogsUploadService.LoadSavedPassword() ?? string.Empty;
            fflogsUploadPasswordInitialized = true;
        }

        fflogsUploadService.Tick();
        CompleteFflogsUploadTasks();
        var status = fflogsUploadService.Status;
        var busy = fflogsUploadTask is { IsCompleted: false } || fflogsPrepareTask is { IsCompleted: false };
        var settingsLabel = text.Get("上传设置", "Upload settings");
        DactUi.BeginCard("fflogs-upload", null);
        DactUi.CardTitleRow(text.Get("日志上传", "Log upload"),
            () => DactUi.Pill(
                status.LiveActive ? text.Get("实时上传中", "Live upload")
                : status.LoggedIn ? text.Get("已登录", "Signed in")
                : text.Get("未登录", "Not signed in"),
                status.LoggedIn ? PillTone.Ok : PillTone.Off),
            status.LoggedIn ? DactUi.ButtonWidth(settingsLabel, ButtonKind.Ghost, "gear") : 0,
            status.LoggedIn
                ? () =>
                {
                    if (DactUi.Button(settingsLabel + "##fflogs-upload-settings", ButtonKind.Ghost, "gear")) fflogsUploadSettingsPage = true;
                }
                : null);

        if (!status.LoggedIn) changed |= DrawFflogsSignIn(busy);
        else DrawFflogsSignedIn(status, busy);

        if (!string.IsNullOrWhiteSpace(fflogsUploadMessage))
            DactUi.Wrapped(fflogsUploadMessage, fflogsUploadMessageIsError ? DactUi.Danger : DactUi.HintColor, DactFonts.Small);
        DactUi.EndCard();
        return changed;
    }

    // 未登录: the form, and below it what signing in is for (the actions appear after sign-in).
    private bool DrawFflogsSignIn(bool busy)
    {
        var s = DactUi.S;
        var changed = false;
        DactUi.Hint(text.Get(
            "用 FFLogs 网站账号把 DACT 保存的 Network 日志传成报告。解析和上传在游戏之外的辅助进程里进行。",
            "Turns the Network logs DACT saves into reports on your FFLogs website account. Parsing and uploading run in a helper process outside the game."));
        DactUi.BeginField(text.Get("登录邮箱", "Sign-in email"));
        var email = configuration.Fflogs.UploadEmail;
        if (DactUi.Input("fflogs-upload-email", ref email, text.Get("输入邮箱", "Enter email"), 256))
        {
            UpdateFflogsSettings(settings => settings.UploadEmail = email.Trim());
            changed = true;
        }
        DactUi.EndField();
        DactUi.BeginField(text.Get("登录密码", "Sign-in password"));
        // The password is not a setting: typing it changes nothing that needs saving.
        DactUi.Input("fflogs-upload-password", ref fflogsUploadPassword, text.Get("输入密码", "Enter password"), 256, secret: true);
        DactUi.EndField();

        var remember = configuration.Fflogs.UploadRememberPassword;
        if (DactUi.Checkbox(text.Get("记住密码", "Remember password") + "##fflogs-remember", ref remember, DactFonts.Body))
        {
            SetFflogsRememberPassword(remember);
            changed = true;
        }
        ImGui.SameLine(0, 8 * s);
        DactUi.Text(text.Get("加密后只保存在这台电脑", "Stored encrypted on this PC only"), DactUi.HintColor, DactFonts.Small);

        var chinese = getGameRegionSelection().EffectiveRegion == HostGameRegion.Chinese;
        var canSignIn = !busy && !string.IsNullOrWhiteSpace(configuration.Fflogs.UploadEmail) && !string.IsNullOrEmpty(fflogsUploadPassword);
        var signInLabel = busy ? text.Get("正在登录…", "Signing in…") : text.Get("登录 FFLogs", "Sign in to FFLogs");
        if (DactUi.Button(signInLabel + "##fflogs-upload-login", ButtonKind.Primary, disabled: !canSignIn) && canSignIn)
        {
            var address = configuration.Fflogs.UploadEmail;
            var password = fflogsUploadPassword;
            var keep = configuration.Fflogs.UploadRememberPassword;
            StartFflogsUploadTask(async cancellationToken =>
            {
                await fflogsUploadService.LoginAsync(address, password, chinese, cancellationToken).ConfigureAwait(false);
                // Only a password the site accepted is worth remembering.
                if (keep) fflogsUploadService.SavePassword(password);
                else fflogsUploadService.ClearSavedPassword();
                return null;
            });
        }

        DactUi.Divider();
        FflogsBenefit("upload", text.Get("上传最新日志", "Upload the latest log"), text.Get("或只选其中几场上传。", "Or only some of its fights."));
        FflogsBenefit("pulse", text.Get("实时上传", "Live upload"), text.Get("打本时每场结束自动上传。", "Each fight is uploaded as it ends."));
        FflogsBenefit("history", text.Get("战斗记录里上传单场", "Upload one pull from Encounter history"), text.Get("在某一场的详情里直接上传。", "Straight from that pull's details."));
        return changed;
    }

    private void DrawFflogsSignedIn(FflogsUploadStatus status, bool busy)
    {
        var s = DactUi.S;
        var picking = fflogsPreparedFights.Count > 0;
        DrawFflogsAccount(status, compact: picking, busy);
        DactUi.Divider();
        var options = configuration.Fflogs.CreateUploadOptions(status.ChineseSite);
        if (picking)
        {
            DrawFflogsFightPicker(options, busy);
            return;
        }

        var canUpload = !busy;
        var latestLabel = text.Get("上传最新日志", "Upload latest log");
        if (DactUi.Button(latestLabel + "##fflogs-upload-latest", ButtonKind.Primary, "upload", disabled: !canUpload) && canUpload)
        {
            var directory = getCombatLogDirectory();
            StartFflogsUploadTask(async cancellationToken =>
                await fflogsUploadService.UploadLatestAsync(directory, options, cancellationToken).ConfigureAwait(false));
        }

        var pickLabel = text.Get("选择战斗…", "Choose fights…");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(pickLabel, icon: "search"));
        if (DactUi.Button(pickLabel + "##fflogs-prepare", icon: "search", disabled: !canUpload) && canUpload)
        {
            ResetFflogsUploadCancellation();
            SetFflogsUploadMessage(string.Empty, false);
            fflogsPreparedLogName = fflogsUploadService.LatestLog(getCombatLogDirectory())?.Name ?? string.Empty;
            fflogsPrepareTask = fflogsUploadService.PrepareAsync(getCombatLogDirectory(), fflogsUploadCancellation!.Token);
        }

        if (busy)
        {
            DrawFflogsProgress();
        }
        else if (fflogsUploadService.LatestLog(getCombatLogDirectory()) is { } latest)
        {
            var written = FflogsWhen(latest.WrittenAt);
            DactUi.Hint(text.Format($"最新日志：{latest.Name} · {written}", $"Latest log: {latest.Name} · {written}"));
        }
        else
        {
            DactUi.Hint(text.Get("日志目录里还没有 Network 日志。", "The log folder has no Network log yet."));
        }

        DactUi.Divider();
        DrawFflogsLiveRow(status, options);
        if (fflogsLastResult is { } result && !status.LiveActive)
        {
            DactUi.Divider();
            var count = result.FightCount;
            DrawFflogsReport(
                "check",
                text.Format($"上传完成 · {count} 场战斗", $"Upload complete · {count} fights"),
                FflogsReportLine(result),
                result.Url,
                DactUi.FieldBorder);
        }
    }

    private void DrawFflogsAccount(FflogsUploadStatus status, bool compact, bool busy)
    {
        var s = DactUi.S;
        var signOut = text.Get("退出登录", "Sign out");
        var signOutWidth = DactUi.ButtonWidth(signOut, ButtonKind.Ghost);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(start + new Vector2(15 * s), 15 * s, DactUi.U32(DactTheme.Palette.Hover), 32);
        DactIcons.Draw(draw, "users", start + new Vector2(7.5f * s), 15 * s, DactTheme.Palette.Accent);
        var site = status.ChineseSite ? "cn.fflogs.com" : "www.fflogs.com";
        var guild = status.Guilds.FirstOrDefault(item => string.Equals(item.Id, configuration.Fflogs.UploadGuildId, StringComparison.Ordinal));
        var detail = guild is null ? site : text.Format($"{site} · 公会 {guild.Name}", $"{site} · guild {guild.Name}");
        var textWidth = width - 40 * s - signOutWidth - 8 * s;
        ImGui.SetCursorScreenPos(start + new Vector2(40 * s, compact ? 6 * s : 0));
        ImGui.BeginGroup();
        using (DactUi.Gap(1))
        {
            DactUi.Text(DactUi.Fit(status.UserName, textWidth));
            if (!compact) DactUi.Text(DactUi.Fit(detail, textWidth), DactUi.HintColor, DactFonts.Small);
        }
        ImGui.EndGroup();
        ImGui.SetCursorScreenPos(start + new Vector2(width - signOutWidth, 0));
        if (DactUi.Button(signOut + "##fflogs-upload-logout", ButtonKind.Ghost, disabled: busy) && !busy)
        {
            _ = fflogsUploadService.LogoutAsync();
            fflogsPreparedFights = [];
            fflogsSelectedFights.Clear();
            fflogsLastResult = null;
            SetFflogsUploadMessage(string.Empty, false);
        }
        // The row is as tall as the avatar whatever the text block measured.
        ImGui.SetCursorScreenPos(start + new Vector2(0, 30 * s));
        ImGui.Dummy(new Vector2(width, 0));
    }

    // 选择战斗: the fights of the log that was parsed, as the helper named them.
    private void DrawFflogsFightPicker(FflogsUploadOptions options, bool busy)
    {
        var s = DactUi.S;
        var fights = fflogsPreparedFights;
        var selectAll = fflogsSelectedFights.Count == fights.Count ? text.Get("全不选", "Select none") : text.Get("全选", "Select all");
        var selectAllWidth = DactUi.ButtonWidth(selectAll, ButtonKind.Ghost);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.BeginGroup();
        using (DactUi.Gap(1))
        {
            DactUi.Text(text.Get("选择要上传的战斗", "Choose the fights to upload"));
            var count = fights.Count;
            var source = string.IsNullOrEmpty(fflogsPreparedLogName)
                ? text.Format($"{count} 场", $"{count} fights")
                : text.Format($"{fflogsPreparedLogName} · {count} 场", $"{fflogsPreparedLogName} · {count} fights");
            DactUi.Text(DactUi.Fit(source, width - selectAllWidth - 8 * s), DactUi.HintColor, DactFonts.Small);
        }
        ImGui.EndGroup();
        var after = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(start + new Vector2(width - selectAllWidth, 2 * s));
        if (DactUi.Button(selectAll + "##fflogs-select-all", ButtonKind.Ghost))
        {
            if (fflogsSelectedFights.Count == fights.Count) fflogsSelectedFights.Clear();
            else foreach (var fight in fights) fflogsSelectedFights.Add(fight.Index);
        }
        ImGui.SetCursorScreenPos(after);

        // The list scrolls on its own so a long night of pulls keeps the buttons in reach.
        var rows = Math.Min(fights.Count, 6);
        var listHeight = rows * 30 * s + (rows - 1) * 2 * s + 8 * s;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4 * s));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8 * s);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, DactUi.Field);
        ImGui.PushStyleColor(ImGuiCol.Border, DactUi.FieldBorder);
        if (ImGui.BeginChild("fflogs-fights", new Vector2(width, listHeight), true))
        {
            // The 2 px row gap applies inside the list only; the card's own gap follows the list.
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2 * s));
            foreach (var fight in fights) DrawFflogsFightRow(fight);
            ImGui.PopStyleVar();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);

        var chosen = fflogsSelectedFights.Count;
        var canUpload = !busy && chosen > 0;
        var uploadLabel = text.Format($"上传选中的 {chosen} 场", $"Upload the {chosen} selected");
        if (DactUi.Button(uploadLabel + "##fflogs-upload-selected", ButtonKind.Primary, "upload", disabled: !canUpload) && canUpload)
        {
            var indices = fflogsSelectedFights.Order().ToArray();
            StartFflogsUploadTask(async cancellationToken =>
                await fflogsUploadService.UploadPreparedAsync(indices, options, cancellationToken).ConfigureAwait(false));
        }
        var cancel = text.Get("取消", "Cancel");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(cancel));
        if (DactUi.Button(cancel + "##fflogs-picker-cancel"))
        {
            // While an upload runs this stops it; otherwise it leaves the list.
            if (busy) fflogsUploadCancellation?.Cancel();
            else
            {
                fflogsPreparedFights = [];
                fflogsSelectedFights.Clear();
            }
        }
        if (busy) DrawFflogsProgress(showCancel: false);
        else DactUi.Hint(text.Get("按“上传设置”里的可见性、区域和公会创建一份新报告。", "Creates a new report with the visibility, region and guild from Upload settings."));
    }

    private void DrawFflogsFightRow(FflogsUploadFight fight)
    {
        var s = DactUi.S;
        var selected = fflogsSelectedFights.Contains(fight.Index);
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, 30 * s);
        if (ImGui.InvisibleButton($"##fflogs-fight-{fight.Index}", size))
        {
            if (selected) fflogsSelectedFights.Remove(fight.Index);
            else fflogsSelectedFights.Add(fight.Index);
            selected = !selected;
        }
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var palette = DactTheme.Palette;
        if (selected) draw.AddRectFilled(start, start + size, DactUi.U32(DactUi.AccentSoft), 6 * s);
        else if (hovered) draw.AddRectFilled(start, start + size, DactUi.U32(palette.Hover with { W = .6f }), 6 * s);

        var box = start + new Vector2(8 * s, 6 * s);
        if (selected)
        {
            draw.AddRectFilled(box, box + new Vector2(18 * s), DactUi.U32(palette.Accent), 4 * s);
            DactIcons.Draw(draw, "check", box + new Vector2(3 * s), 12 * s, palette.Surface);
        }
        else
        {
            draw.AddRectFilled(box, box + new Vector2(18 * s), DactUi.U32(DactUi.Field), 4 * s);
            draw.AddRect(box, box + new Vector2(18 * s), DactUi.U32(DactUi.FieldBorder), 4 * s, ImDrawFlags.None, Math.Max(1, s));
        }

        var ended = DateTimeOffset.FromUnixTimeMilliseconds(fight.EndUnixMs).ToLocalTime().ToString("HH:mm");
        var time = text.Format($"{ended} 结束", $"ended {ended}");
        var timeWidth = DactUi.TextWidth(time, DactFonts.Small);
        DactUi.DrawText(draw, start + new Vector2(36 * s, 0), 30 * s, DactUi.Fit(fight.Name, width - 36 * s - timeWidth - 20 * s), palette.Text);
        DactUi.DrawText(draw, start + new Vector2(width - timeWidth - 8 * s, 0), 30 * s, time, palette.Muted, DactFonts.Small);
    }

    private void DrawFflogsLiveRow(FflogsUploadStatus status, FflogsUploadOptions options)
    {
        var s = DactUi.S;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.BeginGroup();
        using (DactUi.Gap(2))
        {
            DactUi.Text(text.Get("实时上传", "Live upload"));
            DactUi.Wrapped(text.Get("每场战斗结束后自动传到同一份报告。", "Each fight goes into the same report as it ends."),
                DactUi.HintColor, DactFonts.Small, width - 44 * s);
        }
        ImGui.EndGroup();
        var after = ImGui.GetCursorScreenPos();
        var height = ImGui.GetItemRectSize().Y;
        ImGui.SetCursorScreenPos(start + new Vector2(width - 34 * s, (height - 20 * s) * .5f));
        var on = status.LiveActive;
        var pending = fflogsLiveTask is { IsCompleted: false };
        if (DactUi.Toggle("fflogs-live", ref on, disabled: pending) && !pending)
        {
            fflogsLiveTask = on
                ? fflogsUploadService.StartLiveAsync(getCombatLogDirectory(), options)
                : fflogsUploadService.StopLiveAsync();
        }
        ImGui.SetCursorScreenPos(after);

        if (status.LiveActive)
        {
            var count = status.LiveFightCount;
            var code = status.LiveReportCode;
            DrawFflogsReport(
                null,
                text.Format($"已上传 {count} 场", $"{count} fights uploaded"),
                string.IsNullOrEmpty(code)
                    ? text.Get("等待第一场战斗结束", "Waiting for the first fight to end")
                    : text.Format($"报告 {code} · 等待下一场战斗结束", $"Report {code} · waiting for the next fight to end"),
                string.IsNullOrEmpty(code) ? null : FflogsUploadService.ReportUrl(status.ChineseSite, code),
                DactTheme.Palette.Accent with { W = .45f });
        }
        else if (status.LiveError is { Length: > 0 } liveError)
        {
            DactUi.Wrapped(text.Format($"实时上传已停止：{liveError}", $"Live upload stopped: {liveError}"), DactUi.Danger, DactFonts.Small);
        }
    }

    // An inset block for one report: what happened, where it is, and the two things to do with it.
    private void DrawFflogsReport(string? icon, string title, string detail, string? url, Vector4 border)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        FflogsInset(border, inner =>
        {
            var start = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            if (icon is null) draw.AddCircleFilled(start + new Vector2(3 * s, 9 * s), 3 * s, DactUi.U32(palette.Accent), 12);
            else DactIcons.Draw(draw, icon, start + new Vector2(0, 2 * s), 14 * s, palette.Accent);
            var indent = icon is null ? 16 * s : 24 * s;
            ImGui.SetCursorScreenPos(start + new Vector2(indent, 0));
            ImGui.BeginGroup();
            using (DactUi.Gap(2))
            {
                DactUi.Wrapped(title, palette.Text, DactFonts.Body, inner - indent);
                DactUi.Wrapped(detail, DactUi.HintColor, DactFonts.Small, inner - indent);
            }
            ImGui.EndGroup();
            if (url is null) return;
            ImGui.SetCursorScreenPos(new Vector2(start.X + indent - 6 * s, ImGui.GetItemRectMax().Y + 4 * s));
            if (DactUi.Button(text.Get("打开报告", "Open report") + "##" + url, ButtonKind.Ghost, "external")) OpenUrl(url);
            ImGui.SameLine(0, 4 * s);
            if (DactUi.Button(text.Get("复制链接", "Copy link") + "##copy-" + url, ButtonKind.Ghost, "copy"))
            {
                ImGui.SetClipboardText(url);
                SetFflogsUploadMessage(text.Get("链接已复制。", "Link copied."), false);
            }
        });
    }

    // 进行中: one block for the engine download (first use only), parsing and uploading.
    private void DrawFflogsProgress(bool showCancel = true)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var percent = fflogsUploadService.EngineDownloadPercent;
        FflogsInset(palette.Accent with { W = .45f }, inner =>
        {
            var start = ImGui.GetCursorScreenPos();
            var title = percent is null
                ? fflogsPrepareTask is { IsCompleted: false } ? text.Get("正在解析日志", "Parsing the log") : text.Get("正在解析并上传", "Parsing and uploading")
                : text.Get("正在下载解析引擎", "Downloading the parser engine");
            DactUi.Text(title);
            if (percent is { } value)
            {
                var label = $"{value}%";
                DactUi.DrawText(ImGui.GetWindowDrawList(), start + new Vector2(inner - DactUi.TextWidth(label), 0), ImGui.GetTextLineHeight(), label, palette.Accent);
            }

            var bar = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(inner, 6 * s));
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(bar, bar + new Vector2(inner, 6 * s), DactUi.U32(palette.Hover), 3 * s);
            if (percent is { } done)
            {
                draw.AddRectFilled(bar, bar + new Vector2(Math.Max(6 * s, inner * done / 100f), 6 * s), DactUi.U32(palette.Accent), 3 * s);
            }
            else
            {
                // Parsing reports no percentage, so a short segment travels along the track.
                var travel = (float)(ImGui.GetTime() % 1.6 / 1.6);
                var segment = inner * .28f;
                var left = bar.X + (inner + segment) * travel - segment;
                draw.AddRectFilled(new Vector2(Math.Max(bar.X, left), bar.Y), new Vector2(Math.Min(bar.X + inner, left + segment), bar.Y + 6 * s),
                    DactUi.U32(palette.Accent), 3 * s);
            }

            DactUi.Wrapped(percent is null
                    ? text.Get("日志较大时需要几分钟，可以继续使用其他页面。", "A large log takes a few minutes; you can keep using other pages.")
                    : text.Get("只在第一次使用时下载，约 15 MB：微软官方的 V8 运行库，来自 NuGet 并按哈希校验。", "Downloaded on first use only, about 15 MB: Microsoft's official V8 runtime from NuGet, verified by hash."),
                DactUi.HintColor, DactFonts.Small, inner);
            // Cancels the running parse or upload only; the sign-in and a live upload stay.
            if (showCancel && DactUi.Button(text.Get("取消", "Cancel") + "##fflogs-cancel")) fflogsUploadCancellation?.Cancel();
        });
    }

    // Lays its content out on the upper draw channel and paints an inset box behind it once the
    // height is known. inner is the width the content may use.
    private static void FflogsInset(Vector4 border, Action<float> content)
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(12 * s, 10 * s));
        ImGui.BeginGroup();
        using (DactUi.Gap(8)) content(width - 24 * s);
        ImGui.EndGroup();
        var height = ImGui.GetItemRectMax().Y - start.Y + 10 * s;
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(DactUi.Field), 8 * s);
        draw.AddRect(start, start + new Vector2(width, height), DactUi.U32(border), 8 * s, ImDrawFlags.None, Math.Max(1, s));
        draw.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        ImGui.Dummy(new Vector2(width, 0));
    }

    private void FflogsBenefit(string icon, string title, string detail)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start + new Vector2(0, 2 * s), start + new Vector2(26 * s, 28 * s), DactUi.U32(DactUi.AccentSoft), 7 * s);
        DactIcons.Draw(draw, icon, start + new Vector2(6 * s, 8 * s), 14 * s, palette.Accent);
        ImGui.SetCursorScreenPos(start + new Vector2(36 * s, 0));
        ImGui.BeginGroup();
        using (DactUi.Gap(1))
        {
            DactUi.Wrapped(title, palette.Text, DactFonts.Body, width - 36 * s);
            DactUi.Wrapped(detail, DactUi.HintColor, DactFonts.Small, width - 36 * s);
        }
        ImGui.EndGroup();
        ImGui.SetCursorScreenPos(new Vector2(start.X, Math.Max(ImGui.GetItemRectMax().Y, start.Y + 30 * s)));
        ImGui.Dummy(new Vector2(width, 0));
    }

    // ---- 上传设置 (sub-page, like AI 设置 under AI 助手)

    private bool DrawFflogsUploadSettingsPage()
    {
        var changed = false;
        var back = text.Get("返回 FFLogs", "Back to FFLogs");
        DactUi.PageHeader(text.Get("上传设置", "Upload settings"),
            text.Get(
                "FFLogs / 上传设置 · 对上传最新日志、选择战斗、实时上传和战斗记录里的单场上传都生效。",
                "FFLogs / Upload settings · Applies to uploading the latest log, chosen fights, live upload and single pulls from Encounter history."),
            DactUi.ButtonWidth(back),
            () =>
            {
                if (DactUi.Button(back + "##fflogs-settings-back")) fflogsUploadSettingsPage = false;
            });

        var status = fflogsUploadService.Status;
        var chinese = status.LoggedIn ? status.ChineseSite : getGameRegionSelection().EffectiveRegion == HostGameRegion.Chinese;
        if (!DactUi.BeginGrid("fflogs-settings-grid", 14)) return changed;

        DactUi.BeginCard("fflogs-upload-report", text.Get("报告", "Report"));
        DactUi.BeginField(text.Get("可见性", "Visibility"));
        var visibility = Math.Clamp(configuration.Fflogs.UploadVisibility, 0, 2);
        if (DactUi.Select("fflogs-visibility", [text.Get("公开", "Public"), text.Get("私人", "Private"), text.Get("不公开", "Unlisted")], ref visibility))
        {
            UpdateFflogsSettings(settings => settings.UploadVisibility = visibility);
            changed = true;
        }
        DactUi.EndField(text.Get(
            "公开：所有人可见；私人：只有自己和公会成员可见；不公开：知道链接的人可见。",
            "Public: everyone; Private: only you and your guild; Unlisted: anyone with the link."));

        DactUi.BeginField(text.Get("区域", "Region"));
        // FF Logs' own region numbers (FflogsSettings.UploadRegion).
        var regionValues = chinese ? new[] { 1 } : new[] { 1, 2, 3, 6 };
        var regionNames = chinese ? new[] { "CN" } : new[] { "NA", "EU", "JP", "OC" };
        var region = Math.Max(0, Array.IndexOf(regionValues, configuration.Fflogs.UploadRegion));
        if (DactUi.Select("fflogs-region", regionNames, ref region, disabled: regionNames.Length == 1))
        {
            UpdateFflogsSettings(settings => settings.UploadRegion = regionValues[region]);
            changed = true;
        }
        DactUi.EndField(text.Get("国服只有 CN；国际服可选 NA / EU / JP / OC。", "The Chinese site only has CN; the global site offers NA / EU / JP / OC."));

        DactUi.BeginField(text.Get("公会", "Guild"));
        // "No guild" is a real choice: a personal report belongs to no guild.
        var guilds = status.Guilds;
        var guildNames = new[] { text.Get("个人报告（不选公会）", "Personal report (no guild)") }
            .Concat(guilds.Select(guild => guild.Name))
            .ToArray();
        var guildIndex = 0;
        for (var index = 0; index < guilds.Count; index++)
        {
            if (string.Equals(guilds[index].Id, configuration.Fflogs.UploadGuildId, StringComparison.Ordinal)) guildIndex = index + 1;
        }
        if (DactUi.Select("fflogs-guild", guildNames, ref guildIndex, disabled: guilds.Count == 0))
        {
            var guildId = guildIndex == 0 ? string.Empty : guilds[guildIndex - 1].Id;
            UpdateFflogsSettings(settings => settings.UploadGuildId = guildId);
            changed = true;
        }
        DactUi.EndField(text.Get("登录后列出账号所在的公会。", "Lists the account's guilds once you are signed in."));

        DactUi.BeginField(text.Get("报告描述", "Report description"));
        var description = configuration.Fflogs.UploadDescription;
        if (DactUi.Input("fflogs-upload-description", ref description, text.Get("可选", "Optional"), 256))
        {
            UpdateFflogsSettings(settings => settings.UploadDescription = description);
            changed = true;
        }
        DactUi.EndField();
        DactUi.EndCard();

        DactUi.NextColumn();
        DactUi.BeginCard("fflogs-upload-auto", text.Get("自动", "Automatic"));
        var autoLogin = configuration.Fflogs.UploadAutoLogin;
        if (DactUi.ToggleRow(text.Get("启动时自动登录", "Sign in automatically on startup"), ref autoLogin,
                text.Get("需要先记住密码。启动后等 DACT 账号校验和 Host 就绪再登录。", "Needs a remembered password. Signs in once the DACT account check and the Host are ready.")))
        {
            UpdateFflogsSettings(settings => settings.UploadAutoLogin = autoLogin);
            changed = true;
        }
        var autoLive = configuration.Fflogs.UploadAutoStartLive;
        if (DactUi.ToggleRow(text.Get("自动登录后开始实时上传", "Start live upload after the automatic sign-in"), ref autoLive))
        {
            UpdateFflogsSettings(settings => settings.UploadAutoStartLive = autoLive);
            changed = true;
        }
        var previousFights = configuration.Fflogs.UploadPreviousFights;
        if (DactUi.ToggleRow(text.Get("实时上传包含已有战斗", "Include earlier fights in live upload"), ref previousFights,
                text.Get("打开后，开始实时上传时把当前日志里已经打完的战斗一并上传。", "When on, starting a live upload also uploads the fights already finished in the current log.")))
        {
            UpdateFflogsSettings(settings => settings.UploadPreviousFights = previousFights);
            changed = true;
        }
        DactUi.EndCard();

        DactUi.BeginCard("fflogs-upload-engine", text.Get("密码与解析引擎", "Password and parser engine"));
        var remember = configuration.Fflogs.UploadRememberPassword;
        if (DactUi.ToggleRow(text.Get("记住密码", "Remember password"), ref remember,
                text.Get("加密后只保存在这台电脑的当前 Windows 账户下；关闭后立即删除。", "Stored encrypted for the current Windows account on this PC only; turning it off deletes it at once.")))
        {
            SetFflogsRememberPassword(remember);
            changed = true;
        }
        DactUi.Divider();
        DrawFflogsEngineRow();
        DactUi.EndCard();
        DactUi.EndGrid();
        return changed;
    }

    private void DrawFflogsEngineRow()
    {
        var s = DactUi.S;
        var percent = fflogsUploadService.EngineDownloadPercent;
        var (label, tone) = percent is { } value
            ? (text.Format($"下载中 {value}%", $"Downloading {value}%"), PillTone.Warn)
            : fflogsUploadService.EngineVerified
                ? (text.Get("已就绪", "Ready"), PillTone.Ok)
                : (text.Get("使用时检查", "Checked on use"), PillTone.Off);
        var pillWidth = DactUi.PillWidth(label);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.BeginGroup();
        using (DactUi.Gap(2))
        {
            DactUi.Text(text.Get("解析引擎", "Parser engine"));
            DactUi.Wrapped(text.Get("FFLogs 的解析器要用 V8 运行库，第一次解析时下载（约 15 MB）。", "The FFLogs parser needs the V8 runtime, downloaded on the first parse (about 15 MB)."),
                DactUi.HintColor, DactFonts.Small, width - pillWidth - 10 * s);
        }
        ImGui.EndGroup();
        var after = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(start + new Vector2(width - pillWidth, 0));
        DactUi.Pill(label, tone);
        ImGui.SetCursorScreenPos(after);
    }

    private void SetFflogsRememberPassword(bool remember)
    {
        UpdateFflogsSettings(settings => settings.UploadRememberPassword = remember);
        if (!remember)
        {
            fflogsUploadService.ClearSavedPassword();
            return;
        }

        // Signed in with the password still in the field: it was accepted, so it can be kept now.
        if (fflogsUploadService.IsLoggedIn && !string.IsNullOrEmpty(fflogsUploadPassword))
            fflogsUploadService.SavePassword(fflogsUploadPassword);
    }

    // ---- Tasks and wording shared by the card and 战斗记录

    private void ResetFflogsUploadCancellation()
    {
        fflogsUploadCancellation?.Dispose();
        fflogsUploadCancellation = new CancellationTokenSource();
    }

    private void StartFflogsUploadTask(Func<CancellationToken, Task<FflogsUploadResult?>> start)
    {
        ResetFflogsUploadCancellation();
        SetFflogsUploadMessage(string.Empty, false);
        fflogsUploadTask = start(fflogsUploadCancellation!.Token);
    }

    private void SetFflogsUploadMessage(string message, bool isError)
    {
        fflogsUploadMessage = message;
        fflogsUploadMessageIsError = isError;
    }

    private void CompleteFflogsUploadTasks()
    {
        if (fflogsPrepareTask is { IsCompleted: true } prepared)
        {
            fflogsPrepareTask = null;
            if (prepared.IsCompletedSuccessfully)
            {
                fflogsPreparedFights = prepared.Result;
                fflogsSelectedFights.Clear();
                if (fflogsPreparedFights.Count == 0)
                    SetFflogsUploadMessage(text.Get("最新日志里没有可上传的战斗。", "The latest log has no fight to upload."), false);
            }
            else
            {
                DescribeFflogsUploadFailure(prepared.Exception, text.Get("解析失败", "Parsing failed"));
            }
        }

        if (fflogsUploadTask is { IsCompleted: true } completed)
        {
            fflogsUploadTask = null;
            if (!completed.IsCompletedSuccessfully)
            {
                DescribeFflogsUploadFailure(completed.Exception, text.Get("操作失败", "Operation failed"));
            }
            else if (completed.Result is { } result)
            {
                fflogsLastResult = result;
                fflogsPreparedFights = [];
                fflogsSelectedFights.Clear();
            }
        }

        if (fflogsLiveTask is { IsCompleted: true } live)
        {
            fflogsLiveTask = null;
            if (!live.IsCompletedSuccessfully)
                DescribeFflogsUploadFailure(live.Exception, text.Get("实时上传未能开始", "Live upload could not start"));
        }
    }

    private void DescribeFflogsUploadFailure(AggregateException? failure, string prefix)
    {
        var message = FflogsUploadFailureText(failure, prefix, out var cancelled);
        SetFflogsUploadMessage(message, !cancelled);
    }

    // The helper's own messages are English diagnostics; the cases a player can act on are worded
    // here in the interface language.
    private string FflogsUploadFailureText(AggregateException? failure, string prefix, out bool cancelled)
    {
        var cause = failure?.GetBaseException();
        cancelled = cause is OperationCanceledException or null;
        if (cancelled) return text.Get("已取消。", "Cancelled.");
        var detail = cause is FflogsUploadException upload
            ? upload.Code switch
            {
                FflogsUploadProtocol.NotLoggedIn => text.Get("请先登录 FFLogs。", "Sign in to FFLogs first."),
                FflogsUploadProtocol.LoginFailed => text.Format($"登录被拒绝（{upload.Message}）", $"sign-in was refused ({upload.Message})"),
                FflogsUploadProtocol.NoLog => text.Get("没有找到 Network 日志。", "No Network log was found."),
                FflogsUploadProtocol.NoFights => text.Get("日志里没有可上传的战斗。", "The log contains no fight to upload."),
                FflogsUploadProtocol.NoMatch => text.Get("在保存的 Network 日志里没有找到这场战斗。", "This fight was not found in the saved Network logs."),
                FflogsUploadProtocol.EngineUnavailable => text.Format($"解析引擎下载失败（{upload.Message}）", $"the parser engine could not be downloaded ({upload.Message})"),
                FflogsUploadException.HostUnavailable => text.Get("DACT Host 还没有准备好，请稍后再试。", "The DACT Host is not ready yet; try again in a moment."),
                _ => upload.Message,
            }
            : cause!.Message;
        return text.IsChinese ? $"{prefix}：{detail}" : $"{prefix}: {detail}";
    }

    private string FflogsVisibilityLabel(int visibility) => visibility switch
    {
        0 => text.Get("公开", "Public"),
        2 => text.Get("不公开", "Unlisted"),
        _ => text.Get("私人", "Private"),
    };

    private string FflogsWhen(DateTime time)
        => time.Date == DateTime.Today
            ? text.Format($"今天 {time:HH:mm}", $"today {time:HH:mm}")
            : time.ToString("MM-dd HH:mm");

    private string FflogsReportLine(FflogsUploadResult result)
    {
        var visibility = FflogsVisibilityLabel(result.Visibility);
        var when = FflogsWhen(result.FinishedAt);
        return text.Format($"报告 {result.ReportCode} · {visibility} · {when}", $"Report {result.ReportCode} · {visibility} · {when}");
    }

    // 战斗记录: upload the one pull this record stands for.
    private void StartHistoryUpload(Encounter encounter)
    {
        if (historyUploadTask is { IsCompleted: false }) return;
        historyUploadEncounter = encounter.Id;
        historyUploadResult = null;
        var status = fflogsUploadService.Status;
        if (!status.LoggedIn)
        {
            historyUploadMessage = text.Get("请先在 FFLogs 页登录。", "Sign in on the FFLogs page first.");
            return;
        }

        historyUploadCancellation?.Dispose();
        historyUploadCancellation = new CancellationTokenSource();
        historyUploadMessage = text.Get("正在解析并上传这场战斗…", "Parsing and uploading this fight…");
        historyUploadTask = fflogsUploadService.UploadEncounterAsync(
            getCombatLogDirectory(),
            encounter.StartTime,
            encounter.StartTime + encounter.Duration,
            configuration.Fflogs.CreateUploadOptions(status.ChineseSite),
            historyUploadCancellation.Token);
    }

    // Draws the result line under the detail title, for the record the last upload started from.
    private void DrawHistoryUploadStatus(Encounter encounter)
    {
        if (historyUploadTask is { IsCompleted: true } completed)
        {
            historyUploadTask = null;
            if (completed.IsCompletedSuccessfully)
            {
                historyUploadResult = completed.Result;
                historyUploadMessage = string.Empty;
            }
            else
            {
                historyUploadMessage = FflogsUploadFailureText(completed.Exception, text.Get("上传失败", "Upload failed"), out _);
            }
        }

        if (encounter.Id != historyUploadEncounter) return;
        var s = DactUi.S;
        if (historyUploadResult is { } result)
        {
            var open = text.Get("打开报告", "Open report");
            var openWidth = DactUi.ButtonWidth(open, ButtonKind.Ghost, "external");
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            DactIcons.Draw(ImGui.GetWindowDrawList(), "check", start + new Vector2(0, 8 * s), 14 * s, DactTheme.Palette.Accent);
            var line = text.Format($"已上传，报告 {result.ReportCode}", $"Uploaded, report {result.ReportCode}");
            DactUi.DrawText(ImGui.GetWindowDrawList(), start + new Vector2(20 * s, 0), 30 * s,
                DactUi.Fit(line, width - 20 * s - openWidth - 8 * s), DactUi.HintColor, DactFonts.Small);
            ImGui.SetCursorScreenPos(start + new Vector2(width - openWidth, 0));
            if (DactUi.Button(open + "##history-open-report", ButtonKind.Ghost, "external")) OpenUrl(result.Url);
        }
        else if (!string.IsNullOrWhiteSpace(historyUploadMessage))
        {
            DactUi.Hint(historyUploadMessage);
        }
    }
}
