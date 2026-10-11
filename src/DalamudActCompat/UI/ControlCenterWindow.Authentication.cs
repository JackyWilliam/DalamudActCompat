using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using System.Numerics;

namespace DalamudActCompat.UI;

public sealed partial class ControlCenterWindow
{
    private bool cloudShowPassword;
    private bool cloudResetPasswordStep;
    private void DrawAuthenticationGate(CloudClientSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(cloudUsername) && !string.IsNullOrWhiteSpace(snapshot.Username))
            cloudUsername = snapshot.Username;
        var available = ImGui.GetContentRegionAvail();
        // Keep the established landscape shell even at the minimum window size.
        // Only the form scrolls at large font scales; the brand never becomes a header.
        var heroWidth = Math.Clamp(available.X * .31f, 180, 310);
        if (BeginAuthenticationPanel("account-authentication-hero", new(heroWidth, -1), true, false))
            DrawAuthenticationHero(compact: available.Y < 360);
        EndAuthenticationPanel();
        ImGui.SameLine(0, 14);
        if (BeginAuthenticationPanel("account-authentication-card", new(-1, -1), false, true))
        {
            var width = Math.Min(ImGui.GetContentRegionAvail().X, 460 * ImGui.GetIO().FontGlobalScale);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2));
            if (ImGui.BeginChild("authentication-form", new(width, -1), false)) DrawCloudAuthentication(snapshot);
            ImGui.EndChild();
        }
        EndAuthenticationPanel();
    }

    private void SelectAuthenticationPage(CloudAuthenticationPage page, CloudClientSnapshot snapshot)
    {
        cloudAuthenticationPreviousStatus = snapshot.StatusMessage;
        ClearCloudSecrets();
        cloudAuthenticationPage = page;
    }

    private void DrawCloudAuthentication(CloudClientSnapshot snapshot)
    {
        var busy = snapshot.IsBusy;
        if (cloudAuthenticationPage == CloudAuthenticationPage.ResetPassword)
        {
            if (DactTheme.Button(text.Get("‹ 返回登录", "‹ Back to sign in")))
                SelectAuthenticationPage(CloudAuthenticationPage.Login, snapshot);
        }
        else
        {
            var selected = BrandedWindowChrome.DrawNavigationRail("account-authentication-navigation",
                new[] { text.Get("登录账号", "Sign in"), text.Get("创建账号", "Create account") },
                (int)cloudAuthenticationPage, height: 34);
            if (selected != (int)cloudAuthenticationPage) SelectAuthenticationPage((CloudAuthenticationPage)selected, snapshot);
        }
        ImGui.Dummy(new(0, 10));
        var (title, description) = cloudAuthenticationPage switch
        {
            CloudAuthenticationPage.Register => (text.Get("创建 DACT 账号", "Create a DACT account"),
                text.Get("使用免费激活码注册，随后绑定 Discord。", "Register with a free activation key, then link Discord.")),
            CloudAuthenticationPage.ResetPassword => (text.Get("找回账号密码", "Recover your account"),
                text.Get("通过 Discord 机器人找回，保留原有云备份。", "Recover through the Discord bot and keep your cloud backups.")),
            _ => (text.Get("欢迎回来", "Welcome back"), text.Get("登录后继续使用你的解析器、悬浮窗和配置。", "Sign in to continue with your parsers, overlays and settings.")),
        };
        ImGui.SetWindowFontScale(1.2f); DactTheme.TextColored(IceBlue, title); ImGui.SetWindowFontScale(1);
        DrawAuthenticationMutedText(description);
        ImGui.Spacing();
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
    }

    private void DrawAuthenticationUsername()
        => DrawAuthenticationInput(text.Get("用户名", "Username"), text.Get("请输入账号名", "Enter your username"),
            "cloud-username", ref cloudUsername, 32);

    private void DrawCloudRecoveryWizard(CloudClientSnapshot snapshot)
    {
        DactTheme.TextColored(Gold, cloudResetPasswordStep
            ? text.Get("① 获取重置码    →    ② 设置新密码", "1. Get reset code    →    2. Set password")
            : text.Get("① 获取重置码    ·    ② 设置新密码", "1. Get reset code    ·    2. Set password"));
        ImGui.Spacing();
        if (!cloudResetPasswordStep)
        {
            DrawCloudDiscordEntry();
            DrawAuthenticationMutedText(text.Get("在 /dact 菜单点「找回密码」，选择账号后复制重置码。", "Choose Recover password in /dact, select your account, and copy the reset code."));
            ImGui.Spacing();
            DrawAuthenticationUsername();
            DrawAuthenticationInput(text.Get("一次性重置码", "One-time reset code"), text.Get("粘贴机器人提供的 DACT-RESET 码", "Paste the DACT-RESET code from the bot"),
                "cloud-reset-code", ref cloudResetCode, 96);
            if (ImGui.CollapsingHeader(text.Get("其他找回方式", "Other recovery methods")))
            {
                DrawAuthenticationMutedText(text.Get("未绑定 Discord 的旧账号可使用原恢复密钥，或联系管理员获取重置码。", "Legacy accounts can use their original recovery key or request a reset code from an administrator."));
                if (DactTheme.Button(text.Get("使用原恢复密钥", "Use original recovery key")))
                { cloudPasswordResetMethod = CloudPasswordResetMethod.RecoveryKey; cloudResetPasswordStep = true; }
            }
            ImGui.Spacing();
            if (DrawAuthenticationPrimaryButton(text.Get("继续设置密码", "Continue to password"), !snapshot.IsBusy &&
                    !string.IsNullOrWhiteSpace(cloudUsername) && !string.IsNullOrWhiteSpace(cloudResetCode)))
            { cloudPasswordResetMethod = CloudPasswordResetMethod.AdministratorCode; cloudResetPasswordStep = true; }
            // No separate verification endpoint exists: only final submission verifies
            // the code and changes the password atomically, so never claim it is verified here.
            DrawAuthenticationMutedText(text.Get("重置码会在确认修改时验证。", "The reset code is verified when you confirm the change."));
        }
        else
        {
            if (DactTheme.Button(text.Get("‹ 返回上一步", "‹ Previous step")))
            { cloudResetPasswordStep = false; cloudPassword = cloudPasswordConfirmation = string.Empty; }
            DrawAuthenticationUsername();
            DrawCloudPasswordResetForm(snapshot.IsBusy, cloud.HasRecoveryKeyFor(cloudUsername), wizard: true);
        }
    }

    private void DrawCloudLoginForm(bool busy)
    {
        DrawAuthenticationUsername();
        var submitWithEnter = DrawAuthenticationInput(text.Get("密码", "Password"), text.Get("请输入密码", "Enter your password"),
            "cloud-password", ref cloudPassword, 128, ImGuiInputTextFlags.EnterReturnsTrue |
                (cloudShowPassword ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password));
        DactTheme.Checkbox(text.Get("显示密码", "Show password"), ref cloudShowPassword);
        DrawAuthenticationPersistenceOption();
        if (DactTheme.Button(text.Get("忘记密码？", "Forgot password?")))
            SelectAuthenticationPage(CloudAuthenticationPage.ResetPassword, cloud.GetSnapshot());
        ImGui.Spacing();
        var canSubmit = !busy && !string.IsNullOrWhiteSpace(cloudUsername) && cloudPassword.Length > 0;
        if ((DrawAuthenticationPrimaryButton(text.Get("登录 DACT", "Sign in to DACT"), canSubmit) || submitWithEnter) && canSubmit)
        {
            cloud.Login(new(cloudUsername, cloudPassword, cloudRecoveryKey, cloudRememberLogin));
            cloudPassword = cloudRecoveryKey = string.Empty; cloudShowPassword = false;
        }
        ImGui.Spacing();
        if (ImGui.CollapsingHeader(text.Get("旧账号恢复选项", "Legacy account recovery")))
        {
            DrawAuthenticationInput(text.Get("原恢复密钥（可选）", "Original recovery key (optional)"), text.Get("通常无需填写", "Normally not required"),
                "cloud-login-recovery-key", ref cloudRecoveryKey, 96, ImGuiInputTextFlags.Password);
            DrawAuthenticationMutedText(text.Get("仅供管理员直接改密、且本机未保存密钥的旧账号使用。", "For legacy accounts reset directly by an administrator without a saved local key."));
        }
    }

    private void DrawCloudRegistrationForm(bool busy)
    {
        // A table keeps paired fields aligned while respecting native font scaling.
        if (ImGui.BeginTable("register-fields", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); DrawAuthenticationUsername();
            ImGui.TableNextColumn();
            DrawAuthenticationInput(text.Get("免费激活码", "Free activation key"), text.Get("输入一次性激活码", "One-time activation key"),
                "cloud-activation-key", ref cloudActivationKey, 96);
            ImGui.TableNextColumn();
            DrawAuthenticationInput(text.Get("密码", "Password"), text.Get("至少 10 位", "At least 10 characters"),
                "cloud-register-password", ref cloudPassword, 128, ImGuiInputTextFlags.Password);
            ImGui.TableNextColumn();
            DrawAuthenticationInput(text.Get("确认密码", "Confirm password"), text.Get("再次输入密码", "Repeat your password"),
                "cloud-register-confirm", ref cloudPasswordConfirmation, 128, ImGuiInputTextFlags.Password);
            ImGui.EndTable();
        }
        var passwordsMatch = cloudPassword.Length >= 10 && cloudPassword == cloudPasswordConfirmation;
        if (cloudPasswordConfirmation.Length > 0 && !passwordsMatch)
            DrawAuthenticationMutedText(text.Get("两次密码须相同，且至少 10 位。", "Passwords must match and contain at least 10 characters."));
        DrawAuthenticationPersistenceOption();
        ImGui.Spacing();
        if (DrawAuthenticationPrimaryButton(text.Get("创建账号", "Create account"), !busy && passwordsMatch &&
                !string.IsNullOrWhiteSpace(cloudUsername) && !string.IsNullOrWhiteSpace(cloudActivationKey)))
        {
            cloud.Register(new(cloudUsername, cloudPassword, cloudActivationKey, cloudRememberLogin));
            ClearCloudSecrets();
        }
        ImGui.Spacing();
        DrawCloudActivationKeyHelp();
    }

    private void DrawCloudActivationKeyHelp()
    {
        // New users cannot reach the signed-in help page; keep the free-key
        // channels visible beside registration instead of behind another tab.
        DrawAuthenticationMutedText(text.Get(
            "DACT 和激活码均免费，请勿购买或转售。",
            "DACT and activation keys are free. Do not buy or resell them."));
        DrawAuthenticationMutedText(text.Format(
            $"加入 QQ 群 {ActivationQqGroup} 或 Discord，联系群主或频道主获取激活码。",
            $"Join QQ group {ActivationQqGroup} or Discord and contact the group/server owner for an activation key."));

        var qqLabel = text.Get("复制 QQ 群号", "Copy QQ group");
        var discordLabel = text.Get("打开 Discord", "Open Discord");
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var buttonsWidth = ImGui.CalcTextSize(qqLabel).X + ImGui.CalcTextSize(discordLabel).X +
                           ImGui.GetStyle().FramePadding.X * 4 + ImGui.GetStyle().ItemSpacing.X;
        if (DactTheme.Button(qqLabel))
        {
            ImGui.SetClipboardText(ActivationQqGroup);
        }
        // At narrow widths or large font scales, stack the buttons inside the
        // existing scrollable authentication card instead of overflowing it.
        if (availableWidth >= buttonsWidth)
        {
            ImGui.SameLine();
        }
        if (DactTheme.Button(discordLabel))
        {
            OpenUrl(ActivationDiscordUrl);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(ActivationDiscordUrl);
        }
        ImGui.Spacing();
    }

    private void DrawCloudPasswordResetForm(bool busy, bool hasLocalRecoveryKey, bool wizard = false)
    {
        if (!wizard)
        {
            var resetMethods = new[]
            {
                text.Get("Discord 机器人 / 重置码", "Discord bot / reset code"),
                text.Get("恢复密钥自助改密", "Reset with recovery key"),
            };
            var selectedResetMethod = BrandedWindowChrome.DrawNavigationRail(
                "cloud-password-reset-method",
                resetMethods,
                cloudPasswordResetMethod == CloudPasswordResetMethod.AdministratorCode ? 0 : 1,
                height: 32);
            cloudPasswordResetMethod = selectedResetMethod == 0 ? CloudPasswordResetMethod.AdministratorCode : CloudPasswordResetMethod.RecoveryKey;
            ImGui.Spacing();

        }
        if (!wizard && cloudPasswordResetMethod == CloudPasswordResetMethod.AdministratorCode)
        {
            DrawCloudDiscordEntry();
            ImGui.Spacing();
            DrawAuthenticationInput(
                text.Get("一次性重置码（必填）", "One-time reset code (required)"),
                text.Get("输入机器人或管理员提供的重置码", "Enter the code from the bot or an administrator"),
                "cloud-reset-code",
                ref cloudResetCode,
                96);
        }
        DrawAuthenticationInput(
            text.Get("新密码", "New password"),
            text.Get("至少 10 位", "At least 10 characters"),
            "cloud-new-password",
            ref cloudPassword,
            128,
            ImGuiInputTextFlags.Password);
        DrawAuthenticationInput(
            text.Get("确认新密码", "Confirm new password"),
            text.Get("再次输入新密码", "Enter the new password again"),
            "cloud-new-password-confirm",
            ref cloudPasswordConfirmation,
            128,
            ImGuiInputTextFlags.Password);
        if (cloudPasswordResetMethod == CloudPasswordResetMethod.AdministratorCode)
        {
            DrawAuthenticationMutedText(text.Get(
                "已建立恢复保护的账号不需要填写密钥，原有云备份会继续保留。",
                "Protected accounts need no recovery key; existing cloud backups stay available."));
        }
        if (hasLocalRecoveryKey)
        {
            DrawAuthenticationMutedText(text.Get(
                "已检测到本机保存的恢复密钥，将自动用于改密和解锁原有云备份。",
                "The saved recovery key on this PC will reset the password and unlock existing cloud backups."));
        }
        else if (cloudPasswordResetMethod == CloudPasswordResetMethod.RecoveryKey ||
                 ImGui.CollapsingHeader(text.Get("旧账号：手动填写恢复密钥（可选）", "Legacy account: enter recovery key (optional)")))
        {
            DrawAuthenticationInput(
                text.Get("恢复密钥（必填）", "Recovery key (required)"),
                text.Get("输入注册时保存的 dact1_ 密钥", "Enter the dact1_ key saved at registration"),
                "cloud-recovery-key",
                ref cloudRecoveryKey,
                96,
                ImGuiInputTextFlags.Password);
            DrawAuthenticationMutedText(text.Get(
                cloudPasswordResetMethod == CloudPasswordResetMethod.RecoveryKey
                    ? "恢复密钥会验证账号归属，并在改密后继续解锁原有云备份。"
                    : "管理员重置码授权改密；恢复密钥用于继续解锁原有云备份。",
                cloudPasswordResetMethod == CloudPasswordResetMethod.RecoveryKey
                    ? "The recovery key verifies account ownership and keeps existing cloud backups readable."
                    : "The administrator code authorizes the reset; the recovery key keeps existing cloud backups readable."));
        }

        var passwordsMatch = cloudPassword.Length >= 10 &&
                             cloudPassword == cloudPasswordConfirmation;
        var resetCredentialIsValid =
            cloudPasswordResetMethod == CloudPasswordResetMethod.RecoveryKey ||
            !string.IsNullOrWhiteSpace(cloudResetCode);
        DrawAuthenticationPersistenceOption();
        if (DrawAuthenticationPrimaryButton(
                text.Get("确认重置密码", "Reset password"),
                !busy && passwordsMatch && resetCredentialIsValid &&
                !string.IsNullOrWhiteSpace(cloudUsername) &&
                (cloudPasswordResetMethod == CloudPasswordResetMethod.AdministratorCode ||
                 hasLocalRecoveryKey || !string.IsNullOrWhiteSpace(cloudRecoveryKey))))
        {
            cloud.ResetPassword(new CloudPasswordResetRequest(
                cloudUsername,
                cloudResetCode,
                cloudPassword,
                cloudRecoveryKey,
                cloudRememberLogin,
                cloudPasswordResetMethod));
            ClearCloudSecrets();
        }
    }

    private void DrawAuthenticationHero(bool compact)
    {
        ImGui.Dummy(new(0, compact ? 0 : 12));
        ImGui.SetWindowFontScale(1.6f); DrawCenteredAuthenticationText("DACT", Gold); ImGui.SetWindowFontScale(1);
        DrawCenteredAuthenticationText("Dalamud ACT Compat", IceBlue);
        ImGui.Dummy(new(0, compact ? 8 : 28));
        var logoSize = Math.Min(ImGui.GetContentRegionAvail().X, compact ? 96 : 192);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - logoSize) / 2));
        var wrap = logoTexture.GetWrapOrEmpty();
        ImGui.Image(wrap.Handle, new Vector2(logoSize));
        ImGui.Dummy(new(0, compact ? 6 : 24));
        DrawCenteredAuthenticationText(text.Get("你的战斗工具，尽在一处", "Your combat workspace"), Gold);
        ImGui.Spacing();
        DrawAuthenticationMutedText(text.Get("解析器 · 悬浮窗 · 扩展 · 云同步", "Parsers · Overlays · Extensions · Cloud"));
        ImGui.Dummy(new(0, 14));
        DrawAuthenticationMutedText(text.Get("绑定 Discord，随时找回账号与云备份。", "Link Discord to recover your account and cloud backups."));
    }

    private static bool BeginAuthenticationPanel(
        string id,
        Vector2 size,
        bool hero,
        bool allowScrolling)
    {
        DactTheme.PushStyleColor(
            ImGuiCol.ChildBg,
            hero ? AuthenticationHeroBackground : AuthenticationCardBackground);
        DactTheme.PushStyleColor(
            ImGuiCol.Border,
            hero
                ? new Vector4(Gold.X, Gold.Y, Gold.Z, 0.72f)
                : AuthenticationBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 20));
        var flags = allowScrolling
            ? ImGuiWindowFlags.None
            : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        return ImGui.BeginChild(id, size, true, flags);
    }

    private static void EndAuthenticationPanel()
    {
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private void DrawAuthenticationFeature(string title, string description)
    {
        DactTheme.TextColored(IceBlue, "◆");
        ImGui.SameLine();
        ImGui.BeginGroup();
        DactTheme.TextColored(new Vector4(0.90f, 0.94f, 0.98f, 1), title);
        DrawAuthenticationMutedText(description);
        ImGui.EndGroup();
        ImGui.Dummy(new Vector2(0, 9));
    }

    private static void DrawCenteredAuthenticationText(string value, Vector4 color)
    {
        var offset = Math.Max(0, (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(value).X) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        DactTheme.TextColored(color, value);
    }

    private static void DrawAuthenticationMutedText(string value)
    {
        DactTheme.PushStyleColor(ImGuiCol.Text, AuthenticationMutedText);
        ImGui.TextWrapped(value);
        ImGui.PopStyleColor();
    }

    private void DrawAuthenticationStatus(CloudClientSnapshot snapshot)
    {
        var statusMessage = snapshot.IsBusy
            ? $"{text.SystemMessage(snapshot.StatusMessage)}  {text.Get("处理中…", "Working…")}"
            : text.SystemMessage(snapshot.StatusMessage);
        if (string.IsNullOrWhiteSpace(statusMessage))
        {
            return;
        }

        var statusTextColor = snapshot.StatusIsError
            ? new Vector4(1, 0.52f, 0.48f, 1)
            : new Vector4(0.58f, 0.86f, 1, 1);
        var statusBackground = snapshot.StatusIsError
            ? new Vector4(0.30f, 0.075f, 0.075f, 0.70f)
            : new Vector4(0.07f, 0.18f, 0.24f, 0.78f);
        var wrapWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - 42);
        var statusHeight = Math.Max(40, ImGui.CalcTextSize(statusMessage, false, wrapWidth).Y + 16);
        DactTheme.PushStyleColor(ImGuiCol.ChildBg, statusBackground);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        if (ImGui.BeginChild(
                "account-authentication-status",
                new Vector2(-1, statusHeight),
                false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            DactTheme.TextColored(statusTextColor, snapshot.StatusIsError ? "!" : "●");
            ImGui.SameLine();
            DactTheme.PushStyleColor(ImGuiCol.Text, statusTextColor);
            ImGui.TextWrapped(statusMessage);
            ImGui.PopStyleColor();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private bool DrawAuthenticationInput(
        string label,
        string hint,
        string id,
        ref string value,
        int maxLength,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        // Group label and input so table columns share the label's baseline;
        // otherwise the previous input's frame padding offsets the next label.
        ImGui.BeginGroup();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 8));
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.SetNextItemWidth(-1);
        var edited = ImGui.InputTextWithHint($"##{id}", hint, ref value, maxLength, flags);
        ImGui.PopStyleVar();
        ImGui.EndGroup();
        return edited;
    }

    private void DrawAuthenticationPersistenceOption()
    {
        ImGui.Spacing();
        DactTheme.Checkbox(
            text.Get("记住登录状态（下次自动登录）", "Remember sign-in (auto-login next time)"),
            ref cloudRememberLogin);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text.Get(
                "登录状态使用 Windows 当前用户加密，并在重启游戏或电脑后恢复。",
                "Windows protects the saved session for this user and restores it after a game or PC restart."));
        }
    }

    private static bool DrawAuthenticationPrimaryButton(string label, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        DactTheme.PushStyleColor(ImGuiCol.Button, new Vector4(0.10f, 0.36f, 0.50f, 1));
        DactTheme.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.14f, 0.48f, 0.64f, 1));
        DactTheme.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.56f, 0.72f, 1));
        DactTheme.PushStyleColor(ImGuiCol.Text, new Vector4(0.94f, 0.98f, 1, 1));
        var clicked = DactTheme.Button(label, new Vector2(-1, 38));
        ImGui.PopStyleColor(4);
        ImGui.EndDisabled();
        return enabled && clicked;
    }

}
