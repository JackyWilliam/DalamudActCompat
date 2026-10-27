using System.Globalization;
using System.Numerics;
using System.Text;
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
        Account,
        Troubleshooting,
        Commands,
        Copyright,
    }

    private enum CalloutKind
    {
        Tip,
        Warning,
    }

    private static Vector4 Navy => DactTheme.Tone(new Vector4(0.035f, 0.048f, 0.068f, 1), DactTheme.Palette.Surface);
    private static Vector4 NavyRaised => DactTheme.Tone(new Vector4(0.070f, 0.095f, 0.125f, 1), DactTheme.Palette.Raised);
    private static Vector4 NavyHover => DactTheme.Tone(new Vector4(0.105f, 0.145f, 0.185f, 1), DactTheme.Palette.Hover);
    private static Vector4 Gold => DactTheme.Tone(new Vector4(0.78f, 0.66f, 0.36f, 1), DactTheme.Palette.Gold);
    private static Vector4 IceBlue => DactTheme.Tone(new Vector4(0.42f, 0.78f, 0.96f, 1), DactTheme.Palette.Accent);
    private static readonly Vector4 Warning = new(0.96f, 0.36f, 0.34f, 1);

    private readonly UiText text;
    private readonly WindowDragController headerDrag = new();
    private readonly ISharedImmediateTexture logoTexture;
    private readonly string imageDirectory;
    private readonly Func<string, ISharedImmediateTexture> loadImage;
    private readonly PluginLogger logger;
    private readonly Action openRuntimeStatus;
    private readonly Action openLogDirectory;
    private readonly Action openThirdPartyNotice;
    // Cards size themselves from the height their content needed on the previous frame,
    // so language, width and font scale never clip text or leave empty space.
    private readonly Dictionary<string, float> cardHeights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ISharedImmediateTexture?> images = new(StringComparer.Ordinal);
    private HelpPage selectedPage;
    private string searchDraft = string.Empty;
    private string searchQuery = string.Empty;
    private string? pendingSectionId;
    private bool outerFrameStylePushed;

    public HelpWindow(
        UiText text,
        ISharedImmediateTexture logoTexture,
        string imageDirectory,
        Func<string, ISharedImmediateTexture> loadImage,
        PluginLogger logger,
        Action openRuntimeStatus,
        Action openLogDirectory,
        Action openThirdPartyNotice)
        : base("使用帮助###DalamudActCompatHelpWindow")
    {
        this.text = text;
        this.logoTexture = logoTexture;
        this.imageDirectory = imageDirectory;
        this.loadImage = loadImage;
        this.logger = logger;
        this.openRuntimeStatus = openRuntimeStatus;
        this.openLogDirectory = openLogDirectory;
        this.openThirdPartyNotice = openThirdPartyNotice;
        Size = new Vector2(980, 720);
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
        headerDrag.PrepareNextWindow();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        outerFrameStylePushed = true;
        Flags = DactTheme.WindowFlags(Flags);
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
                    headerDrag,
                    logoTexture,
                    text.Get("使用帮助", "Help"),
                    text.Get("使用文档", "User guide"),
                    IceBlue,
                    ControlCenterWindow.FormatVersionLabel(
                        typeof(HelpWindow).Assembly.GetName().Version),
                    "help-window", text: text))
            {
                IsOpen = false;
            }

            DrawNavigation();
            DrawSearchBar();
            ImGui.Spacing();
            if (ImGui.BeginChild("help-document-content", new Vector2(-1, -1), true))
            {
                if (string.IsNullOrWhiteSpace(searchQuery))
                {
                    DrawSelectedPage();
                }
                else
                {
                    DrawSearchResults();
                }
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
        selectedPage = HelpPage.Commands;
        searchDraft = string.Empty;
        searchQuery = string.Empty;
        pendingSectionId = null;
        IsOpen = true;
    }

    private void DrawNavigation()
    {
        var labels = Enum.GetValues<HelpPage>().Select(GetPageLabel).ToArray();
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
            case HelpPage.CombatMeter:
                DrawCombatMeter();
                break;
            case HelpPage.Overlays:
                DrawOverlays();
                break;
            case HelpPage.Extensions:
                DrawExtensions();
                break;
            case HelpPage.Account:
                DrawAccount();
                break;
            case HelpPage.Troubleshooting:
                DrawTroubleshooting();
                break;
            case HelpPage.Commands:
                DrawCommands();
                break;
            case HelpPage.Copyright:
                DrawCopyright();
                break;
        }
    }

    private void DrawSearchBar()
    {
        ImGui.Dummy(new Vector2(0, 8));
        DactTheme.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        // The input and button already define this toolbar; an additional child border adds visual clutter.
        if (ImGui.BeginChild(
                "help-search-card",
                new Vector2(-1, ImGui.GetFrameHeight() + 18),
                false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 7);
            DactTheme.PushStyleColor(ImGuiCol.FrameBg, Navy);
            DactTheme.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.09f, 0.14f, 0.20f, 1));
            DactTheme.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.11f, 0.18f, 0.25f, 1));
            var buttonLabel = text.Get("搜索", "Search");
            var buttonWidth = ImGui.CalcTextSize(buttonLabel).X + 40;
            ImGui.SetNextItemWidth(-buttonWidth - ImGui.GetStyle().ItemSpacing.X);
            var submitted = ImGui.InputTextWithHint(
                "##help-search",
                text.Get("搜索功能、问题、命令或关键词……", "Search features, problems, commands, or keywords..."),
                ref searchDraft,
                128,
                ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.PopStyleColor(3);
            ImGui.SameLine();
            DactTheme.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.29f, 0.38f, 1));
            DactTheme.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.17f, 0.38f, 0.49f, 1));
            DactTheme.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.20f, 0.44f, 0.56f, 1));
            DactTheme.PushStyleColor(ImGuiCol.Text, IceBlue);
            submitted |= DactTheme.Button($"{buttonLabel}##help-search-submit", new Vector2(buttonWidth, 0));
            ImGui.PopStyleColor(4);
            if (submitted)
            {
                searchQuery = searchDraft.Trim();
                pendingSectionId = null;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private void DrawSearchResults()
    {
        var results = CreateSearchEntries()
            .Where(entry => MatchesSearch(searchQuery, entry.SearchText))
            .ToArray();
        DrawPageHeader(
            text.Get("搜索结果", "Search results"),
            text.Format(
                $"找到 {results.Length} 条相关说明。点击结果可跳转到对应章节。",
                $"Found {results.Length} relevant entries. Select one to jump to its section."));
        if (results.Length == 0)
        {
            DrawCard("help-search-empty", text.Get("没有找到", "No results"), () =>
                Paragraph(text.Get(
                    "试试更短的关键词，例如“没数据”“权限”“重启”“悬浮窗”或命令里的单词。",
                    "Try a shorter keyword such as no data, permission, restart, overlay, or a word from a command.")));
            return;
        }

        foreach (var entry in results)
        {
            DrawCard($"help-search-{entry.SectionId}", entry.Title, () =>
            {
                ImGui.TextDisabled(GetPageLabel(entry.Page));
                Paragraph(entry.Summary);
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
        // Nine tabs share one row, so labels stay short enough for 140% text and longer languages.
        HelpPage.UsageNotice => text.Get("使用须知", "Notice"),
        HelpPage.GettingStarted => text.Get("快速上手", "Start"),
        HelpPage.CombatMeter => text.Get("战斗统计", "Meter"),
        HelpPage.Overlays => text.Get("悬浮窗", "Overlays"),
        HelpPage.Extensions => text.Get("扩展与权限", "Extensions"),
        HelpPage.Account => text.Get("账号", "Account"),
        HelpPage.Troubleshooting => text.Get("遇到问题", "Fixes"),
        HelpPage.Commands => text.Get("宏指令", "Commands"),
        HelpPage.Copyright => text.Get("版权声明", "Copyright"),
        _ => page.ToString(),
    };

    // The user wrote this page and asked for it to open first; keep its wording unchanged.
    private void DrawUsageNotice()
    {
        DrawPageHeader(
            text.Get("使用须知", "Usage notice"),
            text.Get("请先理解边界，再使用解析器、悬浮窗和第三方扩展。", "Understand the boundaries before using parsers, overlays, or third-party extensions."));
        DrawCard("help-notice-rules", text.Get("使用前请确认", "Before use"), () =>
            Paragraph(text.Get(
                "使用任何 Dalamud、ACT 或相关第三方工具之前，请自行了解对应游戏的用户协议以及第三方工具相关规则。",
                "Before using Dalamud, ACT, or related third-party tools, learn the applicable game user agreement and third-party-tool rules yourself.")));
        DrawCard("help-notice-discouraged", text.Get("项目本身不鼓励", "This project does not encourage"), () =>
        {
            Bullet(text.Get("PVP 信息优势", "Information advantages in PvP"));
            Bullet(text.Get("影响其他玩家正常游戏", "Interfering with other players' normal gameplay"));
            Bullet(text.Get("利用解析数据骚扰其他玩家", "Using parsed data to harass other players"));
            Bullet(text.Get("在公开环境跳脸", "Flaunting plugins in public spaces"));
            Bullet(text.Get("使用插件进行不当行为", "Using plugins for inappropriate conduct"));
        });
        DrawCard("help-notice-boundary", text.Get("特别提醒", "Important reminder"), () =>
        {
            Paragraph(text.Get("不要去绿玩面前跳脸。", "Do not flaunt plugins in front of players who do not use them."), Warning);
            Paragraph(text.Get("你自己开插件是一回事。", "Using a plugin privately is one thing."));
            Paragraph(text.Get("跑到别人面前：", "Going up to someone else and saying:"));
            Paragraph(text.Get("“你看我这个插件多牛逼！”", "“Look how amazing my plugin is!”"), IceBlue);
            Paragraph(text.Get(
                "然后把截图、悬浮窗、DPS、第三方工具全甩别人脸上，是另一回事。",
                "Then pushing screenshots, overlays, DPS, and third-party tools in their face is another matter."));
            Paragraph(text.Get("一经发现立刻踢出！", "If discovered, you will be removed immediately."), Warning);
            Paragraph(text.Get("请保持最基本的边界感。", "Please maintain basic boundaries and respect for others."));
        });
    }

    private void DrawGettingStarted()
    {
        DrawPageHeader(
            text.Get("快速上手", "Getting started"),
            text.Get("第一次使用？按顺序完成下面四步。", "New here? Follow these four steps in order."));
        DrawCard("help-start-sign-in", text.Get("1  登录 DACT 账号", "1  Sign in to your DACT account"), () =>
        {
            Paragraph(text.Get(
                "点游戏画面上的 ACT 按钮或输入 /actcompat 打开控制中心。没有账号就点「创建账号」，有账号直接登录；勾选「记住登录状态」后下次会自动登录。",
                "Open the control center with the ACT button on screen or /actcompat. Choose Create account if you are new, otherwise sign in. Keep Remember sign-in checked to sign in automatically next time."));
            Image("account-login", text.Get("控制中心的登录界面", "The control center sign-in screen"));
        });
        DrawCard("help-start-discord", text.Get("2  绑定 Discord", "2  Link Discord"), () =>
        {
            Paragraph(text.Get(
                "第一次登录后需要绑定 Discord 才能继续，按界面上的三步操作即可：",
                "After your first sign-in, DACT asks you to link Discord before continuing. Follow the three steps on screen:"));
            Step(1, text.Get(
                "点「复制绑定码并前往 Discord」，加入 DACT 的 Discord 服务器。",
                "Click Copy code and open Discord, then join the DACT Discord server."));
            Step(2, text.Get(
                "在成员列表的「Bot」分组找到 DACT，右键选择「发消息」。",
                "Find DACT under Bot in the member list, right-click it and choose Message."));
            Step(3, text.Get(
                "私聊输入 /dact，选择绑定账号并粘贴绑定码。回到游戏后会自动进入。",
                "In the direct message, type /dact, choose to link your account and paste the code. DACT continues on its own once you return to the game."));
            Image("account-discord", text.Get("绑定 Discord", "Linking Discord"));
            Callout(CalloutKind.Tip, text.Get(
                "绑定后忘记密码或换电脑，都能通过 Discord 找回账号和云备份。一个 Discord 最多绑定 3 个 DACT 账号。",
                "Once linked, you can recover your account and cloud backups through Discord after a forgotten password or on a new PC. One Discord account can link up to 3 DACT accounts."));
        });
        DrawCard("help-start-parser", text.Get("3  确认解析器在运行", "3  Check that the parser is running"), () =>
        {
            Paragraph(text.Get(
                "进入「概览」页，标题栏中间显示「运行中」就说明一切正常。请保持「启用解析」和「自动启动解析器」勾选。",
                "On Overview, the title bar shows Running when everything is fine. Keep Enable parsing and Auto start parser checked."));
            Image("control-overview", text.Get("概览页：解析器状态、快捷入口和基础设置", "Overview: parser status, shortcuts and basic settings"));
        });
        DrawCard("help-start-meter", text.Get("4  打一下木人", "4  Hit a striking dummy"), () =>
        {
            Paragraph(text.Get(
                "对木人或副本里的敌人造成几次伤害，战斗统计就会显示你和队友。右键 ACT 按钮可以打开或关闭战斗统计。",
                "Deal a few hits to a striking dummy or an enemy in a duty and the meter fills with you and your party. Right-click the ACT button to show or hide the meter."));
            Image("meter", text.Get("战斗统计（经典榜）", "The Combat Meter (Classic)"));
            Callout(CalloutKind.Tip, text.Get(
                "没有数据？看「遇到问题」页。控制中心右上角的 ? 随时可以打开这份帮助。",
                "No data? See Troubleshooting. The ? at the top right of the control center opens this help at any time."));
        });
    }

    private void DrawCombatMeter()
    {
        DrawPageHeader(
            text.Get("战斗统计", "Combat Meter"),
            text.Get("调整统计窗的外观、显示的数据和重置方式。", "Adjust how the meter looks, what it shows and when it resets."));
        DrawCard("help-meter-show", text.Get("打开与摆放", "Showing and placing the meter"), () =>
        {
            Bullet(text.Get(
                "在「战斗统计」页勾选「显示战斗统计」；找不到窗口时点「定位到战斗统计」，或输入 /actcompat meter。",
                "On Combat Meter, check Show Combat Meter. If you lose the window, click Open Combat Meter window or type /actcompat meter."));
            Bullet(text.Get(
                "点「自定义」可以取消锁定后拖动、缩放，摆好再锁定。开启「锁定时鼠标穿透」后，点击会穿过统计窗传给游戏。",
                "Click Customize to unlock, move and resize the meter, then lock it again. With Click-through when locked, clicks pass through to the game."));
            Bullet(text.Get(
                "「脱战隐藏」只是把窗口藏起来，统计仍在继续。",
                "Hide out of combat only hides the window; the meter keeps counting."));
            Image("control-meter", text.Get("战斗统计设置页", "Combat Meter settings"));
        });
        DrawCard("help-meter-templates", text.Get("榜单模板与显示的数据", "Templates and columns"), () =>
        {
            Bullet(text.Get(
                "「经典榜」「横版」「职能分栏」三选一；点「自定义」可以调字号、透明度、背景色，以及要显示的列。",
                "Choose Classic, Horizontal or Role split, then click Customize to change font size, opacity, background colour and the columns shown."));
            Bullet(text.Get(
                "经典榜标题栏可以在 DPS 榜 / HPS 榜、8 人本 / 24 人本之间切换。",
                "The Classic header switches between DPS and HPS ranking and between 8-player and 24-player modes."));
            Bullet(text.Get(
                "「显示范围」决定显示哪些玩家：按榜单设置，或跟随概览页的「游戏解析范围」。",
                "Display scope decides which players appear: use the meter settings, or follow the Parse scope on Overview."));
            Bullet(text.Get(
                "「玩家 ID 显示」可以遮盖名字，只影响界面，不会修改日志。",
                "Player identity can mask names. It only changes the display, never the logs."));
        });
        DrawCard("help-meter-metrics", text.Get("数值是怎么算的", "How the numbers are calculated"), () =>
        {
            Definition("DPS", text.Get(
                "按你自己的出手时间计算，晚进场或死亡后会比整场口径高。",
                "Uses your own active time, so it reads higher after a late start or a death."));
            Definition("EncDPS", text.Get(
                "按整场战斗时长计算，适合比较同一场里的队友。",
                "Uses the whole encounter length; best for comparing party members."));
            Definition(text.Get("rDPS（预估）", "rDPS (estimated)"), text.Get(
                "去掉别人给你的团辅、加回你给队友的团辅。这是本地实时估算，和 FFLogs 会有差异。",
                "Removes buffs others gave you and adds back buffs you gave. It is a live local estimate and can differ from FFLogs."));
            Definition("HPS", text.Get(
                "按开怪到现在的完整时间计算，不再治疗时会慢慢下降。",
                "Uses the full time since the pull, so it drifts down while you are not healing."));
            Definition(text.Get("暴击 / 直击 / 直暴", "CRIT / DH / CDH"), text.Get(
                "本场命中里暴击、直击、同时暴击直击的比例，不是角色面板数值。样本少时跳动是正常的。",
                "The share of this fight's hits that crit, direct hit, or both. These are not character sheet values, and small samples move a lot."));
            Definition("--", text.Get("还没有有效数值，不等于 0。", "No valid value yet. It does not mean zero."));
        });
        DrawCard("help-meter-reset", text.Get("统计重置与历史", "Resets and history"), () =>
        {
            Bullet(text.Get(
                "「DACT默认」：副本里普通脱战和转阶段继续累计，团灭或离开副本才结算。",
                "DACT default: inside a duty, ordinary combat breaks and phase changes keep accumulating; a wipe or leaving the duty ends the fight."));
            Bullet(text.Get(
                "「脱战指定秒数后重置」：脱战后倒计时 0–3600 秒再清空；倒计时内重新进战会取消。",
                "Reset after combat: clears 0–3600 seconds after combat ends. Re-entering combat cancels the countdown."));
            Bullet(text.Get(
                "「重置当前战斗」立即清空本场，不会删除历史和原始日志。输入 /actcompat history 查看近期战斗。",
                "Reset current encounter clears this fight at once without deleting history or raw logs. Type /actcompat history for recent fights."));
        });
        DrawCard("help-meter-fflogs", text.Get("FFLogs DPS Parse 预估", "FFLogs DPS Parse estimate"), () =>
        {
            Step(1, text.Get(
                "在「战斗统计」页下方开启「启用 FFLogs 在线估算」。",
                "Further down Combat Meter, turn on Enable FFLogs online estimate."));
            Step(2, text.Get(
                "点「创建 / 管理 API Client」，登录 FFLogs 创建免费 Client；没有网站时回调地址填 https://example.com。",
                "Click Create / manage API client, sign in to FFLogs and create a free client. If you have no website, use https://example.com as the redirect URL."));
            Step(3, text.Get(
                "把 Client ID 和 Client Secret 填进对应输入框，点「测试并刷新」。",
                "Paste the Client ID and Client Secret into their fields, then click Test and refresh."));
            Callout(CalloutKind.Warning, text.Get(
                "Client Secret 只保存在本机，不要截图或发给别人。",
                "The Client Secret is stored only on this PC. Never screenshot or share it."));
            Callout(CalloutKind.Tip, text.Get(
                "预估只是参考：它用本场 DPS 对照缓存的排名分布。上传到 FFLogs 后的成绩以官网为准。",
                "The estimate is a guide that compares this fight's DPS with cached rankings. Your uploaded FFLogs report is authoritative."));
        });
    }

    private void DrawOverlays()
    {
        DrawPageHeader(
            text.Get("悬浮窗", "Overlays"),
            text.Get("Cactbot 提醒、时间轴和其他网页悬浮窗。", "Cactbot alerts, timelines and other web overlays."));
        DrawCard("help-overlay-cactbot", "Cactbot", () =>
        {
            Bullet(text.Get(
                "DACT 自带 Cactbot。「文字提醒」和「时间轴」是两个独立窗口，可以同时打开；旧版「文字提醒 + 时间轴」组合窗和它们二选一。",
                "Cactbot comes with DACT. Alerts and Timeline are separate windows that can be open together; the legacy combined window replaces both."));
            Bullet(text.Get(
                "「Cactbot 设置」里可以改提醒语言、播报方式和自定义脚本目录；「安装 / 更新」用于更新或换用自己的 Cactbot 包。",
                "Cactbot settings holds alert language, speech options and your custom script directory. Install / update refreshes Cactbot or switches to your own package."));
            Image("control-overlays", text.Get("悬浮窗页：左侧选择悬浮窗，右侧调整它的设置", "Overlays: pick an overlay on the left and adjust it on the right"));
        });
        DrawCard("help-overlay-add", text.Get("添加网页悬浮窗", "Adding a web overlay"), () =>
        {
            Step(1, text.Get("点右上角「＋ 添加悬浮窗」。", "Click + Add overlay at the top right."));
            Step(2, text.Get(
                "选择模板，或粘贴完整的 http / https / file 地址。只添加你信任的网页。",
                "Pick a template or paste a full http, https or file address. Add only pages you trust."));
            Step(3, text.Get(
                "在右侧设置「启动时自动打开」「脱战隐藏」等选项。",
                "Use the panel on the right for options such as Open on startup and Hide out of combat."));
        });
        DrawCard("help-overlay-edit", text.Get("移动、缩放与鼠标穿透", "Moving, resizing and click-through"), () =>
        {
            Bullet(text.Get(
                "点「编辑位置和大小」后拖动窗口或右下角，完成后点「完成位置编辑」。",
                "Click Edit position and size, then drag the window or its lower-right corner. Click Finish position editing when done."));
            Bullet(text.Get(
                "鼠标穿透开启时点击会传给游戏，网页本身点不动；需要操作网页时先关闭穿透。",
                "With click-through on, clicks go to the game and the page cannot be used. Turn it off when you need to interact with the page."));
            Bullet(text.Get(
                "字太小就调「页面缩放」，内容被截断就把窗口拉大。",
                "If text is too small, raise Page zoom. If content is cut off, make the window bigger."));
        });
        DrawCard("help-overlay-no-data", text.Get("有画面但没有数据", "The page shows but has no data"), () =>
            Paragraph(text.Get(
                "先确认控制中心标题栏显示「运行中」，再点该悬浮窗的「重新检测」。部分网页还需要联网或单独设置。",
                "First make sure the control center title bar shows Running, then click Detect again for that overlay. Some pages also need internet access or their own settings.")));
    }

    private void DrawExtensions()
    {
        DrawPageHeader(
            text.Get("扩展与权限", "Extensions and permissions"),
            text.Get("触发器、语音、邮差等 ACT 扩展的启用、配置和权限。", "Enable, configure and grant permissions to ACT extensions such as triggers, speech and PostNamazu."));
        DrawCard("help-extension-bundled", text.Get("自带的扩展", "Bundled extensions"), () =>
        {
            Definition("Triggernometry", text.Get(
                "触发器：根据战斗日志播报、计时和绘图。",
                "Triggers: callouts, timers and drawings driven by combat logs."));
            Definition(text.Get("鲶鱼精邮差 / PostNamazu", "PostNamazu"), text.Get(
                "让触发器执行游戏指令、标点和地标。",
                "Lets triggers run game commands, markers and waymarks."));
            Definition("ACT.FoxTTS", text.Get("把文字提醒读出来。", "Reads text alerts aloud."));
            Definition(text.Get("银山雀儿 / SilverDasher", "SilverDasher"), text.Get(
                "狩猎、临危受命和跨区提醒。默认关闭，需要时再勾选。",
                "Hunt, FATE and world-visit alerts. Off by default; enable it if you need it."));
            Definition(text.Get("抹茶 / Cafe.Matcha", "Cafe.Matcha"), text.Get(
                "排本、临危受命和过图提醒。",
                "Duty finder, FATE and zone-change alerts."));
            Image("control-extensions", text.Get("扩展页", "The Extensions page"));
        });
        DrawCard("help-extension-enable", text.Get("启用和打开配置", "Enabling and configuring"), () =>
        {
            Step(1, text.Get("在「扩展」页勾选扩展名称。", "On Extensions, check the extension."));
            Step(2, text.Get(
                "点同一行的「打开配置」。导入触发器、选择语音等都在扩展自己的窗口里完成。",
                "Click Open configuration on the same row. Importing triggers, choosing a voice and similar settings happen in the extension's own window."));
            Callout(CalloutKind.Tip, text.Get(
                "勾选扩展或修改权限后，DACT 会自动重启这个扩展，不用重启游戏。",
                "When you enable an extension or change its permissions, DACT restarts that extension for you. No game restart is needed."));
        });
        DrawCard("help-extension-permissions", text.Get("权限", "Permissions"), () =>
        {
            Paragraph(text.Get(
                "在「扩展」页下方的「ACT 插件权限边界」里，可以按扩展逐项开放能力。只开实际要用的：",
                "Under ACT plugin permission boundary at the bottom of Extensions, grant capabilities one extension at a time. Grant only what you use:"));
            Definition(text.Get("网络请求", "Network requests"), text.Get(
                "在线更新、远程触发器仓库和语音服务。",
                "Online updates, remote trigger repositories and speech services."));
            Definition(text.Get("发送游戏指令", "Send game commands"), text.Get(
                "邮差的指令、标点和地标。",
                "PostNamazu commands, markers and waymarks."));
            Definition(text.Get("访问游戏原生内存", "Access native game memory"), text.Get(
                "银山雀儿和邮差的部分原版功能。",
                "Some original SilverDasher and PostNamazu features."));
            Definition(text.Get("运行高风险脚本", "Run high-risk scripts"), text.Get(
                "触发器里的高级脚本。",
                "Advanced scripts inside triggers."));
            Definition(text.Get("写入文件", "Write files"), text.Get(
                "扩展缓存，或导出到你选择的文件。",
                "Extension caches, or exports to a file you choose."));
            Callout(CalloutKind.Warning, text.Get(
                "只安装来源可信的扩展。权限限制的是 DACT 提供的接口，挡不住 DLL 直接调用 Windows。",
                "Install only extensions from sources you trust. Permissions limit DACT's own interfaces; they cannot stop a DLL from calling Windows directly."));
            if (DactTheme.Button(text.Get("查看内置第三方 DLL 的作者与来源", "View bundled DLL authors and sources")))
            {
                openThirdPartyNotice();
            }
        });
        DrawCard("help-extension-install", text.Get("安装其他 ACT 插件", "Installing other ACT plugins"), () =>
            Paragraph(text.Get(
                "点「安装 DLL / ZIP」，或输入 /actcompat install \"路径\"。DACT 会先检查文件并列出它需要的权限，你同意后它才会运行。",
                "Click Install DLL / ZIP or type /actcompat install \"path\". DACT inspects the file first and lists the permissions it needs; the plugin only runs after you agree.")));
        DrawCard("help-extension-update", text.Get("更新后还是旧版本", "Still the old version after an update"), () =>
            Paragraph(text.Get(
                "外部工具箱更新扩展后，输入 /actcompat status 点「重启共享 Host」，相当于以前的“重启 ACT”，不用退出游戏。抹茶点「重启抹茶 Host」，自己安装的插件点「重启通用 Host」。",
                "After an external toolbox updates an extension, type /actcompat status and click Restart shared Host. This replaces the old \"restart ACT\"; you do not need to exit the game. Use Restart Matcha Host for Matcha and Restart generic Host for plugins you installed yourself.")));
    }

    private void DrawAccount()
    {
        DrawPageHeader(
            text.Get("账号与云同步", "Account and cloud sync"),
            text.Get("云备份、找回账号、好友、皮肤和其他设置。", "Cloud backups, account recovery, friends, skins and other settings."));
        DrawCard("help-account-cloud", text.Get("云同步", "Cloud sync"), () =>
        {
            Bullet(text.Get(
                "勾选「打开游戏后自动同步配置」后，每次登录会自动检查并备份一次；配置没变就不上传。云端保留最近两份。",
                "With Sync configuration after game launch on, DACT checks and backs up once per sign-in and skips unchanged settings. The cloud keeps the two most recent backups."));
            Bullet(text.Get(
                "需要恢复时先选择云端版本、预览恢复内容，再确认恢复；恢复前的配置会被保留，可以回滚。",
                "To restore, choose a cloud version, preview what will change, then confirm. Your previous settings are kept so you can roll back."));
            Image("control-cloud", text.Get("云同步页", "The Cloud Sync page"));
        });
        DrawCard("help-account-recovery", text.Get("找回账号", "Recovering your account"), () =>
            Paragraph(text.Get(
                "账号绑定 Discord 后，忘记密码或换电脑时，在 Discord 私聊 DACT 输入 /dact，按菜单找回账号和云备份。",
                "Once your account is linked to Discord, you can recover it and its cloud backups after a forgotten password or on a new PC: message DACT on Discord, type /dact and follow the menu.")));
        DrawCard("help-account-friends", text.Get("好友", "Friends"), () =>
            Paragraph(text.Get(
                "点控制中心标题栏的「Friends」打开好友栏，可以添加好友、聊天；右键好友可以设置备注，备注随账号在各设备同步。",
                "Click Friends in the control center title bar to add friends and chat. Right-click a friend to set a note; notes sync across your devices with your account.")));
        DrawCard("help-account-settings", text.Get("设置&账号", "Settings & Account"), () =>
        {
            Bullet(text.Get(
                "外观与皮肤：点「管理皮肤」预览和切换，部分皮肤需要对应的赞助等级。",
                "Appearance and skins: click Manage skins to preview and switch. Some skins need a matching sponsor tier."));
            Bullet(text.Get(
                "账号：查看登录有效期、退出登录或修改密码。",
                "Account: see how long you stay signed in, sign out or change your password."));
            Bullet(text.Get(
                "解析器：重启解析器、打开详细运行状态或查看三方扩展声明。",
                "Parser: restart it, open detailed runtime status or view the third-party extension notice."));
            Bullet(text.Get(
                "界面语言、ACT 快捷按钮和恢复出厂设置也在这一页。恢复出厂设置会移走现有数据，只在最后才用。",
                "UI language, the ACT quick button and factory reset are here too. Factory reset moves your existing data away, so use it only as a last resort."));
            Image("control-diagnostics", text.Get("设置&账号页", "The Settings & Account page"));
        });
    }

    private void DrawTroubleshooting()
    {
        DrawPageHeader(
            text.Get("遇到问题", "Troubleshooting"),
            text.Get("按顺序检查，大多数问题不用重启游戏。", "Work through these in order; most problems need no game restart."));
        DrawCard("help-troubleshooting-status", text.Get("1  先看运行状态", "1  Check runtime status first"), () =>
            Paragraph(text.Get(
                "输入 /actcompat status，从上往下找第一个不是「运行中 / 已连接」的组件，先处理它；后面的组件通常会跟着恢复。",
                "Type /actcompat status and find the first component from the top that is not Running or Connected. Fix that one first; the ones below it usually recover too.")));
        DrawCard("help-troubleshooting-restart", text.Get("2  重启对应的组件", "2  Restart the right component"), () =>
        {
            Definition(text.Get("完全没有战斗数据", "No combat data at all"), text.Get(
                "在「设置&账号」点「重启解析器」。",
                "Click Restart parser under Settings & Account."));
            Definition(text.Get("触发器、邮差、TTS 或银山雀儿不工作", "Triggers, PostNamazu, TTS or SilverDasher stopped working"), text.Get(
                "在运行状态里点「重启共享 Host」。",
                "Click Restart shared Host in runtime status."));
            Definition(text.Get("抹茶不工作", "Matcha stopped working"), text.Get(
                "点「重启抹茶 Host」。",
                "Click Restart Matcha Host."));
            Definition(text.Get("自己安装的插件不工作", "A plugin you installed stopped working"), text.Get(
                "点「重启通用 Host」。",
                "Click Restart generic Host."));
            Definition(text.Get("DACT 本体刚更新", "DACT itself was just updated"), text.Get(
                "在卫月插件列表里重新加载 DACT；还不行再完整退出游戏和启动器。",
                "Reload DACT in the Dalamud plugin list. If that does not help, fully exit the game and the launcher."));
        });
        DrawCard("help-troubleshooting-common", text.Get("常见情况", "Common cases"), () =>
        {
            Definition(text.Get("战斗统计没有数据", "The meter shows no data"), text.Get(
                "确认标题栏显示「运行中」，关掉「脱战隐藏」，再对木人造成几次伤害。只是打开窗口、进本或选中目标不会产生数据。",
                "Make sure the title bar shows Running, turn off Hide out of combat, then hit a dummy a few times. Opening the window, entering a duty or targeting alone produces no data."));
            Definition(text.Get("某个触发器、TTS 或标点不工作", "One trigger, TTS or marker does not work"), text.Get(
                "扩展要同时满足三点：已勾选、所需权限已开、扩展自己的配置里已启用这个功能。",
                "Three things must all be true: the extension is checked, it has the permission it needs, and the feature is enabled in its own configuration."));
            Definition(text.Get("提示“重启 ACT”", "Asked to \"restart ACT\""), text.Get(
                "不用重启游戏，在运行状态里点「重启共享 Host」即可。",
                "No game restart needed. Click Restart shared Host in runtime status."));
            Definition(text.Get("通知没有出现", "Notifications do not appear"), text.Get(
                "游戏在前台时用游戏内通知，切到其他程序时用 Windows 通知；请检查 Windows 的专注助手设置。",
                "DACT uses in-game notifications while the game is in front and Windows notifications otherwise. Check Windows Focus Assist."));
            Definition(text.Get("本地 DPS 和 FFLogs 不一样", "Local DPS differs from FFLogs"), text.Get(
                "两边计算方式不同，小差异是正常的。差得很多时保留当场的 Network 日志一起反馈。",
                "They calculate differently, so small gaps are normal. For large gaps, keep that fight's Network log and report it."));
        });
        DrawCard("help-troubleshooting-report", text.Get("3  反馈问题", "3  Reporting a problem"), () =>
        {
            Bullet(text.Get(
                "写清 DACT 版本、发生时间、当时在做什么、看到了什么，以及能不能重复出现。",
                "Include the DACT version, when it happened, what you were doing, what you saw and whether it happens again."));
            Bullet(text.Get(
                "在「设置&账号」点「复制诊断日志」，把完整内容一起发来。",
                "Click Copy diagnostic log under Settings & Account and send the complete text."));
            Bullet(text.Get(
                "战斗数据不对时，再附上「打开 FFLogs 上传日志」目录里当场的 Network 日志。发出前检查角色名和服务器名。",
                "For wrong combat data, also attach that fight's Network log from Open FFLogs upload logs. Check character and world names before sending."));
            if (DactTheme.Button(text.Get("打开运行状态", "Open runtime status")))
            {
                openRuntimeStatus();
            }
            ImGui.SameLine();
            if (DactTheme.Button(text.Get("打开诊断日志目录", "Open diagnostic logs")))
            {
                openLogDirectory();
            }
        });
    }

    private void DrawCommands()
    {
        DrawPageHeader(
            text.Get("宏指令", "Macro commands"),
            text.Get("点击“复制”即可放入剪贴板，再粘贴到游戏聊天框或宏中。", "Use Copy to place a command on the clipboard, then paste it into game chat or a macro."));
        DrawCard("help-commands-common", text.Get("常用入口", "Common entry points"), () =>
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
        DrawCard("help-commands-overlays", text.Get("悬浮窗", "Overlays"), () =>
        {
            DrawCommand("cactbot", "/actcompat cactbot", text.Get("打开当前选择的 Cactbot 悬浮窗。", "Open the selected Cactbot overlay."));
            DrawCommand("overlay", "/actcompat overlay", text.Get("打开当前选择的 HTML 悬浮窗；可在后面追加模板名。", "Open the selected HTML overlay; optionally append a template name."));
        });
        DrawCard("help-commands-maintenance", text.Get("维护与诊断", "Maintenance and diagnostics"), () =>
        {
            DrawCommand("clear", "/actcompat clear", text.Get("立即清空当前战斗显示，不删除历史或原始日志。", "Immediately clear the current encounter display without deleting history or raw logs."));
            DrawCommand("install", "/actcompat install \"<DLL 或 ZIP 路径>\"", text.Get("静态预检并导入第三方 ACT 扩展；路径含空格时保留引号。", "Preflight and import a third-party ACT extension; keep quotes around paths containing spaces."));
            DrawCommand("factory-reset", "/actcompat factory-reset", text.Get("打开控制中心；恢复出厂设置仍需在界面中确认。", "Open the control center; factory reset still requires confirmation in the UI."));
            DrawCommand("host", "/actcompat host", text.Get("启动兼容 Host 并重启解析器，主要用于诊断。", "Start the compatibility Host and restart the parser, primarily for diagnostics."));
            DrawCommand("stop", "/actcompat stop", text.Get("停止解析器与共享兼容 Host，主要用于诊断。", "Stop the parser and shared compatibility Host, primarily for diagnostics."));
            DrawCommand("sample", "/actcompat sample", text.Get("载入本地样例战斗，仅用于开发与界面检查。", "Load a local sample encounter for development and UI checks only."));
        });
    }

    private void DrawCopyright()
    {
        DrawPageHeader(
            text.Get("版权声明", "Copyright notice"),
            text.Get("本页只声明 Dalamud ACT Compat 自身的版权与许可。", "This page states copyright and licensing only for Dalamud ACT Compat itself."));
        DrawCard("help-copyright-project", "Dalamud ACT Compat", () =>
        {
            DactTheme.TextColored(IceBlue, "Copyright © 2026 DalamudActCompat contributors.");
            Paragraph(text.Get(
                "Dalamud ACT Compat 自有源代码以 GNU General Public License version 3（GPL-3.0）发布。你可以在许可证条款下运行、研究、修改与再分发本项目。",
                "Dalamud ACT Compat's own source code is released under the GNU General Public License version 3 (GPL-3.0). You may run, study, modify, and redistribute the project under that license."));
            Paragraph(text.Get(
                "本程序按现状提供，不附带任何明示或默示担保，包括适销性或特定用途适用性的担保。完整条款以项目随附的 LICENSE.md 为准。",
                "This program is provided as-is, without any express or implied warranty, including merchantability or fitness for a particular purpose. The bundled LICENSE.md contains the complete terms."));
            Paragraph(
                text.Get(
                    "本声明不主张任何第三方插件、依赖、网页悬浮窗、游戏内容、商标或素材的版权。",
                    "This notice claims no copyright over third-party plugins, dependencies, web overlays, game content, trademarks, or assets."),
                ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            if (DactTheme.Button(text.Get("查看项目源代码", "View project source")))
            {
                OpenUrl("https://github.com/JackyWilliam/DalamudActCompat");
            }
            ImGui.SameLine();
            if (DactTheme.Button(text.Get("查看 GPL-3.0 许可证", "View GPL-3.0 license")))
            {
                OpenUrl("https://github.com/JackyWilliam/DalamudActCompat/blob/main/LICENSE.md");
            }
        });
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
                $"{text.Get(chineseTitle, englishTitle)} {text.Get(chineseSummary, englishSummary)} {chineseTitle} {englishTitle} {chineseSummary} {englishSummary} {keywords}");

        return
        [
            Entry(HelpPage.UsageNotice, "help-notice-rules", "使用前请确认", "Before use", "第三方工具规则与使用边界。", "Rules and boundaries for third-party tools.", "用户协议 PVP 跳脸 骚扰 safety rules"),
            Entry(HelpPage.GettingStarted, "help-start-sign-in", "登录 DACT 账号", "Sign in to DACT", "创建账号或登录，记住登录状态。", "Create an account or sign in, and stay signed in.", "注册 登录 账号 密码 首次 第一次 register login password"),
            Entry(HelpPage.GettingStarted, "help-start-discord", "绑定 Discord", "Link Discord", "按三步绑定 Discord 后才能继续使用。", "Link Discord in three steps before continuing.", "Discord 绑定码 /dact Bot link code"),
            Entry(HelpPage.GettingStarted, "help-start-parser", "确认解析器在运行", "Parser is running", "标题栏显示运行中，保持启用解析。", "The title bar shows Running; keep parsing enabled.", "解析器 运行中 初始化 parser running"),
            Entry(HelpPage.GettingStarted, "help-start-meter", "打一下木人", "Hit a dummy", "造成伤害后战斗统计才有数据。", "The meter fills once you deal damage.", "木人 没数据 统计 dummy meter"),
            Entry(HelpPage.CombatMeter, "help-meter-show", "打开与摆放战斗统计", "Showing the meter", "显示、定位、锁定、鼠标穿透和脱战隐藏。", "Show, locate, lock, click-through and hide out of combat.", "找不到窗口 定位 锁定 穿透 隐藏 lock click-through"),
            Entry(HelpPage.CombatMeter, "help-meter-templates", "榜单模板与显示的数据", "Templates and columns", "经典榜、横版、职能分栏、8/24 人、显示范围和 ID 遮盖。", "Classic, Horizontal, Role split, 8/24 players, display scope and name masking.", "模板 自定义 字号 透明度 背景 列 24人 匿名 template"),
            Entry(HelpPage.CombatMeter, "help-meter-metrics", "数值是怎么算的", "How numbers are calculated", "DPS、EncDPS、rDPS、HPS、暴击直击和 --。", "DPS, EncDPS, rDPS, HPS, crit and direct hit rates, and --.", "口径 暴击 直击 直暴 crit direct hit"),
            Entry(HelpPage.CombatMeter, "help-meter-reset", "统计重置与历史", "Resets and history", "DACT默认、脱战倒计时重置、重置当前战斗和历史。", "DACT default, timed reset, resetting the current fight and history.", "重置 清空 历史 团灭 reset history"),
            Entry(HelpPage.CombatMeter, "help-meter-fflogs", "FFLogs DPS Parse 预估", "FFLogs Parse estimate", "创建 API Client、填写凭据并测试。", "Create an API client, enter credentials and test.", "FFLogs Parse Client ID Secret 排名 percentile"),
            Entry(HelpPage.Overlays, "help-overlay-cactbot", "Cactbot", "Cactbot", "文字提醒、时间轴、Cactbot 设置和自定义目录。", "Alerts, timeline, Cactbot settings and custom directory.", "cactbot raidboss timeline alerts 时间轴 提醒 目录"),
            Entry(HelpPage.Overlays, "help-overlay-add", "添加网页悬浮窗", "Add a web overlay", "从模板或网址创建悬浮窗。", "Create an overlay from a template or address.", "网页 http https 模板 添加 URL"),
            Entry(HelpPage.Overlays, "help-overlay-edit", "移动、缩放与鼠标穿透", "Move, resize and click-through", "编辑位置、页面缩放和穿透。", "Edit position, page zoom and click-through.", "拖动 缩放 点不到 鼠标 穿透 zoom"),
            Entry(HelpPage.Overlays, "help-overlay-no-data", "悬浮窗没有数据", "Overlay has no data", "确认运行中后重新检测。", "Check Running, then detect again.", "空白 重新检测 连接 blank"),
            Entry(HelpPage.Extensions, "help-extension-bundled", "自带的扩展", "Bundled extensions", "Triggernometry、邮差、FoxTTS、银山雀儿和抹茶。", "Triggernometry, PostNamazu, FoxTTS, SilverDasher and Matcha.", "触发器 鲶鱼精 邮差 TTS 语音 雀儿 抹茶 triggers"),
            Entry(HelpPage.Extensions, "help-extension-enable", "启用和打开配置", "Enable and configure", "勾选扩展并打开它自己的配置窗口。", "Check an extension and open its configuration.", "打开配置 导入触发器 音色 configuration"),
            Entry(HelpPage.Extensions, "help-extension-permissions", "扩展权限", "Extension permissions", "按需开放网络、指令、内存、脚本和文件权限。", "Grant network, command, memory, script and file capabilities as needed.", "权限 授权 网络 指令 内存 脚本 文件 permission"),
            Entry(HelpPage.Extensions, "help-extension-install", "安装其他 ACT 插件", "Install other ACT plugins", "导入 DLL 或 ZIP，确认权限后运行。", "Import a DLL or ZIP and approve its permissions.", "安装 导入 DLL ZIP install"),
            Entry(HelpPage.Extensions, "help-extension-update", "更新后还是旧版本", "Old version after update", "重启共享、抹茶或通用 Host。", "Restart the shared, Matcha or generic Host.", "重启 ACT 工具箱 更新 restart host"),
            Entry(HelpPage.Account, "help-account-cloud", "云同步", "Cloud sync", "自动备份、预览恢复和回滚。", "Automatic backup, restore preview and rollback.", "云备份 恢复 回滚 同步 backup restore"),
            Entry(HelpPage.Account, "help-account-recovery", "找回账号", "Recover your account", "通过 Discord 找回账号和云备份。", "Recover your account and backups through Discord.", "忘记密码 找回 换电脑 Discord recovery"),
            Entry(HelpPage.Account, "help-account-friends", "好友", "Friends", "添加好友、聊天和备注。", "Add friends, chat and set notes.", "好友 聊天 备注 friends note"),
            Entry(HelpPage.Account, "help-account-settings", "设置&账号", "Settings & Account", "皮肤、账号、解析器、语言和恢复出厂设置。", "Skins, account, parser, language and factory reset.", "皮肤 赞助 语言 快捷按钮 恢复出厂 skins language"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-status", "先看运行状态", "Check runtime status", "找到第一个不正常的组件。", "Find the first component that is not running.", "/actcompat status 初始化 运行状态"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-restart", "重启对应的组件", "Restart the right component", "解析器、共享、抹茶和通用 Host 各管什么。", "What the parser and each Host are responsible for.", "重启 解析器 Host reload 卫月"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-common", "常见情况", "Common cases", "没数据、触发器不工作、重启 ACT、通知和 FFLogs 差异。", "No data, a trigger not working, restart ACT, notifications and FFLogs gaps.", "没数据 不播报 标点 通知 FFLogs 差异"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-report", "反馈问题", "Report a problem", "需要的说明、诊断日志和 Network 日志。", "What to include, diagnostic log and Network log.", "反馈 截图 日志 诊断 report log"),
            Entry(HelpPage.Commands, "help-commands-common", "常用宏指令", "Common commands", "打开控制中心、统计、历史、日志和状态。", "Open the control center, meter, history, logs, and status.", "/actcompat on meter history logs status copy 复制"),
            Entry(HelpPage.Commands, "help-commands-overlays", "悬浮窗命令", "Overlay commands", "打开 Cactbot 或指定 HTML 模板。", "Open Cactbot or a named HTML template.", "cactbot overlay template 模板"),
            Entry(HelpPage.Commands, "help-commands-maintenance", "维护与诊断命令", "Maintenance commands", "清空、安装、Host、停止和恢复出厂设置。", "Clear, install, Host, stop, and factory reset commands.", "clear install host stop factory-reset sample DLL ZIP"),
            Entry(HelpPage.Copyright, "help-copyright-project", "版权与 GPL-3.0", "Copyright and GPL-3.0", "DACT 自有代码的许可证和第三方边界。", "License for DACT-owned code and third-party boundaries.", "license source 源代码 warranty"),
        ];
    }

    private void DrawPageHeader(string title, string description)
    {
        DactTheme.TextColored(Gold, title);
        Paragraph(description, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.Spacing();
    }

    private void DrawCard(string id, string title, Action drawContent)
    {
        if (string.Equals(pendingSectionId, id, StringComparison.Ordinal))
        {
            ImGui.SetScrollHereY(0);
            pendingSectionId = null;
        }

        var height = cardHeights.TryGetValue(id, out var measured)
            ? measured
            : ImGui.GetTextLineHeightWithSpacing() * 4;
        if (BrandedWindowChrome.BeginGoldCard(id, height, allowScrolling: false))
        {
            DactTheme.TextColored(IceBlue, title);
            ImGui.Separator();
            drawContent();
            var fitted = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + ImGui.GetStyle().WindowPadding.Y;
            cardHeights[id] = MathF.Ceiling(fitted);
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

    private static void Paragraph(string value, Vector4? color = null)
    {
        var lines = WrapLines(value, ImGui.GetContentRegionAvail().X, line => ImGui.CalcTextSize(line).X);
        if (color is { } textColor) ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        // One multi-line item keeps every wrapped line aligned with the first one,
        // which also gives bullets and steps a hanging indent.
        ImGui.TextUnformatted(string.Join('\n', lines));
        if (color is not null) ImGui.PopStyleColor();
    }

    private static void Bullet(string value)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        Paragraph(value);
    }

    private static void Step(int number, string value)
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetTextLineHeight();
        var radius = lineHeight * 0.55f;
        var center = start + new Vector2(radius, lineHeight * 0.5f);
        drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(IceBlue with { W = 0.85f }));
        var label = number.ToString(CultureInfo.InvariantCulture);
        drawList.AddText(center - ImGui.CalcTextSize(label) * 0.5f, ImGui.GetColorU32(Navy), label);
        ImGui.Dummy(new Vector2(radius * 2, lineHeight));
        ImGui.SameLine(0, radius);
        Paragraph(value);
    }

    private static void Definition(string term, string value)
    {
        DactTheme.TextColored(IceBlue, term);
        ImGui.Indent(ImGui.GetFontSize());
        Paragraph(value);
        ImGui.Unindent(ImGui.GetFontSize());
    }

    private void Callout(CalloutKind kind, string value)
    {
        var color = kind == CalloutKind.Warning ? Warning : Gold;
        var start = ImGui.GetCursorScreenPos();
        var indent = ImGui.GetFontSize();
        ImGui.Indent(indent);
        DactTheme.TextColored(color, kind == CalloutKind.Warning ? text.Get("注意", "Caution") : text.Get("提示", "Tip"));
        Paragraph(value);
        ImGui.Unindent(indent);
        // Drawn after the text so the bar spans exactly the callout's measured height.
        ImGui.GetWindowDrawList().AddLine(
            start + new Vector2(indent * 0.3f, 2),
            new Vector2(start.X + indent * 0.3f, ImGui.GetItemRectMax().Y - 2),
            ImGui.GetColorU32(color),
            3);
    }

    private void Image(string name, string caption)
    {
        if (ResolveImage(name) is not { } texture)
        {
            return;
        }

        var wrap = texture.GetWrapOrEmpty();
        if (wrap.Width <= 1 || wrap.Height <= 1)
        {
            // Still loading; the card grows once the texture arrives.
            return;
        }

        var width = Math.Min(ImGui.GetContentRegionAvail().X, wrap.Width);
        var size = new Vector2(width, width * wrap.Height / wrap.Width);
        var min = ImGui.GetCursorScreenPos();
        ImGui.Image(wrap.Handle, size);
        ImGui.GetWindowDrawList().AddRect(min, min + size, ImGui.GetColorU32(Gold with { W = 0.35f }), 4);
        Paragraph(caption, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
    }

    // Screenshots exist per UI language; fall back to English, then Simplified Chinese, and
    // cache the decision so the help does not touch the disk every frame.
    private ISharedImmediateTexture? ResolveImage(string name)
    {
        var key = $"{text.Language}/{name}";
        if (images.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ISharedImmediateTexture? resolved = null;
        foreach (var language in new[] { text.Language, "en", "zh-CN" })
        {
            var path = Path.Combine(imageDirectory, language, name + ".png");
            if (File.Exists(path))
            {
                resolved = loadImage(path);
                break;
            }
        }

        images[key] = resolved;
        return resolved;
    }

    private void DrawCommand(string id, string command, string description)
    {
        if (DactTheme.SmallButton($"{text.Get("复制", "Copy")}##help-command-{id}"))
        {
            ImGui.SetClipboardText(command);
        }
        ImGui.SameLine();
        DactTheme.TextColored(IceBlue, command);
        ImGui.Indent();
        Paragraph(description);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    // ImGui only breaks at spaces, so Chinese and Japanese sentences mixed with Latin words
    // broke at the first space instead of at the edge. Break between CJK characters too, and
    // keep closing punctuation off the start of a line and opening punctuation off its end.
    internal static IReadOnlyList<string> WrapLines(string value, float width, Func<string, float> measure)
    {
        var lines = new List<string>();
        foreach (var paragraph in value.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var token in Tokenize(paragraph))
            {
                var candidate = line.ToString() + token;
                if (line.Length > 0 && measure(candidate.TrimEnd()) > width)
                {
                    lines.Add(line.ToString().TrimEnd());
                    line.Clear();
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }
                }

                if (line.Length == 0 && measure(token) > width)
                {
                    // A single word wider than the column is split by character as a last resort.
                    foreach (var character in token)
                    {
                        if (line.Length > 0 && measure(line.ToString() + character) > width)
                        {
                            lines.Add(line.ToString());
                            line.Clear();
                        }

                        line.Append(character);
                    }

                    continue;
                }

                line.Append(token);
            }

            lines.Add(line.ToString().TrimEnd());
        }

        return lines;
    }

    private static IEnumerable<string> Tokenize(string paragraph)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        void FlushWord()
        {
            if (word.Length == 0) return;
            tokens.Add(word.ToString());
            word.Clear();
        }

        foreach (var character in paragraph)
        {
            if (char.IsWhiteSpace(character))
            {
                FlushWord();
                tokens.Add(character.ToString());
            }
            else if (IsWideCharacter(character))
            {
                FlushWord();
                tokens.Add(character.ToString());
            }
            else
            {
                word.Append(character);
            }
        }

        FlushWord();
        var merged = new List<string>(tokens.Count);
        var carry = string.Empty;
        foreach (var token in tokens)
        {
            if (merged.Count > 0 && carry.Length == 0 && IsNoLineStart(token[0]))
            {
                merged[^1] += token;
            }
            else if (token.Length == 1 && IsNoLineEnd(token[0]))
            {
                carry += token;
            }
            else
            {
                merged.Add(carry + token);
                carry = string.Empty;
            }
        }

        if (carry.Length > 0) merged.Add(carry);
        return merged;
    }

    private static bool IsWideCharacter(char character)
        => character is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    private static bool IsNoLineStart(char character)
        => "，。、；：！？）」』】〉》”’％…·,.;:!?)%".Contains(character);

    private static bool IsNoLineEnd(char character)
        => "（「『【〈《“‘(".Contains(character);

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
        DactTheme.PushStyleColor(ImGuiCol.ChildBg, Navy);
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        DactTheme.PushStyleColor(ImGuiCol.Separator, new Vector4(0.34f, 0.29f, 0.18f, 0.70f));
        DactTheme.PushStyleColor(ImGuiCol.FrameBg, NavyRaised);
        DactTheme.PushStyleColor(ImGuiCol.FrameBgHovered, NavyHover);
        DactTheme.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.17f, 0.24f, 1));
        DactTheme.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.25f, 0.34f, 1));
        DactTheme.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.30f, 0.37f, 1));
        DactTheme.PushStyleColor(ImGuiCol.CheckMark, IceBlue);
        DactTheme.PushStyleColor(ImGuiCol.Header, new Vector4(0.22f, 0.25f, 0.28f, 1));
        DactTheme.PushStyleColor(ImGuiCol.HeaderHovered, NavyHover);
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
