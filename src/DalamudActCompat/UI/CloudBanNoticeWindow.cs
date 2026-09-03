using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal sealed class CloudBanNoticeWindow : Window
{
    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 NavyHover = new(0.105f, 0.145f, 0.185f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private static readonly Vector4 Red = new(0.96f, 0.42f, 0.38f, 1);

    private readonly UiText text;
    private readonly ISharedImmediateTexture logoTexture;
    private CloudBanNotice? notice;
    private bool lifted;
    private bool locateOnNextDraw;
    private bool outerFrameStylePushed;

    public CloudBanNoticeWindow(UiText text, ISharedImmediateTexture logoTexture)
        : base("账号安全###DalamudActCompatCloudBanNotice")
    {
        this.text = text;
        this.logoTexture = logoTexture;
        Size = new Vector2(610, 330);
        SizeCondition = ImGuiCond.Always;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        // This is deliberately a normal window rather than a modal popup: account
        // enforcement disables DACT, but must never capture unrelated game controls.
        Flags = ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoNavInputs |
                ImGuiWindowFlags.NoNavFocus |
                ImGuiWindowFlags.NoFocusOnAppearing;
    }

    public void Show(CloudBanNotice nextNotice, bool lifted)
    {
        notice = nextNotice;
        this.lifted = lifted;
        locateOnNextDraw = true;
        IsOpen = true;
    }

    public override void PreDraw()
    {
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
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        outerFrameStylePushed = true;
    }

    public override void PostDraw()
    {
        if (!outerFrameStylePushed)
        {
            return;
        }

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
        outerFrameStylePushed = false;
    }

    public override void Draw()
    {
        var current = notice;
        if (current is null)
        {
            IsOpen = false;
            return;
        }

        WindowName = text.Get(
            "账号安全###DalamudActCompatCloudBanNotice",
            "Account security###DalamudActCompatCloudBanNotice");
        PushTheme();
        try
        {
            BrandedWindowChrome.Draw(
                logoTexture,
                text.Get("账号安全", "Account security"),
                lifted
                    ? text.Get("账号已解封", "Account unbanned")
                    : text.Get("访问已禁用", "Access disabled"),
                lifted ? IceBlue : Red,
                ControlCenterWindow.FormatVersionLabel(
                    typeof(CloudBanNoticeWindow).Assembly.GetName().Version),
                "cloud-ban-notice",
                showCloseButton: false);

            if (BrandedWindowChrome.BeginGoldCard(
                    "cloud-ban-notice-card",
                    lifted ? 172 : 194))
            {
                if (lifted)
                {
                    DrawLiftedContent(current);
                }
                else
                {
                    DrawBannedContent(current);
                }
            }
            BrandedWindowChrome.EndGoldCard();

            ImGui.Spacing();
            const float buttonWidth = 132;
            var cursorX = ImGui.GetCursorPosX();
            ImGui.SetCursorPosX(Math.Max(cursorX, cursorX + ImGui.GetContentRegionAvail().X - buttonWidth));
            if (ImGui.Button(text.Get("确认", "Confirm"), new Vector2(buttonWidth, 32)))
            {
                IsOpen = false;
            }
        }
        finally
        {
            PopTheme();
        }
    }

    private void DrawBannedContent(CloudBanNotice current)
    {
        ImGui.TextColored(Red, text.Get("您的账号已经被封禁", "Your account has been banned"));
        ImGui.Separator();
        ImGui.TextUnformatted(text.Get(
            $"封禁时间：{current.BannedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
            $"Banned at: {current.BannedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}"));
        ImGui.TextUnformatted(current.BanExpiresAt is { } expiresAt
            ? text.Get(
                $"封禁结束：{expiresAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
                $"Ends at: {expiresAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}")
            : text.Get("封禁结束：永久", "Ends at: permanent"));
        ImGui.TextUnformatted(text.Get(
            $"封禁方式：{FormatBanType(current.BanType, chinese: true)}",
            $"Ban type: {FormatBanType(current.BanType, chinese: false)}"));
        if (!string.IsNullOrWhiteSpace(current.BanReason))
        {
            ImGui.TextWrapped(text.Get(
                $"封禁原因：{current.BanReason}",
                $"Reason: {current.BanReason}"));
        }
        ImGui.Spacing();
        ImGui.TextDisabled(text.Get(
            "DACT 解析器、悬浮窗和所有 DACT 管理的扩展服务已停止。确认只会关闭本提示，不会解除封禁。",
            "The DACT parser, overlays, and all DACT-managed extension services have stopped. Confirm only dismisses this notice; it does not remove the ban."));
    }

    private void DrawLiftedContent(CloudBanNotice current)
    {
        ImGui.TextColored(IceBlue, text.Get("您的账号已经解除封禁", "Your account is no longer banned"));
        ImGui.Separator();
        ImGui.TextWrapped(text.Get(
            "服务器已确认该账号的封禁已经解除。",
            "The server confirmed that this account is no longer banned."));
        ImGui.TextDisabled(text.Get(
            $"原封禁时间：{current.BannedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
            $"Previous ban time: {current.BannedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}"));
        ImGui.Spacing();
        ImGui.TextWrapped(text.Get(
            "为避免在当前运行中自动重启已经停止的原生服务，请重新加载 DACT 或重启游戏，然后重新登录。",
            "To avoid restarting stopped native services during this run, reload DACT or restart the game, then sign in again."));
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

    private static void PushTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.34f, 0.29f, 0.18f, 0.70f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.17f, 0.24f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, NavyHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.25f, 0.34f, 1));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
    }

    private static void PopTheme()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(7);
    }
}
