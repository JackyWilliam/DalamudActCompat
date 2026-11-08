using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

internal sealed class CloudBanNoticeWindow : Window
{
    private readonly UiText text;
    private readonly WindowDragController headerDrag = new();
    private readonly ISharedImmediateTexture logoTexture;
    private readonly Func<CloudClientSnapshot> getSnapshot;
    private readonly Action<string, string> recheckBan;
    private string username = string.Empty;
    private string password = string.Empty;
    private CloudBanNotice? notice;
    private bool lifted;
    private bool requiresRestart;
    private bool locateOnNextDraw;
    private bool outerFrameStylePushed;

    public CloudBanNoticeWindow(UiText text, ISharedImmediateTexture logoTexture,
        Func<CloudClientSnapshot> getSnapshot, Action<string, string> recheckBan)
        : base("账号安全###DalamudActCompatCloudBanNotice")
    {
        this.text = text;
        this.logoTexture = logoTexture;
        this.getSnapshot = getSnapshot;
        this.recheckBan = recheckBan;
        Size = new Vector2(610, 330);
        SizeCondition = ImGuiCond.Always;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        // This is deliberately a normal window rather than a modal popup: account
        // enforcement disables DACT, but must never capture unrelated game controls.
        Flags = ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoNavInputs |
                ImGuiWindowFlags.NoNavFocus |
                ImGuiWindowFlags.NoFocusOnAppearing;
    }

    public void Show(
        CloudBanNotice? nextNotice,
        bool lifted,
        bool requiresRestart = false)
    {
        notice = nextNotice;
        this.lifted = lifted;
        this.requiresRestart = requiresRestart;
        password = string.Empty;
        Size = new Vector2(680, lifted ? 380 : 580);
        locateOnNextDraw = true;
        IsOpen = true;
    }

    private readonly WindowFade fade = new();

    public override void OnOpen() => fade.Opened();

