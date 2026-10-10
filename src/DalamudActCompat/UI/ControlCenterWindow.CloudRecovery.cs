using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

public sealed partial class ControlCenterWindow
{
    private void DrawCloudDiscordRecovery(CloudClientSnapshot snapshot)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(text.Get("Discord 账号找回", "Discord account recovery"));
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
            ImGui.TextWrapped(text.Get("忘记密码时，向机器人使用 /dact-reset 并填写账号名。", "If you forget your password, use /dact-reset with the bot and enter your account name."));
            return;
        }
        if (recovery?.DiscordAvailable != true)
            ImGui.TextWrapped(text.Get("Discord 机器人尚未就绪。", "The Discord bot is not ready yet."));
        ImGui.BeginDisabled(snapshot.IsBusy || recovery is not { Protected: true, DiscordAvailable: true });
        if (ImGui.Button(text.Get("生成账号绑定码", "Generate account binding code"))) cloud.CreateDiscordLinkCode();
        ImGui.EndDisabled();
        if (snapshot.DiscordLinkCode is { } code && code.ExpiresAt > DateTimeOffset.UtcNow)
        {
            ImGui.TextWrapped(text.Get("复制绑定码，向 DACT 机器人使用 /dact-bind 并粘贴。10 分钟内有效，请勿转发。", "Copy the code and paste it into /dact-bind with the DACT bot. It expires in 10 minutes; do not share it."));
            if (ImGui.Button(text.Get("复制绑定码", "Copy binding code"))) ImGui.SetClipboardText(code.Code);
        }
    }
}
