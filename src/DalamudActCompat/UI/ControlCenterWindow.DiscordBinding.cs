using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

public sealed partial class ControlCenterWindow
{
    private void DrawDiscordBindingGate(CloudClientSnapshot snapshot)
    {
        // Authorization belongs to DACT's runtime and content gate. A global
        // ImGui modal would also dim the game and capture unrelated clicks.
        var width = Math.Clamp(ImGui.GetContentRegionAvail().X * .31f, 180, 310);
        if (BeginAuthenticationPanel("account-binding-hero", new(width, -1), true, false))
            DrawAuthenticationHero(compact: ImGui.GetContentRegionAvail().Y < 360);
        EndAuthenticationPanel();
        ImGui.SameLine(0, 14);
        if (BeginAuthenticationPanel("account-binding-steps", new(-1, -1), false, true))
        {
            DactTheme.TextColored(Gold, text.Get("绑定 Discord 后继续", "Link Discord to continue"));
            ImGui.TextWrapped(text.Format($"当前账号：{snapshot.Username}", $"Account: {snapshot.Username}"));
            DrawAuthenticationMutedText(text.Get("一个 Discord 账号可绑定 3 个 DACT 账号。完成后自动进入。",
                "One Discord account can link up to 3 DACT accounts. DACT opens automatically when linked."));
            ImGui.Spacing();
            ImGui.TextWrapped(text.Get("1. 复制绑定码，进入我们的 Discord 服务器。", "1. Copy your code and join our Discord server."));
            DrawCloudDiscordBindingAction(snapshot);
            ImGui.Spacing();
            ImGui.TextWrapped(text.Get("2. 在右侧成员列表的「Bot」分组找到 DACT，右键选择「发消息」。",
                "2. Find DACT under Bot in the member list on the right. Right-click it and choose Message."));
            ImGui.Spacing();
            ImGui.TextWrapped(text.Get("3. 在私聊输入 /dact 并选择 DACT 命令，点「绑定账号」，粘贴后提交。",
                "3. In the private chat, use /dact and select DACT's command. Choose Link account, paste and submit."));
            DrawAuthenticationMutedText(text.Get("机器人确认后回到游戏即可，无需再点确认。",
                "Once the bot confirms, return to the game. No extra confirmation is needed."));
            var recovery = snapshot.Recovery;
            if (snapshot.DiscordLinkCode is { } code && code.ExpiresAt <= DateTimeOffset.UtcNow)
                DrawAuthenticationMutedText(text.Get("绑定码已过期，请重新准备。", "This code has expired. Prepare a new one."));
            if (snapshot.RecoveryStatusError is { } error) DrawAuthenticationMutedText(text.SystemMessage(error));
            else if (recovery?.Available == false || recovery?.DiscordAvailable == false)
                DrawAuthenticationMutedText(text.Get("绑定服务暂时不可用，请稍后重试或联系管理员。", "Linking is temporarily unavailable. Retry later or contact an administrator."));
            else if (recovery?.Protected != true)
                DrawAuthenticationMutedText(text.Get("正在确认绑定并建立云备份恢复保护，请稍候。", "Checking your link and preparing cloud recovery protection. Please wait."));
            if (snapshot.StatusIsError) DrawAuthenticationStatus(snapshot);
            ImGui.Spacing();
            ImGui.BeginDisabled(snapshot.IsBusy);
            if (DactTheme.Button(text.Get("尚未自动进入？重新检查", "Still waiting? Check again"))) cloud.RefreshRecoveryStatus();
            if (DactTheme.Button(text.Get("退出登录 / 更换账号", "Sign out / switch account"))) cloud.Logout();
            ImGui.EndDisabled();
        }
        EndAuthenticationPanel();
    }
}
