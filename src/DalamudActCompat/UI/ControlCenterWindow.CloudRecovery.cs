using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

public sealed partial class ControlCenterWindow
{
    private string? cloudDiscordCodeRequestedFor;

    // dsl Discord 入口: an inset box explaining where the reset code comes from, with 打开 Discord.
    // The support server already contains the bot; an application-install consent page would
    // neither join that server nor open a conversation.
    private void DrawCloudDiscordEntry()
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var open = text.Get("打开 Discord", "Open Discord");
        var buttonWidth = DactUi.ButtonWidth(open, icon: "external");
        var textWidth = width - 24 * s - buttonWidth - 10 * s;
        var hint = text.Get("在 /dact 菜单点「找回密码」，选择账号后复制重置码。在右侧成员列表的「Bot」分组找到 DACT，右键「发消息」后输入 /dact。",
            "In /dact choose Recover password, pick the account and copy the reset code. Find DACT under Bot in the member list, right-click, Message, then type /dact.");
        var lines = DactUi.WrapLines(hint, textWidth);
        var height = 10 * s + 18 * s + 2 * s + lines.Count * 16 * s + 10 * s;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(DactUi.Field), 8 * s);
        draw.AddRect(start, start + new Vector2(width, height), DactUi.U32(DactUi.FieldBorder), 8 * s, ImDrawFlags.None, Math.Max(1, s));
        DactUi.DrawText(draw, start + new Vector2(12 * s, 10 * s), 18 * s, text.Get("Discord 机器人 / 重置码", "Discord bot / reset code"), palette.Text);
        var y = start.Y + 30 * s;
        foreach (var line in lines)
        {
            DactUi.DrawText(draw, new Vector2(start.X + 12 * s, y), 16 * s, line, DactUi.HintColor, DactFonts.Small);
            y += 16 * s;
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - 12 * s - buttonWidth, start.Y + (height - 30 * s) * .5f));
        if (DactUi.Button(open + "##discord-entry", icon: "external")) OpenUrl(ActivationDiscordUrl);
        ImGui.SetCursorScreenPos(start + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 0));
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
        if (DactUi.Button((validCode
                ? text.Get("复制绑定码并前往 Discord", "Copy code and open Discord")
                : text.Get("重新准备绑定码", "Retry preparing code")) + "##discord-link-code", ButtonKind.Primary, validCode ? "copy" : "refresh",
                disabled: snapshot.IsBusy || !ready))
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
        if (validCode)
        {
            var copyOnly = text.Get("只复制绑定码", "Copy code only");
            DactUi.SameLineIfFits(DactUi.ButtonWidth(copyOnly));
            if (DactUi.Button(copyOnly + "##discord-copy-only")) ImGui.SetClipboardText(snapshot.DiscordLinkCode!.Code);
            DactUi.Hint(text.Format($"有效期至 {snapshot.DiscordLinkCode!.ExpiresAt.LocalDateTime:HH:mm}，请勿转发。",
                $"Valid until {snapshot.DiscordLinkCode!.ExpiresAt.LocalDateTime:HH:mm}. Do not share this code."));
        }
    }

    private void DrawCloudDiscordRecovery(CloudClientSnapshot snapshot)
    {
        var s = DactUi.S;
        var recovery = snapshot.Recovery;
        var bound = recovery?.DiscordBound == true;
        DactUi.BeginCard("cloud-discord", null);
        DactUi.CardTitleRow(text.Get("Discord 账号找回", "Discord account recovery"), () =>
        {
            DactUi.Pill(bound
                    ? text.Format($"已绑定 · {recovery!.BoundAccounts}/3", $"Linked · {recovery!.BoundAccounts}/3")
                    : text.Get("未绑定", "Not linked"),
                bound ? PillTone.Ok : PillTone.Warn);
            if (ImGui.IsItemHovered())
                DactUi.Tooltip(text.Get(
                    "恢复保护会将原备份密钥加密保存在服务端，用于验证身份后找回。一个 Discord 最多绑定 3 个 DACT 账号。",
                    "Recovery protection stores an encrypted copy of the backup key on the server for verified recovery. One Discord account can link up to 3 DACT accounts."));
        });
        var protection = recovery?.Protected == true
            ? text.Get("云备份恢复保护已开启：改密后可继续读取原备份。", "Cloud recovery protection is ready: password resets preserve existing backups.")
            : text.Get("云备份恢复保护尚未就绪；登录后会自动检查，请暂时保留原恢复密钥。", "Cloud recovery protection is pending; it is checked automatically after login. Keep your original recovery key for now.");
        var how = bound
            ? text.Get("忘记密码时，在 /dact 菜单点「找回密码」，再选择账号。", "To recover a password, choose Recover password in /dact, then select your account.")
            : text.Get("在 /dact 菜单点「绑定账号」，粘贴后提交。", "Choose Link account in /dact, paste your code, and submit.");
        DactUi.Hint(text.IsChinese ? protection + how : protection + " " + how);
        if (!bound && recovery?.DiscordAvailable != true)
            DactUi.Wrapped(text.Get("Discord 机器人尚未就绪。", "The Discord bot is not ready yet."), DactTheme.Palette.Gold, DactFonts.Small);
        // The support server already contains the bot. An application-install
        // consent page neither joins that server nor opens a conversation.
        if (DactUi.Button(text.Get("进入 DACT Discord 服务器", "Join the DACT Discord server") + "##cloud-discord-join", icon: "external"))
            OpenUrl(ActivationDiscordUrl);
        if (ImGui.IsItemHovered())
            DactUi.Tooltip(text.Get("在右侧成员列表的「Bot」分组找到 DACT，右键选择「发消息」，在私聊输入 /dact 并选择账号菜单。",
                "Find DACT under Bot in the member list on the right, right-click and choose Message, then use /dact in the private chat."));
        if (!bound) DrawCloudDiscordBindingAction(snapshot);
        DactUi.EndCard();
    }
}
