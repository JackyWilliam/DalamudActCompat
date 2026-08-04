using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Infrastructure.Logging;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace DalamudActCompat.UI;

public sealed class ThirdPartyPluginNoticeWindow : Window
{
    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);
    private static readonly Vector4 NavyHover = new(0.105f, 0.145f, 0.185f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private readonly Func<IReadOnlyList<BundledActPluginDescriptor>> getDisclosures;
    private readonly Func<IReadOnlyList<BundledActPluginDescriptor>> getPending;
    private readonly Func<IReadOnlyList<BundledActPluginDescriptor>, Task> install;
    private readonly Action<bool> configureFullPermissions;
    private readonly PluginLogger logger;
    private readonly UiText text;
    private readonly ISharedImmediateTexture logoTexture;
    private IReadOnlyList<BundledActPluginDescriptor> disclosures = [];
    private IReadOnlyList<BundledActPluginDescriptor> pending = [];
    private Task? installTask;
    private string result = string.Empty;
    private bool showPermissionChoice;
    private bool outerFrameStylePushed;

    public ThirdPartyPluginNoticeWindow(
        Func<IReadOnlyList<BundledActPluginDescriptor>> getDisclosures,
        Func<IReadOnlyList<BundledActPluginDescriptor>> getPending,
        Func<IReadOnlyList<BundledActPluginDescriptor>, Task> install,
        Action<bool> configureFullPermissions,
        PluginLogger logger,
        UiText text,
        ISharedImmediateTexture logoTexture)
        : base("内置第三方 DLL 告知###DalamudActCompatThirdPartyDllNoticeLandscape")
    {
        this.getDisclosures = getDisclosures;
        this.getPending = getPending;
        this.install = install;
        this.configureFullPermissions = configureFullPermissions;
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
                    logoTexture,
                    text.Get("三方扩展", "Third-party extensions"),
                    noticeState,
                    pending.Count > 0 ? Gold : IceBlue,
                    ControlCenterWindow.FormatVersionLabel(
                        typeof(ThirdPartyPluginNoticeWindow).Assembly.GetName().Version),
                    "third-party-notice"))
            {
                IsOpen = false;
            }

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
                else if (showPermissionChoice)
                {
                    DrawPermissionChoice();
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
                    if (ImGui.Button(text.Get(
                            "稍后处理",
                            "Later"),
                            new Vector2(110, 36)))
                    {
                        IsOpen = false;
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled(text.Get(
                        "稍后处理不会启用待安装的 DLL。",
                        "Choosing later keeps pending DLLs disabled."));
                }

                if (!string.IsNullOrWhiteSpace(result))
                {
                    ImGui.Spacing();
                    ImGui.TextWrapped(result);
                }
            }
            ImGui.EndChild();
        }
        finally
        {
            PopTheme();
        }
    }

    public void OpenNotice()
    {
        Refresh(openWhenPending: false);
        IsOpen = true;
    }

    public void BeginUpdateCheck()
    {
        result = text.Get(
            "正在检查三项 DLL 的作者上游版本……",
            "Checking the author sources for all three DLLs...");
    }

    public void CompleteUpdateCheck(string message, bool showWindow)
    {
        result = message;
        Refresh(openWhenPending: false);
        if (showWindow)
        {
            IsOpen = true;
        }
    }

    internal static bool ShouldOpenUpdateResult(
        int pendingCount,
        bool failed,
        bool userInitiated)
        => pendingCount > 0 || (failed && userInitiated);

    private void CompleteInstallWhenReady()
    {
        if (installTask is not { IsCompleted: true })
        {
            return;
        }

        try
        {
            installTask.GetAwaiter().GetResult();
            result = text.Get(
                "内置 DLL 已安装/更新，告知记录已保存。",
                "Bundled DLLs were installed/updated and the notice acknowledgement was saved.");
            Refresh(openWhenPending: false);
            showPermissionChoice = true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Bundled ACT plugin installation failed.");
            result = $"{text.Get("内置 DLL 安装失败", "Bundled DLL installation failed")}: {ex.GetBaseException().Message}";
        }
        finally
        {
            installTask = null;
        }
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
        if (ImGui.BeginChild("card", new Vector2(-1, 356), true))
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
            ImGui.TextDisabled("SHA-256");
            ImGui.TextWrapped(plugin.Sha256);
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

    private void DrawPermissionChoice()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
        if (ImGui.BeginChild("third-party-permission-choice", new Vector2(-1, 188), true))
        {
            ImGui.TextColored(Gold, text.Get("是否启用扩展的完整功能？", "Enable full extension functionality?"));
            ImGui.TextWrapped(text.Get(
                "DLL 已完成安装。安全默认设置会关闭网络请求、启动外部程序、写入文件、游戏指令、原生内存和高风险脚本等能力；部分 Triggernometry 与鲶鱼精邮差功能因此不可用。你可以现在一次性启用三项随包扩展已声明的全部能力，也可以保持安全默认，之后在“扩展 → ACT 插件权限边界”逐项开启。",
                "The DLLs are installed. Safe defaults deny network requests, launching external processes, file writes, game commands, native memory access, and high-risk scripts, so some Triggernometry and PostNamazu features will remain unavailable. You can enable every declared capability for the three bundled extensions now, or keep the safe defaults and grant them individually later under Extensions > ACT plugin permission boundary."));

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.11f, 0.29f, 0.38f, 1));
            if (ImGui.Button(text.Get("启用完整功能", "Enable full functionality"), new Vector2(170, 36)))
            {
                configureFullPermissions(true);
                showPermissionChoice = false;
                IsOpen = false;
            }
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Button(text.Get("保持安全默认", "Keep safe defaults"), new Vector2(160, 36)))
            {
                configureFullPermissions(false);
                showPermissionChoice = false;
                IsOpen = false;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
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
