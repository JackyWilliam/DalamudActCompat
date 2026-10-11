using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

public sealed partial class ControlCenterWindow
{
    private string? cloudDiscordCodeRequestedFor;

    private void DrawCloudDiscordEntry()
    {
        // The support server already contains the bot. An application-install
        // consent page neither joins that server nor opens a conversation.
        if (DactTheme.Button(text.Get("进入 DACT Discord 服务器", "Join the DACT Discord server")))
            OpenUrl(ActivationDiscordUrl);
        ImGui.TextWrapped(text.Get("在右侧成员列表的「Bot」分组找到 DACT，右键选择「发消息」，在私聊输入 /dact 并选择账号菜单。",
            "Find DACT under Bot in the member list on the right, right-click and choose Message, then use /dact in the private chat."));
    }

    private void DrawCloudDiscordBindingAction(CloudClientSnapshot snapshot)
    {
        var ready = snapshot.Recovery is { Protected: true, DiscordAvailable: true, DiscordBound: false };
        var validCode = snapshot.DiscordLinkCode is { } existing && existing.ExpiresAt > DateTimeOffset.UtcNow;
        // Prepare once while the binding UI is visible; do not regenerate each
        // frame or while the user is pasting a still-valid code in Discord.
        if (ready && !snapshot.IsBusy && !validCode && cloudDiscordCodeRequestedFor != snapshot.Username)
        {
            cloudDiscordCodeRequestedFor = snapshot.Username;
            cloud.CreateDiscordLinkCode();
        }
        ImGui.BeginDisabled(snapshot.IsBusy || !ready);
        if (DactTheme.Button(validCode
                ? text.Get("复制绑定码并前往 Discord", "Copy code and open Discord")
                : text.Get("重新准备绑定码", "Retry preparing code")))
        {
            if (validCode)
            {
                // Only an explicit click touches the clipboard or opens the
                // browser, and the one-time credential never enters the URL.
                ImGui.SetClipboardText(snapshot.DiscordLinkCode!.Code);
                OpenUrl(ActivationDiscordUrl);
            }
            else cloud.CreateDiscordLinkCode();
        }
        ImGui.EndDisabled();
        if (validCode)
        {
            if (DactTheme.Button(text.Get("只复制绑定码", "Copy code only"))) ImGui.SetClipboardText(snapshot.DiscordLinkCode!.Code);
            DrawAuthenticationMutedText(text.Format($"有效期至 {snapshot.DiscordLinkCode!.ExpiresAt.LocalDateTime:HH:mm}，请勿转发。",
                $"Valid until {snapshot.DiscordLinkCode!.ExpiresAt.LocalDateTime:HH:mm}. Do not share this code."));
        }
    }

    private void DrawCloudDiscordRecovery(CloudClientSnapshot snapshot)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(text.Get("Discord 账号找回", "Discord account recovery"));
        DrawCloudDiscordEntry();
        var recovery = snapshot.Recovery;
        ImGui.TextWrapped(recovery?.Protected == true
            ? text.Get("云备份恢复保护已开启：改密后可继续读取原备份。", "Cloud recovery protection is ready: password resets preserve existing backups.")
            : text.Get("云备份恢复保护尚未就绪；登录后会自动检查，请暂时保留原恢复密钥。", "Cloud recovery protection is pending; it is checked automatically after login. Keep your original recovery key for now."));
        ImGui.TextWrapped(text.Get(
            "恢复保护会将原备份密钥加密保存在服务端，用于验证身份后找回。一个 Discord 最多绑定 3 个 DACT 账号。",
            "Recovery protection stores an encrypted copy of the backup key on the server for verified recovery. One Discord account can link up to 3 DACT accounts."));
        if (recovery?.DiscordBound == true)
        {
            ImGui.TextUnformatted(text.Get($"已绑定 Discord · 已用名额 {recovery.BoundAccounts}/3", $"Discord linked · {recovery.BoundAccounts}/3 slots used"));
            ImGui.TextWrapped(text.Get("忘记密码时，在 /dact 菜单点「找回密码」，再选择账号。", "To recover a password, choose Recover password in /dact, then select your account."));
            return;
        }
        if (recovery?.DiscordAvailable != true)
            ImGui.TextWrapped(text.Get("Discord 机器人尚未就绪。", "The Discord bot is not ready yet."));
        DrawCloudDiscordBindingAction(snapshot);
        ImGui.TextWrapped(text.Get("在 /dact 菜单点「绑定账号」，粘贴后提交。", "Choose Link account in /dact, paste your code, and submit."));
    }
}
