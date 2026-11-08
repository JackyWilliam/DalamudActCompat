using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 绑定 Discord 后继续 (screens.js discordGate): shown after sign-in until the account is linked,
// in the same frame as the sign-in pages. The steps carry their own buttons; the status row says
// what the client is waiting for. Authorization belongs to DACT's runtime and content gate, so it
// is not a global modal that would dim the game.
public sealed partial class ControlCenterWindow
{
    private void DrawDiscordBindingGate(CloudClientSnapshot snapshot)
        => DrawAuthenticationFrame("auth-discord", () => DrawDiscordBindingForm(snapshot));

    private void DrawDiscordBindingForm(CloudClientSnapshot snapshot)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        DrawAuthenticationTitle(text.Get("绑定 Discord 后继续", "Link Discord to continue"),
            text.Get("一个 Discord 账号可绑定 3 个 DACT 账号。完成后自动进入。",
                "One Discord account can link up to 3 DACT accounts. DACT opens automatically when linked."));

        // 当前账号 · name with identity markers · 退出登录 / 更换账号 at the right end.
        var row = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var signOut = text.Get("退出登录 / 更换账号", "Sign out / switch account");
        var signOutWidth = DactUi.ButtonWidth(signOut, ButtonKind.Ghost);
        var label = text.Get("当前账号", "Account");
        DactUi.DrawText(ImGui.GetWindowDrawList(), row, 30 * s, label, palette.Muted, DactFonts.Small);
        var nameX = row.X + DactUi.TextWidth(label, DactFonts.Small) + 8 * s;
        AccountIdentityBadge.DrawName(null, snapshot.Username ?? string.Empty, snapshot.Administrator?.IsAdmin == true,
            new Vector2(nameX, row.Y + 6 * s), width - (nameX - row.X) - signOutWidth - 8 * s, palette.Text, null,
            snapshot.Sponsor?.Tier ?? 0, text);
        ImGui.SetCursorScreenPos(new Vector2(row.X + width - signOutWidth, row.Y));
        if (DactUi.Button(signOut + "##gate-signout", ButtonKind.Ghost, disabled: snapshot.IsBusy)) cloud.Logout();
        ImGui.SetCursorScreenPos(row + new Vector2(0, 30 * s));
        ImGui.Dummy(new Vector2(width, 0));

        // The three steps in one inset box (dsl bindStep).
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(14 * s, 12 * s));
        ImGui.BeginGroup();
        var inner = width - 28 * s;
        using (DactUi.Gap(14))
        {
            BindStep("1", text.Get("复制绑定码，进入我们的 Discord 服务器。", "Copy your code and join our Discord server."), inner,
                () => DrawCloudDiscordBindingAction(snapshot));
            BindStep("2", text.Get("在右侧成员列表的「Bot」分组找到 DACT，右键选择「发消息」。",
                "Find DACT under Bot in the member list on the right. Right-click it and choose Message."), inner, null);
            BindStep("3", text.Get("在私聊输入 /dact 并选择 DACT 命令，点「绑定账号」，粘贴后提交。",
                "In the private chat, use /dact and select DACT's command. Choose Link account, paste and submit."), inner,
                () => DactUi.Hint(text.Get("机器人确认后回到游戏即可，无需再点确认。", "Once the bot confirms, return to the game. No extra confirmation is needed.")));
        }
        ImGui.EndGroup();
        var end = ImGui.GetItemRectMax() + new Vector2(0, 12 * s);
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, new Vector2(start.X + width, end.Y), DactUi.U32(DactUi.Field), 8 * s);
        draw.AddRect(start, new Vector2(start.X + width, end.Y), DactUi.U32(DactUi.FieldBorder), 8 * s, ImDrawFlags.None, Math.Max(1, s));
        draw.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
        ImGui.Dummy(new Vector2(width, 0));

        // What the client is waiting for, with 重新检查.
        var recovery = snapshot.Recovery;
        string message;
        var error = false;
        if (snapshot.DiscordLinkCode is { } code && code.ExpiresAt <= DateTimeOffset.UtcNow)
            message = text.Get("绑定码已过期，请重新准备。", "This code has expired. Prepare a new one.");
        else if (snapshot.RecoveryStatusError is { } failure)
        {
            message = text.SystemMessage(failure);
            error = true;
        }
        else if (recovery?.Available == false || recovery?.DiscordAvailable == false)
        {
            message = text.Get("绑定服务暂时不可用，请稍后重试或联系管理员。", "Linking is temporarily unavailable. Retry later or contact an administrator.");
            error = true;
        }
        else if (recovery?.Protected != true)
            message = text.Get("正在确认绑定并建立云备份恢复保护，请稍候。", "Checking your link and preparing cloud recovery protection. Please wait.");
        else
            message = text.Get("等待你在 Discord 完成绑定，完成后会自动进入。", "Waiting for you to finish linking in Discord; DACT opens automatically.");
        if (DrawAuthStatusRow(message, error, "refresh", text.Get("重新检查", "Check again")) && !snapshot.IsBusy) cloud.RefreshRecoveryStatus();
        if (snapshot.StatusIsError) DrawAuthenticationStatus(snapshot);
    }

    private void BindStep(string number, string title, float width, Action? content)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(start + new Vector2(11 * s, 11 * s), 11 * s, DactUi.U32(DactUi.AccentSoft), 24);
        DactUi.DrawText(draw, new Vector2(start.X + 11 * s - DactUi.TextWidth(number, DactFonts.Small) * .5f, start.Y), 22 * s, number, palette.Accent, DactFonts.Small);
        ImGui.SetCursorScreenPos(start + new Vector2(32 * s, 2 * s));
        ImGui.BeginGroup();
        using (DactUi.Gap(6))
        {
            DactUi.Wrapped(title, palette.Text, DactFonts.Body, width - 32 * s);
            content?.Invoke();
        }
        ImGui.EndGroup();
    }
}
