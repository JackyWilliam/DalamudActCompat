using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.UI;

public sealed class HelpWindow : Window
{
    private enum HelpPage
    {
        UsageNotice,
        GettingStarted,
        CombatMeter,
        Overlays,
        Extensions,
        Troubleshooting,
        Copyright,
    }

    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);
    private static readonly Vector4 NavyHover = new(0.105f, 0.145f, 0.185f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private static readonly Vector4 Warning = new(0.96f, 0.36f, 0.34f, 1);

    private readonly UiText text;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly PluginLogger logger;
    private readonly Action openRuntimeStatus;
    private readonly Action openLogDirectory;
    private readonly Action openThirdPartyNotice;
    private HelpPage selectedPage;
    private bool outerFrameStylePushed;

    public HelpWindow(
        UiText text,
        ISharedImmediateTexture logoTexture,
        PluginLogger logger,
        Action openRuntimeStatus,
        Action openLogDirectory,
        Action openThirdPartyNotice)
        : base("使用帮助###DalamudActCompatHelpWindow")
    {
        this.text = text;
        this.logoTexture = logoTexture;
        this.logger = logger;
        this.openRuntimeStatus = openRuntimeStatus;
        this.openLogDirectory = openLogDirectory;
        this.openThirdPartyNotice = openThirdPartyNotice;
        Size = new Vector2(980, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 580),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
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
            "使用帮助###DalamudActCompatHelpWindow",
            "Help###DalamudActCompatHelpWindow");
        PushTheme();
        try
        {
            if (BrandedWindowChrome.Draw(
                    logoTexture,
                    text.Get("使用帮助", "Help"),
                    text.Get("使用文档", "User guide"),
                    IceBlue,
                    ControlCenterWindow.FormatVersionLabel(
                        typeof(HelpWindow).Assembly.GetName().Version),
                    "help-window"))
            {
                IsOpen = false;
            }

            DrawNavigation();
            ImGui.Spacing();
            if (ImGui.BeginChild("help-document-content", new Vector2(-1, -1), true))
            {
                ImGui.PushTextWrapPos(0);
                DrawSelectedPage();
                ImGui.PopTextWrapPos();
            }
            ImGui.EndChild();
        }
        finally
        {
            PopTheme();
        }
    }

    private void DrawNavigation()
    {
        var labels = new[]
        {
            text.Get("使用须知", "Notice"),
            text.Get("快速开始", "Start"),
            text.Get("战斗统计", "Meter"),
            text.Get("悬浮窗", "Overlays"),
            text.Get("扩展", "Extensions"),
            text.Get("排错", "Troubleshooting"),
            text.Get("版权声明", "Copyright"),
        };
        selectedPage = (HelpPage)BrandedWindowChrome.DrawNavigationRail(
            "help-document-navigation",
            labels,
            (int)selectedPage);
    }

    private void DrawSelectedPage()
    {
        switch (selectedPage)
        {
            case HelpPage.UsageNotice:
                DrawUsageNotice();
                break;
            case HelpPage.GettingStarted:
                DrawGettingStarted();
                break;
            case HelpPage.CombatMeter:
                DrawCombatMeter();
                break;
            case HelpPage.Overlays:
                DrawOverlays();
                break;
            case HelpPage.Extensions:
                DrawExtensions();
                break;
            case HelpPage.Troubleshooting:
                DrawTroubleshooting();
                break;
            case HelpPage.Copyright:
                DrawCopyright();
                break;
        }
    }

    private void DrawUsageNotice()
    {
        DrawPageHeader(
            text.Get("使用须知", "Usage notice"),
            text.Get("请先理解边界，再使用解析器、悬浮窗和第三方扩展。", "Understand the boundaries before using parsers, overlays, or third-party extensions."));
        DrawCard("help-notice-rules", text.Get("使用前请确认", "Before use"), 112, () =>
        {
            ImGui.TextWrapped(text.Get(
                "使用任何 Dalamud、ACT 或相关第三方工具之前，请自行了解对应游戏的用户协议以及第三方工具相关规则。",
                "Before using Dalamud, ACT, or related third-party tools, learn the applicable game user agreement and third-party-tool rules yourself."));
        });
        DrawCard("help-notice-discouraged", text.Get("项目本身不鼓励", "This project does not encourage"), 206, () =>
        {
            DrawBullet(text.Get("PVP 信息优势", "Information advantages in PvP"));
            DrawBullet(text.Get("影响其他玩家正常游戏", "Interfering with other players' normal gameplay"));
            DrawBullet(text.Get("利用解析数据骚扰其他玩家", "Using parsed data to harass other players"));
            DrawBullet(text.Get("在公开环境跳脸", "Flaunting plugins in public spaces"));
            DrawBullet(text.Get("使用插件进行不当行为", "Using plugins for inappropriate conduct"));
        });
        DrawCard("help-notice-boundary", text.Get("特别提醒", "Important reminder"), 284, () =>
        {
            ImGui.TextColored(Warning, text.Get("不要去绿玩面前跳脸。", "Do not flaunt plugins in front of players who do not use them."));
            ImGui.Spacing();
            ImGui.TextWrapped(text.Get("你自己开插件是一回事。", "Using a plugin privately is one thing."));
            ImGui.TextWrapped(text.Get("跑到别人面前：", "Going up to someone else and saying:"));
            ImGui.TextColored(IceBlue, text.Get("“你看我这个插件多牛逼！”", "“Look how amazing my plugin is!”"));
            ImGui.TextWrapped(text.Get(
                "然后把截图、悬浮窗、DPS、第三方工具全甩别人脸上，是另一回事。",
                "Then pushing screenshots, overlays, DPS, and third-party tools in their face is another matter."));
            ImGui.Spacing();
            ImGui.TextColored(Warning, text.Get("一经发现立刻踢出！", "If discovered, you will be removed immediately."));
            ImGui.TextWrapped(text.Get("请保持最基本的边界感。", "Please maintain basic boundaries and respect for others."));
        });
    }

    private void DrawGettingStarted()
    {
        DrawPageHeader(
            text.Get("快速开始", "Getting started"),
            text.Get("启动解析、打开常用入口并理解日志用途。", "Start parsing, use common entry points, and understand the available logs."));
        DrawCard("help-start-parser", text.Get("第一次使用", "First use"), 190, () =>
        {
            DrawBullet(text.Get(
                "保持“启用解析”和“自动启动解析器”开启；概览状态显示“运行中”后即可记录战斗。",
                "Keep Enable parsing and Auto start parser enabled. Combat recording is ready when Overview shows Running."));
            DrawBullet(text.Get(
                "“显示 ACT 快捷按钮”控制游戏画面上的入口：左键打开设置，右键打开战斗统计，按住中键拖动。",
                "Show ACT quick button controls the in-game entry: left-click settings, right-click Combat Meter, and hold middle mouse to drag."));
            DrawBullet(text.Get(
                "“战斗历史”查看已结束的战斗；“运行状态”查看解析器和各 Host。",
                "Encounter history shows completed encounters; Runtime status shows the parser and each Host."));
        });
        DrawCard("help-start-data", text.Get("数据与日志", "Data and logs"), 158, () =>
        {
            ImGui.TextWrapped(text.Get(
                "战斗数据由内置 FFXIV_ACT_Plugin 解析。“打开 FFLogs 上传日志”只会打开原始 Network 日志目录并复制路径，不会自动上传任何文件。诊断日志用于排查插件问题，两者用途不同。",
                "Combat data is parsed by the bundled FFXIV_ACT_Plugin. Open FFLogs upload logs only opens the raw Network log folder and copies its path; it never uploads files. Diagnostic logs are for troubleshooting and serve a different purpose."));
        });
    }

    private void DrawCombatMeter()
    {
        DrawPageHeader(
            text.Get("战斗统计", "Combat Meter"),
            text.Get("显示行为、数值口径和数据清理范围。", "Display behavior, metric definitions, and data-reset scope."));
        DrawCard("help-meter-display", text.Get("显示与交互", "Display and interaction"), 158, () =>
        {
            DrawBullet(text.Get(
                "锁定窗口用于固定位置；只有同时开启“锁定时鼠标穿透”时，点击才会传给游戏。",
                "Lock window fixes its position. Clicks pass through to the game only when Click-through when locked is also enabled."));
            DrawBullet(text.Get(
                "“脱战自动隐藏”只改变显示状态，不会停止解析或删除战斗数据。",
                "Auto hide out of combat changes only visibility; it does not stop parsing or delete combat data."));
        });
        DrawCard("help-meter-metrics", text.Get("数值口径", "Metrics"), 190, () =>
        {
            ImGui.TextWrapped(text.Get(
                "排序可选择 DPS 或 HPS。DPS 口径决定主数值使用个人动作时长、整场时长或 FF Logs 团队贡献估算。带“估算”的项目不是上传 FF Logs 后的最终排名。",
                "Sort by DPS or HPS. The DPS metric chooses personal active time, full encounter time, or an estimated FF Logs contribution metric. Estimated values are not final rankings from an FF Logs upload."));
            ImGui.Spacing();
            ImGui.TextWrapped(text.Get(
                "“重置当前战斗”只清空当前显示；历史记录和已经写入磁盘的原始日志不受影响。",
                "Reset current encounter clears only the current display; history and raw logs already written to disk are unaffected."));
        });
    }

    private void DrawOverlays()
    {
        DrawPageHeader(
            text.Get("悬浮窗", "Overlays"),
            text.Get("Cactbot 与自定义 HTML 悬浮窗的创建和交互规则。", "Creation and interaction rules for Cactbot and custom HTML overlays."));
        DrawCard("help-overlay-cactbot", "Cactbot", 130, () =>
        {
            ImGui.TextWrapped(text.Get(
                "Cactbot 使用插件安装到本地的页面。文字提醒与时间轴可以同时打开；它们与旧版组合窗口互斥。",
                "Cactbot uses pages installed locally by the plugin. Alerts and Timeline may run together; both conflict with the legacy combined window."));
        });
        DrawCard("help-overlay-html", text.Get("HTML 悬浮窗", "HTML overlays"), 254, () =>
        {
            DrawBullet(text.Get(
                "可从解析器模板或完整的 http、https、file 地址创建；只添加你信任的页面。",
                "Create from parser templates or complete http, https, and file URLs. Add only pages you trust."));
            DrawBullet(text.Get(
                "自定义页面会自动检测现代 OverlayPlugin 协议，再尝试旧 ACTWS 协议；保存的网址不会被改写。",
                "Custom pages automatically detect the modern OverlayPlugin protocol, then try legacy ACTWS. The saved URL is never rewritten."));
            DrawBullet(text.Get(
                "位置、大小、显示名称、锁定、穿透和缩放会按每个悬浮窗分别保存。",
                "Position, size, display name, lock, click-through, and zoom are stored per overlay."));
            DrawBullet(text.Get(
                "未启用鼠标穿透时可向网页传递点击和滚轮；自动检测失败后可重新检测或手动微调。",
                "Clicks and wheel input reach the page while click-through is off. If detection fails, retry it or make a manual adjustment."));
        });
    }

    private void DrawExtensions()
    {
        DrawPageHeader(
            text.Get("扩展与权限", "Extensions and permissions"),
            text.Get("安装流程、权限边界和用户需要承担的安全判断。", "Installation flow, permission boundaries, and the security decisions expected of users."));
        DrawCard("help-extension-install", text.Get("安装 DLL / ZIP", "Install DLL / ZIP"), 238, () =>
        {
            DrawBullet(text.Get(
                "选择文件后先进行静态预检；此阶段不会执行 DLL，并会生成需要的权限清单。",
                "Static preflight runs after file selection without executing the DLL and generates its requested permission list."));
            DrawBullet(text.Get(
                "普通第三方插件共用一个按需启动的通用 Host，不会为每个插件常驻一个进程。",
                "Generic third-party plugins share one on-demand Host; one persistent process is not created per plugin."));
            DrawBullet(text.Get(
                "同意授权后才会进行运行时检查；拒绝后插件保留但禁用，可稍后重新授权。",
                "Runtime checks begin only after consent. Denied plugins remain installed but disabled and may be authorized later."));
            DrawBullet(text.Get(
                "导入失败对话会显示原因并允许复制诊断；关闭对话只清除失败提示缓存。",
                "The import-failure window shows the reason and can copy diagnostics; closing it clears only the failure-notice cache."));
        });
        DrawCard("help-extension-safety", text.Get("安全要求", "Security requirements"), 190, () =>
        {
            ImGui.TextColored(Warning, text.Get("只安装你信任并能确认来源的 DLL、ZIP 和网页。", "Install only DLLs, ZIPs, and pages whose source you trust and can verify."));
            ImGui.TextWrapped(text.Get(
                "DLL 仍是桌面代码。权限清单约束兼容接口，但无法拦截 DLL 直接调用 Windows API。授权前请自行判断来源和风险；分享日志前请检查其中的角色名、服务器名和本地路径。",
                "A DLL remains desktop code. The permission list governs compatibility APIs but cannot intercept direct Windows API calls. Judge the source and risk before authorization, and inspect character names, server names, and local paths before sharing logs."));
            if (ImGui.Button(text.Get("查看内置第三方 DLL 的作者与来源", "View bundled DLL authors and sources")))
            {
                openThirdPartyNotice();
            }
        });
    }

    private void DrawTroubleshooting()
    {
        DrawPageHeader(
            text.Get("通知与排错", "Notifications and troubleshooting"),
            text.Get("先定位故障层级，再复制对应日志。", "Identify the failing layer before copying the relevant log."));
        DrawCard("help-troubleshooting-notifications", text.Get("通知策略", "Notification routing"), 140, () =>
        {
            ImGui.TextWrapped(text.Get(
                "抹茶和银山雀儿在游戏位于前台时使用游戏内卫月通知；切到其他应用时使用 Windows 通知中心。Windows 投递失败时会回退到游戏内通知。",
                "Matcha and SilverDasher use Dalamud notifications while the game is foreground. When another app is foreground, they use Windows Notification Center and fall back to Dalamud if Windows delivery fails."));
        });
        DrawCard("help-troubleshooting-steps", text.Get("没有数据或功能失败", "Missing data or failed features"), 238, () =>
        {
            DrawBullet(text.Get(
                "先确认概览状态为“运行中”，再查看解析器和各 Host 是否成功启动。",
                "First confirm Overview says Running, then check whether the parser and each Host started successfully."));
            DrawBullet(text.Get(
                "悬浮窗有画面但无数据时，查看自动协议检测状态并执行“重新检测”。",
                "If an overlay renders but has no data, inspect automatic protocol detection and use Detect again."));
            DrawBullet(text.Get(
                "第三方扩展失败时复制失败窗口的日志；其他问题使用设置页中的诊断日志。",
                "For extension failures, copy the log from the failure window. For other issues, use the diagnostic log in Settings."));
            DrawBullet(text.Get(
                "上传 FFLogs 使用原始 Network 日志；诊断日志不能代替战斗日志。",
                "Use raw Network logs for FFLogs uploads; diagnostic logs cannot replace combat logs."));
        });
        if (ImGui.Button(text.Get("打开运行状态", "Open runtime status"), new Vector2(170, 34)))
        {
            openRuntimeStatus();
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("打开诊断日志目录", "Open diagnostic logs"), new Vector2(190, 34)))
        {
            openLogDirectory();
        }
    }

    private void DrawCopyright()
    {
        DrawPageHeader(
            text.Get("版权声明", "Copyright notice"),
            text.Get("本页只声明 Dalamud ACT Compat 自身的版权与许可。", "This page states copyright and licensing only for Dalamud ACT Compat itself."));
        DrawCard("help-copyright-project", "Dalamud ACT Compat", 260, () =>
        {
            ImGui.TextColored(IceBlue, "Copyright © 2026 DalamudActCompat contributors.");
            ImGui.Spacing();
            ImGui.TextWrapped(text.Get(
                "Dalamud ACT Compat 自有源代码以 GNU General Public License version 3（GPL-3.0）发布。你可以在许可证条款下运行、研究、修改与再分发本项目。",
                "Dalamud ACT Compat's own source code is released under the GNU General Public License version 3 (GPL-3.0). You may run, study, modify, and redistribute the project under that license."));
            ImGui.Spacing();
            ImGui.TextWrapped(text.Get(
                "本程序按现状提供，不附带任何明示或默示担保，包括适销性或特定用途适用性的担保。完整条款以项目随附的 LICENSE.md 为准。",
                "This program is provided as-is, without any express or implied warranty, including merchantability or fitness for a particular purpose. The bundled LICENSE.md contains the complete terms."));
            ImGui.Spacing();
            ImGui.TextDisabled(text.Get(
                "本声明不主张任何第三方插件、依赖、网页悬浮窗、游戏内容、商标或素材的版权。",
                "This notice claims no copyright over third-party plugins, dependencies, web overlays, game content, trademarks, or assets."));
        });
        if (ImGui.Button(text.Get("查看项目源代码", "View project source"), new Vector2(170, 34)))
        {
            OpenUrl("https://github.com/JackyWilliam/DalamudActCompat");
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("查看 GPL-3.0 许可证", "View GPL-3.0 license"), new Vector2(190, 34)))
        {
            OpenUrl("https://github.com/JackyWilliam/DalamudActCompat/blob/main/LICENSE.md");
        }
    }

    private void DrawPageHeader(string title, string description)
    {
        ImGui.TextColored(Gold, title);
        ImGui.TextDisabled(description);
        ImGui.Spacing();
    }

    private static void DrawCard(string id, string title, float height, Action drawContent)
    {
        if (BrandedWindowChrome.BeginGoldCard(id, height, allowScrolling: false))
        {
            ImGui.TextColored(IceBlue, title);
            ImGui.Separator();
            drawContent();
        }
        BrandedWindowChrome.EndGoldCard();
        ImGui.Spacing();
    }

    private static void DrawBullet(string value)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped(value);
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
            logger.Error(ex, $"Could not open help URL '{url}'.");
        }
    }

    private static void PushTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.34f, 0.29f, 0.18f, 0.70f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, NavyRaised);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, NavyHover);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.17f, 0.24f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.25f, 0.34f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.30f, 0.37f, 1));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, IceBlue);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.22f, 0.25f, 0.28f, 1));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, NavyHover);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
    }

    private static void PopTheme()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(11);
    }
}
