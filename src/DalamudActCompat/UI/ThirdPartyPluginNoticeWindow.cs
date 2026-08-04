using Dalamud.Interface.Windowing;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Infrastructure.Logging;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI;

public sealed class ThirdPartyPluginNoticeWindow : Window
{
    private readonly Func<IReadOnlyList<BundledActPluginDescriptor>> getPending;
    private readonly Func<IReadOnlyList<BundledActPluginDescriptor>, Task> install;
    private readonly PluginLogger logger;
    private readonly UiText text;
    private IReadOnlyList<BundledActPluginDescriptor> pending = [];
    private Task? installTask;
    private string result = string.Empty;

    public ThirdPartyPluginNoticeWindow(
        Func<IReadOnlyList<BundledActPluginDescriptor>> getPending,
        Func<IReadOnlyList<BundledActPluginDescriptor>, Task> install,
        PluginLogger logger,
        UiText text)
        : base("内置第三方 DLL 告知###DalamudActCompatThirdPartyDllNotice")
    {
        this.getPending = getPending;
        this.install = install;
        this.logger = logger;
        this.text = text;
        Refresh(openWhenPending: false);
    }

    public override void Draw()
    {
        WindowName = text.Get(
            "内置第三方 DLL 告知###DalamudActCompatThirdPartyDllNotice",
            "Bundled third-party DLL notice###DalamudActCompatThirdPartyDllNotice");

        CompleteInstallWhenReady();
        ImGui.TextWrapped(text.Get(
            "Dalamud ACT Compat 随安装包提供以下第三方 ACT DLL，并在启动时检查其公开上游。它们不由本项目开发，也不代表原作者或维护者与本项目存在合作、认可或联系。首次安装、本插件每次更新及发现上游 DLL 更新后，都会在安装或更新前展示本告知。",
            "Dalamud ACT Compat bundles the third-party ACT DLLs below and checks their public upstream sources at startup. They are not authored by this project, and no collaboration, endorsement, or affiliation with their authors or maintainers is implied. This notice is shown before installation on first use, after every Dalamud ACT Compat update, and when a newer upstream DLL is found."));
        ImGui.TextWrapped(text.Get(
            "网址仅用于说明 DLL 的作者项目、源码和实际下载来源；本窗口不会自动打开网页。",
            "The URLs identify the author project, source, and actual DLL download location. This window does not open a web page."));

        foreach (var plugin in pending)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"{plugin.Name}  {plugin.Version}");
            ImGui.TextDisabled(plugin.IsOnlineUpdate
                ? text.Get("来源：启动时检查到的作者上游版本", "Source: author upstream version found at startup")
                : text.Get("来源：当前 Dalamud ACT Compat 安装包", "Source: current Dalamud ACT Compat package"));
            ImGui.TextWrapped($"{text.Get("DLL 作者", "DLL author")}: {plugin.Author}");
            if (!string.Equals(plugin.Author, plugin.Maintainer, StringComparison.Ordinal))
            {
                ImGui.TextWrapped($"{text.Get("当前维护者", "Current maintainer")}: {plugin.Maintainer}");
            }

            if (!string.IsNullOrWhiteSpace(plugin.Copyright))
            {
                ImGui.TextWrapped($"{text.Get("版权", "Copyright")}: {plugin.Copyright}");
            }

            ImGui.TextWrapped($"{text.Get("许可证", "License")}: {plugin.License}");
            DrawUrl(text.Get("项目网址", "Project URL"), plugin.ProjectUrl);
            DrawUrl(text.Get("源码网址", "Source URL"), plugin.SourceUrl);
            DrawUrl(text.Get("DLL 下载网址", "DLL download URL"), plugin.DownloadUrl);
            ImGui.TextWrapped($"SHA-256: {plugin.Sha256}");
        }

        ImGui.Separator();
        if (installTask is not null)
        {
            ImGui.TextUnformatted(text.Get(
                "正在安装/更新内置 DLL，请稍候……",
                "Installing/updating bundled DLLs..."));
        }
        else if (pending.Count > 0)
        {
            if (ImGui.Button(text.Get(
                    "我已知悉，安装/更新并启用",
                    "I understand; install/update and enable")))
            {
                result = string.Empty;
                installTask = install(pending);
            }

            ImGui.SameLine();
            if (ImGui.Button(text.Get(
                    "稍后处理（暂不启用这些 DLL）",
                    "Later (keep these DLLs disabled)")))
            {
                IsOpen = false;
            }
        }
        else
        {
            ImGui.TextDisabled(text.Get(
                "当前没有需要确认或安装的 DLL 更新。",
                "There are no DLL updates awaiting acknowledgement or installation."));
        }

        if (!string.IsNullOrWhiteSpace(result))
        {
            ImGui.TextWrapped(result);
        }
    }

    public void OpenNotice()
    {
        Refresh(openWhenPending: false);
        IsOpen = true;
    }

    public void BeginUpdateCheck(bool openWindow)
    {
        result = text.Get(
            "正在检查三项 DLL 的作者上游版本……",
            "Checking the author sources for all three DLLs...");
        if (openWindow)
        {
            IsOpen = true;
        }
    }

    public void CompleteUpdateCheck(string message, bool openWindow)
    {
        result = message;
        Refresh(openWhenPending: true);
        if (openWindow)
        {
            IsOpen = true;
        }
    }

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
            if (pending.Count == 0)
            {
                IsOpen = false;
            }
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
        pending = getPending();
        if (openWhenPending && pending.Count > 0)
        {
            IsOpen = true;
        }
    }

    private static void DrawUrl(string label, string url)
    {
        ImGui.TextWrapped($"{label}:");
        ImGui.SameLine();
        ImGui.TextWrapped(url);
    }
}
