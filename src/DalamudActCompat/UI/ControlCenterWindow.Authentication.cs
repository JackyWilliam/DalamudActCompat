using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// Signed-out pages (screens.js authShell: 登录 / 创建账号 / 找回密码, and 绑定 Discord in
// DiscordBinding.cs): the landscape frame with the brand panel on the left (欢迎使用 / DACT /
// logo / slogan, as the user redrew it in Figma) and the form panel on the right.
public sealed partial class ControlCenterWindow
{
    private bool cloudShowPassword;
    private bool cloudResetPasswordStep;
    private bool authLegacyOpen;
    private bool authOtherRecoveryOpen;
    private readonly Dictionary<string, float> authFormHeights = new(StringComparer.Ordinal);

    private void DrawAuthenticationGate(CloudClientSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(cloudUsername) && !string.IsNullOrWhiteSpace(snapshot.Username))
            cloudUsername = snapshot.Username;
        DrawAuthenticationFrame("auth-" + cloudAuthenticationPage, () => DrawCloudAuthentication(snapshot));
    }

    // Brand panel (300 px) + form panel; the form is centred vertically from last frame's height.
    private void DrawAuthenticationFrame(string formId, Action drawForm)
    {
        var s = DactUi.S;
        var available = ImGui.GetContentRegionAvail();
        var brandWidth = Math.Clamp(available.X * .34f, 200 * s, 300 * s);
        if (BeginAuthenticationPanel("auth-brand", new Vector2(brandWidth, available.Y), false, new Vector2(24, 28)))
            DrawAuthenticationBrand();
        EndAuthenticationPanel();
        ImGui.SameLine(0, 18 * s);
        // The form keeps the design's 64 px side padding on wide windows and tightens on narrow ones.
        var sidePadding = Math.Clamp((available.X - brandWidth - 18 * s - 360 * s) / s * .5f, 20, 64);
        if (BeginAuthenticationPanel("auth-form", new Vector2(-1, available.Y), true, new Vector2(sidePadding, 26)))
        {
            var room = ImGui.GetContentRegionAvail().Y;
            var measured = authFormHeights.GetValueOrDefault(formId);
            // ImGui truncates every item's position to a whole pixel, so the same form measures a
            // little taller or shorter depending on the fraction of its starting y. At non-integer
            // UI scales (Dalamud's default 16 px font is already one), centring on last frame's
            // height moved that fraction, which changed the height again and the form shook every
            // frame. The form always starts on a whole pixel, centred or not, so its height no
            // longer depends on where it starts.
            var cursor = ImGui.GetCursorScreenPos();
            var offset = measured > 0 && measured < room ? (room - measured) * .5f : 0;
            ImGui.SetCursorScreenPos(cursor with { Y = MathF.Ceiling(cursor.Y + offset) });
            var start = ImGui.GetCursorPosY();
            using (DactUi.Gap(14)) drawForm();
            authFormHeights[formId] = ImGui.GetCursorPosY() - start - 14 * s;
        }
        EndAuthenticationPanel();
    }

    private static bool BeginAuthenticationPanel(string id, Vector2 size, bool accent, Vector2 padding)
    {
        var s = DactUi.S;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, Math.Max(1, s));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding * s);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, DactTheme.Palette.Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, accent ? DactTheme.Palette.Accent with { W = .45f } : DactTheme.Palette.Border);
        return ImGui.BeginChild(id, size, true, accent ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
    }

    private static void EndAuthenticationPanel()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    private void DrawAuthenticationBrand()
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var width = ImGui.GetContentRegionAvail().X;
        var room = ImGui.GetContentRegionAvail().Y;
        var logo = Math.Min(184 * s, Math.Min(width, room - 120 * s));
        var total = 24 * s * 2 + logo + 18 * s + 10 * s * 3;
        var origin = ImGui.GetCursorScreenPos() + new Vector2(0, Math.Max(0, (room - total) * .5f));
        var draw = ImGui.GetWindowDrawList();
        void Centered(string value, Vector4 color, int size, float y)
            => DactUi.DrawText(draw, new Vector2(origin.X + (width - DactUi.TextWidth(value, size)) * .5f, y), 24 * s, value, color, size);
        var y = origin.Y;
        Centered(text.Get("欢迎使用", "Welcome to"), palette.Text, DactFonts.Title, y);
        y += 24 * s + 10 * s;
        Centered("DACT", palette.Accent, DactFonts.Title, y);
        y += 24 * s + 10 * s;
        if (logo > 0)
        {
            // The splash's flying logo lands on this spot, so it stays hidden until the splash ends.
            brandLogoSpot = (new Vector2(origin.X + (width - logo) * .5f, y), logo);
            if (!SplashActive)
                draw.AddImage(logoTexture.GetWrapOrEmpty().Handle, new Vector2(origin.X + (width - logo) * .5f, y), new Vector2(origin.X + (width + logo) * .5f, y + logo));
            y += logo + 10 * s;
        }
        Centered(text.Get("你的战斗工具，尽在一处", "Your combat workspace"), palette.Accent, DactFonts.Body, y - 3 * s);
        ImGui.Dummy(new Vector2(width, room));
    }

    private void SelectAuthenticationPage(CloudAuthenticationPage page, CloudClientSnapshot snapshot)
    {
        cloudAuthenticationPreviousStatus = snapshot.StatusMessage;
        ClearCloudSecrets();
        cloudAuthenticationPage = page;
    }

    // dsl authTitle: accent heading (15) with a hint below.
    private void DrawAuthenticationTitle(string title, string description)
    {
        using (DactUi.Gap(4))
        {
            DactUi.Text(title, DactTheme.Palette.Accent, DactFonts.H2);
            DactUi.Hint(description);
        }
    }

    private void DrawCloudAuthentication(CloudClientSnapshot snapshot)
    {
        var busy = snapshot.IsBusy;
        if (cloudAuthenticationPage == CloudAuthenticationPage.ResetPassword)
        {
            if (DactUi.Button(text.Get("‹ 返回登录", "‹ Back to sign in") + "##auth-back", ButtonKind.Ghost))
                SelectAuthenticationPage(CloudAuthenticationPage.Login, snapshot);
        }
        else
        {
            var selected = (int)cloudAuthenticationPage;
            if (DactUi.Segmented("##auth-pages", [text.Get("登录账号", "Sign in"), text.Get("创建账号", "Create account")], ref selected,
                    ImGui.GetContentRegionAvail().X))
                SelectAuthenticationPage((CloudAuthenticationPage)selected, snapshot);
        }
        var (title, description) = cloudAuthenticationPage switch
        {
            CloudAuthenticationPage.Register => (text.Get("创建 DACT 账号", "Create a DACT account"),
                text.Get("使用免费激活码注册，随后绑定 Discord。", "Register with a free activation key, then link Discord.")),
            CloudAuthenticationPage.ResetPassword => (text.Get("找回账号密码", "Recover your account"),
                text.Get("通过 Discord 机器人找回，保留原有云备份。", "Recover through the Discord bot and keep your cloud backups.")),
            _ => (text.Get("欢迎回来", "Welcome back"), text.Get("登录后继续使用你的解析器、悬浮窗和配置。", "Sign in to continue with your parsers, overlays and settings.")),
        };
        DrawAuthenticationTitle(title, description);
        if ((snapshot.IsBusy || snapshot.StatusIsError) && snapshot.StatusMessage != cloudAuthenticationPreviousStatus)
            DrawAuthenticationStatus(snapshot);
        ImGui.BeginDisabled(busy);
        switch (cloudAuthenticationPage)
        {
            case CloudAuthenticationPage.Login: DrawCloudLoginForm(busy); break;
            case CloudAuthenticationPage.Register: DrawCloudRegistrationForm(busy); break;
            case CloudAuthenticationPage.ResetPassword: DrawCloudRecoveryWizard(snapshot); break;
        }
        ImGui.EndDisabled();
        // Outside the disabled block: the prompt must stay usable while the page is idle-busy.
        DrawDeviceLimitDialog(snapshot);
    }

    private void DrawAuthenticationUsername()
    {
        DactUi.BeginField(text.Get("用户名", "Username"));
        DactUi.Input("##cloud-username", ref cloudUsername, text.Get("请输入账号名", "Enter your username"), 32);
        DactUi.EndField();
    }

    private void DrawSecretField(string label, string placeholder, string id, ref string value, int maxLength, string? hint = null)
    {
        DactUi.BeginField(label);
        DactUi.Input(id, ref value, placeholder, maxLength, secret: true);
        DactUi.EndField(hint);
    }

    private bool EnterPressedInForm() => ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) &&
                                         (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter));

    private void DrawCloudLoginForm(bool busy)
    {
        var s = DactUi.S;
        DrawAuthenticationUsername();
        DrawSecretField(text.Get("密码", "Password"), text.Get("请输入密码", "Enter your password"), "##cloud-password", ref cloudPassword, 128);
        // 记住登录状态 on the left, 忘记密码？ at the right end of the same row.
        var rowStart = ImGui.GetCursorPos();
        var width = ImGui.GetContentRegionAvail().X;
        DrawAuthenticationPersistenceOption();
        var optionWidth = ImGui.GetItemRectMax().X - ImGui.GetItemRectMin().X;
        var forgot = text.Get("忘记密码？", "Forgot password?");
        var forgotWidth = DactUi.ButtonWidth(forgot, ButtonKind.Ghost);
        var after = ImGui.GetCursorPos();
        // Long translations (German) would run the checkbox label under the link; it then moves
        // to its own line, still right-aligned.
        var ownLine = optionWidth + 12 * s > width - forgotWidth;
        ImGui.SetCursorPos(new Vector2(rowStart.X + width - forgotWidth, (ownLine ? after.Y : rowStart.Y) - 3 * s));
        if (DactUi.Button(forgot + "##auth-forgot", ButtonKind.Ghost))
            SelectAuthenticationPage(CloudAuthenticationPage.ResetPassword, cloud.GetSnapshot());
        if (ownLine) after = ImGui.GetCursorPos();
        ImGui.SetCursorPos(after);
        var canSubmit = !busy && !string.IsNullOrWhiteSpace(cloudUsername) && cloudPassword.Length > 0;
        if ((DactUi.Button(text.Get("登录 DACT", "Sign in to DACT") + "##auth-login", ButtonKind.Primary, width: width, disabled: !canSubmit) ||
             EnterPressedInForm()) && canSubmit)
        {
            cloud.Login(new(cloudUsername, cloudPassword, cloudRecoveryKey, cloudRememberLogin));
            cloudPassword = cloudRecoveryKey = string.Empty;
            cloudShowPassword = false;
        }
        // Said before the name leaves the PC: it is new data the server did not receive before.
        DactUi.Hint(text.Get("登录时会把这台电脑的名称发给 DACT 服务器，只用于在你的账号里区分设备；每个账号最多同时登录 5 台。",
            "Signing in sends this PC's name to the DACT server, only to tell your devices apart; an account can be signed in on up to 5 at once."));
        DactUi.Disclosure(text.Get("旧账号恢复选项", "Legacy account recovery"), ref authLegacyOpen);
        if (authLegacyOpen)
            DrawSecretField(text.Get("原恢复密钥（可选）", "Original recovery key (optional)"), text.Get("通常无需填写", "Normally not required"),
                "##cloud-login-recovery-key", ref cloudRecoveryKey, 96,
                text.Get("仅供管理员直接改密、且本机未保存密钥的旧账号使用。", "For legacy accounts reset directly by an administrator without a saved local key."));
    }

    private void DrawCloudRegistrationForm(bool busy)
    {
        var width = ImGui.GetContentRegionAvail().X;
        if (DactUi.BeginGrid("##register-fields", 12))
        {
            using (DactUi.Gap(14))
            {
                DrawAuthenticationUsername();
                DrawSecretField(text.Get("密码", "Password"), text.Get("至少 10 位", "At least 10 characters"), "##cloud-register-password", ref cloudPassword, 128);
            }
            DactUi.NextColumn();
            using (DactUi.Gap(14))
            {
                DactUi.BeginField(text.Get("免费激活码", "Free activation key"));
                DactUi.Input("##cloud-activation-key", ref cloudActivationKey, text.Get("输入一次性激活码", "One-time activation key"), 96);
                DactUi.EndField();
                DrawSecretField(text.Get("确认密码", "Confirm password"), text.Get("再次输入密码", "Repeat your password"), "##cloud-register-confirm", ref cloudPasswordConfirmation, 128);
            }
            DactUi.EndGrid();
        }
        var passwordsMatch = cloudPassword.Length >= 10 && cloudPassword == cloudPasswordConfirmation;
        DactUi.Wrapped(text.Get("两次密码须相同，且至少 10 位。", "Passwords must match and contain at least 10 characters."),
            cloudPasswordConfirmation.Length > 0 && !passwordsMatch ? DactTheme.Palette.Gold : DactUi.HintColor, DactFonts.Small);
        DrawAuthenticationPersistenceOption();
        if (DactUi.Button(text.Get("创建账号", "Create account") + "##auth-register", ButtonKind.Primary, width: width,
                disabled: busy || !passwordsMatch || string.IsNullOrWhiteSpace(cloudUsername) || string.IsNullOrWhiteSpace(cloudActivationKey)))
        {
            cloud.Register(new(cloudUsername, cloudPassword, cloudActivationKey, cloudRememberLogin));
            ClearCloudSecrets();
        }
        DrawCloudActivationKeyHelp();
    }

    // New users cannot reach the signed-in help yet, so the free-key channels sit beside the form
    // in a gold-tinted box (dsl 激活码说明).
    private void DrawCloudActivationKeyHelp()
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(12 * s, 10 * s));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - 24 * s);
        using (DactUi.Gap(6))
        {
            DactUi.Wrapped(text.Get("DACT 和激活码均免费，请勿购买或转售。", "DACT and activation keys are free. Do not buy or resell them."), palette.Gold, DactFonts.Small, width - 24 * s);
            DactUi.Wrapped(text.Format($"加入 QQ 群 {ActivationQqGroup} 或 Discord，联系群主或频道主获取激活码。",
                $"Join QQ group {ActivationQqGroup} or Discord and contact the group/server owner for an activation key."), DactUi.HintColor, DactFonts.Small, width - 24 * s);
            if (DactUi.Button(text.Get("复制 QQ 群号", "Copy QQ group") + "##auth-qq")) ImGui.SetClipboardText(ActivationQqGroup);
            var discord = text.Get("打开 Discord", "Open Discord");
            DactUi.SameLineIfFits(DactUi.ButtonWidth(discord, icon: "external"));
            if (DactUi.Button(discord + "##auth-discord", icon: "external")) OpenUrl(ActivationDiscordUrl);
            if (ImGui.IsItemHovered()) DactUi.Tooltip(ActivationDiscordUrl);
        }
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        var end = ImGui.GetItemRectMax() + new Vector2(0, 10 * s);
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, new Vector2(start.X + width, end.Y), DactUi.U32(DactUi.GoldSoft), 8 * s);
        draw.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
        ImGui.Dummy(new Vector2(width, 0));
    }

    // 找回密码: step 1 takes the bot's reset code, step 2 is the same password pair as 创建账号.
    private void DrawCloudRecoveryWizard(CloudClientSnapshot snapshot)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        DrawResetSteps();
        if (!cloudResetPasswordStep)
        {
            DrawCloudDiscordEntry();
            DrawAuthenticationUsername();
            DactUi.BeginField(text.Get("一次性重置码", "One-time reset code"));
            DactUi.Input("##cloud-reset-code", ref cloudResetCode, text.Get("粘贴机器人提供的 DACT-RESET 码", "Paste the DACT-RESET code from the bot"), 96);
            // No separate verification endpoint exists: only the final submission verifies the
            // code and changes the password atomically, so never claim it is verified here.
            DactUi.EndField(text.Get("重置码会在确认修改时验证。", "The reset code is verified when you confirm the change."));
            DactUi.Disclosure(text.Get("其他找回方式（旧账号恢复密钥、管理员重置码）", "Other recovery methods (legacy recovery key, administrator code)"), ref authOtherRecoveryOpen);
            if (authOtherRecoveryOpen)
            {
                DactUi.Hint(text.Get("未绑定 Discord 的旧账号可使用原恢复密钥，或联系管理员获取重置码。",
                    "Legacy accounts can use their original recovery key or request a reset code from an administrator."));
                if (DactUi.Button(text.Get("使用原恢复密钥", "Use original recovery key") + "##auth-use-key"))
                {
                    cloudPasswordResetMethod = CloudPasswordResetMethod.RecoveryKey;
                    cloudResetPasswordStep = true;
                }
            }
            if (DactUi.Button(text.Get("继续设置密码", "Continue to password") + "##auth-reset-next", ButtonKind.Primary, width: ImGui.GetContentRegionAvail().X,
                    disabled: snapshot.IsBusy || string.IsNullOrWhiteSpace(cloudUsername) || string.IsNullOrWhiteSpace(cloudResetCode)))
            {
                cloudPasswordResetMethod = CloudPasswordResetMethod.AdministratorCode;
                cloudResetPasswordStep = true;
            }
            return;
        }

        if (DactUi.Button(text.Get("‹ 返回上一步", "‹ Previous step") + "##auth-reset-back", ButtonKind.Ghost))
        {
            cloudResetPasswordStep = false;
            cloudPassword = cloudPasswordConfirmation = string.Empty;
        }
        DrawAuthenticationUsername();
        DrawCloudPasswordResetForm(snapshot.IsBusy, cloud.HasRecoveryKeyFor(cloudUsername));
    }

    // dsl resetStep: 1 获取重置码 ── 2 设置新密码, the current step filled with the accent.
    private void DrawResetSteps()
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        float Step(float x, string number, string label, bool active)
        {
            var center = new Vector2(x + 10 * s, start.Y + 10 * s);
            draw.AddCircleFilled(center, 10 * s, DactUi.U32(active ? palette.Accent : palette.Hover), 24);
            DactUi.DrawText(draw, new Vector2(center.X - DactUi.TextWidth(number, DactFonts.Tiny) * .5f, start.Y), 20 * s, number, active ? palette.Surface : palette.Muted, DactFonts.Tiny);
            DactUi.DrawText(draw, new Vector2(x + 26 * s, start.Y), 20 * s, label, active ? palette.Text : palette.Muted);
            return x + 26 * s + DactUi.TextWidth(label);
        }
        var x = Step(start.X, "1", text.Get("获取重置码", "Get reset code"), !cloudResetPasswordStep);
        draw.AddRectFilled(new Vector2(x + 10 * s, start.Y + 10 * s), new Vector2(x + 50 * s, start.Y + 10 * s + Math.Max(1, s)), DactUi.U32(DactUi.DividerColor));
        Step(x + 60 * s, "2", text.Get("设置新密码", "Set new password"), cloudResetPasswordStep);
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, 20 * s));
    }

    private void DrawCloudPasswordResetForm(bool busy, bool hasLocalRecoveryKey)
    {
        DrawSecretField(text.Get("新密码", "New password"), text.Get("至少 10 位", "At least 10 characters"), "##cloud-new-password", ref cloudPassword, 128);
        DrawSecretField(text.Get("确认新密码", "Confirm new password"), text.Get("再次输入新密码", "Enter the new password again"),
            "##cloud-new-password-confirm", ref cloudPasswordConfirmation, 128, text.Get("两次密码须相同，且至少 10 位。", "Passwords must match and contain at least 10 characters."));
        if (cloudPasswordResetMethod == CloudPasswordResetMethod.AdministratorCode)
            DactUi.Hint(text.Get("已建立恢复保护的账号不需要填写密钥，原有云备份会继续保留。",
                "Protected accounts need no recovery key; existing cloud backups stay available."));
        if (hasLocalRecoveryKey)
            DactUi.Hint(text.Get("已检测到本机保存的恢复密钥，将自动用于改密和解锁原有云备份。",
                "The saved recovery key on this PC will reset the password and unlock existing cloud backups."));
        else
        {
            var required = cloudPasswordResetMethod == CloudPasswordResetMethod.RecoveryKey;
            if (!required) DactUi.Disclosure(text.Get("旧账号：手动填写恢复密钥（可选）", "Legacy account: enter recovery key (optional)"), ref authLegacyOpen);
            if (required || authLegacyOpen)
                DrawSecretField(text.Get(required ? "恢复密钥（必填）" : "恢复密钥（可选）", required ? "Recovery key (required)" : "Recovery key (optional)"),
                    text.Get("输入注册时保存的 dact1_ 密钥", "Enter the dact1_ key saved at registration"), "##cloud-recovery-key", ref cloudRecoveryKey, 96,
                    text.Get(required
                            ? "恢复密钥会验证账号归属，并在改密后继续解锁原有云备份。"
                            : "管理员重置码授权改密；恢复密钥用于继续解锁原有云备份。",
                        required
                            ? "The recovery key verifies account ownership and keeps existing cloud backups readable."
                            : "The administrator code authorizes the reset; the recovery key keeps existing cloud backups readable."));
        }
        var passwordsMatch = cloudPassword.Length >= 10 && cloudPassword == cloudPasswordConfirmation;
        var credentialValid = cloudPasswordResetMethod == CloudPasswordResetMethod.RecoveryKey || !string.IsNullOrWhiteSpace(cloudResetCode);
        DrawAuthenticationPersistenceOption();
        if (DactUi.Button(text.Get("确认重置密码", "Reset password") + "##auth-reset", ButtonKind.Primary, width: ImGui.GetContentRegionAvail().X,
                disabled: busy || !passwordsMatch || !credentialValid || string.IsNullOrWhiteSpace(cloudUsername) ||
                          cloudPasswordResetMethod == CloudPasswordResetMethod.RecoveryKey && !hasLocalRecoveryKey && string.IsNullOrWhiteSpace(cloudRecoveryKey)))
        {
            cloud.ResetPassword(new CloudPasswordResetRequest(cloudUsername, cloudResetCode, cloudPassword, cloudRecoveryKey,
                cloudRememberLogin, cloudPasswordResetMethod));
            ClearCloudSecrets();
        }
    }

    // Busy / error line under the title: accent or danger tint with an icon (dsl 状态 row).
    private void DrawAuthenticationStatus(CloudClientSnapshot snapshot)
    {
        var message = snapshot.IsBusy
            ? $"{text.SystemMessage(snapshot.StatusMessage)}  {text.Get("处理中…", "Working…")}"
            : text.SystemMessage(snapshot.StatusMessage);
        if (string.IsNullOrWhiteSpace(message)) return;
        DrawAuthStatusRow(message, snapshot.StatusIsError, snapshot.IsBusy ? "refresh" : snapshot.StatusIsError ? "close" : "check", null);
    }

    // Returns true when the optional trailing action was clicked.
    private bool DrawAuthStatusRow(string message, bool error, string icon, string? action)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var ink = error ? DactUi.Danger : palette.Accent;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var actionWidth = action is null ? 0 : DactUi.ButtonWidth(action, ButtonKind.Ghost) + 8 * s;
        var lines = DactUi.WrapLines(message, width - 46 * s - actionWidth);
        var height = Math.Max(36 * s, lines.Count * 16 * s + 16 * s);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(error ? DactUi.DangerSoft : DactUi.AccentSoft), 8 * s);
        DactIcons.Draw(draw, icon, start + new Vector2(12 * s, (height - 14 * s) * .5f), 14 * s, ink);
        var y = start.Y + (height - lines.Count * 16 * s) * .5f;
        foreach (var line in lines)
        {
            DactUi.DrawText(draw, new Vector2(start.X + 34 * s, y), 16 * s, line, ink, DactFonts.Small);
            y += 16 * s;
        }
        var clicked = false;
        if (action is not null)
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X + width - actionWidth, start.Y + (height - 30 * s) * .5f));
            clicked = DactUi.Button(action + "##auth-status-action", ButtonKind.Ghost);
        }
        ImGui.SetCursorScreenPos(start + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 0));
        return clicked;
    }

    private void DrawAuthenticationPersistenceOption()
    {
        DactUi.Checkbox(text.Get("记住登录状态（下次自动登录）", "Remember sign-in (auto-login next time)") + "##auth-remember", ref cloudRememberLogin);
        if (ImGui.IsItemHovered())
            DactUi.Tooltip(text.Get("登录状态使用 Windows 当前用户加密，并在重启游戏或电脑后恢复。",
                "Windows protects the saved session for this user and restores it after a game or PC restart."));
    }
}
