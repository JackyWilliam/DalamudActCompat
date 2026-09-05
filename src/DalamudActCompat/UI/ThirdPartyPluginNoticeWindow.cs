using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Infrastructure.Logging;
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

public sealed class ThirdPartyPluginNoticeWindow : Window
{
    private const string PermissionPopupId = "扩展完整功能###DalamudActCompatFullPermissions";
    private const string TtsProPopupId = "FoxTTS Pro 设置###DalamudActCompatFoxTtsPro";
    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);
    private static readonly Vector4 NavyHover = new(0.105f, 0.145f, 0.185f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
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
        Size = new Vector2(1200, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(960, 540),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
        Refresh(openWhenPending: false);
    }

    public override void PreDraw()
    {
        headerDrag.PrepareNextWindow();
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
        WindowName = text.Get(
            "内置第三方 DLL 告知###DalamudActCompatThirdPartyDllNoticeLandscape",
            "Bundled third-party DLL notice###DalamudActCompatThirdPartyDllNoticeLandscape");

        PushTheme();
        try
        {
            CompleteInstallWhenReady();
            var noticeState = pending.Count > 0
                ? text.Get($"待确认 {pending.Count} 项", $"{pending.Count} pending")
                : text.Get("声明已确认", "Notices acknowledged");
            if (BrandedWindowChrome.Draw(
                    headerDrag,
                    logoTexture,
                    text.Get("三方扩展", "Third-party extensions"),
                    noticeState,
                    pending.Count > 0 ? Gold : IceBlue,
                    ControlCenterWindow.FormatVersionLabel(
                        typeof(ThirdPartyPluginNoticeWindow).Assembly.GetName().Version),
                    "third-party-notice",
                    showCloseButton: ShouldShowCloseButton(
                        openMode,
                        pending.Count,
                        installTask is not null,
                        showPermissionChoice,
                        showTtsProChoice)))
            {
                IsOpen = false;
            }

            DrawPermissionChoiceModal();
            DrawTtsProChoiceModal();
            DrawUpdateStatusBanner();

            if (ImGui.BeginChild("third-party-notice-content", new Vector2(-1, -1), true))
            {
                ImGui.TextColored(Gold, text.Get("第三方扩展来源声明", "Third-party extension sources"));
                ImGui.SameLine();
                ImGui.TextDisabled(text.Get("安装前核对", "Review before installation"));
                ImGui.Spacing();

                ImGui.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
                if (ImGui.BeginChild("third-party-notice-summary", new Vector2(-1, 110), true))
                {
                    ImGui.TextColored(IceBlue, text.Get("关于这些 DLL", "About these DLLs"));
                    ImGui.TextWrapped(text.Get(
                        "Dalamud ACT Compat 随安装包提供以下第三方 ACT DLL，并在启动时检查其公开上游。它们不由本项目开发，也不代表原作者或维护者与本项目存在合作、认可或联系。首次安装、本插件每次更新及发现上游 DLL 更新后，都会在安装或更新前展示本声明。",
                        "Dalamud ACT Compat bundles the third-party ACT DLLs below and checks their public upstream sources at startup. They are not authored by this project, and no collaboration, endorsement, or affiliation with their authors or maintainers is implied. This notice is shown before installation on first use, after each plugin update, and when a newer upstream DLL is found."));
                    ImGui.TextDisabled(text.Get(
                        "下列网址仅用于核对作者项目、许可证、源码和实际下载来源；仅在点击按钮后打开。",
                        "The URLs below identify the author project, license, source, and actual download location; they open only when you click a button."));
                }
                ImGui.EndChild();
                ImGui.PopStyleColor();

                ImGui.Spacing();
                if (disclosures.Count == 0)
                {
                    DrawEmptyState();
                }
                else
                {
                    if (pending.Count == 0)
                    {
                        ImGui.TextColored(IceBlue, text.Get(
                            "当前声明已确认；作者和来源信息仍会长期显示。",
                            "The current notices are acknowledged; author and source details remain visible."));
                        ImGui.Spacing();
                    }

                    var pendingIds = pending
                        .Select(static plugin => plugin.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (ImGui.BeginTable(
                            "third-party-plugin-cards",
                            3,
                            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
                    {
                        foreach (var plugin in disclosures)
                        {
                            ImGui.TableNextColumn();
                            DrawPluginCard(plugin, pendingIds.Contains(plugin.Id));
                        }
                        ImGui.EndTable();
                    }
                }

                ImGui.Separator();
                if (installTask is not null)
                {
                    ImGui.TextColored(IceBlue, text.Get(
                        "正在安装 / 更新扩展，请稍候……",
                        "Installing / updating extensions..."));
                }
                else if (pending.Count > 0)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.11f, 0.29f, 0.38f, 1));
                    if (ImGui.Button(text.Get(
                            "知悉并安装 / 更新",
                            "Acknowledge and install / update"),
                            new Vector2(190, 36)))
                    {
                        result = string.Empty;
                        installTask = install(pending);
                    }
                    ImGui.PopStyleColor();

                    ImGui.SameLine();
                    ImGui.TextDisabled(openMode is ThirdPartyNoticeOpenMode.RequiredAfterPluginUpdate
                        ? text.Get(
                            "必须确认来源并完成安装后才能继续权限设置。",
                            "Acknowledge the sources and finish installation to continue to permissions.")
                        : text.Get(
                            "可关闭后稍后处理；未确认的扩展保持禁用。确认来源并完成安装后才能继续权限设置。",
                            "You may close and defer this step; unacknowledged extensions remain disabled. Acknowledge the sources and finish installation to continue to permissions."));
                }

            }
            ImGui.EndChild();
        }
        finally
        {
            PopTheme();
        }
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
                : text.Get(
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

    private void DrawPluginCard(BundledActPluginDescriptor plugin, bool requiresAcknowledgement)
    {
        ImGui.PushID($"third-party-card-{plugin.Id}");
        ImGui.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
        if (ImGui.BeginChild("card", new Vector2(-1, 388), true))
        {
            ImGui.TextColored(Gold, plugin.Name);
            ImGui.SameLine();
            ImGui.TextColored(IceBlue, $"v{plugin.Version}");
            ImGui.TextColored(
                requiresAcknowledgement ? Gold : new Vector4(0.66f, 0.70f, 0.75f, 1),
                requiresAcknowledgement
                    ? plugin.IsOnlineUpdate
                        ? text.Get("待确认作者上游更新", "Author update awaiting acknowledgement")
                        : text.Get("待确认安装包内版本", "Bundled version awaiting acknowledgement")
                    : text.Get("当前声明已确认", "Current notice acknowledged"));

            DrawMetadata(text.Get("DLL 作者", "DLL author"), plugin.Author);
            if (!string.Equals(plugin.Author, plugin.Maintainer, StringComparison.Ordinal))
            {
                DrawMetadata(text.Get("当前维护者", "Current maintainer"), plugin.Maintainer);
            }
            if (!string.IsNullOrWhiteSpace(plugin.Copyright))
            {
                DrawMetadata(text.Get("版权", "Copyright"), plugin.Copyright);
            }
            DrawMetadata(text.Get("许可证", "License"), plugin.License);

            ImGui.Separator();
            DrawUrl(text.Get("项目", "Project"), plugin.ProjectUrl);
            DrawUrl(text.Get("源码", "Source"), plugin.SourceUrl);
            DrawUrl(text.Get("下载", "Download"), plugin.DownloadUrl);
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(plugin.PackageSha256)
                ? "SHA-256"
                : text.Get("入口 DLL SHA-256", "Entry DLL SHA-256"));
            ImGui.TextWrapped(plugin.Sha256);
            if (!string.IsNullOrWhiteSpace(plugin.PackageSha256))
            {
                ImGui.TextDisabled(text.Get("完整包 SHA-256", "Complete package SHA-256"));
                ImGui.TextWrapped(plugin.PackageSha256);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopID();
    }

    private static void DrawMetadata(string label, string value)
    {
        ImGui.TextDisabled($"{label}  ");
        ImGui.SameLine();
        ImGui.TextWrapped(value);
    }

    private void DrawUrl(string label, string url)
    {
        ImGui.TextColored(IceBlue, label);
        ImGui.SameLine(88);
        if (ImGui.SmallButton($"{text.Get("打开", "Open")}##{label}"))
        {
            OpenUrl(url);
        }
        ImGui.SameLine();
        ImGui.TextWrapped(url);
    }

    private void DrawEmptyState()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
        if (ImGui.BeginChild("third-party-empty-state", new Vector2(-1, 92), true))
        {
            ImGui.TextColored(IceBlue, text.Get("来源检查已完成", "Source check complete"));
            ImGui.TextDisabled(text.Get(
                "当前没有需要确认或安装的 DLL 更新。",
                "There are no DLL updates awaiting acknowledgement or installation."));
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawPermissionChoiceModal()
    {
        if (!showPermissionChoice)
        {
            return;
        }

        if (permissionPopupRequested)
        {
            ImGui.OpenPopup(PermissionPopupId);
            permissionPopupRequested = false;
        }

        const float popupWidth = 650;
        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        ImGui.SetNextWindowPos(
            new Vector2(
                parentPosition.X + Math.Max(16, (parentSize.X - popupWidth) * 0.5f),
                parentPosition.Y + 72),
            ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0), ImGuiCond.Appearing);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.Border, Gold);
        ImGui.PushStyleColor(ImGuiCol.ModalWindowDimBg, new Vector4(0, 0, 0, 0.66f));
        if (!ImGui.BeginPopupModal(
                PermissionPopupId,
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove))
        {
            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar(2);
            return;
        }

        ImGui.TextColored(Gold, text.Get("是否启用扩展的完整功能？", "Enable full extension functionality?"));
        ImGui.TextWrapped(text.Get(
            "DLL 已完成安装。安全默认设置会关闭网络请求、启动外部程序、写入文件、游戏指令、原生内存和高风险脚本等能力；部分 Triggernometry、鲶鱼精邮差、银山雀儿和抹茶功能因此不可用。你可以现在一次性启用五项随包扩展各自声明的全部能力，也可以保持安全默认，之后在“扩展 → ACT 插件权限边界”逐项开启。",
            "The DLLs are installed. Safe defaults deny network requests, launching external processes, file writes, game commands, native memory access, and high-risk scripts, so some Triggernometry, PostNamazu, SilverDasher, and Matcha features will remain unavailable. You can enable every capability declared by the five bundled extensions now, or keep the safe defaults and grant them individually later under Extensions > ACT plugin permission boundary."));
        ImGui.TextColored(IceBlue, text.Get(
            "请选择“同意完整权限”或“不同意并保持安全模式”；作出选择前此窗口无法关闭。",
            "Choose either full permissions or safe mode; this window cannot close until you make a choice."));
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.11f, 0.29f, 0.38f, 1));
        var enableFull = ImGui.Button(
            text.Get("同意并启用完整权限", "Accept full permissions"),
            new Vector2(190, 36));
        ImGui.PopStyleColor();
        ImGui.SameLine();
        var keepSafe = ImGui.Button(
            text.Get("不同意完整权限，保持安全模式", "Decline full permissions; keep safe mode"),
            new Vector2(240, 36));

        if (enableFull || keepSafe)
        {
            configureFullPermissions(enableFull);
            showPermissionChoice = false;
            ImGui.CloseCurrentPopup();
            ContinueAfterPermissionChoice();
        }

        ImGui.EndPopup();
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(2);
    }

    private void DrawTtsProChoiceModal()
    {
        if (!showTtsProChoice)
        {
            return;
        }

        if (ttsProPopupRequested)
        {
            ImGui.OpenPopup(TtsProPopupId);
            ttsProPopupRequested = false;
        }

        const float popupWidth = 650;
        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        ImGui.SetNextWindowPos(
            new Vector2(
                parentPosition.X + Math.Max(16, (parentSize.X - popupWidth) * 0.5f),
                parentPosition.Y + 72),
            ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0), ImGuiCond.Appearing);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.Border, Gold);
        ImGui.PushStyleColor(ImGuiCol.ModalWindowDimBg, new Vector4(0, 0, 0, 0.66f));
        var ttsPopupOpen = true;
        if (!ImGui.BeginPopupModal(
                TtsProPopupId,
                ref ttsPopupOpen,
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove))
        {
            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar(2);
            if (!ttsPopupOpen)
            {
                completeSetup(FoxTtsProChoice.KeepCurrent);
                showTtsProChoice = false;
                IsOpen = false;
            }
            return;
        }

        ImGui.TextColored(Gold, text.Get(
            "是否将 FoxTTS 改为 Cafe TTS Pro？",
            "Switch FoxTTS to Cafe TTS Pro?"));
        ImGui.TextWrapped(text.Get(
            "本插件的默认语音链路以 Cafe TTS Pro 为目标。改用 Pro 后，Cactbot 与 Triggernometry 产生的播报文字会交给 Cafe TTS Pro，避免文字正常显示、语音却仍送往旧引擎或没有声音。这里只修改 FoxTTS 的语音引擎，不会覆盖其他 FoxTTS 设置。",
            "This plugin's default speech path targets Cafe TTS Pro. Switching lets Cactbot and Triggernometry speech use Cafe TTS Pro instead of an older engine that may display text but remain silent. Only the FoxTTS engine selection is changed; other FoxTTS settings are preserved."));
        ImGui.TextDisabled(text.Get(
            "“本次不更改”会在下次插件更新时再询问；“不再提醒”会永久保留当前引擎且停止询问。",
            "Keep current asks again after the next plugin update; never remind preserves the current engine and stops future prompts."));
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.11f, 0.29f, 0.38f, 1));
        var enablePro = ImGui.Button(
            text.Get("更改为 Pro", "Switch to Pro"),
            new Vector2(150, 36));
        ImGui.PopStyleColor();
        ImGui.SameLine();
        var keepCurrent = ImGui.Button(
            text.Get("本次不更改", "Keep current"),
            new Vector2(150, 36));
        ImGui.SameLine();
        var neverRemind = ImGui.Button(
            text.Get("不再提醒", "Never remind"),
            new Vector2(150, 36));

        if (enablePro || keepCurrent || neverRemind)
        {
            completeSetup(enablePro
                ? FoxTtsProChoice.EnablePro
                : neverRemind
                    ? FoxTtsProChoice.NeverRemind
                    : FoxTtsProChoice.KeepCurrent);
            showTtsProChoice = false;
            ImGui.CloseCurrentPopup();
            IsOpen = false;
        }

        ImGui.EndPopup();
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(2);
    }

    private void DrawUpdateStatusBanner()
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        if (BrandedWindowChrome.BeginGoldCard("third-party-update-status", 72))
        {
            ImGui.TextColored(
                updateCheckInProgress ? Gold : IceBlue,
                updateCheckInProgress
                    ? text.Get("正在检查更新", "Checking for updates")
                    : text.Get("检查结果", "Check result"));
            ImGui.TextWrapped(result);
        }
        BrandedWindowChrome.EndGoldCard();
        ImGui.Spacing();
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
