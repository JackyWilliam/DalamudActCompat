using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 登录 · 设备已满: an account may stay signed in on a limited number of PCs. When sign-in is
// refused for that reason the server lists the signed-in devices by computer name, and this
// dialog lets the player sign one of them out, or all of them, before signing in here. The
// typed credentials wait in CloudClientService, so no second password entry is needed.
public sealed partial class ControlCenterWindow
{
    private const string DeviceLimitDialog = "##device-limit";
    // The refusal the dialog was last opened (or answered) for, so it opens once per refusal.
    private CloudDeviceLimit? deviceLimitShown;

    private void DrawDeviceLimitDialog(CloudClientSnapshot snapshot)
    {
        var s = DactUi.S;
        if (snapshot.DeviceLimit is { } limit && !ReferenceEquals(limit, deviceLimitShown))
        {
            deviceLimitShown = limit;
            DactUi.OpenDialog(DeviceLimitDialog);
        }
        // Sign-in waits on this choice, so a stray click beside it does not dismiss it.
        if (!DactUi.BeginDialog(DeviceLimitDialog, 480, dismissible: false)) return;
        // Answered or cleared elsewhere (a new sign-in, sign-out): the list is stale, so it goes.
        if (snapshot.DeviceLimit is null && !snapshot.IsBusy) DactUi.CloseDialog(DeviceLimitDialog);
        var shown = deviceLimitShown;
        var palette = DactTheme.Palette;
        var answered = snapshot.DeviceLimit is null || snapshot.IsBusy;
        var count = shown?.Limit ?? 5;
        // Gold like the other confirmation dialogs' titles (Figma 好友 · 解除好友确认).
        DactUi.Text(text.Format($"这个账号已在 {count} 台设备上登录", $"This account is signed in on {count} devices"), palette.Gold, DactFonts.H2);
        DactUi.Wrapped(text.Format(
            $"每个账号最多同时登录 {count} 台设备。选一台登出，或者全部登出，然后在这台电脑上登录；被登出的设备下次要重新输入密码。",
            $"An account can stay signed in on up to {count} devices at once. Sign one out, or all of them, to sign in on this PC; signed-out devices ask for the password next time."));
        DactUi.Hint(text.Format($"这台电脑：{CloudMachineIdentity.GetDeviceName()}", $"This PC: {CloudMachineIdentity.GetDeviceName()}"));
        DactUi.Divider();
        var devices = shown?.Devices ?? [];
        var signOutOne = text.Get("登出这台", "Sign out");
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            var width = ImGui.GetContentRegionAvail().X;
            var buttonWidth = DactUi.ButtonWidth(signOutOne);
            var row = ImGui.GetCursorPos();
            ImGui.BeginGroup();
            DactUi.Text(device.Name ?? text.Get("未命名设备（旧版 DACT）", "Unnamed device (older DACT)"), palette.Text, DactFonts.Body);
            var signedIn = device.SignedInAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var detail = device.FirstSeenAt is { } first
                ? text.Format($"上次登录 {signedIn} · 首次登录 {first.ToLocalTime():yyyy-MM-dd}", $"Last sign-in {signedIn} · first seen {first.ToLocalTime():yyyy-MM-dd}")
                : text.Format($"上次登录 {signedIn}", $"Last sign-in {signedIn}");
            DactUi.Wrapped(detail, DactUi.HintColor, DactFonts.Small, width - buttonWidth - 12 * s);
            ImGui.EndGroup();
            var after = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(row.X + width - buttonWidth, row.Y + 2 * s));
            if (DactUi.Button(signOutOne + $"##device-limit-{index}", disabled: answered))
            {
                cloud.ResolveDeviceLimit([device.Id], false);
                DactUi.CloseDialog(DeviceLimitDialog);
            }
            ImGui.SetCursorPos(after);
            if (index < devices.Count - 1) DactUi.Divider();
        }
        DactUi.Divider();
        var cancel = text.Get("取消", "Cancel");
        var all = text.Get("全部登出并在这台登录", "Sign out all and sign in here");
        if (DactUi.Button(cancel + "##device-limit-cancel"))
        {
            cloud.CancelDeviceLimit();
            DactUi.CloseDialog(DeviceLimitDialog);
        }
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(all + "##device-limit-all", ButtonKind.Danger, disabled: answered))
        {
            cloud.ResolveDeviceLimit([], true);
            DactUi.CloseDialog(DeviceLimitDialog);
        }
        DactUi.EndDialog(DeviceLimitDialog);
    }
}
