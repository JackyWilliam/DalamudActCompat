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
        MacroCommands,
        CombatMeter,
        Overlays,
        Extensions,
        Troubleshooting,
        FrequentlyAskedQuestions,
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
    private string searchDraft = string.Empty;
    private string searchQuery = string.Empty;
    private string? pendingSectionId;
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
            DrawSearchBar();
            ImGui.Spacing();
            if (ImGui.BeginChild("help-document-content", new Vector2(-1, -1), true))
            {
                ImGui.PushTextWrapPos(0);
                if (string.IsNullOrWhiteSpace(searchQuery))
                {
                    DrawSelectedPage();
                }
                else
                {
                    DrawSearchResults();
                }
                ImGui.PopTextWrapPos();
            }
            ImGui.EndChild();
        }
        finally
        {
            PopTheme();
        }
    }

    public void OpenCommands()
    {
        selectedPage = HelpPage.MacroCommands;
        searchDraft = string.Empty;
        searchQuery = string.Empty;
        pendingSectionId = null;
        IsOpen = true;
    }

    private void DrawNavigation()
    {
        var labels = new[]
        {
            text.Get("使用须知", "Notice"),
            text.Get("快速开始", "Start"),
            text.Get("宏指令", "Commands"),
            text.Get("战斗统计", "Meter"),
            text.Get("悬浮窗", "Overlays"),
            text.Get("扩展", "Extensions"),
            text.Get("排错", "Fixes"),
            text.Get("常见问题", "FAQ"),
            text.Get("版权声明", "Copyright"),
        };
        var nextPage = (HelpPage)BrandedWindowChrome.DrawNavigationRail(
            "help-document-navigation",
            labels,
            (int)selectedPage);
        if (nextPage != selectedPage)
        {
            selectedPage = nextPage;
            searchDraft = string.Empty;
            searchQuery = string.Empty;
            pendingSectionId = null;
        }
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
            case HelpPage.MacroCommands:
                DrawMacroCommands();
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
            case HelpPage.FrequentlyAskedQuestions:
                DrawFrequentlyAskedQuestions();
                break;
            case HelpPage.Copyright:
                DrawCopyright();
                break;
        }
    }

    private void DrawSearchBar()
    {
        ImGui.Dummy(new Vector2(0, 8));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.72f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        if (ImGui.BeginChild(
                "help-search-card",
                new Vector2(-1, 58),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 7);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Navy);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.09f, 0.14f, 0.20f, 1));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.11f, 0.18f, 0.25f, 1));
            ImGui.SetNextItemWidth(-94);
            var submitted = ImGui.InputTextWithHint(
                "##help-search",
                text.Get("搜索功能、问题、命令或关键词……", "Search features, problems, commands, or keywords..."),
                ref searchDraft,
                128,
                ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.PopStyleColor(3);
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.29f, 0.38f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.17f, 0.38f, 0.49f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.20f, 0.44f, 0.56f, 1));
            ImGui.PushStyleColor(ImGuiCol.Text, IceBlue);
            submitted |= ImGui.Button("Search", new Vector2(82, 0));
            ImGui.PopStyleColor(4);
            if (submitted)
            {
                searchQuery = searchDraft.Trim();
                pendingSectionId = null;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
        ImGui.Spacing();
    }

    private void DrawSearchResults()
    {
        var results = CreateSearchEntries()
            .Where(entry => MatchesSearch(searchQuery, entry.SearchText))
            .ToArray();
        DrawPageHeader(
            text.Get("搜索结果", "Search results"),
            text.Get(
                $"找到 {results.Length} 条相关说明。点击结果可跳转到对应章节。",
                $"Found {results.Length} relevant entries. Select one to jump to its section."));
        if (results.Length == 0)
        {
            DrawCard("help-search-empty", text.Get("没有找到", "No results"), 120, () =>
            {
                ImGui.TextWrapped(text.Get(
                    "请尝试更短的关键词，例如“鲶鱼”“重启”“悬浮窗”“FFLogs”或命令中的参数。",
                    "Try a shorter keyword such as PostNamazu, restart, overlay, FFLogs, or a command argument."));
            });
            return;
        }

        foreach (var entry in results)
        {
            DrawCard($"help-search-{entry.SectionId}", entry.Title, 112, () =>
            {
                ImGui.TextDisabled(GetPageLabel(entry.Page));
                ImGui.TextWrapped(entry.Summary);
                if (ImGui.Selectable(
                        $"{text.Get("打开这一节", "Open this section")}##open-{entry.SectionId}"))
                {
                    selectedPage = entry.Page;
                    pendingSectionId = entry.SectionId;
                    searchDraft = string.Empty;
                    searchQuery = string.Empty;
                }
            });
        }
    }

    internal static bool MatchesSearch(string query, string content)
    {
        var tokens = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && tokens.All(token =>
            content.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private string GetPageLabel(HelpPage page) => page switch
    {
        HelpPage.UsageNotice => text.Get("使用须知", "Notice"),
        HelpPage.GettingStarted => text.Get("快速开始", "Getting started"),
        HelpPage.MacroCommands => text.Get("宏指令", "Commands"),
        HelpPage.CombatMeter => text.Get("战斗统计", "Combat Meter"),
        HelpPage.Overlays => text.Get("悬浮窗", "Overlays"),
        HelpPage.Extensions => text.Get("扩展", "Extensions"),
        HelpPage.Troubleshooting => text.Get("排错", "Troubleshooting"),
        HelpPage.FrequentlyAskedQuestions => text.Get("常见问题", "FAQ"),
        HelpPage.Copyright => text.Get("版权声明", "Copyright"),
        _ => page.ToString(),
    };

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
            text.Get("按顺序完成首次确认、认识控制中心并检查第一场战斗。", "Complete first-run confirmation, learn the control center, and check your first encounter in order."));
        DrawCard("help-start-parser", text.Get("第一次打开插件", "Opening the plugin for the first time"), 292, () =>
        {
            DrawBullet(text.Get(
                "首次启动会显示第三方扩展的作者、来源、版本和权限。请阅读后再确认；未确认的扩展不会加载。",
                "First launch shows each third-party extension's author, source, version, and permissions. Review them before accepting; unaccepted extensions are not loaded."));
            DrawBullet(text.Get(
                "打开主页，保持“启用解析”和“自动启动解析器”开启。顶部显示“运行中”后，战斗日志才会进入统计。",
                "On Home, keep Enable parsing and Auto start parser enabled. Combat logs enter the meter only after the header shows Running."));
            DrawBullet(text.Get(
                "如果一直停在初始化，输入 /actcompat status：先看解析器，再看共享 Host、抹茶 Host 和通用 Host，不要反复重载整个游戏。",
                "If initialization does not finish, use /actcompat status. Check the parser first, then the shared, Matcha, and generic Hosts instead of repeatedly reloading the whole game."));
        });
        DrawCard("help-start-control-center", text.Get("控制中心五个页面怎么用", "Using the five control-center pages"), 420, () =>
        {
            DrawBullet(text.Get(
                "概览：查看解析器是否运行，开关解析和自动启动，并打开战斗统计、战斗历史、运行状态或 FFLogs 原始日志目录。日常确认状态从这里开始。",
                "Overview: check whether the parser is running, control parsing and automatic startup, and open Combat Meter, history, Runtime Status, or the raw FFLogs log folder. Start routine checks here."));
            DrawBullet(text.Get(
                "战斗统计：显示或定位统计窗，调整锁定、穿透、自动隐藏、DPS/HPS 排序、DPS 口径、职业显示、匿名显示和 FFLogs DPS Parse 预估。",
                "Combat Meter: show or locate the meter and configure lock, click-through, auto hide, DPS/HPS sorting, DPS metric, job labels, anonymization, and estimated FFLogs DPS Parse."));
            DrawBullet(text.Get(
                "悬浮窗：安装或设置 Cactbot，从本地模板添加窗口，或用可信网址创建 HTML 悬浮窗；打开过的窗口在上方列表中分别管理。",
                "Overlays: install or configure Cactbot, add windows from local templates, or create an HTML overlay from a trusted URL. Previously opened windows are managed individually in the upper list."));
            DrawBullet(text.Get(
                "扩展：启停 FFXIV_ACT_Plugin、OverlayPlugin 和 ACT 扩展，打开扩展配置，检查版本与来源，安装 DLL/ZIP，并在页面下方设置权限。",
                "Extensions: enable or disable FFXIV_ACT_Plugin, OverlayPlugin, and ACT extensions; open extension configuration, check versions and sources, install DLL/ZIP files, and set permissions at the bottom of the page."));
            DrawBullet(text.Get(
                "设置：重启解析器、打开详细运行状态、复制诊断日志、调整语言和快捷按钮。恢复出厂设置会移动现有数据，只应作为最后手段。",
                "Settings: restart the parser, open detailed Runtime Status, copy diagnostics, and configure language and the quick button. Factory reset moves existing data and is a last resort."));
        });
        DrawCard("help-start-first-fight", text.Get("第一场战斗怎么确认成功", "Confirming your first encounter"), 246, () =>
        {
            DrawBullet(text.Get(
                "进入副本或攻击木人后产生一次有效伤害；战斗统计应出现自己和当前小队成员。联盟副本支持 24 人并按 A/B/C 三组显示；宠物和普通 NPC 不会作为独立玩家行显示。",
                "Enter a duty or attack a striking dummy and deal valid damage. Combat Meter should show you and the current party. Alliance duties support 24 players grouped as A/B/C; pets and ordinary NPCs are not separate player rows."));
            DrawBullet(text.Get(
                "右键 ACT 快捷按钮可打开或关闭战斗统计，输入 /actcompat meter 可打开并定位；战斗结束后用 /actcompat history 查看近期战斗。",
                "Right-click the ACT quick button to open or close Combat Meter, or use /actcompat meter to open and locate it. After combat, use /actcompat history for recent encounters."));
            DrawBullet(text.Get(
                "没有数据时先检查运行状态和当前是否真的产生伤害日志，再检查窗口是否被“脱战自动隐藏”，不要先删除配置。",
                "If no data appears, first check runtime status and whether damage logs were actually generated, then check Auto hide out of combat. Do not delete configuration first."));
        });
        DrawCard("help-start-shortcuts", text.Get("常用入口", "Common entry points"), 190, () =>
        {
            DrawBullet(text.Get(
                "“显示 ACT 快捷按钮”控制游戏画面上的入口：左键打开或关闭控制中心，右键打开或关闭战斗统计，按住中键拖动。",
                "Show ACT quick button controls the in-game entry: left-click to open or close Control Center, right-click to open or close Combat Meter, and hold middle mouse to drag."));
            DrawBullet(text.Get(
                "/actcompat 切换控制中心的打开与关闭，/actcompat on 始终打开，/actcompat off 始终关闭；/actcompat status 打开运行状态；所有命令都可在“宏指令”页一键复制。",
                "/actcompat toggles the control center, /actcompat on always opens it, and /actcompat off always closes it; /actcompat status opens runtime status. Every command can be copied from the Commands page."));
        });
        DrawCard("help-start-data", text.Get("战斗日志与诊断日志不是同一种文件", "Combat logs and diagnostic logs are different"), 200, () =>
        {
            DrawBullet(text.Get(
                "“打开 FFLogs 上传日志”打开原始 Network 日志目录并复制路径；它不会自动上传任何内容。",
                "Open FFLogs upload logs opens the raw Network log folder and copies its path. It does not upload anything automatically."));
            DrawBullet(text.Get(
                "“复制诊断日志”用于排查 DACT、解析器、Host 或扩展错误，不能代替 FFLogs 战斗日志。分享前仍应检查角色名、服务器名和路径。",
                "Copy diagnostic log is for DACT, parser, Host, or extension failures and cannot replace an FFLogs combat log. Inspect names, worlds, and paths before sharing."));
        });
    }

    private void DrawCombatMeter()
    {
        DrawPageHeader(
            text.Get("战斗统计", "Combat Meter"),
            text.Get("看懂显示方式、DPS 口径、暴直率和 FFLogs 预估。", "Understand display behavior, DPS metrics, hit rates, and FFLogs estimates."));
        DrawCard("help-meter-display", text.Get("窗口显示与交互", "Window display and interaction"), 270, () =>
        {
            DrawBullet(text.Get(
                "在“控制中心 → 战斗统计”开启“显示战斗统计”；找不到窗口时点击“定位到战斗统计”。也可右键 ACT 快捷按钮或输入 /actcompat meter。",
                "Under Control Center > Combat Meter, enable Show Combat Meter. If the window is lost, select Open Combat Meter window. You may also right-click the ACT quick button or use /actcompat meter."));
            DrawBullet(text.Get(
                "锁定窗口用于固定位置；只有同时开启“锁定时鼠标穿透”时，点击才会传给游戏。",
                "Lock window fixes its position. Clicks pass through to the game only when Click-through when locked is also enabled."));
            DrawBullet(text.Get(
                "“脱战自动隐藏”只改变显示状态，不会停止解析或删除战斗数据。",
                "Auto hide out of combat changes only visibility; it does not stop parsing or delete combat data."));
            DrawBullet(text.Get(
                "背景透明度会统一作用于窗口、标题、表头、玩家行和进度条的底色；设为 0 时背景完全透明，但文字和图标仍会显示。它不会改变计算结果。",
                "Background opacity applies consistently to the window, title, table header, player rows, and bar fills. At zero the background is fully transparent while text and icons remain visible. It does not change calculations."));
        });
        DrawCard("help-meter-setup", text.Get("第一次配置战斗统计", "Configuring Combat Meter for the first time"), 560, () =>
        {
            DrawBullet(text.Get(
                "先关闭“锁定窗口”，把统计窗拖到需要的位置，再重新锁定；需要让鼠标操作游戏时同时开启“锁定时鼠标穿透”。",
                "First disable Lock window, move the meter to the desired position, then lock it again. Also enable Click-through when locked when mouse input should go to the game."));
            DrawBullet(text.Get(
                "“排序 / 主要数据”只决定按 DPS 还是 HPS 排名；“DPS 口径”决定 DPS 列使用个人有效时长、整场时长、兼容字段或预估 rDPS。它们不会强制打开或关闭显示列。",
                "Sort / primary metric controls only DPS or HPS ranking. DPS metric selects personal active duration, full-encounter duration, the compatibility field, or estimated rDPS for the DPS column. Neither setting forces a display column on or off."));
            DrawBullet(text.Get(
                "在“显示列”中可分别开关 FFLogs、DPS、rDPS、HPS、暴击%、直击%、直暴%、伤害占比%、总伤害、最高技能伤害和死亡。最高技能使用紧凑宽度显示，悬停可查看完整技能名与数值；下一场有效战斗会重新记录。FFLogs 只有同时开启在线预估和 FFLogs 显示列时才出现。",
                "Visible columns independently controls FFLogs, DPS, HPS, CRIT %, DH %, CDH %, damage %, total damage, highest single-hit action, and deaths. The highest hit updates only when a larger hit appears and resets for the next encounter. FFLogs appears only when both online estimates and its display column are enabled."));
            DrawBullet(text.Get(
                "经典榜、透明横版和职能分栏是三个独立窗口，可以同时开启并分别保存位置、大小、锁定和槽位。三个编辑器都使用自动排布槽位：可新增、隐藏、删除和调整顺序，不需要拖入坐标，也不会互相重叠。透明横版没有窗口或玩家卡片背景，可横向滑动，并根据 DPS/HPS 模式从高到低排列；职能分栏保持 D/T 与治疗两段普通列表。",
                "Display presets include the existing default, Horizontal Transparent, and Healer vs D/T Split as read-only styles. In 8- and 24-player content the split preset ranks healers by HPS and excludes their damage from D/T ranking; 4-player content keeps the existing rules. Horizontal Transparent can be copied into a custom style whose slots, sizes, and metrics are editable on a 24x6 grid."));
            DrawBullet(text.Get(
                "“收起（只显示自己）”只隐藏其他队员的行，不会停止统计；想看全队时取消勾选。玩家 ID 遮盖也只影响界面，不会改写战斗日志。",
                "Collapsed (self only) hides other party rows without stopping collection. Disable it to see the party. Player-ID masking also affects only the UI and does not rewrite combat logs."));
            DrawBullet(text.Get(
                "战斗结束后悬浮窗会保留上一把结果，方便继续查看；下一场出现有效战斗数据后会用新数据从 0 重新计算。普通脱战、阶段切换、击杀前置目标和 ACT 自动分段不会提前清零。副本内只有确认全队团灭后的重新开怪才开始新一把；手动重置会立即清空，并阻止旧总数在后续刷新中弹回。",
                "Outside duties, the live meter clears after the party produces no relevant combat data for five seconds, so a party member pulling first no longer causes repeated resets while the local player is still out of combat. Duty statistics keep accumulating through ordinary combat exits, phase changes, defeated preliminary targets, and ACT segment boundaries. Only a confirmed party wipe makes the repull start from zero, including checkpoint restarts from an intermediate phase such as P2."));
            DrawBullet(text.Get(
                "历史记录以“一次副本进入”为一个可展开文件夹，里面每条子记录代表一次团灭前累计的完整战斗。团灭重开会在同一文件夹新增记录，但实时统计立即从 0 开始；退出副本后关闭该文件夹，下次进本才新建文件夹。同一把的转阶段 ACT 片段只在内部合并。",
                "History stores one duty entry as an expandable folder, with each child representing the complete totals accumulated before a wipe. A wipe adds the repull to the same folder while the live meter resets immediately. Leaving the duty closes the folder, and phase-split ACT fragments within one pull are merged internally."));
            DrawBullet(text.Get(
                "“重置当前战斗”需要二次确认，会结束并清空本把统计；同一底层战斗段的后续刷新不会把旧数据带回。已保存的历史和原始 Network 日志不会被删除。",
                "Reset current encounter requires confirmation and closes and clears the current pull. Later refreshes from the same underlying combat segment cannot restore old totals. Saved history and raw Network logs are not deleted."));
        });
        DrawCard("help-meter-metrics", text.Get("DPS / HPS 数值口径", "DPS / HPS metric definitions"), 390, () =>
        {
            DrawBullet(text.Get(
                "DPS：按玩家自己的有效动作时长计算，晚开怪、死亡或长时间停手时与整场口径差异会更明显。",
                "DPS uses each player's active duration, so late engagement, death, or long inactivity can differ noticeably from full-encounter metrics."));
            DrawBullet(text.Get(
                "EncDPS：用整场战斗时长计算，适合比较同一场战斗中所有成员。",
                "EncDPS uses the full encounter duration and is suitable for comparing party members in the same encounter."));
            DrawBullet(text.Get(
                "ExtDPS：保留 ACT 兼容字段，供旧悬浮窗或扩展使用；它不代表额外伤害。",
                "ExtDPS preserves the ACT compatibility field for legacy overlays or extensions; it does not mean extra damage."));
            DrawBullet(text.Get(
                "rDPS（预估）：根据本地事件估算移除别人给你的团辅、加回你给队友的贡献。它是实时近似值，不是 FFLogs 的权威实现。",
                "rDPS (estimated) removes estimated buffs received from others and adds estimated contribution you gave the party. It is a live approximation, not the authoritative FFLogs implementation."));
            DrawBullet(text.Get(
                "HPS 用本把从开怪到结束的完整经过时间计算，包括转阶段和目标不可选中的时间；伤害技能附带的自疗也会从原始效果中补充累计。没有新治疗时累计治疗不会归零，但分母仍随时间增加，所以 HPS 会逐步下降。切换排序不会改变原始累计。",
                "HPS uses the pull's full elapsed time, including transitions and untargetable periods. Self-healing embedded in damage actions is supplemented from raw effects. With no new healing, the healing total remains while HPS gradually falls as elapsed time grows. Sorting never changes raw totals."));
        });
        DrawCard("help-meter-hit-rates", text.Get("暴击率、直暴率为什么会变化", "Why CRIT and CDH rates change"), 218, () =>
        {
            DrawBullet(text.Get(
                "暴击率 = 暴击伤害命中数 ÷ 伤害命中数；直击率 = 直击伤害命中数 ÷ 伤害命中数；直暴率 = 同时暴击并直击的命中数 ÷ 伤害命中数。它们不是角色面板属性。",
                "CRIT rate is critical damage hits divided by damage hits; DH rate is direct damage hits divided by damage hits; CDH rate is simultaneous critical-direct hits divided by damage hits. They are not character-sheet attributes."));
            DrawBullet(text.Get(
                "战斗样本较少时，每次新命中都会让百分比明显跳动，这是正常统计变化。短暂零命中快照会沿用本场最近有效数字，不再在两次数字之间插入 --。",
                "With few samples, every new hit can move the percentage sharply. Brief zero-hit snapshots retain the latest valid value for the encounter instead of inserting -- between numbers."));
        });
        DrawCard("help-meter-fflogs", text.Get("DPS Parse 与 FFLogs", "DPS Parse and FFLogs"), 246, () =>
        {
            DrawBullet(text.Get(
                "DPS Parse 预估使用本场实际 DPS，对照同职业、同副本、同难度、同区域与同分区的缓存分布。鼠标悬停可查看 FFLogs 数据日期。",
                "Estimated DPS Parse compares this encounter's actual DPS with cached distributions for the same job, encounter, difficulty, region, and partition. Hover it to see the FFLogs data date."));
            DrawBullet(text.Get(
                "它不会读取你上传后的正式报告，也不会包含 FFLogs 服务器端所有过滤、分段和归属逻辑，因此可能与最终成绩不同。",
                "It does not read your uploaded report and cannot include every FFLogs server-side filter, phase rule, or attribution rule, so it may differ from the final result."));
            DrawBullet(text.Get(
                "显示 -- 表示当前没有可用分布、职业/副本无法映射，或本场尚无有效 DPS；这不是零分。",
                "-- means no usable distribution, unresolved job/encounter mapping, or no valid DPS yet. It does not mean a zero parse."));
            ImGui.Spacing();
            ImGui.TextWrapped(text.Get(
                "团灭重开和手动重置都会让下一把预估从 0 开始；历史记录和已经写入磁盘的原始日志不受影响。",
                "A repull after a wipe and a manual reset both start the next estimate from zero; history and raw logs already written to disk are unaffected."));
        });
        DrawCard("help-meter-fflogs-setup", text.Get("如何开启 FFLogs DPS Parse 预估", "Enabling the FFLogs DPS Parse estimate"), 362, () =>
        {
            DrawBullet(text.Get(
                "进入“控制中心 → 战斗统计 → FFLogs DPS Parse 预估”，开启“启用 FFLogs 在线估算”。预估功能与上传战斗报告是两件事。",
                "Go to Control Center > Combat Meter > FFLogs DPS Parse estimate and enable the online estimate. Estimation and uploading a combat report are separate features."));
            DrawBullet(text.Get(
                "首次使用时展开“如何创建 FFLogs API Client”，或点击“创建 / 管理 API Client”，登录 FFLogs 创建免费的 Client。没有自己的网址时可按页面提示填写 https://example.com。",
                "On first use, expand How to create an FFLogs API client or select Create / manage API client, then sign in to FFLogs and create a free client. If you have no URL, use https://example.com as instructed."));
            DrawBullet(text.Get(
                "把 Client ID 和 Client Secret 分别填入对应输入框，点击“测试并刷新”，确认状态不再报错。Client Secret 只保存在本机配置中，不要截图或发送给他人。",
                "Enter Client ID and Client Secret in their matching fields, select Test and refresh, and confirm the status no longer reports an error. The Client Secret is stored only in local configuration; do not screenshot or share it."));
            DrawBullet(text.Get(
                "进入已支持的副本并产生有效 DPS 后才会显示预估；没有缓存、职业或副本无法映射、凭据错误或尚无 DPS 时都会显示 --。",
                "An estimate appears only after valid DPS is recorded in a supported duty. Missing cache data, an unmapped job or encounter, invalid credentials, or no DPS yet will display --."));
        });
    }

    private void DrawMacroCommands()
    {
        DrawPageHeader(
            text.Get("宏指令", "Macro commands"),
            text.Get("点击“复制”即可放入剪贴板，再粘贴到游戏聊天框或宏中。", "Use Copy to place a command on the clipboard, then paste it into game chat or a macro."));
        DrawCard("help-commands-common", text.Get("常用入口", "Common entry points"), 405, () =>
        {
            DrawCommand("control-default", "/actcompat", text.Get("打开或关闭插件控制中心。", "Open or close the plugin control center."));
            DrawCommand("on", "/actcompat on", text.Get("始终打开插件控制中心。", "Always open the plugin control center."));
            DrawCommand("off", "/actcompat off", text.Get("关闭插件控制中心。", "Close the plugin control center."));
            DrawCommand("meter", "/actcompat meter", text.Get("打开战斗统计。", "Open Combat Meter."));
            DrawCommand("simple", "/actcompat simple on|off", text.Get(
                "开启或退出精简模式；即使其他界面已关闭，也可用 off 恢复。",
                "Enable or exit simplified mode; off remains available when every other UI is closed."));
            DrawCommand("history", "/actcompat history", text.Get("打开近期战斗。", "Open recent encounters."));
            DrawCommand("logs", "/actcompat logs", text.Get("打开已保存的日志文件列表。", "Open the saved log-file list."));
            DrawCommand("status", "/actcompat status", text.Get("打开解析器与 Host 运行状态。", "Open parser and Host runtime status."));
        });
        DrawCard("help-commands-overlays", text.Get("悬浮窗", "Overlays"), 150, () =>
        {
            DrawCommand("cactbot", "/actcompat cactbot", text.Get("打开当前选择的 Cactbot 悬浮窗。", "Open the selected Cactbot overlay."));
            DrawCommand("overlay", "/actcompat overlay", text.Get("打开当前选择的 HTML 悬浮窗；可在后面追加模板名。", "Open the selected HTML overlay; optionally append a template name."));
        });
        DrawCard("help-commands-maintenance", text.Get("维护与诊断", "Maintenance and diagnostics"), 380, () =>
        {
            DrawCommand("clear", "/actcompat clear", text.Get("立即清空当前战斗显示，不删除历史或原始日志。", "Immediately clear the current encounter display without deleting history or raw logs."));
            DrawCommand("install", "/actcompat install \"<DLL 或 ZIP 路径>\"", text.Get("静态预检并导入第三方 ACT 扩展；路径含空格时保留引号。", "Preflight and import a third-party ACT extension; keep quotes around paths containing spaces."));
            DrawCommand("factory-reset", "/actcompat factory-reset", text.Get("打开控制中心；恢复出厂设置仍需在界面中确认。", "Open the control center; factory reset still requires confirmation in the UI."));
            DrawCommand("host", "/actcompat host", text.Get("启动兼容 Host 并重启解析器，主要用于诊断。", "Start the compatibility Host and restart the parser, primarily for diagnostics."));
            DrawCommand("stop", "/actcompat stop", text.Get("停止解析器与共享兼容 Host，主要用于诊断。", "Stop the parser and shared compatibility Host, primarily for diagnostics."));
            DrawCommand("sample", "/actcompat sample", text.Get("载入本地样例战斗，仅用于开发与界面检查。", "Load a local sample encounter for development and UI checks only."));
        });
    }

    private void DrawOverlays()
    {
        DrawPageHeader(
            text.Get("悬浮窗", "Overlays"),
            text.Get("创建、编辑、操作和排查 Cactbot 与 HTML 悬浮窗。", "Create, edit, interact with, and troubleshoot Cactbot and HTML overlays."));
        DrawCard("help-overlay-cactbot", "Cactbot", 222, () =>
        {
            DrawBullet(text.Get(
                "“文字提醒”和“时间轴”可以同时打开；旧版“文字+时间轴组合窗”与这两个独立窗口互斥，打开一方会关闭另一方。",
                "Alerts and Timeline may run together. The legacy combined Alerts + Timeline window conflicts with both independent windows; opening one side closes the other."));
            DrawBullet(text.Get(
                "Cactbot 页面来自插件安装到本地的资源，不需要手动粘贴网址。没有提醒时先确认副本、语言、时间轴资源和 OverlayPlugin 事件源。",
                "Cactbot pages come from resources installed locally by the plugin; no URL is required. If alerts are missing, check the duty, language, timeline resources, and OverlayPlugin event source."));
        });
        DrawCard("help-overlay-html", text.Get("创建 HTML 悬浮窗", "Creating HTML overlays"), 270, () =>
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
                "同一网页可以创建多个悬浮窗，但名称必须能区分；“关闭”只关闭窗口，“删除悬浮窗”才会移除保存记录。",
                "The same page may be used by multiple overlays, but their names must be distinguishable. Close hides the window; Delete overlay removes the saved entry."));
        });
        DrawCard("help-overlay-edit", text.Get("编辑位置、大小和网页交互", "Editing position, size, and page input"), 292, () =>
        {
            DrawBullet(text.Get(
                "点击“编辑位置和大小”后可拖动窗口、拖右下角缩放，并临时关闭锁定和鼠标穿透。",
                "After selecting Edit position and size, drag the window or its lower-right corner; lock and click-through are temporarily disabled."));
            DrawBullet(text.Get(
                "点击“完成位置编辑”会恢复位置锁定和鼠标穿透，把鼠标交还游戏。若要点击网页按钮，请在悬浮窗设置里手动关闭鼠标穿透。",
                "Finish position editing restores position lock and click-through, returning mouse input to the game. To click page controls, manually disable click-through in overlay settings."));
            DrawBullet(text.Get(
                "页面缩放只改变网页内容比例；窗口大小决定可视区域。文字太小先调页面缩放，内容被截断再调窗口大小。",
                "Page zoom changes web-content scale; window size controls the visible area. Increase zoom for tiny text and resize the window for clipped content."));
        });
        DrawCard("help-overlay-empty", text.Get("有画面但没有数据", "Page renders but has no data"), 230, () =>
        {
            DrawBullet(text.Get(
                "查看连接状态：现代悬浮窗通常使用现代 OverlayPlugin 协议，旧页面可能使用 ACTWS。自动检测失败时点击“重新检测”。",
                "Check connection status. Modern overlays usually use the modern OverlayPlugin protocol; older pages may use ACTWS. Use Detect again if automatic detection fails."));
            DrawBullet(text.Get(
                "网页能打开不代表数据协议已经连接。先确认解析器与 OverlayPlugin 正在运行，再检查网址本身是否要求外网或额外配置。",
                "A rendered page does not guarantee a data connection. Confirm the parser and OverlayPlugin are running, then check whether the URL needs internet access or extra configuration."));
        });
    }

    private void DrawExtensions()
    {
        DrawPageHeader(
            text.Get("扩展与权限", "Extensions and permissions"),
            text.Get("认识内置扩展，理解版本、更新、重启和权限边界。", "Understand bundled extensions, versions, updates, restarts, and permission boundaries."));
        DrawCard("help-extension-bundled", text.Get("随 DACT 提供的兼容扩展", "Compatibility extensions included with DACT"), 350, () =>
        {
            DrawBullet(text.Get(
                "Triggernometry（触发器）：读取战斗日志并运行用户导入的触发器、时间条件和动作。",
                "Triggernometry runs user-imported triggers, timing conditions, and actions from combat logs."));
            DrawBullet(text.Get(
                "PostNamazu（鲶鱼精邮差）：为经过授权的触发器提供命令、头标、地标、队列等游戏动作。高风险动作仍受 DACT 权限控制。",
                "PostNamazu provides authorized triggers with commands, marks, waymarks, queues, and related game actions. High-risk actions remain permission-gated by DACT."));
            DrawBullet(text.Get(
                "ACT.FoxTTS：把触发器或 Cactbot 的文字提醒转换为语音；语音服务、音色和 Pro 选项在扩展配置中设置。",
                "ACT.FoxTTS converts Triggernometry or Cactbot text alerts to speech. Configure the speech service, voice, and Pro option from the extension."));
            DrawBullet(text.Get(
                "银山雀儿：默认关闭，启用后提供其原有通知与网络功能；抹茶使用单独的专属 Host。",
                "SilverDasher is disabled by default and retains its original notification/network features when enabled. Matcha runs in a separate dedicated Host."));
            DrawBullet(text.Get(
                "Triggernometry、鲶鱼精、FoxTTS 和银山雀儿共用“共享 ACT Host”；普通自行导入扩展使用“通用 Host”。",
                "Triggernometry, PostNamazu, FoxTTS, and SilverDasher share the Shared ACT Host. Ordinary imported extensions use the Generic Host."));
        });
        DrawCard("help-extension-install", text.Get("安装 DLL / ZIP", "Install DLL / ZIP"), 286, () =>
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
            DrawBullet(text.Get(
                "安装成功后如果页面提示重启，请按扩展所在的 Host 重启；不要为了一个 ACT 扩展先重启整个游戏。",
                "If an installed extension asks for a restart, restart the Host assigned to that extension. Do not restart the whole game first for one ACT extension."));
        });
        DrawCard("help-extension-enable", text.Get("启用扩展并打开它的配置", "Enabling and configuring an extension"), 286, () =>
        {
            DrawBullet(text.Get(
                "进入“控制中心 → 扩展”。先确认扩展显示“已安装”，再勾选扩展名称；显示“已启用”只代表它会被加载，不代表所有高风险权限都已开放。",
                "Go to Control Center > Extensions. First confirm that the extension is installed, then enable its checkbox. Enabled means it will be loaded; it does not mean every high-risk permission has been granted."));
            DrawBullet(text.Get(
                "扩展启用后点击同一行的“打开配置”。Triggernometry 的触发器导入与启停、FoxTTS 的音色和语音服务、鲶鱼精或提醒扩展的选项都在各自配置中完成。",
                "After enabling an extension, select Open configuration on the same row. Trigger import and enablement, FoxTTS voices and speech services, and PostNamazu or notification-extension options are configured in their own windows."));
            DrawBullet(text.Get(
                "启停兼容扩展或保存权限时 DACT 会自动重启对应 Host；如果磁盘 DLL 被外部工具替换，则需要在运行状态页手动重启对应 Host。",
                "DACT automatically restarts the assigned Host when a compatibility extension is enabled/disabled or permissions are saved. If an external tool replaces the DLL on disk, manually restart the assigned Host from Runtime Status."));
        });
        DrawCard("help-extension-permissions", text.Get("如何给扩展开权限", "How to grant extension permissions"), 520, () =>
        {
            DrawBullet(text.Get(
                "首次安装 DACT 或内置扩展更新后：先在“三方扩展声明”中阅读作者、来源、版本和哈希，点击“知悉并安装 / 更新”；安装完成后选择“同意并启用完整权限”，或选择安全模式后稍后逐项开放。",
                "After first installing DACT or updating bundled extensions, review author, source, version, and hash under Third-party extension notice, then select Acknowledge and install / update. When installation finishes, accept full permissions or keep safe mode and grant individual permissions later."));
            DrawBullet(text.Get(
                "给内置扩展补权限：打开“控制中心 → 扩展”，滚动到“ACT 插件权限边界”，展开鲶鱼精、Triggernometry、银山雀儿或抹茶，勾选需要的能力。更改会保存并自动重启对应 Host。",
                "To grant permissions to a bundled extension, open Control Center > Extensions, scroll to ACT plugin permission boundary, expand PostNamazu, Triggernometry, SilverDasher, or Matcha, and enable the required capabilities. Changes are saved and the assigned Host restarts automatically."));
            DrawBullet(text.Get(
                "给自行导入的普通 ACT 插件授权：在“用户安装的普通 ACT 插件”中找到显示“未授权”的项目，点击“查看并授权”，核对预检清单后点击“授权并启用”。它随后会在通用 Host 中运行。",
                "To authorize an imported generic ACT plugin, find the Not authorized item under User-installed generic ACT plugins, select Review and authorize, verify the preflight list, then select Authorize and enable. It will then run in Generic Host."));
            DrawBullet(text.Get(
                "按实际用途开放：联网更新或查询需要“网络请求”；写扩展缓存或导出到用户选择的文件需要“写入文件”（抹茶保存专属目录内的自身配置不需要）；鲶鱼精命令与标点需要“发送游戏指令”，部分原版功能还需要“访问游戏原生内存”或“调用 Windows 原生接口”；高级触发器脚本需要“运行高风险脚本”。",
                "Grant only what the feature needs: updates or online lookups need Network requests; extension cache writes or exports to a user-selected file need Write files (Matcha can save its own path-confined configuration without it); PostNamazu commands and markers need Send game commands, and some original features also need Access native game memory or Call native Windows APIs; advanced trigger scripts need Run high-risk scripts."));
            DrawBullet(text.Get(
                "权限勾选后功能仍不可用时，先确认扩展本身也已启用，再用 /actcompat status 检查对应 Host。权限和“启用扩展”是两个独立条件。",
                "If a feature still fails after permission is granted, confirm the extension itself is also enabled, then use /actcompat status to inspect the assigned Host. Permission and extension enablement are separate requirements."));
        });
        DrawCard("help-extension-update", text.Get("版本、更新与“重启 ACT”", "Versions, updates, and 'restart ACT'"), 382, () =>
        {
            DrawBullet(text.Get(
                "扩展页显示入口 DLL 的实际 FileVersion，不再只显示安装清单。实际 DLL 与清单不一致时，把鼠标移到版本号上可查看两者。",
                "The Extensions page shows the entry DLL's actual FileVersion instead of only the install manifest. Hover the version when the DLL and manifest differ to see both."));
            DrawBullet(text.Get(
                "DACT 自带更新检查会下载作者发布的候选并校验版本与 SHA-256；外部工具箱可能只替换 DLL，不会同步 DACT 的安装清单。",
                "DACT's updater downloads author-published candidates and verifies version and SHA-256. External toolboxes may replace only the DLL without updating DACT's install manifest."));
            DrawBullet(text.Get(
                "Triggernometry 工具箱更新鲶鱼精后，输入 /actcompat status，点击“重启共享 Host”。这就是 DACT 对应的“重启 ACT”，通常不需要退出游戏。",
                "After Triggernometry Toolbox updates PostNamazu, use /actcompat status and select Restart shared Host. This is DACT's equivalent of 'restart ACT' and normally does not require exiting the game."));
            DrawBullet(text.Get(
                "抹茶更新后重启“抹茶 Host”；普通自行导入扩展更新后重启“通用 Host”。只有对应 Host 无法停止或 DACT 自身更新要求重载时，才考虑完整重启游戏。",
                "Restart Matcha Host after a Matcha update and Generic Host after updating an ordinary imported extension. Restart the whole game only if the assigned Host cannot stop or DACT itself requires a reload."));
        });
        DrawCard("help-extension-safety", text.Get("权限与安全要求", "Permissions and security requirements"), 286, () =>
        {
            ImGui.TextColored(Warning, text.Get("只安装你信任并能确认来源的 DLL、ZIP 和网页。", "Install only DLLs, ZIPs, and pages whose source you trust and can verify."));
            ImGui.TextWrapped(text.Get(
                "DLL 仍是桌面代码。权限清单约束兼容接口，但无法拦截 DLL 直接调用 Windows API。授权前请自行判断来源和风险；分享日志前请检查其中的角色名、服务器名和本地路径。",
                "A DLL remains desktop code. The permission list governs compatibility APIs but cannot intercept direct Windows API calls. Judge the source and risk before authorization, and inspect character names, server names, and local paths before sharing logs."));
            ImGui.Spacing();
            ImGui.TextWrapped(text.Get(
                "关闭某项权限后，对应网络、文件、命令、原生内存或脚本功能可能失效，这是安全边界生效，不一定是插件故障。权限保存后 DACT 会自动重启相关 Host。",
                "Disabling a permission can intentionally disable network, file, command, native-memory, or scripting features. Saving permissions automatically restarts the related Host."));
            ImGui.TextWrapped(text.Get(
                "“同意完整权限”表示允许该扩展声明的全部能力，不等于 DACT 已证明第三方代码绝对安全。来源不明时应保持安全模式，只按需要逐项开放。",
                "Accept full permissions allows every capability declared by that extension; it does not mean DACT has proven the third-party code absolutely safe. Keep safe mode for unknown sources and grant only what is needed."));
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
            text.Get("先判断是解析器、悬浮窗还是扩展，再重启对应组件。", "Decide whether the parser, overlay, or extension failed before restarting the relevant component."));
        DrawCard("help-troubleshooting-layers", text.Get("先看运行状态，不要先删配置", "Check runtime status before deleting configuration"), 244, () =>
        {
            DrawBullet(text.Get(
                "解析器负责读取战斗日志；共享 Host 负责 Triggernometry、鲶鱼精、FoxTTS 和银山雀儿；抹茶与普通扩展各有自己的 Host。",
                "The parser reads combat logs. Shared Host runs Triggernometry, PostNamazu, FoxTTS, and SilverDasher. Matcha and ordinary imported extensions use their own Hosts."));
            DrawBullet(text.Get(
                "输入 /actcompat status，找到最先不是“运行中/已连接”的一层。下游一起报错时先修上游，例如解析器未运行时不要先重装悬浮窗。",
                "Use /actcompat status and find the first layer that is not Running/Connected. When downstream layers fail together, fix the upstream layer first; for example, do not reinstall overlays while the parser is stopped."));
            DrawBullet(text.Get(
                "恢复出厂设置会移动配置、日志、历史和扩展目录，属于最后手段；普通故障先使用重启按钮和诊断日志。",
                "Factory reset moves configuration, logs, history, and extensions and is a last resort. Use restart controls and diagnostics first."));
        });
        DrawCard("help-troubleshooting-restart", text.Get("应该重启哪一个", "What should be restarted"), 276, () =>
        {
            DrawBullet(text.Get(
                "鲶鱼精、Triggernometry、FoxTTS、银山雀儿：重启共享 Host。",
                "PostNamazu, Triggernometry, FoxTTS, or SilverDasher: restart Shared Host."));
            DrawBullet(text.Get(
                "抹茶：重启抹茶 Host。自行导入的普通 ACT 插件：重启通用 Host。",
                "Matcha: restart Matcha Host. Ordinary imported ACT plugins: restart Generic Host."));
            DrawBullet(text.Get(
                "战斗统计没有任何日志、系统 FFXIV_ACT_Plugin/OverlayPlugin 状态变化：重启解析器。",
                "No combat logs at all, or changed system FFXIV_ACT_Plugin/OverlayPlugin state: restart the parser."));
            DrawBullet(text.Get(
                "DACT 本体更新或 Host 无法在状态页停止：先尝试 Dalamud 重载；仍失败再完整退出游戏和启动器。",
                "DACT itself updated, or a Host cannot stop from Runtime Status: try a Dalamud reload first, then fully exit the game and launcher if needed."));
        });
        DrawCard("help-troubleshooting-plugin-unavailable", text.Get("插件打不开、命令没反应或一直初始化", "Plugin does not open, commands do nothing, or initialization never finishes"), 354, () =>
        {
            DrawBullet(text.Get(
                "先输入 /actcompat。若聊天框提示命令不存在，或连控制中心都无法打开，请在卫月插件安装器中确认 Dalamud ACT Compat 已安装、已启用且没有加载错误；此时插件内帮助和重启按钮本身也无法工作。",
                "First use /actcompat. If chat reports an unknown command or the control center never opens, verify in the Dalamud plugin installer that Dalamud ACT Compat is installed, enabled, and has no load error. In this state, in-plugin help and restart controls cannot work either."));
            DrawBullet(text.Get(
                "若控制中心能打开，在“概览”确认“启用解析”和“自动启动解析器”已勾选。解析器长时间停在“已停止”“初始化中”或“错误”时，到“设置”点击“重启解析器”。",
                "If the control center opens, confirm Enable parsing and Auto start parser on Overview. If the parser remains Stopped, Initializing, or Error, go to Settings and select Restart parser."));
            DrawBullet(text.Get(
                "再输入 /actcompat status，从上到下寻找第一个不是“运行中 / 已连接”的组件。先修第一个失败的上游，不要在解析器未运行时反复重装悬浮窗或扩展。",
                "Then use /actcompat status and find the first component from the top that is not Running / Connected. Fix that first upstream failure instead of repeatedly reinstalling overlays or extensions while the parser is stopped."));
            DrawBullet(text.Get(
                "仍无法启动时，在“设置”点击“复制诊断日志”，反馈 DACT 版本、刚才执行的操作、页面显示的状态与错误原文。只说“无法使用”不足以判断失败层。",
                "If startup still fails, select Copy diagnostic log under Settings and report the DACT version, the action performed, visible states, and exact error text. Saying only that it does not work is not enough to identify the failed layer."));
        });
        DrawCard("help-troubleshooting-no-meter", text.Get("没有战斗统计、没有队员或窗口不见了", "No combat data, missing party members, or a lost meter window"), 430, () =>
        {
            DrawBullet(text.Get(
                "先输入 /actcompat meter；也可在“控制中心 → 战斗统计”开启“显示战斗统计”并点击“定位到战斗统计”。排查期间先关闭“脱战自动隐藏”，避免把隐藏误认为没有窗口。",
                "First use /actcompat meter. You may also enable Show Combat Meter under Control Center > Combat Meter and select Open Combat Meter window. Disable Auto hide out of combat while testing so a hidden window is not mistaken for a missing one."));
            DrawBullet(text.Get(
                "确认概览和 /actcompat status 中的解析器为“运行中”，然后攻击木人或副本敌人并实际造成几次有效伤害。仅打开窗口、进本、选中目标或站在战斗区域不会产生统计。",
                "Confirm that the parser is Running on Overview and in /actcompat status, then attack a striking dummy or duty enemy and deal several valid hits. Opening the window, entering a duty, targeting an enemy, or merely standing in combat does not create statistics."));
            DrawBullet(text.Get(
                "“收起（只显示自己）”开启时只显示自己；取消后显示当前小队，联盟副本最多显示 24 人并按 A/B/C 分组。宠物和普通 NPC 不作为独立玩家行，离队与补位成员也按当前队伍容量处理。",
                "Collapsed (self only) shows only you; disable it to show the current party. Alliance duties can show up to 24 players grouped as A/B/C. Pets and ordinary NPCs are not separate player rows, and replacements are kept within the current roster capacity."));
            DrawBullet(text.Get(
                "解析器已运行且已造成伤害，但数次刷新后仍完全没有行：先重启解析器并重新打一个全新的木人或副本样本。不要用重置当前战斗或恢复出厂设置代替这一步。",
                "If the parser is running and damage was dealt but no rows appear after several refreshes, restart the parser and create a new striking-dummy or duty sample. Do not substitute Reset current encounter or Factory reset for this check."));
            DrawBullet(text.Get(
                "仍无数据时，同时保留“打开 FFLogs 上传日志”目录中的当场 Network 日志，并复制诊断日志。Network 日志证明原始战斗事件，诊断日志说明 DACT 与解析器状态，两者用途不同。",
                "If data is still missing, keep the matching Network log from Open FFLogs upload logs and copy the diagnostic log. The Network log shows raw combat events; diagnostics show DACT and parser state. They serve different purposes."));
        });
        DrawCard("help-troubleshooting-extension-failed", text.Get("只有某个扩展、TTS、触发器或标点不能用", "Only one extension, TTS, trigger, or marker feature fails"), 394, () =>
        {
            DrawBullet(text.Get(
                "到“控制中心 → 扩展”同时确认三件事：扩展已安装、扩展复选框已启用、所需权限已勾选。显示“未授权”的普通插件必须点击“查看并授权”。",
                "Under Control Center > Extensions, confirm all three conditions: the extension is installed, its checkbox is enabled, and required permissions are granted. A generic plugin shown as Not authorized must be reviewed and authorized."));
            DrawBullet(text.Get(
                "然后点击该扩展的“打开配置”，确认功能本身已在扩展内部启用。例如触发器需要已经导入并启用，FoxTTS 需要可用语音服务、音色和输出设备。",
                "Then open that extension's configuration and confirm the feature is enabled inside the extension itself. For example, triggers must be imported and enabled, while FoxTTS needs a working speech service, voice, and output device."));
            DrawBullet(text.Get(
                "用 /actcompat status 检查它所在的 Host：Triggernometry、鲶鱼精、FoxTTS、银山雀儿看共享 Host；抹茶看抹茶 Host；普通导入插件看通用 Host。只重启对应 Host。",
                "Use /actcompat status to inspect its assigned Host: Shared Host for Triggernometry, PostNamazu, FoxTTS, and SilverDasher; Matcha Host for Matcha; Generic Host for imported plugins. Restart only that Host."));
            DrawBullet(text.Get(
                "若共享 Host 整体运行而仅一个动作失败，优先看权限和该扩展配置；若整个 Host 报错，复制状态页或扩展失败窗口中的完整诊断，不要只截最后一行。",
                "If Shared Host is running and only one action fails, check permissions and that extension's settings first. If the entire Host errors, copy the complete diagnostic from Runtime Status or the extension-failure window instead of capturing only its last line."));
        });
        DrawCard("help-troubleshooting-update-old", text.Get("已经更新但仍像旧版本", "Updated, but behavior still looks old"), 238, () =>
        {
            DrawBullet(text.Get(
                "先在扩展页看实际 DLL 版本；实际版本与清单版本不同不一定是错误，外部工具箱可能只替换了 DLL。",
                "First inspect the actual DLL version on Extensions. A difference between DLL and manifest is not necessarily an error; an external toolbox may have replaced only the DLL."));
            DrawBullet(text.Get(
                "磁盘版本已更新但行为没变，说明运行中的 Host 仍持有旧程序集。按扩展归属重启共享、抹茶或通用 Host；DACT 本体更新则执行 Dalamud 重载，必要时完整退出游戏和启动器。",
                "If the DLL on disk is new but behavior is unchanged, the running Host still holds the old assembly. Restart Shared, Matcha, or Generic Host as assigned. For a DACT update, reload Dalamud and fully exit the game and launcher only if needed."));
        });
        DrawCard("help-troubleshooting-notifications", text.Get("通知没有出现", "Notifications did not appear"), 160, () =>
        {
            ImGui.TextWrapped(text.Get(
                "抹茶和银山雀儿在游戏位于前台时使用游戏内卫月通知；切到其他应用时使用 Windows 通知中心。Windows 投递失败时会回退到游戏内通知。请同时检查 Windows 专注助手、游戏内通知设置和对应 Host 日志。",
                "Matcha and SilverDasher use Dalamud notifications while the game is foreground. When another app is foreground, they use Windows Notification Center and fall back to Dalamud if Windows delivery fails. Check Windows Focus Assist, in-game notification settings, and the assigned Host log."));
        });
        DrawCard("help-troubleshooting-report", text.Get("反馈问题时请提供什么", "What to include in a problem report"), 286, () =>
        {
            DrawBullet(text.Get(
                "必须说明：DACT 版本、问题发生时间、当时在做什么、预期结果、实际现象，以及能否稳定复现。",
                "Always include the DACT version, occurrence time, what you were doing, expected result, actual behavior, and whether it reproduces consistently."));
            DrawBullet(text.Get(
                "插件、解析器、Host、悬浮窗或扩展启动问题：提供“复制诊断日志”的完整内容和 /actcompat status 中最先失败的组件。",
                "For DACT, parser, Host, overlay, or extension startup problems, provide the full Copy diagnostic log output and the first failed component in /actcompat status."));
            DrawBullet(text.Get(
                "战斗统计缺失、数值明显异常或 FFLogs 对不上：除诊断日志外，保留对应时间的原始 Network 日志。发送前自行检查角色名、服务器名和本地路径。",
                "For missing combat data, clearly incorrect values, or FFLogs discrepancies, keep the matching raw Network log in addition to diagnostics. Review character names, worlds, and local paths before sharing."));
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

    private void DrawFrequentlyAskedQuestions()
    {
        DrawPageHeader(
            text.Get("常见问题", "Frequently asked questions"),
            text.Get("对照用户最常遇到的更新、统计、隐私和操作问题。", "Answers to common update, statistics, privacy, and interaction questions."));
        DrawCard("help-faq-no-meter", text.Get("为什么打开了插件却没有战斗统计？", "Why is there no combat data after opening the plugin?"), 214, () =>
        {
            ImGui.TextWrapped(text.Get(
                "打开插件只会显示界面，不会生成战斗数据。请确认解析器为“运行中”，用 /actcompat meter 打开统计窗，然后对木人或副本敌人实际造成有效伤害。窗口仍为空时关闭脱战自动隐藏、重启解析器并重新产生一场新样本；继续失败请同时保留 Network 日志和诊断日志。",
                "Opening the plugin only shows its UI; it does not generate combat data. Confirm the parser is Running, use /actcompat meter, and deal valid damage to a striking dummy or duty enemy. If the meter stays empty, disable auto hide, restart the parser, and create a new sample. If it still fails, keep both the Network log and diagnostic log."));
        });
        DrawCard("help-faq-permissions", text.Get("权限在哪里开？需要全部打开吗？", "Where are permissions granted, and must all be enabled?"), 238, () =>
        {
            ImGui.TextWrapped(text.Get(
                "进入“控制中心 → 扩展”，在页面下方展开“ACT 插件权限边界”中的对应扩展并勾选所需能力；显示“未授权”的普通插件使用“查看并授权”。不要求全部打开，应根据实际功能逐项授权。权限保存后对应 Host 自动重启；扩展本身还必须处于“已启用”。",
                "Go to Control Center > Extensions and expand the target extension under ACT plugin permission boundary, then enable the capabilities it needs. For a generic plugin shown as Not authorized, use Review and authorize. You do not need to enable everything; grant only what the feature requires. The assigned Host restarts after saving, and the extension itself must also be Enabled."));
        });
        DrawCard("help-faq-restart-act", text.Get("提示“重启 ACT”，需要重启游戏吗？", "Does 'restart ACT' mean restarting the game?"), 190, () =>
        {
            ImGui.TextWrapped(text.Get(
                "通常不需要。在 DACT 中，传统 ACT 扩展运行在独立 Host。鲶鱼精等共享扩展更新后，输入 /actcompat status 并点击“重启共享 Host”即可。只有 Host 无法退出或 DACT 本体无法重载时才完整重启游戏。",
                "Usually not. Traditional ACT extensions run in independent Hosts. After updating a shared extension such as PostNamazu, use /actcompat status and select Restart shared Host. Restart the whole game only if the Host cannot exit or DACT itself cannot reload."));
        });
        DrawCard("help-faq-version", text.Get("为什么扩展版本和安装清单不同？", "Why does the extension version differ from the manifest?"), 176, () =>
        {
            ImGui.TextWrapped(text.Get(
                "外部工具箱可能只替换 DLL。扩展页优先显示 DLL 的实际版本，悬停版本号会显示清单版本。清单继续用于来源、授权和哈希记录，不会被静默改写。",
                "An external toolbox may replace only the DLL. Extensions shows the DLL's actual version and reveals the manifest version on hover. The manifest remains unchanged for source, consent, and hash records."));
        });
        DrawCard("help-faq-fflogs", text.Get("为什么本地 DPS、rDPS、Parse 和 FFLogs 不完全一样？", "Why do local DPS, rDPS, Parse, and FFLogs differ?"), 226, () =>
        {
            ImGui.TextWrapped(text.Get(
                "它们使用不同口径。本地 DPS/EncDPS 的分母不同；rDPS 是基于本地事件的实时团队贡献估算；DPS Parse 是把本场实际 DPS 代入缓存分布；上传后的 FFLogs 还会执行服务器端归属、阶段、过滤和版本规则。小幅差异正常，明显差异请保留原始 Network 日志排查。",
                "They use different conventions. Local DPS and EncDPS use different durations; rDPS is a live local contribution estimate; DPS Parse maps actual DPS into a cached distribution; uploaded FFLogs additionally applies server-side attribution, phase, filtering, and version rules. Small differences are expected; keep the raw Network log when differences are large."));
        });
        DrawCard("help-faq-dashes", text.Get("--、数字跳动和暴直率分别表示什么？", "What do -- and changing hit rates mean?"), 210, () =>
        {
            ImGui.TextWrapped(text.Get(
                "-- 表示当前还没有有效值，不等于 0。战斗中新命中持续加入样本，DPS、百分比、暴击率、直击率和直暴率会自然变化。暴击率统计暴击命中，直击率统计直击命中，直暴率统计同时暴击并直击的命中；它们都不是角色面板概率。",
                "-- means no valid value yet, not zero. New hits continuously change DPS, percentages, CRIT, DH, and CDH rates. CRIT counts critical hits, DH counts direct hits, and CDH counts hits that were both critical and direct. None is a character-sheet probability."));
        });
        DrawCard("help-faq-upload", text.Get("DACT 会自动上传战斗或个人信息吗？", "Does DACT automatically upload combat or personal data?"), 170, () =>
        {
            ImGui.TextWrapped(text.Get(
                "“打开 FFLogs 上传日志”只打开本地目录并复制路径，不会自动上传。第三方扩展可能具有网络权限，应在扩展权限和来源声明中单独判断。分享任何日志前请自行检查角色名、服务器名和本地路径。",
                "Open FFLogs upload logs only opens a local folder and copies its path; it does not upload automatically. Third-party extensions may have network permission and must be judged separately from their source and permission notice. Inspect names, worlds, and local paths before sharing logs."));
        });
        DrawCard("help-faq-overlay-input", text.Get("为什么点不到悬浮窗，或鼠标不能操作游戏？", "Why can I not click the overlay, or why does it capture game input?"), 192, () =>
        {
            ImGui.TextWrapped(text.Get(
                "鼠标穿透开启时，点击会传给游戏，所以网页不能操作；关闭穿透后网页会接收点击和滚轮。“完成位置编辑”会恢复锁定与穿透，这是正常结束状态。要临时操作网页，请单独关闭该悬浮窗的鼠标穿透。",
                "With click-through enabled, clicks go to the game and the web page cannot be controlled. Disabling it sends clicks and wheel input to the page. Finish position editing restores lock and click-through by design; temporarily disable click-through to operate the page."));
        });
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

    private IReadOnlyList<HelpSearchEntry> CreateSearchEntries()
    {
        HelpSearchEntry Entry(
            HelpPage page,
            string sectionId,
            string chineseTitle,
            string englishTitle,
            string chineseSummary,
            string englishSummary,
            string keywords) => new(
                page,
                sectionId,
                text.Get(chineseTitle, englishTitle),
                text.Get(chineseSummary, englishSummary),
                $"{chineseTitle} {englishTitle} {chineseSummary} {englishSummary} {keywords}");

        return
        [
            Entry(HelpPage.UsageNotice, "help-notice-rules", "使用前请确认", "Before use", "第三方工具规则与使用边界。", "Rules and boundaries for third-party tools.", "用户协议 PVP 跳脸 骚扰 safety rules"),
            Entry(HelpPage.GettingStarted, "help-start-parser", "第一次打开插件", "First launch", "确认扩展来源并等待主页显示运行中。", "Review extension sources and wait for Home to show Running.", "首次安装 初始化 parser 解析器 permissions 权限"),
            Entry(HelpPage.GettingStarted, "help-start-control-center", "控制中心五个页面", "Five control-center pages", "说明概览、战斗统计、悬浮窗、扩展和设置分别怎么用。", "How to use Overview, Combat Meter, Overlays, Extensions, and Settings.", "主页 用户手册 功能说明 overview settings diagnostics"),
            Entry(HelpPage.GettingStarted, "help-start-first-fight", "第一场战斗", "First encounter", "确认战斗统计出现自己和当前小队。", "Confirm Combat Meter shows you and the current party.", "没有数据 木人 副本 party meter history"),
            Entry(HelpPage.GettingStarted, "help-start-shortcuts", "常用入口", "Common entry points", "快捷按钮、控制中心和运行状态入口。", "Quick button, control center, and runtime status shortcuts.", "/actcompat on status 快捷按钮"),
            Entry(HelpPage.GettingStarted, "help-start-data", "战斗日志与诊断日志", "Combat and diagnostic logs", "区分 FFLogs 上传日志与排错日志。", "Distinguish FFLogs upload logs from troubleshooting logs.", "Network 原始日志 upload privacy 隐私"),
            Entry(HelpPage.MacroCommands, "help-commands-common", "常用宏指令", "Common commands", "打开控制中心、统计、历史、日志和状态。", "Open the control center, meter, history, logs, and status.", "/actcompat /actcompat on meter history logs status copy 复制"),
            Entry(HelpPage.MacroCommands, "help-commands-overlays", "悬浮窗命令", "Overlay commands", "打开 Cactbot 或指定 HTML 模板。", "Open Cactbot or a named HTML template.", "cactbot overlay template 模板"),
            Entry(HelpPage.MacroCommands, "help-commands-maintenance", "维护与诊断命令", "Maintenance commands", "清空、安装、Host、停止和恢复出厂设置。", "Clear, install, Host, stop, and factory reset commands.", "clear install host stop factory-reset sample DLL ZIP"),
            Entry(HelpPage.CombatMeter, "help-meter-display", "窗口显示与交互", "Meter display", "锁定、穿透、自动隐藏和界面缩放。", "Lock, click-through, auto hide, and display scaling.", "锁定 鼠标穿透 compact opacity font"),
            Entry(HelpPage.CombatMeter, "help-meter-setup", "第一次配置战斗统计", "Configure Combat Meter", "设置位置、排序、口径、显示列、每把文件夹、收起、匿名和重置。", "Configure position, sorting, metrics, visible columns, pull folders, collapsed mode, anonymization, and reset.", "定位 只显示自己 全队 玩家 ID 清空 当前战斗 FFLogs DPS HPS 暴击 直击 直暴 占比 死亡 文件夹 一把 分段 记录"),
            Entry(HelpPage.CombatMeter, "help-meter-metrics", "DPS 与 HPS 口径", "DPS and HPS metrics", "解释 DPS、EncDPS、ExtDPS、rDPS 和 HPS。", "Definitions for DPS, EncDPS, ExtDPS, rDPS, and HPS.", "团队贡献 预估 active duration damage healing"),
            Entry(HelpPage.CombatMeter, "help-meter-hit-rates", "暴击率、直击率与直暴率", "CRIT, DH, and CDH rates", "解释百分比跳动、有效样本与 --。", "Explains changing percentages, valid samples, and --.", "暴击 直击 直暴 crit direct hit DH CDH 数字跳动 闪"),
            Entry(HelpPage.CombatMeter, "help-meter-fflogs", "DPS Parse 与 FFLogs", "DPS Parse and FFLogs", "解释本地预估、缓存日期和正式成绩差异。", "Explains local estimates, cache dates, and final-report differences.", "排名 percentile partition 分区 curve 曲线 上传"),
            Entry(HelpPage.CombatMeter, "help-meter-fflogs-setup", "开启 FFLogs DPS Parse 预估", "Enable FFLogs DPS Parse estimate", "创建 API Client、填写凭据并测试刷新。", "Create an API client, enter credentials, and test refresh.", "Client ID Client Secret example.com 在线估算 凭据 --"),
            Entry(HelpPage.Overlays, "help-overlay-cactbot", "Cactbot 悬浮窗", "Cactbot overlays", "文字提醒、时间轴与旧组合窗的关系。", "Alerts, Timeline, and the legacy combined window.", "raidboss timeline alerts 文字提醒 时间轴"),
            Entry(HelpPage.Overlays, "help-overlay-html", "创建 HTML 悬浮窗", "Create HTML overlays", "从模板或可信网址创建并保存独立设置。", "Create from templates or trusted URLs with independent settings.", "http https file URL websocket ACTWS"),
            Entry(HelpPage.Overlays, "help-overlay-edit", "编辑位置与网页交互", "Edit and interact", "拖动、缩放、完成编辑、锁定与鼠标穿透。", "Move, resize, finish editing, lock, and click-through behavior.", "完成位置编辑 操作网页 滚轮 页面缩放"),
            Entry(HelpPage.Overlays, "help-overlay-empty", "悬浮窗没有数据", "Overlay has no data", "检查现代协议、ACTWS、解析器和网址要求。", "Check modern protocol, ACTWS, parser, and URL requirements.", "空白 重新检测 connected 连接"),
            Entry(HelpPage.Extensions, "help-extension-bundled", "内置兼容扩展", "Bundled compatibility extensions", "Triggernometry、鲶鱼精、FoxTTS、银山雀儿与抹茶分别做什么。", "What Triggernometry, PostNamazu, FoxTTS, SilverDasher, and Matcha do.", "触发器 PostNamazu TTS Matcha SilverDasher 共享 Host"),
            Entry(HelpPage.Extensions, "help-extension-install", "安装 DLL 或 ZIP", "Install DLL or ZIP", "静态预检、授权、失败诊断和 Host 分配。", "Static preflight, authorization, failure diagnostics, and Host assignment.", "第三方插件 import 导入 generic 通用"),
            Entry(HelpPage.Extensions, "help-extension-enable", "启用扩展和打开配置", "Enable and configure extensions", "说明已安装、已启用、扩展配置和 Host 重启。", "Explains installation, enablement, extension settings, and Host restarts.", "复选框 打开配置 Triggernometry FoxTTS 语音 音色"),
            Entry(HelpPage.Extensions, "help-extension-permissions", "如何给扩展开权限", "Grant extension permissions", "首次完整授权、内置扩展逐项授权和普通插件授权流程。", "Full first-run consent, per-capability bundled grants, and generic-plugin authorization.", "同意完整权限 安全模式 ACT 插件权限边界 查看并授权 网络 文件 游戏指令 原生内存 高风险脚本"),
            Entry(HelpPage.Extensions, "help-extension-update", "版本、更新与重启 ACT", "Versions, updates, and restart ACT", "实际 DLL 版本、清单版本和更新后重启对应 Host。", "Actual DLL version, manifest version, and restarting the assigned Host after updates.", "1.3.6.7 工具箱 toolbox FileVersion SHA-256 /actcompat status 重启共享 Host"),
            Entry(HelpPage.Extensions, "help-extension-safety", "扩展权限与安全", "Extension permissions and safety", "理解网络、文件、命令、内存和脚本权限。", "Understand network, file, command, memory, and scripting permissions.", "授权 Windows API 来源 风险"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-layers", "按层排错", "Layered troubleshooting", "从解析器和 Host 中找到最先失败的一层。", "Find the first failing layer among the parser and Hosts.", "运行状态 初始化 删除配置 factory reset"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-restart", "应该重启哪一个", "What to restart", "共享、抹茶、通用 Host 与解析器的对应关系。", "Mapping between Shared, Matcha, Generic Hosts, and the parser.", "重启游戏 reload ACT PostNamazu"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-plugin-unavailable", "插件打不开或一直初始化", "Plugin unavailable or stuck initializing", "从命令、卫月加载状态、解析器和诊断日志逐步检查。", "Check commands, Dalamud load state, parser state, and diagnostics in order.", "无法使用 没反应 unknown command 命令不存在 加载错误 重启解析器"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-no-meter", "没有战斗统计", "No combat data", "找回窗口并确认解析器、有效伤害、小队显示和所需日志。", "Find the meter and verify parser state, valid damage, party display, and required logs.", "没数据 没有队员 窗口不见 木人 auto hide 脱战自动隐藏 Network"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-extension-failed", "某个扩展或 TTS 不能用", "One extension or TTS fails", "检查安装、启用、权限、扩展内部配置和对应 Host。", "Check installation, enablement, permissions, extension settings, and assigned Host.", "触发器 标点 鲶鱼 TTS 语音 未授权 配置"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-update-old", "更新后仍是旧行为", "Old behavior after update", "核对实际 DLL 版本并重启仍持有旧程序集的 Host。", "Check the actual DLL version and restart the Host that still holds the old assembly.", "版本号 更新没生效 缓存 old assembly 工具箱"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-notifications", "通知没有出现", "Missing notifications", "检查游戏前台、Windows 通知和专注助手。", "Check game focus, Windows notifications, and Focus Assist.", "抹茶 银山雀儿 toast notification"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-report", "反馈问题需要的材料", "Problem report checklist", "版本、时间、复现步骤、运行状态、诊断日志和 Network 日志。", "Version, time, reproduction steps, runtime state, diagnostics, and Network logs.", "怎么反馈 截图 日志 error report 角色名 服务器名"),
            Entry(HelpPage.FrequentlyAskedQuestions, "help-faq-no-meter", "打开插件却没有战斗统计", "No data after opening the plugin", "说明打开界面不等于已经产生战斗数据。", "Opening the UI does not itself produce combat data.", "空白 没人 没统计 有效伤害"),
            Entry(HelpPage.FrequentlyAskedQuestions, "help-faq-permissions", "权限在哪里开", "Where to grant permissions", "在扩展页逐项授权，并同时启用扩展。", "Grant capabilities on Extensions and also enable the extension.", "怎么开权限 全部打开 安全模式 未授权"),
            Entry(HelpPage.FrequentlyAskedQuestions, "help-faq-restart-act", "重启 ACT 是否等于重启游戏", "Does restart ACT mean restart game", "DACT 通常只需重启对应共享 Host。", "DACT normally needs only the assigned Shared Host restarted.", "鲶鱼精 更新 status"),
            Entry(HelpPage.FrequentlyAskedQuestions, "help-faq-version", "版本与清单不同", "Version differs from manifest", "外部工具替换 DLL 后的显示与安全记录。", "Display and safety records after an external tool replaces a DLL.", "实际版本 悬停 mismatch"),
            Entry(HelpPage.FrequentlyAskedQuestions, "help-faq-fflogs", "本地数值与 FFLogs 不同", "Local values differ from FFLogs", "解释 DPS、rDPS、Parse 与正式报告口径。", "Explains DPS, rDPS, Parse, and final-report conventions.", "低 离谱 奶妈 healer DoT"),
            Entry(HelpPage.FrequentlyAskedQuestions, "help-faq-dashes", "-- 与数字跳动", "Dashes and changing numbers", "没有有效值、样本变化、暴击与直暴定义。", "No valid value, changing samples, CRIT, and CDH definitions.", "闪烁 概率"),
            Entry(HelpPage.FrequentlyAskedQuestions, "help-faq-upload", "上传与隐私", "Uploads and privacy", "DACT 不会自动上传，第三方网络权限需单独判断。", "DACT does not auto-upload; judge third-party network permissions separately.", "个人信息 角色名 服务器名 path"),
            Entry(HelpPage.FrequentlyAskedQuestions, "help-faq-overlay-input", "悬浮窗点不到或抢鼠标", "Overlay input problems", "理解穿透、锁定和完成位置编辑。", "Understand click-through, lock, and finishing position editing.", "编辑版 鼠标 游戏"),
            Entry(HelpPage.Copyright, "help-copyright-project", "版权与 GPL-3.0", "Copyright and GPL-3.0", "DACT 自有代码的许可证和第三方边界。", "License for DACT-owned code and third-party boundaries.", "license source 源代码 warranty"),
        ];
    }

    private void DrawPageHeader(string title, string description)
    {
        ImGui.TextColored(Gold, title);
        ImGui.TextDisabled(description);
        ImGui.Spacing();
    }

    private void DrawCard(string id, string title, float height, Action drawContent)
    {
        if (string.Equals(pendingSectionId, id, StringComparison.Ordinal))
        {
            ImGui.SetScrollHereY(0);
            pendingSectionId = null;
        }

        if (BrandedWindowChrome.BeginGoldCard(id, height, allowScrolling: false))
        {
            ImGui.TextColored(IceBlue, title);
            ImGui.Separator();
            drawContent();
        }
        BrandedWindowChrome.EndGoldCard();
        ImGui.Spacing();
    }

    private sealed record HelpSearchEntry(
        HelpPage Page,
        string SectionId,
        string Title,
        string Summary,
        string SearchText);

    private static void DrawBullet(string value)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped(value);
    }

    private void DrawCommand(string id, string command, string description)
    {
        if (ImGui.SmallButton($"{text.Get("复制", "Copy")}##help-command-{id}"))
        {
            ImGui.SetClipboardText(command);
        }
        ImGui.SameLine();
        ImGui.TextColored(IceBlue, command);
        ImGui.Indent();
        ImGui.TextWrapped(description);
        ImGui.Unindent();
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
