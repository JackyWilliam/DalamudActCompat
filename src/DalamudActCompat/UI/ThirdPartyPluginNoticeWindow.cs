using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.UI.Kit;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace DalamudActCompat.UI;

public enum FoxTtsProChoice
{
    EnablePro,
    KeepCurrent,
    NeverRemind,
}

internal enum ThirdPartyNoticeOpenMode
{
    ManualDisclosure,
    ManualUpdateCheck,
    RequiredAfterPluginUpdate,
}

// 三方扩展 (tools/ui-redesign/notices.js): the bundled third-party DLLs as cards with author,
// licence, hash and links. The window has a fixed height: only the middle scrolls, so the
// 知悉并安装 / 更新 footer stays on screen however long the cards are. The permission and TTS Pro
// choices are centred dialogs that cannot be dismissed without an answer.
public sealed class ThirdPartyPluginNoticeWindow : Window
{
    private const string PermissionDialog = "##dact-full-permissions";
    private const string TtsProDialog = "##dact-foxtts-pro";
    private readonly Func<IReadOnlyList<BundledActPluginDescriptor>> getDisclosures;
    private readonly Func<IReadOnlyList<BundledActPluginDescriptor>> getPending;
    private readonly Func<IReadOnlyList<BundledActPluginDescriptor>, Task<BundledPluginInstallOutcome>> install;
    private readonly Func<bool> shouldPromptForPermissions;
    private readonly Action<bool> configureFullPermissions;
    private readonly Func<bool> shouldOfferTtsPro;
    private readonly Action<FoxTtsProChoice> completeSetup;
    private readonly PluginLogger logger;
    private readonly UiText text;
    private readonly WindowDragController headerDrag = new();
    private readonly ISharedImmediateTexture logoTexture;
    // Card content heights from the previous frame; each row takes its tallest card so the
    // hash and link bars of a row line up.
    private readonly Dictionary<string, float> cardHeights = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<BundledActPluginDescriptor> disclosures = [];
    private IReadOnlyList<BundledActPluginDescriptor> pending = [];
    private Task<BundledPluginInstallOutcome>? installTask;
    private string result = string.Empty;
    private bool showPermissionChoice;
    private bool permissionPopupRequested;
    private bool showTtsProChoice;
    private bool ttsProPopupRequested;
    private bool updateCheckInProgress;
    private bool outerFrameStylePushed;
    private ThirdPartyNoticeOpenMode openMode = ThirdPartyNoticeOpenMode.ManualDisclosure;

    public ThirdPartyPluginNoticeWindow(
        Func<IReadOnlyList<BundledActPluginDescriptor>> getDisclosures,
        Func<IReadOnlyList<BundledActPluginDescriptor>> getPending,
        Func<IReadOnlyList<BundledActPluginDescriptor>, Task<BundledPluginInstallOutcome>> install,
        Func<bool> shouldPromptForPermissions,
        Action<bool> configureFullPermissions,
        Func<bool> shouldOfferTtsPro,
        Action<FoxTtsProChoice> completeSetup,
        PluginLogger logger,
        UiText text,
        ISharedImmediateTexture logoTexture)
        : base("内置第三方 DLL 告知###DalamudActCompatThirdPartyDllNoticeLandscape")
    {
        this.getDisclosures = getDisclosures;
        this.getPending = getPending;
        this.install = install;
        this.shouldPromptForPermissions = shouldPromptForPermissions;
        this.configureFullPermissions = configureFullPermissions;
        this.shouldOfferTtsPro = shouldOfferTtsPro;
        this.completeSetup = completeSetup;
        this.logger = logger;
        this.text = text;
        this.logoTexture = logoTexture;
        Size = new Vector2(1000, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860, 540),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Refresh(openWhenPending: false);
    }

    private readonly WindowFade fade = new();

    public override void OnOpen() => fade.Opened();

