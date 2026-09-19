using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal enum CloudRoleNoticeKind { Administrator, Sponsor }

internal sealed class CloudRoleNotice(UiText text, ISharedImmediateTexture icon, Action<string, string> acknowledge,
    Func<double>? clock = null, CloudRoleNoticeKind kind = CloudRoleNoticeKind.Administrator)
{
    private static readonly Vector4 Gold = new(.91f, .75f, .42f, 1);
    private static readonly Vector4 Ice = new(.56f, .81f, .94f, 1);
    private readonly Func<double> now = clock ?? (() => Environment.TickCount64 / 1000d);
    private string? username;
    private string? grantId;
    private double openedAt;
    private bool confirmationAttempted;
    public bool IsOpen => grantId is not null;

    public void Update(CloudClientSnapshot snapshot)
    {
        var next = snapshot is { IsSignedIn: true, ActiveBan: null }
            ? kind == CloudRoleNoticeKind.Sponsor
                ? snapshot.Sponsor is { Tier: > 0, SponsorNoticePending: true } sponsor ? sponsor.SponsorGrantId : null
                : snapshot.Administrator is { IsAdmin: true, AdminNoticePending: true } role ? role.AdminGrantId : null
            : null;
        if (string.IsNullOrWhiteSpace(next) || string.IsNullOrWhiteSpace(snapshot.Username))
        {
            grantId = null;
            username = null;
            return;
        }
        if (next == grantId && username == snapshot.Username) return;
        username = snapshot.Username;
        grantId = next;
        openedAt = now();
        confirmationAttempted = false;
    }

    public void Draw(CloudClientSnapshot snapshot)
    {
        Update(snapshot);
        if (!IsOpen) return;
        var elapsed = Math.Max(0, now() - openedAt);
        var progress = Math.Clamp((float)(elapsed / .45), 0, 1);
        var eased = 1 - MathF.Pow(1 - progress, 3);
        var viewport = ImGui.GetMainViewport();
        var scale = Math.Min(Math.Max(.5f, ImGui.GetIO().FontGlobalScale),
            Math.Min(viewport.Size.X / 584, viewport.Size.Y / 384));
        scale *= .94f + .06f * eased + .018f * MathF.Sin(progress * MathF.PI);
        var size = new Vector2(560, 360) * scale;
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * .5f, ImGuiCond.Always, new(.5f));
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, eased);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 18 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8 * scale);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(.035f, .052f, .081f, .99f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(.65f, .49f, .25f, .8f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.78f, .61f, .32f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Gold);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(.68f, .51f, .25f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(.06f, .075f, .1f, 1));
        // A normal window keeps the celebration local to DACT; it must not take
        // keyboard focus from the game or capture input outside its own bounds.
        var visible = ImGui.Begin(kind == CloudRoleNoticeKind.Sponsor ? "###DACTSponsorCongratulations" : "###DACTAdministratorCongratulations", ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoNavInputs);
        try
        {
            if (!visible) return;
            ImGui.SetWindowFontScale(scale);
            var origin = ImGui.GetWindowPos();
            var draw = ImGui.GetWindowDrawList();
            uint Color(Vector4 color, float opacity = 1) => ImGui.ColorConvertFloat4ToU32(color with { W = color.W * opacity * eased });
            Vector2 At(float x, float y) => origin + new Vector2(x, y) * scale;
            void Center(string value, float y, float fontSize, Vector4 color)
            {
                var font = ImGui.GetFont();
                var width = ImGui.CalcTextSize(value).X * fontSize * scale / ImGui.GetFontSize();
                draw.AddText(font, fontSize * scale, At(280, y) - new Vector2(width * .5f, 0), Color(color), value);
            }
            Center("D A C T", 22, 13, Gold);
            var badge = At(280, 92);
            draw.AddCircleFilled(badge, 38 * scale, Color(new(.12f, .13f, .14f, 1)), 64);
            draw.AddCircle(badge, 38 * scale, Color(Gold, .7f), 64, scale);
            // Ship the user's chosen badge with the client; Dalamud owns its
            // texture lifetime and the notice never depends on a remote image.
            draw.AddImage(icon.GetWrapOrEmpty().Handle, At(256, 68), At(304, 116), Vector2.Zero, Vector2.One, Color(Vector4.One));
            // One short, deterministic burst; refreshing account state never
            // restarts it and the settled dialog performs no particle drawing.
            if (elapsed is > .12 and < 2.1)
            {
                var t = (float)(elapsed - .12);
                for (var i = 0; i < 28; i++)
                {
                    var angle = i * 2.399963f;
                    var radius = 44 + (42 + i % 5 * 8) * (1 - MathF.Exp(-t * 2));
                    var point = badge + new Vector2(MathF.Cos(angle) * radius * 1.7f,
                        MathF.Sin(angle) * radius * .6f + t * t * 12) * scale;
                    var fade = Math.Clamp((2 - t) / 1.2f, 0, 1);
                    draw.AddCircleFilled(point, (i % 3 == 0 ? 2 : 1.3f) * scale, Color(i % 4 == 0 ? Ice : Gold, fade), 6);
                }
            }
            if (kind == CloudRoleNoticeKind.Sponsor)
            {
                Center(text.Get("感谢您成为 DACT 赞助者", "Thank you for supporting DACT!"), 148, 24, Gold);
                Center(text.Get($"赞助等级 {snapshot.Sponsor!.Tier} · 永久身份", $"Sponsor level {snapshot.Sponsor!.Tier} · Permanent"), 199, 16, Vector4.One);
                Center(text.Get("专属皇冠等级标识与红色名字已解锁", "Your crown badge and red account name are unlocked."), 229, 14, Ice);
            }
            else
            {
                Center(text.Get("恭喜您成为管理员", "Congratulations, Administrator!"), 148, 24, Gold);
                Center(text.Get("您已获得无限生成激活码的权限", "You can now generate unlimited activation keys."), 199, 16, Vector4.One);
                Center(text.Get("无需额外额度，即可继续邀请好友加入 DACT", "Invite friends to DACT without requesting more quota."), 229, 14, Ice);
            }
            ImGui.SetCursorPos(new Vector2(180, 283) * scale);
            ImGui.BeginDisabled(snapshot.IsBusy || progress < 1);
            if (ImGui.Button(text.Get("我知道了", "Got it"), new Vector2(200, 40) * scale))
            {
                confirmationAttempted = true;
                acknowledge(username!, grantId!);
            }
            ImGui.EndDisabled();
            if (confirmationAttempted && snapshot.StatusIsError)
                Center(text.Get("确认失败，请重试", "Confirmation failed. Please try again."), 334, 12, new(1, .55f, .45f, 1));
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleColor(6);
            ImGui.PopStyleVar(4);
        }
    }
}