    public override void PreDraw()
    {
        headerDrag.PrepareNextWindow(!locateOnNextDraw);
        if (locateOnNextDraw)
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                viewport.Pos + (viewport.Size * 0.5f),
                ImGuiCond.Always,
                new Vector2(0.5f));
            locateOnNextDraw = false;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // Indigo of the DACT logo, like the control center; skins substitute their own border.
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(.208f, .251f, .420f, 1));
        outerFrameStylePushed = true;
        Flags = DactTheme.WindowFlags(Flags);
        // Pushed last so it nests inside this window's other style pushes (WindowFade).
        if (!fade.Push()) IsOpen = false;
    }

    public override void PostDraw()
    {
        fade.Pop();
        if (!outerFrameStylePushed)
        {
            return;
        }

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        outerFrameStylePushed = false;
    }

    // 账号安全 in the 1.0 frame: a 48 px header with the ban state, the notice card, the
    // re-verification card while banned, and a fixed footer whose 确认 only dismisses the notice.
    public override void Draw()
    {
        var current = notice;
        if (current is null && !lifted)
        {
            IsOpen = false;
            return;
        }

        WindowName = text.Get(
            "账号安全###DalamudActCompatCloudBanNotice",
            "Account security###DalamudActCompatCloudBanNotice");
        ControlCenterWindow.PushTheme();
        try
        {
            DactUi.BeginFrame();
            DactTheme.DrawGameWindow();
            var s = DactUi.S;
            var min = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            var line = Math.Max(1, s);
            var footer = 60 * s;
            var draw = ImGui.GetWindowDrawList();
            DrawHeader(min, size.X, 48 * s, current);
            draw.AddRectFilled(new Vector2(min.X, min.Y + 48 * s), new Vector2(min.X + size.X, min.Y + 48 * s + line), DactUi.U32(DactUi.DividerColor));
            ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + 48 * s + line));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 18) * s);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8 * s);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            if (ImGui.BeginChild("cloud-ban-notice-content", new Vector2(-1, size.Y - 48 * s - line - footer), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
            {
                using (DactUi.Gap(14))
                {
                    if (lifted) DrawLiftedContent(current);
                    else DrawBannedContent(current!);
                    if (!lifted) DrawBanRecovery();
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);

            var footerTop = min.Y + size.Y - footer;
            draw.AddRectFilled(new Vector2(min.X, footerTop), new Vector2(min.X + size.X, footerTop + line), DactUi.U32(DactUi.DividerColor));
            var confirm = text.Get("确认", "Confirm");
            var width = Math.Max(120 * s, DactUi.ButtonWidth(confirm, ButtonKind.Primary));
            ImGui.SetCursorScreenPos(new Vector2(min.X + size.X - 22 * s - width, footerTop + (footer - 32 * s) * .5f));
            if (DactUi.Button(confirm + "##ban-confirm", ButtonKind.Primary, width: width))
            {
                password = string.Empty;
                IsOpen = false;
            }
        }
        finally
        {
            ControlCenterWindow.PopTheme();
        }
    }

    private void DrawHeader(Vector2 min, float width, float height, CloudBanNotice? current)
    {
        var s = DactUi.S;
        var draw = ImGui.GetWindowDrawList();
        var logo = new Vector2(min.X + 14 * s, min.Y + (height - 26 * s) * .5f);
        draw.AddImageRounded(logoTexture.GetWrapOrEmpty().Handle, logo, logo + new Vector2(26 * s), Vector2.Zero, Vector2.One,
            ImGui.GetColorU32(Vector4.One), 6 * s);
        var title = text.Get("账号安全", "Account security");
        var x = logo.X + 36 * s;
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, title, DactTheme.Palette.Text);
        x += DactUi.TextWidth(title) + 10 * s;
        var version = ControlCenterWindow.FormatVersionLabel(typeof(CloudBanNoticeWindow).Assembly.GetName().Version);
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, version, DactUi.HintColor, DactFonts.Small);
        var dragLeft = x + DactUi.TextWidth(version, DactFonts.Small) + 8 * s;

        // No close button: enforcement notices are dismissed only through 确认 in the footer.
        var state = lifted
            ? IsDeviceBan(current)
                ? text.Get("账号与机器已解封", "Account and devices unbanned")
                : text.Get("账号已解封", "Account unbanned")
            : IsDeviceBan(current)
                ? text.Get("账号与机器访问已禁用", "Account and device access disabled")
                : text.Get("账号访问已禁用", "Account access disabled");
        var right = min.X + width - 16 * s - DactUi.PillWidth(state);
        DactUi.DrawPill(draw, new Vector2(right, min.Y + (height - 22 * s) * .5f), state, lifted ? PillTone.Ok : PillTone.Error);
        if (right - 8 * s > dragLeft)
        {
            ImGui.SetCursorScreenPos(new Vector2(dragLeft, min.Y));
            ImGui.InvisibleButton("##ban-drag", new Vector2(right - 8 * s - dragLeft, height));
            headerDrag.HandleItem();
        }
    }

    private void DrawBanRecovery()
    {
        var snapshot = getSnapshot();
        DactUi.BeginCard("cloud-ban-recovery-card", text.Get("重新验证", "Verify again"));
        DactUi.Hint(text.Get("管理员解封后，请输入账号密码重新验证；验证成功后重载 DACT 或重启游戏。",
            "After an administrator lifts the ban, verify your username and password here, then reload DACT or restart the game."));
        ImGui.BeginDisabled(snapshot.IsBusy);
        DactUi.BeginField(text.Get("用户名", "Username"));
        DactUi.Input("##ban-username", ref username, text.Get("请输入账号名", "Enter your username"), 32);
        DactUi.EndField();
        DactUi.BeginField(text.Get("密码", "Password"));
        DactUi.Input("##ban-password", ref password, text.Get("请输入密码", "Enter your password"), 128, secret: true);
        DactUi.EndField();
        var ready = !string.IsNullOrWhiteSpace(username) && password.Length > 0;
        var submit = ready && ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) &&
                     (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter));
        submit |= DactUi.Button(text.Get("重新验证解封", "Verify ban removal") + "##ban-verify", ButtonKind.Primary, disabled: !ready);
        ImGui.EndDisabled();
        if (submit && ready && !snapshot.IsBusy)
        {
            var submittedPassword = password;
            password = string.Empty;
            recheckBan(username, submittedPassword);
        }
        var status = text.SystemMessage(snapshot.StatusMessage);
        if (!string.IsNullOrWhiteSpace(status)) DactUi.Hint(status);
        DactUi.EndCard();
    }

    private void DrawBannedContent(CloudBanNotice current)
    {
        DactUi.BeginCard("cloud-ban-notice-card", null);
        DactUi.Text(IsDeviceBan(current)
            ? text.Get(
                "您的账号及关联机器已经被封禁",
                "Your account and associated devices have been banned")
            : text.Get("您的账号已经被封禁", "Your account has been banned"), DactUi.Danger, DactFonts.H2);
        var rows = new List<(string Label, string Value)>
        {
            (text.Get("封禁时间", "Banned at"), current.BannedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")),
            (text.Get("封禁结束", "Ends at"), current.BanExpiresAt is { } expiresAt
                ? expiresAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
                : text.Get("永久", "Permanent")),
            (text.Get("封禁方式", "Ban type"), text.Get(FormatBanType(current.BanType, chinese: true), FormatBanType(current.BanType, chinese: false))),
        };
        if (!string.IsNullOrWhiteSpace(current.BanReason)) rows.Add((text.Get("封禁原因", "Reason"), current.BanReason));
        Rows(rows);
        DactUi.Hint(text.Get(
            "DACT 解析器、悬浮窗和所有 DACT 管理的扩展服务已停止。确认只会关闭本提示，不会解除封禁。",
            "The DACT parser, overlays, and all DACT-managed extension services have stopped. Confirm only dismisses this notice; it does not remove the ban."));
        DactUi.EndCard();
    }

    private void DrawLiftedContent(CloudBanNotice? current)
    {
        DactUi.BeginCard("cloud-ban-notice-card", null);
        DactUi.Text(IsDeviceBan(current)
            ? text.Get(
                "您的账号及关联机器已经解除封禁",
                "Your account and associated devices are no longer banned")
            : text.Get("您的账号已经解除封禁", "Your account is no longer banned"), DactTheme.Palette.Accent, DactFonts.H2);
        DactUi.Wrapped(IsDeviceBan(current)
            ? text.Get(
                "服务器已确认本次机器码封禁及其附带的账号封禁已经解除。",
                "The server confirmed that this device ban and its linked account ban were lifted.")
            : text.Get(
                "服务器已确认该账号的封禁已经解除。",
                "The server confirmed that this account is no longer banned."));
        if (current is not null)
            Rows([(text.Get("原封禁时间", "Previous ban time"), current.BannedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))]);
        DactUi.Hint(text.Get(
            requiresRestart
                ? "为避免在当前运行中自动重启已经停止的原生服务，请重新加载 DACT 或重启游戏，然后重新登录。"
                : "服务器已将解封状态附在本次登录结果中，本次登录已经生效，可以继续使用 DACT。",
            requiresRestart
                ? "To avoid restarting stopped native services during this run, reload DACT or restart the game, then sign in again."
                : "The server included the unban state in this login result. This login is active and DACT can be used."));
        DactUi.EndCard();
    }

    // Label/value rows like 运行状态: muted labels in a fixed column, values wrapping beside them.
    private static void Rows(IReadOnlyList<(string Label, string Value)> rows)
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        var labelWidth = Math.Min(width * .35f, Math.Max(96 * s, rows.Max(row => DactUi.TextWidth(row.Label, DactFonts.Small)) + 8 * s));
        using (DactUi.Gap(6))
        {
            foreach (var (label, value) in rows)
            {
                var start = ImGui.GetCursorScreenPos();
                ImGui.BeginGroup();
                DactUi.Wrapped(label, DactTheme.Palette.Muted, DactFonts.Small, labelWidth);
                ImGui.EndGroup();
                var bottom = ImGui.GetItemRectMax().Y;
                ImGui.SetCursorScreenPos(start + new Vector2(labelWidth + 12 * s, 0));
                ImGui.BeginGroup();
                DactUi.Wrapped(value, DactTheme.Palette.Text, DactFonts.Small, width - labelWidth - 12 * s);
                ImGui.EndGroup();
                bottom = Math.Max(bottom, ImGui.GetItemRectMax().Y);
                ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
                ImGui.Dummy(new Vector2(width, 0));
            }
        }
    }

    private static string FormatBanType(string type, bool chinese)
        => (type, chinese) switch
        {
            ("device", true) => "机器码封禁",
            ("cascade", true) => "连坐封禁",
            ("unknown", true) => "本地封禁标记",
            (_, true) => "账号封禁",
            ("device", false) => "Device ban",
            ("cascade", false) => "Cascade ban",
            ("unknown", false) => "Local ban marker",
            _ => "Account ban",
        };

    private static bool IsDeviceBan(CloudBanNotice? current)
        => string.Equals(current?.BanType, "device", StringComparison.Ordinal);
}