    public override void PreDraw()
    {
        headerDrag.PrepareNextWindow();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // Indigo of the DACT logo, like the control center; skins substitute their own border.
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(.208f, .251f, .420f, 1));
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
        outerFrameStylePushed = true;
        Flags = DactTheme.WindowFlags(Flags);
        // Pushed last so it nests inside this window's other style pushes (WindowFade).
        if (!fade.Push()) IsOpen = false;
    }

    public override void PostDraw()
    {
        fade.Pop();
        if (!outerFrameStylePushed) return;
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        outerFrameStylePushed = false;
    }

    public override void Draw()
    {
        WindowName = text.Get(
            "内置第三方 DLL 告知###DalamudActCompatThirdPartyDllNoticeLandscape",
            "Bundled third-party DLL notice###DalamudActCompatThirdPartyDllNoticeLandscape");
        ControlCenterWindow.PushTheme();
        try
        {
            CompleteInstallWhenReady();
            DactUi.BeginFrame();
            DactTheme.DrawGameWindow();
            var s = DactUi.S;
            var min = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            // Dialogs centre on this window.
            DactUi.HostMin = min;
            DactUi.HostMax = min + size;
            var draw = ImGui.GetWindowDrawList();
            var line = Math.Max(1, s);
            DrawHeader(min, size.X, 48 * s);
            draw.AddRectFilled(new Vector2(min.X, min.Y + 48 * s), new Vector2(min.X + size.X, min.Y + 48 * s + line), DactUi.U32(DactUi.DividerColor));

            var hasFooter = pending.Count > 0 || installTask is not null;
            var footerHeight = hasFooter ? 64 * s + line : 0;
            var bodyTop = min.Y + 48 * s + line;
            ImGui.SetCursorScreenPos(new Vector2(min.X, bodyTop));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 18) * s);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8 * s);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            // 艾欧泽亚: the cards and the footer stay above the frame's bottom rim.
            var bodyBottom = min.Y + size.Y - DactTheme.GameFrameBottomInset;
            if (ImGui.BeginChild("third-party-notice-content", new Vector2(-1, bodyBottom - bodyTop - footerHeight), false,
                    ImGuiWindowFlags.AlwaysUseWindowPadding))
            {
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 14) * s);
                DrawContent();
                ImGui.PopStyleVar();
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);
            if (hasFooter) DrawFooter(min, size, bodyBottom, footerHeight);

            // This window paints its own scrim: a child submitted last lies above the page and
            // below the dialogs, which are popups of their own.
            var scrim = Math.Max(DactUi.DialogOpacity(PermissionDialog), DactUi.DialogOpacity(TtsProDialog));
            if (scrim > 0)
            {
                ImGui.SetCursorPos(Vector2.Zero);
                ImGui.BeginChild("##third-party-scrim", size, false,
                    ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings);
                ImGui.GetWindowDrawList().AddRectFilled(min, min + size, DactUi.U32(new Vector4(.016f, .024f, .035f, .62f * scrim)), 10 * s);
                ImGui.EndChild();
            }
            DrawPermissionChoiceDialog();
            DrawTtsProChoiceDialog();
        }
        finally
        {
            ControlCenterWindow.PopTheme();
        }
    }

    private void DrawHeader(Vector2 min, float width, float height)
    {
        var s = DactUi.S;
        var draw = ImGui.GetWindowDrawList();
        var logo = new Vector2(min.X + 14 * s, min.Y + (height - 26 * s) * .5f);
        draw.AddImageRounded(logoTexture.GetWrapOrEmpty().Handle, logo, logo + new Vector2(26 * s), Vector2.Zero, Vector2.One,
            ImGui.GetColorU32(Vector4.One), 6 * s);
        var title = text.Get("三方扩展", "Third-party extensions");
        var x = logo.X + 36 * s;
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, title, DactTheme.Palette.Text);
        x += DactUi.TextWidth(title) + 10 * s;
        var subtitle = text.Get("第三方扩展来源声明", "Third-party extension sources");
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, subtitle, DactUi.HintColor, DactFonts.Small);
        var dragLeft = x + DactUi.TextWidth(subtitle, DactFonts.Small) + 8 * s;

        var right = min.X + width - 10 * s;
        if (ShouldShowCloseButton(openMode, pending.Count, installTask is not null, showPermissionChoice, showTtsProChoice))
        {
            right -= 28 * s;
            ImGui.SetCursorScreenPos(new Vector2(right, min.Y + (height - 28 * s) * .5f));
            if (DactUi.IconButton("##third-party-close", "close", text.Get("关闭", "Close"))) fade.RequestClose();
            right -= 10 * s;
        }
        var status = pending.Count > 0
            ? text.Format($"待确认 {pending.Count} 项", $"{pending.Count} pending")
            : text.Get("声明已确认", "Notices acknowledged");
        var tone = pending.Count > 0 ? PillTone.Warn : PillTone.Ok;
        var pillWidth = DactUi.PillWidth(status);
        right -= pillWidth;
        DactUi.DrawPill(draw, new Vector2(right, min.Y + (height - 22 * s) * .5f), status, tone);

        if (right - 8 * s > dragLeft)
        {
            ImGui.SetCursorScreenPos(new Vector2(dragLeft, min.Y));
            ImGui.InvisibleButton("##third-party-drag", new Vector2(right - 8 * s - dragLeft, height));
            headerDrag.HandleItem();
        }
    }

    private void DrawContent()
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        // Title with its quiet note on the same line.
        var row = ImGui.GetCursorScreenPos();
        var heading = text.Get("第三方扩展来源声明", "Third-party extension sources");
        DactUi.Text(heading, size: DactFonts.Title);
        var note = text.Get("安装前核对", "Review before installation");
        using (DactFonts.Push(DactFonts.Title))
            DactUi.DrawText(ImGui.GetWindowDrawList(), new Vector2(row.X + DactUi.TextWidth(heading, DactFonts.Title) + 10 * s, row.Y), 24 * s, note, DactUi.HintColor, DactFonts.Small);

        if (!string.IsNullOrWhiteSpace(result)) DrawBanner();
        DrawAbout();
        if (disclosures.Count == 0)
        {
            DactUi.BeginCard("third-party-empty-state", text.Get("来源检查已完成", "Source check complete"));
            DactUi.Hint(text.Get("当前没有需要确认或安装的 DLL 更新。", "There are no DLL updates awaiting acknowledgement or installation."));
            DactUi.EndCard();
            return;
        }
        if (pending.Count == 0)
            DactUi.Wrapped(text.Get("当前声明已确认；作者和来源信息仍会长期显示。", "The current notices are acknowledged; author and source details remain visible."), palette.Accent);

        var pendingIds = pending.Select(static plugin => plugin.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var width = ImGui.GetContentRegionAvail().X;
        var gap = 12 * s;
        var cardWidth = (width - gap * 2) / 3;
        for (var start = 0; start < disclosures.Count; start += 3)
        {
            var rowItems = disclosures.Skip(start).Take(3).ToArray();
            var rowTop = ImGui.GetCursorScreenPos();
            var rowHeight = Math.Max(240 * s, rowItems.Max(plugin => cardHeights.TryGetValue(plugin.Id, out var measured) ? measured : 0));
            for (var index = 0; index < rowItems.Length; index++)
            {
                var plugin = rowItems[index];
                cardHeights[plugin.Id] = DrawPluginCard(plugin, pendingIds.Contains(plugin.Id),
                    rowTop + new Vector2(index * (cardWidth + gap), 0), cardWidth, rowHeight);
            }
            ImGui.SetCursorScreenPos(new Vector2(rowTop.X, rowTop.Y + rowHeight));
            ImGui.Dummy(new Vector2(width, 0));
        }
    }

    // 检查结果 / 正在检查更新 strip under the title.
    private void DrawBanner()
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var label = updateCheckInProgress ? text.Get("正在检查更新", "Checking for updates") : text.Get("检查结果", "Check result");
        var labelWidth = DactUi.TextWidth(label, DactFonts.Small) + 8 * s;
        var lines = DactUi.WrapLines(result, width - 24 * s - labelWidth);
        float textHeight;
        using (DactFonts.Push(DactFonts.Small)) textHeight = ImGui.CalcTextSize(string.Join("\n", lines)).Y;
        var height = Math.Max(32 * s, textHeight + 16 * s);
        ImGui.Dummy(new Vector2(width, height));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(DactUi.AccentSoft), 8 * s);
        using (DactFonts.Push(DactFonts.Small))
        {
            draw.AddText(start + new Vector2(12 * s, 8 * s), DactUi.U32(DactTheme.Palette.Accent), label);
            draw.AddText(start + new Vector2(12 * s + labelWidth, 8 * s), DactUi.U32(DactTheme.Foreground(DactTheme.Palette.Text)), string.Join("\n", lines));
        }
    }

    private void DrawAbout()
    {
        DactUi.BeginCard("third-party-notice-summary", text.Get("关于这些 DLL", "About these DLLs"), gap: 6);
        DactUi.Wrapped(text.Get(
            "DACT 随安装包提供以下第三方 ACT DLL，并在启动时检查其公开上游。它们不由本项目开发，也不代表原作者或维护者与本项目存在合作、认可或联系。首次安装、本插件每次更新及发现上游 DLL 更新后，都会在安装或更新前展示本声明。",
            "DACT bundles the third-party ACT DLLs below and checks their public upstream sources at startup. They are not authored by this project, and no collaboration, endorsement, or affiliation with their authors or maintainers is implied. This notice is shown before installation on first use, after each plugin update, and when a newer upstream DLL is found."));
        DactUi.Hint(text.Get(
            "下列网址仅用于核对作者项目、许可证、源码和实际下载来源；仅在点击按钮后打开。",
            "The URLs below identify the author project, license, source, and actual download location; they open only when you click a button."));
        DactUi.EndCard();
    }

    // Returns the height the card's content needs; the caller sizes the whole row from it.
    private float DrawPluginCard(BundledActPluginDescriptor plugin, bool requiresAcknowledgement, Vector2 min, float width, float height)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + new Vector2(width, height), DactUi.U32(palette.Raised), 10 * s);
        draw.AddRect(min, min + new Vector2(width, height), DactUi.U32(requiresAcknowledgement ? palette.Gold with { W = .7f } : DactUi.CardBorder),
            10 * s, ImDrawFlags.None, Math.Max(1, s));
        var inner = width - 28 * s;
        ImGui.PushID($"third-party-card-{plugin.Id}");

        // Top: name, version + state, then the metadata rows.
        ImGui.SetCursorScreenPos(min + new Vector2(14 * s));
        ImGui.BeginGroup();
        using (DactUi.Gap(10))
        {
            ImGui.BeginGroup();
            using (DactUi.Gap(4))
            {
                DactUi.Wrapped(plugin.Name, palette.Text, DactFonts.Body, inner);
                var version = text.Format($"版本 {plugin.Version}", $"Version {plugin.Version}");
                var stateRow = ImGui.GetCursorScreenPos();
                DactUi.DrawText(draw, stateRow, 22 * s, version, DactUi.HintColor, DactFonts.Small);
                var state = requiresAcknowledgement
                    ? plugin.IsOnlineUpdate
                        ? text.Get("待确认作者上游更新", "Author update awaiting acknowledgement")
                        : text.Get("待确认安装包内版本", "Bundled version awaiting acknowledgement")
                    : text.Get("当前声明已确认", "Current notice acknowledged");
                var stateX = DactUi.TextWidth(version, DactFonts.Small) + 8 * s;
                var stateLabel = DactUi.Fit(state, inner - stateX - 18 * s);
                DactUi.DrawPill(draw, stateRow + new Vector2(stateX, 0), stateLabel, requiresAcknowledgement ? PillTone.Warn : PillTone.Ok, dot: false);
                ImGui.Dummy(new Vector2(inner, 22 * s));
            }
            ImGui.EndGroup();

            var meta = new List<(string Label, string Value)> { (text.Get("DLL 作者", "DLL author"), plugin.Author) };
            if (!string.Equals(plugin.Author, plugin.Maintainer, StringComparison.Ordinal)) meta.Add((text.Get("当前维护者", "Current maintainer"), plugin.Maintainer));
            if (!string.IsNullOrWhiteSpace(plugin.Copyright)) meta.Add((text.Get("版权", "Copyright"), plugin.Copyright));
            meta.Add((text.Get("许可证", "License"), plugin.License));
            var labelWidth = Math.Min(inner * .4f, Math.Max(72 * s, meta.Max(item => DactUi.TextWidth(item.Label, DactFonts.Small)) + 4 * s));
            ImGui.BeginGroup();
            using (DactUi.Gap(6))
            {
                foreach (var (label, value) in meta)
                {
                    var rowStart = ImGui.GetCursorScreenPos();
                    ImGui.BeginGroup();
                    DactUi.Wrapped(label, palette.Muted, DactFonts.Small, labelWidth);
                    ImGui.EndGroup();
                    var bottom = ImGui.GetItemRectMax().Y;
                    ImGui.SetCursorScreenPos(rowStart + new Vector2(labelWidth + 10 * s, 0));
                    ImGui.BeginGroup();
                    DactUi.Wrapped(value, palette.Text, DactFonts.Small, inner - labelWidth - 10 * s);
                    ImGui.EndGroup();
                    bottom = Math.Max(bottom, ImGui.GetItemRectMax().Y);
                    ImGui.SetCursorScreenPos(new Vector2(rowStart.X, bottom));
                    ImGui.Dummy(new Vector2(inner, 0));
                }
            }
            ImGui.EndGroup();
        }
        ImGui.EndGroup();
        var topHeight = ImGui.GetItemRectMax().Y - min.Y - 14 * s;

        // Bottom, pinned to the card's lower edge: hashes, divider, links.
        var hashes = new List<(string Label, string Value)>
        {
            (string.IsNullOrWhiteSpace(plugin.PackageSha256) ? "SHA-256" : text.Get("入口 DLL SHA-256", "Entry DLL SHA-256"), plugin.Sha256),
        };
        if (!string.IsNullOrWhiteSpace(plugin.PackageSha256)) hashes.Add((text.Get("完整包 SHA-256", "Complete package SHA-256"), plugin.PackageSha256));
        var hashBlock = 14 * s + 2 * s + 16 * s;
        var bottomHeight = hashes.Count * hashBlock + (hashes.Count - 1) * 6 * s + 10 * s + Math.Max(1, s) + 10 * s + 30 * s;
        var y = min.Y + height - 14 * s - bottomHeight;
        foreach (var (label, value) in hashes)
        {
            DactUi.DrawText(draw, new Vector2(min.X + 14 * s, y), 14 * s, label, palette.Muted, DactFonts.Tiny);
            // The full hash is long; the card shows its ends and the tooltip the whole value.
            var shown = value.Length > 12 ? $"{value[..4]}…{value[^4..]}" : value;
            var valueMin = new Vector2(min.X + 14 * s, y + 16 * s);
            DactUi.DrawText(draw, valueMin, 16 * s, shown, palette.Muted, DactFonts.Small);
            if (ImGui.IsMouseHoveringRect(valueMin, valueMin + new Vector2(inner, 16 * s)) && ImGui.IsWindowHovered()) DactUi.Tooltip(value);
            y += hashBlock + 6 * s;
        }
        y += 4 * s;
        draw.AddRectFilled(new Vector2(min.X + 14 * s, y), new Vector2(min.X + 14 * s + inner, y + Math.Max(1, s)), DactUi.U32(DactUi.DividerColor));
        y += Math.Max(1, s) + 10 * s;
        ImGui.SetCursorScreenPos(new Vector2(min.X + 14 * s, y));
        var links = new[]
        {
            (text.Get("项目", "Project"), plugin.ProjectUrl),
            (text.Get("源码", "Source"), plugin.SourceUrl),
            (text.Get("下载", "Download"), plugin.DownloadUrl),
        };
        for (var index = 0; index < links.Length; index++)
        {
            if (index > 0) ImGui.SameLine(0, 4 * s);
            var (label, url) = links[index];
            if (DactUi.Button(label + $"##link-{index}", ButtonKind.Ghost, "external", disabled: string.IsNullOrWhiteSpace(url))) OpenUrl(url);
            if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(url)) DactUi.Tooltip(url);
        }
        ImGui.PopID();
        return 14 * s + topHeight + 18 * s + bottomHeight + 14 * s;
    }

    // Outside the scroll area at the window bottom, so it is always visible.
    // bottom: where the window's usable area ends (above 艾欧泽亚's rim); the footer's divider and
    // fill also keep clear of the side rims there.
    private void DrawFooter(Vector2 min, Vector2 size, float bottom, float height)
    {
        var s = DactUi.S;
        var draw = ImGui.GetWindowDrawList();
        var top = bottom - height;
        var side = DactTheme.GameFrameSideInset;
        draw.AddRectFilled(new Vector2(min.X + side, top), new Vector2(min.X + size.X - side, top + Math.Max(1, s)), DactUi.U32(DactUi.DividerColor));
        draw.AddRectFilled(new Vector2(min.X + 1 + side, top + Math.Max(1, s)), new Vector2(min.X + size.X - 1 - side, bottom - 1),
            DactUi.U32(DactTheme.Palette.Raised), 10 * s, ImDrawFlags.RoundCornersBottom);
        var buttonTop = top + Math.Max(1, s) + (64 * s - 30 * s) * .5f;
        ImGui.SetCursorScreenPos(new Vector2(min.X + 22 * s, buttonTop));
        var installing = installTask is not null;
        var label = installing ? text.Get("正在安装 / 更新…", "Installing / updating…") : text.Get("知悉并安装 / 更新", "Acknowledge and install / update");
        if (DactUi.Button(label + "##third-party-install", ButtonKind.Primary, disabled: installing))
        {
            result = string.Empty;
            installTask = install(pending);
        }
        var hintX = ImGui.GetItemRectMax().X + 14 * s;
        var hint = openMode is ThirdPartyNoticeOpenMode.RequiredAfterPluginUpdate
            ? text.Get("必须确认来源并完成安装后才能继续权限设置。", "Acknowledge the sources and finish installation to continue to permissions.")
            : text.Get("可关闭后稍后处理；未确认的扩展保持禁用。确认来源并完成安装后才能继续权限设置。",
                "You may close and defer this step; unacknowledged extensions remain disabled. Acknowledge the sources and finish installation to continue to permissions.");
        var room = min.X + size.X - 22 * s - hintX;
        var lines = DactUi.WrapLines(hint, room);
        using (DactFonts.Push(DactFonts.Small))
        {
            var textHeight = ImGui.CalcTextSize(string.Join("\n", lines)).Y;
            draw.AddText(new Vector2(hintX, top + Math.Max(1, s) + (64 * s - textHeight) * .5f), DactUi.U32(DactTheme.Foreground(DactUi.HintColor)), string.Join("\n", lines));
        }
    }

    private void DrawPermissionChoiceDialog()
    {
        if (!showPermissionChoice) return;
        if (permissionPopupRequested)
        {
            DactUi.OpenDialog(PermissionDialog);
            permissionPopupRequested = false;
        }
        if (!DactUi.BeginDialog(PermissionDialog, 560, sharedScrim: false)) return;
        var s = DactUi.S;
        DactUi.Text(text.Get("是否启用扩展的完整功能？", "Enable full extension functionality?"), DactTheme.Palette.Gold, DactFonts.H2);
        DactUi.Wrapped(text.Get(
            "DLL 已完成安装。安全默认设置会关闭网络请求、启动外部程序、写入文件、游戏指令、原生内存和高风险脚本等能力；部分 Triggernometry、鲶鱼精邮差、银山雀儿和抹茶功能因此不可用。你可以现在一次性启用五项随包扩展各自声明的全部能力，也可以保持安全默认，之后在「扩展」每行的「权限」里逐项开启。",
            "The DLLs are installed. Safe defaults deny network requests, launching external processes, file writes, game commands, native memory access, and high-risk scripts, so some Triggernometry, PostNamazu, SilverDasher, and Matcha features will remain unavailable. You can enable every capability declared by the five bundled extensions now, or keep the safe defaults and grant them individually later with Permissions on each row of Extensions."));
        DactUi.Wrapped(text.Get(
            "请选择「同意完整权限」或「不同意并保持安全模式」；作出选择前此窗口无法关闭。",
            "Choose either full permissions or safe mode; this window cannot close until you make a choice."), DactTheme.Palette.Accent);
        var enableFull = DactUi.Button(text.Get("同意并启用完整权限", "Accept full permissions") + "##full-permissions", ButtonKind.Primary);
        ImGui.SameLine(0, 8 * s);
        var keepSafe = DactUi.Button(text.Get("不同意完整权限，保持安全模式", "Decline full permissions; keep safe mode") + "##safe-mode");
        if (enableFull || keepSafe)
        {
            configureFullPermissions(enableFull);
            showPermissionChoice = false;
            DactUi.CloseDialog(PermissionDialog);
            ContinueAfterPermissionChoice();
        }
        DactUi.EndDialog(PermissionDialog);
    }

    private void DrawTtsProChoiceDialog()
    {
        if (!showTtsProChoice) return;
        if (ttsProPopupRequested)
        {
            DactUi.OpenDialog(TtsProDialog);
            ttsProPopupRequested = false;
        }
        if (!DactUi.BeginDialog(TtsProDialog, 560, sharedScrim: false)) return;
        var s = DactUi.S;
        DactUi.Text(text.Get("是否将 FoxTTS 改为 Cafe TTS Pro？", "Switch FoxTTS to Cafe TTS Pro?"), DactTheme.Palette.Gold, DactFonts.H2);
        DactUi.Wrapped(text.Get(
            "本插件的默认语音链路以 Cafe TTS Pro 为目标。改用 Pro 后，Cactbot 与 Triggernometry 产生的播报文字会交给 Cafe TTS Pro，避免文字正常显示、语音却仍送往旧引擎或没有声音。这里只修改 FoxTTS 的语音引擎，不会覆盖其他 FoxTTS 设置。",
            "This plugin's default speech path targets Cafe TTS Pro. Switching lets Cactbot and Triggernometry speech use Cafe TTS Pro instead of an older engine that may display text but remain silent. Only the FoxTTS engine selection is changed; other FoxTTS settings are preserved."));
        DactUi.Hint(text.Get(
            "「本次不更改」会在下次插件更新时再询问；「不再提醒」会永久保留当前引擎且停止询问。",
            "Keep current asks again after the next plugin update; never remind preserves the current engine and stops future prompts."));
        var enablePro = DactUi.Button(text.Get("更改为 Pro", "Switch to Pro") + "##tts-pro", ButtonKind.Primary);
        ImGui.SameLine(0, 8 * s);
        var keepCurrent = DactUi.Button(text.Get("本次不更改", "Keep current") + "##tts-keep");
        var never = text.Get("不再提醒", "Never remind");
        ImGui.SameLine(ImGui.GetContentRegionMax().X - DactUi.ButtonWidth(never, ButtonKind.Ghost));
        var neverRemind = DactUi.Button(never + "##tts-never", ButtonKind.Ghost);
        if (enablePro || keepCurrent || neverRemind)
        {
            completeSetup(enablePro ? FoxTtsProChoice.EnablePro : neverRemind ? FoxTtsProChoice.NeverRemind : FoxTtsProChoice.KeepCurrent);
            showTtsProChoice = false;
            DactUi.CloseDialog(TtsProDialog);
            IsOpen = false;
        }
        DactUi.EndDialog(TtsProDialog);
    }

    public void OpenManualDisclosure()
    {
        openMode = ThirdPartyNoticeOpenMode.ManualDisclosure;
        Refresh(openWhenPending: false);
        IsOpen = true;
    }

    public void OpenRequiredAfterPluginUpdateWhenPending()
    {
        Refresh(openWhenPending: false);
        if (pending.Count > 0)
        {
            openMode = ThirdPartyNoticeOpenMode.RequiredAfterPluginUpdate;
            IsOpen = true;
        }
    }

    public void BeginUpdateCheck(bool userInitiated)
    {
        updateCheckInProgress = true;
        result = text.Get(
            "正在检查三项已注册在线来源的 DLL……",
            "Checking the three DLLs with registered online sources...");
        if (userInitiated)
        {
            openMode = ThirdPartyNoticeOpenMode.ManualUpdateCheck;
            Refresh(openWhenPending: false);
            IsOpen = true;
        }
    }

    public void CompleteUpdateCheck(
        string message,
        bool showWindow,
        bool userInitiated)
    {
        updateCheckInProgress = false;
        result = message;
        Refresh(openWhenPending: false);
        if (showWindow)
        {
            openMode = userInitiated
                ? ThirdPartyNoticeOpenMode.ManualUpdateCheck
                : ThirdPartyNoticeOpenMode.RequiredAfterPluginUpdate;
            IsOpen = true;
        }
    }

    internal static bool ShouldOpenUpdateResult(
        int pendingCount,
        bool failed,
        bool userInitiated)
        => pendingCount > 0 || (failed && userInitiated);

    internal static bool ShouldShowCloseButton(
        ThirdPartyNoticeOpenMode openMode,
        int pendingCount,
        bool installInProgress,
        bool permissionChoicePending,
        bool ttsProChoicePending)
        // Required post-update disclosures must remain visible until acknowledged. Manually
        // opened disclosures may still be deferred because unacknowledged DLLs fail closed.
        => !installInProgress &&
           !permissionChoicePending &&
           !ttsProChoicePending &&
           (pendingCount == 0 || openMode is not ThirdPartyNoticeOpenMode.RequiredAfterPluginUpdate);

    internal static bool CanAdvanceToPermissionChoice(int pendingCount)
        => pendingCount == 0;

    private void CompleteInstallWhenReady()
    {
        if (installTask is not { IsCompleted: true })
        {
            return;
        }

        try
        {
            var outcome = installTask.GetAwaiter().GetResult();
            updateCheckInProgress = false;
            Refresh(openWhenPending: false);
            if (!CanAdvanceToPermissionChoice(pending.Count))
            {
                throw new InvalidOperationException(
                    "Bundled plugin installation returned successfully, but one or more disclosures remain pending.");
            }

            result = outcome.RuntimeReady
                ? text.Get(
                    "内置 DLL 已安装/更新，告知记录已保存。",
                    "Bundled DLLs were installed/updated and the notice acknowledgement was saved.")
                : text.Format(
                    $"内置 DLL 和告知记录已保存；兼容 Host 正在后台恢复：{outcome.RuntimeWarning}",
                    $"Bundled DLLs and acknowledgements were saved; the compatibility Host is recovering in the background: {outcome.RuntimeWarning}");
            if (shouldPromptForPermissions())
            {
                BeginPermissionChoice();
            }
            else
            {
                ContinueAfterPermissionChoice();
            }
        }
        catch (Exception ex)
        {
            updateCheckInProgress = false;
            Refresh(openWhenPending: false);
            logger.Error(ex, "Bundled ACT plugin installation failed.");
            result = $"{text.Get("内置 DLL 安装失败", "Bundled DLL installation failed")}: {ex.GetBaseException().Message}";
        }
        finally
        {
            installTask = null;
        }
    }

    private void BeginPermissionChoice()
    {
        showPermissionChoice = true;
        permissionPopupRequested = true;
        IsOpen = true;
    }

    private void ContinueAfterPermissionChoice()
    {
        if (shouldOfferTtsPro())
        {
            showTtsProChoice = true;
            ttsProPopupRequested = true;
            IsOpen = true;
            return;
        }

        completeSetup(FoxTtsProChoice.KeepCurrent);
        IsOpen = false;
    }

    private void Refresh(bool openWhenPending)
    {
        disclosures = getDisclosures();
        pending = getPending();
        if (openWhenPending && pending.Count > 0)
        {
            IsOpen = true;
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Failed to open third-party source URL: {url}");
        }
    }
}
