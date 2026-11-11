using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 使用帮助 in the 1.0 layout (tools/ui-redesign/help.js): a 48 px header, a 196 px sidebar with
// search and the nine pages, and a scrolling content column of cards. Sentences that point at a
// control name its 1.0 location; the usage notice is the user's own wording and stays verbatim.
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

    private readonly UiText text;
    private readonly WindowDragController headerDrag = new();
    private string? scrolledView;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly string imageDirectory;
    private readonly Func<string, ISharedImmediateTexture> loadImage;
    private readonly PluginLogger logger;
    private readonly Action openRuntimeStatus;
    private readonly Action openLogDirectory;
    private readonly Action openThirdPartyNotice;
    private readonly Dictionary<string, ISharedImmediateTexture?> images = new(StringComparer.Ordinal);
    private HelpPage selectedPage;
    private string searchDraft = string.Empty;
    private string searchQuery = string.Empty;
    private string? pendingSectionId;
    private bool outerFrameStylePushed;
    private bool searchFieldActive;
    private string? copiedCommand;
    private double copiedAt;

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
        Size = new Vector2(1100, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860, 580),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    private readonly WindowFade fade = new();

    public override void OnOpen() => fade.Opened();

    public override void PreDraw()
    {
        headerDrag.PrepareNextWindow();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        // The header, sidebar and content run to the window edges and pad themselves.
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
        WindowName = text.Get("使用帮助###DalamudActCompatHelpWindow", "Help###DalamudActCompatHelpWindow");
        ControlCenterWindow.PushTheme();
        try
        {
            DactUi.BeginFrame();
            DactTheme.DrawGameWindow();
            var s = DactUi.S;
            var min = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            var draw = ImGui.GetWindowDrawList();
            var line = Math.Max(1, s);
            DrawHeader(min, size.X, 48 * s);
            draw.AddRectFilled(new Vector2(min.X, min.Y + 48 * s), new Vector2(min.X + size.X, min.Y + 48 * s + line), DactUi.U32(DactUi.DividerColor));

            var bodyTop = min.Y + 48 * s + line;
            // 艾欧泽亚: the pages, the topic list and their divider stop above the frame's bottom rim.
            var bodyHeight = min.Y + size.Y - DactTheme.GameFrameBottomInset - bodyTop;
            ImGui.SetCursorScreenPos(new Vector2(min.X, bodyTop));
            DrawSidebar(bodyHeight);
            var separatorX = min.X + 196 * s;
            draw.AddRectFilled(new Vector2(separatorX, bodyTop), new Vector2(separatorX + line, bodyTop + bodyHeight), DactUi.U32(DactUi.DividerColor));

            ImGui.SetCursorScreenPos(new Vector2(separatorX + line, bodyTop));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 18) * s);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8 * s);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            if (ImGui.BeginChild("help-document-content", new Vector2(-1, bodyHeight), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
            {
                // Every way to another page (sidebar, a search result, a topic opened from DACT)
                // starts it at the top. Scrolling had been reset on the sidebar, not on this pane.
                var view = string.IsNullOrWhiteSpace(searchQuery) ? selectedPage.ToString() : "search";
                if (view != scrolledView)
                {
                    ImGui.SetScrollY(0);
                    scrolledView = view;
                }
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 14) * s);
                if (string.IsNullOrWhiteSpace(searchQuery)) DrawSelectedPage();
                else DrawSearchResults();
                ImGui.PopStyleVar();
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);
        }
        finally
        {
            ControlCenterWindow.PopTheme();
        }
    }

    // 全局搜索 and the AI assistant open a help page by its name; unknown names open the first page.
    public void OpenTopic(string page)
    {
        selectedPage = Enum.TryParse<HelpPage>(page, out var parsed) ? parsed : HelpPage.UsageNotice;
        ClearSearch();
        IsOpen = true;
    }

    public void OpenCommands()
    {
        selectedPage = HelpPage.Commands;
        ClearSearch();
        IsOpen = true;
    }

    private void ClearSearch()
    {
        searchDraft = string.Empty;
        searchQuery = string.Empty;
        pendingSectionId = null;
    }

    // ---- Frame

    private void DrawHeader(Vector2 min, float width, float height)
    {
        var s = DactUi.S;
        var draw = ImGui.GetWindowDrawList();
        var logo = new Vector2(min.X + 14 * s, min.Y + (height - 26 * s) * .5f);
        draw.AddImageRounded(logoTexture.GetWrapOrEmpty().Handle, logo, logo + new Vector2(26 * s), Vector2.Zero, Vector2.One,
            ImGui.GetColorU32(Vector4.One), 6 * s);
        var title = text.Get("使用帮助", "Help");
        var x = logo.X + 26 * s + 10 * s;
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, title, DactTheme.Palette.Text);
        x += DactUi.TextWidth(title) + 10 * s;
        var subtitle = text.Get("使用文档", "User guide");
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, subtitle, DactUi.HintColor, DactFonts.Small);
        var dragLeft = x + DactUi.TextWidth(subtitle, DactFonts.Small) + 8 * s;

        var right = min.X + width - 10 * s - 28 * s;
        ImGui.SetCursorScreenPos(new Vector2(right, min.Y + (height - 28 * s) * .5f));
        if (DactUi.IconButton("##help-close", "close", text.Get("关闭", "Close"))) fade.RequestClose();
        var version = ControlCenterWindow.FormatVersionLabel(typeof(HelpWindow).Assembly.GetName().Version);
        var versionX = right - 10 * s - DactUi.TextWidth(version, DactFonts.Small);
        DactUi.DrawText(draw, new Vector2(versionX, min.Y), height, version, DactTheme.Palette.Muted, DactFonts.Small);

        // Everything between the subtitle and the version drags the window.
        if (versionX - 8 * s > dragLeft)
        {
            ImGui.SetCursorScreenPos(new Vector2(dragLeft, min.Y));
            ImGui.InvisibleButton("##help-drag", new Vector2(versionX - 8 * s - dragLeft, height));
            headerDrag.HandleItem();
        }
    }

    private static readonly (HelpPage Page, string Icon)?[] Navigation =
    [
        (HelpPage.UsageNotice, "shield"), (HelpPage.GettingStarted, "play"),
        null,
        (HelpPage.CombatMeter, "meter"), (HelpPage.Overlays, "layers"), (HelpPage.Extensions, "plug"), (HelpPage.Account, "cloud"),
        null,
        (HelpPage.Troubleshooting, "wrench"), (HelpPage.Commands, "terminal"), (HelpPage.Copyright, "copyright"),
    ];

    private void DrawSidebar(float height)
    {
        var s = DactUi.S;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 12) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2) * s);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.BeginChild("help-document-navigation", new Vector2(196 * s, height), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysUseWindowPadding);
        var width = ImGui.GetContentRegionAvail().X;
        DrawSearchField(width);
        ImGui.Dummy(new Vector2(width, 6 * s));

        var draw = ImGui.GetWindowDrawList();
        var itemHeight = 34 * s;
        var origin = ImGui.GetCursorScreenPos();
        var offsets = new float[Navigation.Length];
        var y = 0f;
        float? activeOffset = null;
        var searching = !string.IsNullOrWhiteSpace(searchQuery);
        for (var i = 0; i < Navigation.Length; i++)
        {
            offsets[i] = y;
            if (!searching && Navigation[i]?.Page == selectedPage) activeOffset = y;
            y += (Navigation[i] is null ? 13 * s : itemHeight) + 2 * s;
        }
        // The selection slides between pages; search results select none.
        if (activeOffset is { } offset)
        {
            var highlight = DactMotion.Float("##help-nav-highlight", offset, DactMotion.Normal);
            draw.AddRectFilled(origin + new Vector2(0, highlight), origin + new Vector2(width, highlight + itemHeight), DactUi.U32(DactTheme.Palette.Hover), 7 * s);
        }

        for (var i = 0; i < Navigation.Length; i++)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(0, offsets[i]));
            if (Navigation[i] is not { } entry)
            {
                ImGui.Dummy(new Vector2(width, 13 * s));
                draw.AddRectFilled(origin + new Vector2(0, offsets[i] + 6 * s), origin + new Vector2(width, offsets[i] + 6 * s + Math.Max(1, s)),
                    DactUi.U32(DactUi.DividerColor));
                continue;
            }
            var active = !searching && entry.Page == selectedPage;
            if (ImGui.InvisibleButton($"##help-nav-{i}", new Vector2(width, itemHeight)))
            {
                selectedPage = entry.Page;
                ClearSearch();
            }
            var id = ImGuiP.GetItemID();
            var hovered = ImGui.IsItemHovered();
            var min = ImGui.GetItemRectMin();
            var hover = DactMotion.Float(id, hovered && !active ? 1 : 0, DactMotion.Fast);
            if (hover > 0) draw.AddRectFilled(min, min + new Vector2(width, itemHeight), DactUi.U32(DactTheme.Palette.Hover with { W = .55f * hover }), 7 * s);
            var iconColor = DactMotion.Color(id + 1, active ? DactTheme.Palette.Accent : hovered ? DactTheme.Palette.Text : DactTheme.Palette.Muted, DactMotion.Fast);
            var labelColor = DactMotion.Color(id + 2, active || hovered ? DactTheme.Palette.Text : DactTheme.Palette.Muted, DactMotion.Fast);
            DactIcons.Draw(draw, entry.Icon, min + new Vector2(10 * s, (itemHeight - 16 * s) * .5f), 16 * s, iconColor);
            var label = GetPageLabel(entry.Page);
            var shown = DactUi.Fit(label, width - 46 * s);
            if (shown != label && hovered) DactUi.Tooltip(label);
            DactUi.DrawText(draw, min + new Vector2(36 * s, 0), itemHeight, shown, labelColor);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    // Enter or the magnifier searches; with results showing, the × clears them and returns to the page.
    private void DrawSearchField(float width)
    {
        var s = DactUi.S;
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var key = ImGui.GetID("##help-search");
        // The border turns accent while typing (last frame's state, as ImGui reports it after the item).
        var border = DactMotion.Color(key + 1, searchFieldActive ? DactTheme.Palette.Accent with { W = .8f } : DactUi.FieldBorder, DactMotion.Fast);
        draw.AddRectFilled(min, min + new Vector2(width, 30 * s), DactUi.U32(DactUi.Field), 6 * s);
        draw.AddRect(min, min + new Vector2(width, 30 * s), DactUi.U32(border), 6 * s, ImDrawFlags.None, Math.Max(1, s));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10 * s, (30 * s - ImGui.GetTextLineHeight()) * .5f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, DactTheme.Palette.Muted);
        ImGui.SetNextItemWidth(width - 30 * s);
        var submitted = ImGui.InputTextWithHint("##help-search", text.Get("搜索功能、问题或命令", "Search features, problems or commands"),
            ref searchDraft, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        searchFieldActive = ImGui.IsItemActive();
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
        ImGui.SameLine(0, 0);
        ImGui.SetCursorScreenPos(min + new Vector2(width - 29 * s, 2 * s));
        var showingResults = !string.IsNullOrWhiteSpace(searchQuery);
        if (ImGui.InvisibleButton("##help-search-action", new Vector2(26 * s)))
        {
            if (showingResults) ClearSearch();
            else submitted = true;
        }
        var ink = DactMotion.Color(ImGuiP.GetItemID(), ImGui.IsItemHovered() ? DactTheme.Palette.Text : DactTheme.Palette.Muted, DactMotion.Fast);
        DactIcons.Draw(draw, showingResults ? "close" : "search", ImGui.GetItemRectMin() + new Vector2(6 * s), 14 * s, ink);
        if (submitted)
        {
            searchQuery = searchDraft.Trim();
            pendingSectionId = null;
        }
        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(new Vector2(width, 30 * s));
    }

    private string GetPageLabel(HelpPage page) => page switch
    {
        HelpPage.UsageNotice => text.Get("使用须知", "Usage notice"),
        HelpPage.GettingStarted => text.Get("快速上手", "Getting started"),
        HelpPage.CombatMeter => text.Get("战斗统计", "Combat Meter"),
        HelpPage.Overlays => text.Get("悬浮窗", "Overlays"),
        HelpPage.Extensions => text.Get("扩展与权限", "Extensions"),
        HelpPage.Account => text.Get("账号与云同步", "Account & Cloud"),
        HelpPage.Troubleshooting => text.Get("遇到问题", "Troubleshooting"),
        HelpPage.Commands => text.Get("宏指令", "Commands"),
        HelpPage.Copyright => text.Get("版权声明", "Copyright"),
        _ => page.ToString(),
    };

    private void DrawSelectedPage()
    {
        switch (selectedPage)
        {
            case HelpPage.UsageNotice: DrawUsageNotice(); break;
            case HelpPage.GettingStarted: DrawGettingStarted(); break;
            case HelpPage.CombatMeter: DrawCombatMeter(); break;
            case HelpPage.Overlays: DrawOverlays(); break;
            case HelpPage.Extensions: DrawExtensions(); break;
            case HelpPage.Account: DrawAccount(); break;
            case HelpPage.Troubleshooting: DrawTroubleshooting(); break;
            case HelpPage.Commands: DrawCommands(); break;
            case HelpPage.Copyright: DrawCopyright(); break;
        }
    }

    private void DrawSearchResults()
    {
        var s = DactUi.S;
        var results = CreateSearchEntries().Where(entry => MatchesSearch(searchQuery, entry.SearchText)).ToArray();
        var clear = text.Get("清除搜索", "Clear search");
        DactUi.PageHeader(text.Get("搜索结果", "Search results"),
            text.Format($"找到 {results.Length} 条相关说明。点击结果可跳转到对应章节。", $"Found {results.Length} relevant entries. Select one to jump to its section."),
            DactUi.ButtonWidth(clear, ButtonKind.Ghost),
            () =>
            {
                if (DactUi.Button(clear + "##help-clear-search", ButtonKind.Ghost)) ClearSearch();
            });
        if (results.Length == 0)
        {
            DrawCard("help-search-empty", text.Get("没有找到", "No results"), () =>
                Paragraph(text.Get(
                    "试试更短的关键词，例如“没数据”“权限”“重启”“悬浮窗”或命令里的单词。",
                    "Try a shorter keyword such as no data, permission, restart, overlay, or a word from a command.")));
            return;
        }

        var open = text.Get("打开这一节", "Open this section");
        foreach (var entry in results)
        {
            DactUi.BeginCard($"help-search-{entry.SectionId}", null);
            var page = GetPageLabel(entry.Page);
            DactUi.CardTitleRow(entry.Title,
                () => DactUi.Pill(page, PillTone.Off, dot: false),
                DactUi.ButtonWidth(open, ButtonKind.Ghost),
                () =>
                {
                    if (DactUi.Button(open + $"##open-{entry.SectionId}", ButtonKind.Ghost))
                    {
                        selectedPage = entry.Page;
                        ClearSearch();
                        pendingSectionId = entry.SectionId;
                    }
                });
            Paragraph(entry.Summary, DactTheme.Palette.Muted);
            DactUi.EndCard();
        }
        _ = s;
    }

    internal static bool MatchesSearch(string query, string content)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && tokens.All(token => content.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Pages

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
        DrawCard("help-notice-discouraged", text.Get("项目本身不鼓励", "This project does not encourage"), () => Bullets(
            text.Get("PVP 信息优势", "Information advantages in PvP"),
            text.Get("影响其他玩家正常游戏", "Interfering with other players' normal gameplay"),
            text.Get("利用解析数据骚扰其他玩家", "Using parsed data to harass other players"),
            text.Get("在公开环境跳脸", "Flaunting plugins in public spaces"),
            text.Get("使用插件进行不当行为", "Using plugins for inappropriate conduct")));
        DrawCard("help-notice-boundary", text.Get("特别提醒", "Important reminder"), () =>
        {
            using (DactUi.Gap(6))
            {
                Paragraph(text.Get("不要去绿玩面前跳脸。", "Do not flaunt plugins in front of players who do not use them."), DactUi.Danger);
                Paragraph(text.Get("你自己开插件是一回事。", "Using a plugin privately is one thing."));
                Paragraph(text.Get("跑到别人面前：", "Going up to someone else and saying:"));
                Paragraph(text.Get("“你看我这个插件多牛逼！”", "“Look how amazing my plugin is!”"), DactTheme.Palette.Accent);
                Paragraph(text.Get(
                    "然后把截图、悬浮窗、DPS、第三方工具全甩别人脸上，是另一回事。",
                    "Then pushing screenshots, overlays, DPS, and third-party tools in their face is another matter."));
                Paragraph(text.Get("一经发现立刻踢出！", "If discovered, you will be removed immediately."), DactUi.Danger);
                Paragraph(text.Get("请保持最基本的边界感。", "Please maintain basic boundaries and respect for others."));
            }
        });
    }

    private void DrawGettingStarted()
    {
        DrawPageHeader(
            text.Get("快速上手", "Getting started"),
            text.Get("第一次使用？按顺序完成下面四步。", "New here? Follow these four steps in order."));
        DrawCard("help-start-sign-in", StepTitle(1, text.Get("登录 DACT 账号", "Sign in to your DACT account")), () =>
        {
            Paragraph(text.Get(
                "点游戏画面上的 ACT 按钮或输入 /actcompat 打开控制中心。没有账号就点「创建账号」，有账号直接登录；勾选「记住登录状态」后下次会自动登录。",
                "Open the control center with the ACT button on screen or /actcompat. Choose Create account if you are new, otherwise sign in. Keep Remember sign-in checked to sign in automatically next time."));
            Image("account-login", text.Get("控制中心的登录界面", "The control center sign-in screen"));
        });
        DrawCard("help-start-discord", StepTitle(2, text.Get("绑定 Discord", "Link Discord")), () =>
        {
            Paragraph(text.Get(
                "第一次登录后需要绑定 Discord 才能继续，按界面上的三步操作即可：",
                "After your first sign-in, DACT asks you to link Discord before continuing. Follow the three steps on screen:"));
            Steps(
                text.Get("点「复制绑定码并前往 Discord」，加入 DACT 的 Discord 服务器。", "Click Copy code and open Discord, then join the DACT Discord server."),
                text.Get("在成员列表的「Bot」分组找到 DACT，右键选择「发消息」。", "Find DACT under Bot in the member list, right-click it and choose Message."),
                text.Get("私聊输入 /dact，选择绑定账号并粘贴绑定码。回到游戏后会自动进入。",
                    "In the direct message, type /dact, choose to link your account and paste the code. DACT continues on its own once you return to the game."));
            Image("account-discord", text.Get("绑定 Discord", "Linking Discord"));
            Callout(CalloutKind.Tip, text.Get(
                "绑定后忘记密码或换电脑，都能通过 Discord 找回账号和云备份。一个 Discord 最多绑定 3 个 DACT 账号。",
                "Once linked, you can recover your account and cloud backups through Discord after a forgotten password or on a new PC. One Discord account can link up to 3 DACT accounts."));
        });
        DrawCard("help-start-parser", StepTitle(3, text.Get("确认解析器在运行", "Check that the parser is running")), () =>
        {
            Paragraph(text.Get(
                "打开「主页」，「解析器」状态卡和侧栏底部显示「运行中」就说明一切正常。请保持基础设置里的「启用解析」和「自动启动解析器」开启。",
                "On Home, the Parser card and the bottom of the sidebar show Running when everything is fine. Keep Enable parsing and Auto start parser on under Basics."));
            Image("control-overview", text.Get("主页：解析器状态、快捷入口和基础设置", "Home: parser status, shortcuts and basic settings"));
        });
        DrawCard("help-start-meter", StepTitle(4, text.Get("打一下木人", "Hit a striking dummy")), () =>
        {
            Paragraph(text.Get(
                "对木人或副本里的敌人造成几次伤害，战斗统计就会显示你和队友。右键 ACT 按钮可以打开或关闭战斗统计。",
                "Deal a few hits to a striking dummy or an enemy in a duty and the meter fills with you and your party. Right-click the ACT button to show or hide the meter."));
            Image("meter", text.Get("战斗统计（经典榜）", "The Combat Meter (Classic)"));
            Callout(CalloutKind.Tip, text.Get(
                "没有数据？看「遇到问题」。控制中心右上角的 ? 随时可以打开这份帮助。",
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
            Bullets(
                text.Get("在「战斗统计」页打开「显示战斗统计」；找不到窗口时点「定位到战斗统计」，或输入 /actcompat meter。",
                    "On Combat Meter, turn on Show Combat Meter. If you lose the window, click Locate Combat Meter or type /actcompat meter."),
                text.Get("点「自定义…」打开自定义布局，取消「锁定」后可以拖动、缩放，摆好再锁定。开启「锁定时鼠标穿透」后，点击会穿过统计窗传给游戏。",
                    "Click Customize… to open the layout editor, unlock to move and resize the meter, then lock it again. With Click-through when locked, clicks pass through to the game."),
                text.Get("「脱战隐藏」只是把窗口藏起来，统计仍在继续。", "Hide out of combat only hides the window; the meter keeps counting."),
                text.Get("打开「脱战隐藏」后，旁边可以选脱战后多久隐藏，例如「30 秒后」：脱战后再显示一会儿，方便看完上一场。悬浮窗页的「隐藏时机」是同样的设置。",
                    "With Hide out of combat on, the choice next to it sets how long after combat the window hides, for example After 30 s, so the last pull can still be read. Hide timing on the Overlays page works the same way."));
            Image("control-meter", text.Get("战斗统计页", "The Combat Meter page"));
        });
        DrawCard("help-meter-templates", text.Get("榜单模板与显示的数据", "Templates and columns"), () => Bullets(
            text.Get("「经典榜」「横版」「职能分栏」三选一；点「自定义…」可以调字号、透明度、背景色，以及要显示的槽位。",
                "Choose Classic, Horizontal or Role split, then click Customize… to change font size, opacity, background colour and the slots shown."),
            text.Get("经典榜标题栏可以在 DPS 榜 / HPS 榜、8 人本 / 24 人本之间切换。",
                "The Classic header switches between DPS and HPS ranking and between 8-player and 24-player modes."),
            text.Get("「显示范围」决定显示哪些玩家：按榜单设置，或跟随主页的「游戏解析范围」。",
                "Display scope decides which players appear: use the meter settings, or follow the Parse scope on Home."),
            text.Get("「玩家 ID 显示」可以遮盖名字，只影响界面，不会修改日志。",
                "Player identity can mask names. It only changes the display, never the logs.")));
        DrawCard("help-meter-metrics", text.Get("数值是怎么算的", "How the numbers are calculated"), () => Definitions(132,
            ("DPS", text.Get("按你自己的出手时间计算，晚进场或死亡后会比整场口径高。", "Uses your own active time, so it reads higher after a late start or a death.")),
            ("EncDPS", text.Get("按整场战斗时长计算，适合比较同一场里的队友。", "Uses the whole encounter length; best for comparing party members.")),
            (text.Get("rDPS（预估）", "rDPS (estimated)"), text.Get(
                "去掉别人给你的团辅、加回你给队友的团辅。这是本地实时估算，和 FFLogs 会有差异。",
                "Removes buffs others gave you and adds back buffs you gave. It is a live local estimate and can differ from FFLogs.")),
            ("HPS", text.Get("按开怪到现在的完整时间计算，不再治疗时会慢慢下降。", "Uses the full time since the pull, so it drifts down while you are not healing.")),
            (text.Get("暴击 / 直击 / 直暴", "CRIT / DH / CDH"), text.Get(
                "本场命中里暴击、直击、同时暴击直击的比例，不是角色面板数值。样本少时跳动是正常的。",
                "The share of this fight's hits that crit, direct hit, or both. These are not character sheet values, and small samples move a lot.")),
            ("--", text.Get("还没有有效数值，不等于 0。", "No valid value yet. It does not mean zero."))));
        DrawCard("help-meter-reset", text.Get("统计重置与历史", "Resets and history"), () => Bullets(
            text.Get("「DACT默认」：副本里普通脱战和转阶段继续累计，团灭或离开副本才结算。",
                "DACT default: inside a duty, ordinary combat breaks and phase changes keep accumulating; a wipe or leaving the duty ends the fight."),
            text.Get("「脱战指定秒数后重置」：脱战后倒计时 0–3600 秒再清空；倒计时内重新进战会取消。",
                "Reset after combat: clears 0–3600 seconds after combat ends. Re-entering combat cancels the countdown."),
            text.Get("「重置当前战斗…」立即清空本场，不会删除历史和原始日志。侧栏「战斗记录」或输入 /actcompat history 查看近期战斗。",
                "Reset current encounter… clears this fight at once without deleting history or raw logs. Open Encounter history in the sidebar or type /actcompat history for recent fights.")));
        DrawCard("help-meter-fflogs", text.Get("FFLogs DPS Parse 预估", "FFLogs DPS Parse estimate"), () =>
        {
            Steps(
                text.Get("在「战斗统计」页的 FFLogs 卡片里开启「启用 FFLogs 在线估算」。", "In the FFLogs card on Combat Meter, turn on Enable FFLogs online estimate."),
                text.Get("点「创建 / 管理 API Client」，登录 FFLogs 创建免费 Client；没有网站时回调地址填 https://example.com。",
                    "Click Create / manage API client, sign in to FFLogs and create a free client. If you have no website, use https://example.com as the redirect URL."),
                text.Get("把 Client ID 和 Client Secret 填进对应输入框，点「测试并刷新」。", "Paste the Client ID and Client Secret into their fields, then click Test and refresh."));
            Callout(CalloutKind.Warning, text.Get("Client Secret 只保存在本机，不要截图或发给别人。", "The Client Secret is stored only on this PC. Never screenshot or share it."));
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
            Bullets(
                text.Get("DACT 自带 Cactbot。「文字提醒」和「时间轴」是两个独立窗口，可以同时打开；旧版「文字提醒 + 时间轴」组合窗和它们二选一。",
                    "Cactbot comes with DACT. Alerts and Timeline are separate windows that can be open together; the legacy combined window replaces both."),
                text.Get("「悬浮窗」页顶部的 Cactbot 条里，「Cactbot 设置」可以改提醒语言、播报方式和自定义脚本目录；「安装 / 更新」用于更新或换用自己的 Cactbot 包。",
                    "In the Cactbot strip at the top of Overlays, Cactbot settings holds alert language, speech options and your custom script directory. Install / update refreshes Cactbot or switches to your own package."));
            Image("control-overlays", text.Get("悬浮窗页：左侧选择悬浮窗，右侧调整它的设置", "Overlays: pick an overlay on the left and adjust it on the right"));
        });
        DrawCard("help-overlay-add", text.Get("添加网页悬浮窗", "Adding a web overlay"), () => Steps(
            text.Get("点页面右上角「添加悬浮窗」。", "Click Add overlay at the top right of the page."),
            text.Get("选择模板，或粘贴完整的 http / https / file 地址。只添加你信任的网页。",
                "Pick a template or paste a full http, https or file address. Add only pages you trust."),
            text.Get("在右侧「常规」里设置「启动时自动打开」「脱战隐藏」等选项。",
                "Under General on the right, set options such as Open on startup and Hide out of combat.")));
        DrawCard("help-overlay-edit", text.Get("移动、缩放与鼠标穿透", "Moving, resizing and click-through"), () => Bullets(
            text.Get("点「编辑位置和大小」后拖动窗口或右下角，完成后点「完成位置编辑」。编辑时会暂时关闭穿透与锁定，完成后恢复。",
                "Click Edit position and size, then drag the window or its lower-right corner. Click Finish position editing when done. Click-through and lock pause while you edit and come back afterwards."),
            text.Get("鼠标穿透开启时点击会传给游戏，网页本身点不动；需要操作网页时先关闭穿透。",
                "With click-through on, clicks go to the game and the page cannot be used. Turn it off when you need to interact with the page."),
            text.Get("字太小就调「页面缩放」，内容被截断就把窗口拉大。", "If text is too small, raise Page zoom. If content is cut off, make the window bigger.")));
        DrawCard("help-overlay-no-data", text.Get("有画面但没有数据", "The page shows but has no data"), () =>
            Paragraph(text.Get(
                "先确认侧栏底部显示「解析器运行中」，再到该悬浮窗的「来源」标签，在「连接高级设置」里点「重新检测」。部分网页还需要联网或单独设置。",
                "First make sure the bottom of the sidebar shows Parser running, then open that overlay's Source tab and click Detect again under Advanced connection settings. Some pages also need internet access or their own settings.")));
    }

    private void DrawExtensions()
    {
        DrawPageHeader(
            text.Get("扩展与权限", "Extensions and permissions"),
            text.Get("触发器、语音、邮差等 ACT 扩展的启用、配置和权限。", "Enable, configure and grant permissions to ACT extensions such as triggers, speech and PostNamazu."));
        DrawCard("help-extension-bundled", text.Get("自带的扩展", "Bundled extensions"), () =>
        {
            Definitions(170,
                ("Triggernometry", text.Get("触发器：根据战斗日志播报、计时和绘图。", "Triggers: callouts, timers and drawings driven by combat logs.")),
                (text.Get("鲶鱼精邮差 / PostNamazu", "PostNamazu"), text.Get("让触发器执行游戏指令、标点和地标。", "Lets triggers run game commands, markers and waymarks.")),
                ("ACT.FoxTTS", text.Get("把文字提醒读出来。", "Reads text alerts aloud.")),
                (text.Get("银山雀儿 / SilverDasher", "SilverDasher"), text.Get(
                    "狩猎、临危受命和跨区提醒。默认关闭，需要时再打开。", "Hunt, FATE and world-visit alerts. Off by default; turn it on if you need it.")),
                (text.Get("抹茶 / Cafe.Matcha", "Cafe.Matcha"), text.Get("排本、临危受命和过图提醒。", "Duty finder, FATE and zone-change alerts.")));
            Image("control-extensions", text.Get("扩展页", "The Extensions page"));
        });
        DrawCard("help-extension-enable", text.Get("启用和打开配置", "Enabling and configuring"), () =>
        {
            Steps(
                text.Get("在「扩展」页打开扩展那一行的开关。", "On Extensions, turn on the switch in the extension's row."),
                text.Get("点同一行的「打开配置」。导入触发器、选择语音等都在扩展自己的窗口里完成。",
                    "Click Open configuration on the same row. Importing triggers, choosing a voice and similar settings happen in the extension's own window."));
            Callout(CalloutKind.Tip, text.Get(
                "启用扩展或修改权限后，DACT 会自动重启这个扩展，不用重启游戏。",
                "When you enable an extension or change its permissions, DACT restarts that extension for you. No game restart is needed."));
        });
        DrawCard("help-extension-permissions", text.Get("权限", "Permissions"), () =>
        {
            Paragraph(text.Get(
                "在「扩展」页点扩展那一行的「权限」，可以按扩展逐项开放能力。只开实际要用的：",
                "On Extensions, click Permissions in an extension's row to grant capabilities one at a time. Grant only what you use:"));
            Definitions(132,
                (text.Get("网络请求", "Network requests"), text.Get("在线更新、远程触发器仓库和语音服务。", "Online updates, remote trigger repositories and speech services.")),
                (text.Get("发送游戏指令", "Send game commands"), text.Get("邮差的指令、标点和地标。", "PostNamazu commands, markers and waymarks.")),
                (text.Get("访问游戏原生内存", "Access native game memory"), text.Get("银山雀儿和邮差的部分原版功能。", "Some original SilverDasher and PostNamazu features.")),
                (text.Get("运行高风险脚本", "Run high-risk scripts"), text.Get("触发器里的高级脚本。", "Advanced scripts inside triggers.")),
                (text.Get("写入文件", "Write files"), text.Get("扩展缓存，或导出到你选择的文件。", "Extension caches, or exports to a file you choose.")));
            Callout(CalloutKind.Warning, text.Get(
                "只安装来源可信的扩展。权限限制的是 DACT 提供的接口，挡不住 DLL 直接调用 Windows。",
                "Install only extensions from sources you trust. Permissions limit DACT's own interfaces; they cannot stop a DLL from calling Windows directly."));
            if (DactUi.Button(text.Get("查看内置第三方 DLL 的作者与来源", "View bundled DLL authors and sources") + "##help-third-party")) openThirdPartyNotice();
        });
        DrawCard("help-extension-install", text.Get("安装其他 ACT 插件", "Installing other ACT plugins"), () =>
            Paragraph(text.Get(
                "点「扩展」页右上角「安装 DLL / ZIP」，或输入 /actcompat install \"路径\"。DACT 会先检查文件并列出它需要的权限，你同意后它才会运行。",
                "Click Install DLL / ZIP at the top right of Extensions or type /actcompat install \"path\". DACT inspects the file first and lists the permissions it needs; the plugin only runs after you agree.")));
        DrawCard("help-extension-update", text.Get("更新后还是旧版本", "Still the old version after an update"), () =>
            Paragraph(text.Get(
                "外部工具箱更新扩展后，点侧栏底部「运行状态」（或输入 /actcompat status），点「重启共享 Host」，相当于以前的“重启 ACT”，不用退出游戏。抹茶点「重启抹茶 Host」，自己安装的插件点「重启通用 Host」。",
                "After an external toolbox updates an extension, click Runtime status at the bottom of the sidebar (or type /actcompat status) and click Restart shared Host. This replaces the old \"restart ACT\"; you do not need to exit the game. Use Restart Matcha Host for Matcha and Restart generic Host for plugins you installed yourself.")));
    }

    private void DrawAccount()
    {
        DrawPageHeader(
            text.Get("账号与云同步", "Account and cloud sync"),
            text.Get("账号、云备份、找回账号、好友、皮肤和其他设置。", "Account, cloud backups, account recovery, friends, skins and other settings."));
        DrawCard("help-account-cloud", text.Get("云同步", "Cloud sync"), () =>
        {
            Bullets(
                text.Get("打开「打开游戏后自动同步配置」后，每次登录会自动检查并备份一次；配置没变就不上传。云端保留最近两份。",
                    "With Sync configuration after game launch on, DACT checks and backs up once per sign-in and skips unchanged settings. The cloud keeps the two most recent backups."),
                text.Get("需要恢复时先选择云端版本、预览恢复内容，再确认恢复；恢复前的配置会被保留，可以回滚。",
                    "To restore, choose a cloud version, preview what will change, then confirm. Your previous settings are kept so you can roll back."));
            Image("control-cloud", text.Get("账号与云同步页", "The Account & Cloud page"));
        });
        DrawCard("help-account-account", text.Get("账号", "Account"), () =>
            Bullets(
                text.Get("在「账号与云同步」页查看账号名和登录有效期，退出登录或修改密码。",
                    "On Account & Cloud, see your account name and how long you stay signed in, sign out or change your password."),
                text.Get("一个账号最多同时登录 5 台电脑。在第 6 台登录时，会按电脑名列出已登录的设备，选一台登出或全部登出后继续；被登出的电脑下次要重新输入密码。",
                    "An account can be signed in on up to 5 PCs at once. Signing in on a sixth lists the signed-in devices by computer name; sign one or all of them out to continue. Signed-out PCs ask for the password next time.")));
        DrawCard("help-account-recovery", text.Get("找回账号", "Recovering your account"), () =>
            Paragraph(text.Get(
                "账号绑定 Discord 后，忘记密码或换电脑时，在 Discord 私聊 DACT 输入 /dact，按菜单找回账号和云备份。",
                "Once your account is linked to Discord, you can recover it and its cloud backups after a forgotten password or on a new PC: message DACT on Discord, type /dact and follow the menu.")));
        DrawCard("help-account-friends", text.Get("好友", "Friends"), () =>
            Paragraph(text.Get(
                "点侧栏「好友」打开好友页，可以添加好友、聊天；右键好友可以设置备注，备注随账号在各设备同步。有未读消息时「好友」旁会显示数字。",
                "Click Friends in the sidebar to add friends and chat. Right-click a friend to set a note; notes sync across your devices with your account. The unread count shows next to Friends.")));
        DrawCard("help-account-settings", text.Get("设置", "Settings"), () =>
        {
            Bullets(
                text.Get("外观与皮肤：点「管理皮肤」预览和切换，部分皮肤需要对应的赞助等级。",
                    "Appearance and skins: click Manage skins to preview and switch. Some skins need a matching sponsor tier."),
                text.Get("解析器与诊断：重启解析器、打开详细运行状态或查看三方扩展声明。",
                    "Parser and diagnostics: restart the parser, open detailed runtime status or view the third-party extension notice."),
                text.Get("通用：界面语言和 ACT 快捷按钮；数据与日志：诊断日志、FFLogs 上传日志目录和历史记录上限。",
                    "General: UI language and the ACT quick button. Data and logs: diagnostic logs, the FFLogs upload log folder and the history limit."),
                text.Get("恢复出厂设置在页面最底部，会移走现有数据，只在最后才用。",
                    "Factory reset is at the very bottom. It moves your existing data away, so use it only as a last resort."));
            Image("control-diagnostics", text.Get("设置页", "The Settings page"));
        });
    }

    private void DrawTroubleshooting()
    {
        DrawPageHeader(
            text.Get("遇到问题", "Troubleshooting"),
            text.Get("按顺序检查，大多数问题不用重启游戏。", "Work through these in order; most problems need no game restart."));
        DrawCard("help-troubleshooting-status", StepTitle(1, text.Get("先看运行状态", "Check runtime status first")), () =>
            Paragraph(text.Get(
                "点侧栏底部「运行状态」或输入 /actcompat status，从上往下找第一个不是「运行中 / 已连接」的组件，先处理它；后面的组件通常会跟着恢复。",
                "Click Runtime status at the bottom of the sidebar or type /actcompat status, and find the first component from the top that is not Running or Connected. Fix that one first; the ones below it usually recover too.")));
        DrawCard("help-troubleshooting-restart", StepTitle(2, text.Get("重启对应的组件", "Restart the right component")), () => Definitions(226,
            (text.Get("完全没有战斗数据", "No combat data at all"), text.Get("在「设置 · 解析器与诊断」点「重启解析器」。", "Click Restart parser under Settings · Parser and diagnostics.")),
            (text.Get("触发器、邮差、TTS 或银山雀儿不工作", "Triggers, PostNamazu, TTS or SilverDasher stopped working"), text.Get("在运行状态里点「重启共享 Host」。", "Click Restart shared Host in runtime status.")),
            (text.Get("抹茶不工作", "Matcha stopped working"), text.Get("点「重启抹茶 Host」。", "Click Restart Matcha Host.")),
            (text.Get("自己安装的插件不工作", "A plugin you installed stopped working"), text.Get("点「重启通用 Host」。", "Click Restart generic Host.")),
            (text.Get("DACT 本体刚更新", "DACT itself was just updated"), text.Get(
                "在卫月插件列表里重新加载 DACT；还不行再完整退出游戏和启动器。",
                "Reload DACT in the Dalamud plugin list. If that does not help, fully exit the game and the launcher."))));
        DrawCard("help-troubleshooting-common", text.Get("常见情况", "Common cases"), () => Definitions(226,
            (text.Get("战斗统计没有数据", "The meter shows no data"), text.Get(
                "确认侧栏底部显示「解析器运行中」，关掉「脱战隐藏」，再对木人造成几次伤害。只是打开窗口、进本或选中目标不会产生数据。",
                "Make sure the bottom of the sidebar shows Parser running, turn off Hide out of combat, then hit a dummy a few times. Opening the window, entering a duty or targeting alone produces no data.")),
            (text.Get("某个触发器、TTS 或标点不工作", "One trigger, TTS or marker does not work"), text.Get(
                "扩展要同时满足三点：已启用、所需权限已开、扩展自己的配置里已启用这个功能。",
                "Three things must all be true: the extension is on, it has the permission it needs, and the feature is enabled in its own configuration.")),
            (text.Get("提示“重启 ACT”", "Asked to \"restart ACT\""), text.Get("不用重启游戏，在运行状态里点「重启共享 Host」即可。", "No game restart needed. Click Restart shared Host in runtime status.")),
            (text.Get("通知没有出现", "Notifications do not appear"), text.Get(
                "游戏在前台时用游戏内通知，切到其他程序时用 Windows 通知；请检查 Windows 的专注助手设置。",
                "DACT uses in-game notifications while the game is in front and Windows notifications otherwise. Check Windows Focus Assist.")),
            (text.Get("本地 DPS 和 FFLogs 不一样", "Local DPS differs from FFLogs"), text.Get(
                "两边计算方式不同，小差异是正常的。差得很多时保留当场的 Network 日志一起反馈。",
                "They calculate differently, so small gaps are normal. For large gaps, keep that fight's Network log and report it."))));
        DrawCard("help-troubleshooting-report", StepTitle(3, text.Get("反馈问题", "Reporting a problem")), () =>
        {
            Bullets(
                text.Get("点顶栏的反馈图标「反馈问题或建议」直接发给开发者，可以附上诊断日志和最多 3 张截图；开发者的回复会出现在「好友 · 官方通知」，处理状态在「设置 · 我的反馈」里查看。",
                    "Click Send feedback (the title-bar icon) to send it straight to the developer, with the diagnostic log and up to 3 screenshots; replies arrive in Friends · DACT announcements, and My feedback under Settings shows the status."),
                text.Get("写清 DACT 版本、发生时间、当时在做什么、看到了什么，以及能不能重复出现。",
                    "Include the DACT version, when it happened, what you were doing, what you saw and whether it happens again."),
                text.Get("在「设置」点「复制诊断日志」，再到聊天框按 Ctrl+V，会作为 .txt 文件发出。",
                    "Click Copy diagnostic log under Settings, then press Ctrl+V in the chat to send it as a .txt file."),
                text.Get("战斗数据不对时，再附上「打开 FFLogs 上传日志」目录里当场的 Network 日志。发出前检查角色名和服务器名。",
                    "For wrong combat data, also attach that fight's Network log from Open FFLogs upload logs. Check character and world names before sending."));
            var s = DactUi.S;
            if (DactUi.Button(text.Get("打开运行状态", "Open runtime status") + "##help-status", icon: "pulse")) openRuntimeStatus();
            ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(text.Get("打开诊断日志目录", "Open diagnostic logs") + "##help-logs", icon: "folder")) openLogDirectory();
        });
    }

    private void DrawCommands()
    {
        DrawPageHeader(
            text.Get("宏指令", "Macro commands"),
            text.Get("点「复制」即可放入剪贴板，再粘贴到游戏聊天框或宏中。", "Use Copy to place a command on the clipboard, then paste it into game chat or a macro."));
        DrawCard("help-commands-common", text.Get("常用入口", "Common entry points"), () => Commands(
            ("/actcompat", text.Get("打开或关闭插件控制中心。", "Open or close the plugin control center.")),
            ("/actcompat on", text.Get("始终打开插件控制中心。", "Always open the plugin control center.")),
            ("/actcompat off", text.Get("关闭插件控制中心。", "Close the plugin control center.")),
            ("/actcompat meter", text.Get("打开战斗统计。", "Open Combat Meter.")),
            ("/actcompat simple on|off", text.Get("开启或退出精简模式；即使其他界面已关闭，也可用 off 恢复。",
                "Enable or exit simplified mode; off remains available when every other UI is closed.")),
            ("/actcompat history", text.Get("打开近期战斗。", "Open recent encounters.")),
            ("/actcompat logs", text.Get("打开已保存的日志文件列表。", "Open the saved log-file list.")),
            ("/actcompat status", text.Get("打开解析器与 Host 运行状态。", "Open parser and Host runtime status."))));
        DrawCard("help-commands-overlays", text.Get("悬浮窗", "Overlays"), () => Commands(
            ("/actcompat cactbot", text.Get("打开当前选择的 Cactbot 悬浮窗。", "Open the selected Cactbot overlay.")),
            ("/actcompat overlay", text.Get("打开当前选择的 HTML 悬浮窗；可在后面追加模板名。", "Open the selected HTML overlay; optionally append a template name."))));
        DrawCard("help-commands-maintenance", text.Get("维护与诊断", "Maintenance and diagnostics"), () => Commands(
            ("/actcompat clear", text.Get("立即清空当前战斗显示，不删除历史或原始日志。", "Immediately clear the current encounter display without deleting history or raw logs.")),
            (text.Get("/actcompat install \"<DLL 或 ZIP 路径>\"", "/actcompat install \"<DLL or ZIP path>\""),
                text.Get("静态预检并导入第三方 ACT 扩展；路径含空格时保留引号。", "Preflight and import a third-party ACT extension; keep quotes around paths containing spaces.")),
            ("/actcompat factory-reset", text.Get("打开控制中心；恢复出厂设置仍需在界面中确认。", "Open the control center; factory reset still requires confirmation in the UI.")),
            ("/actcompat host", text.Get("启动兼容 Host 并重启解析器，主要用于诊断。", "Start the compatibility Host and restart the parser, primarily for diagnostics.")),
            ("/actcompat stop", text.Get("停止解析器与共享兼容 Host，主要用于诊断。", "Stop the parser and shared compatibility Host, primarily for diagnostics.")),
            ("/actcompat sample", text.Get("载入本地样例战斗，仅用于开发与界面检查。", "Load a local sample encounter for development and UI checks only."))));
    }

    private void DrawCopyright()
    {
        DrawPageHeader(
            text.Get("版权声明", "Copyright notice"),
            text.Get("本页只声明 DACT 自身的版权与许可。", "This page states copyright and licensing only for DACT itself."));
        DrawCard("help-copyright-project", "DACT", () =>
        {
            // The holder line matches LICENSE.md and the repository name, so the product rename
            // does not change it; the next paragraph links the old name for continuity.
            Paragraph("Copyright © 2026 DalamudActCompat contributors.", DactTheme.Palette.Accent);
            Paragraph(text.Get(
                "DACT（原名 Dalamud ACT Compat）自有源代码以 GNU General Public License version 3（GPL-3.0）发布。你可以在许可证条款下运行、研究、修改与再分发本项目。",
                "DACT (formerly Dalamud ACT Compat) releases its own source code under the GNU General Public License version 3 (GPL-3.0). You may run, study, modify, and redistribute the project under that license."));
            Paragraph(text.Get(
                "本程序按现状提供，不附带任何明示或默示担保，包括适销性或特定用途适用性的担保。完整条款以项目随附的 LICENSE.md 为准。",
                "This program is provided as-is, without any express or implied warranty, including merchantability or fitness for a particular purpose. The bundled LICENSE.md contains the complete terms."));
            Paragraph(text.Get(
                "本声明不主张任何第三方插件、依赖、网页悬浮窗、游戏内容、商标或素材的版权。",
                "This notice claims no copyright over third-party plugins, dependencies, web overlays, game content, trademarks, or assets."), DactTheme.Palette.Muted);
            var s = DactUi.S;
            if (DactUi.Button(text.Get("查看项目源代码", "View project source") + "##help-source", icon: "external"))
                OpenUrl("https://github.com/JackyWilliam/DalamudActCompat");
            ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(text.Get("查看 GPL-3.0 许可证", "View GPL-3.0 license") + "##help-license", icon: "external"))
                OpenUrl("https://github.com/JackyWilliam/DalamudActCompat/blob/main/LICENSE.md");
        });
    }

    // ---- Content blocks (help.js para / bullets / defs / stepsPanel / callout / shot / command)

    private string StepTitle(int number, string title) => text.Format($"第 {number} 步 · {title}", $"Step {number} · {title}");

    private static void DrawPageHeader(string title, string description) => DactUi.PageHeader(title, description);

    private void DrawCard(string id, string title, Action drawContent)
    {
        // A search result jumps here: the card scrolls into view and pulses once.
        if (string.Equals(pendingSectionId, id, StringComparison.Ordinal))
        {
            DactUi.HighlightCard(id);
            pendingSectionId = null;
        }
        DactUi.BeginCard(id, title);
        drawContent();
        DactUi.EndCard();
    }

    private static void Paragraph(string value, Vector4? color = null) => DactUi.Wrapped(value, color);

    private static void Bullets(params string[] items)
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.BeginGroup();
        using (DactUi.Gap(6))
        {
            foreach (var item in items)
            {
                var start = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddCircleFilled(start + new Vector2(6 * s, 9 * s), 2 * s, DactUi.U32(DactTheme.Palette.Muted), 8);
                ImGui.SetCursorScreenPos(start + new Vector2(18 * s, 0));
                DactUi.Wrapped(item, null, DactFonts.Body, width - 18 * s);
            }
        }
        ImGui.EndGroup();
    }

    private static void Steps(params string[] steps) => DactUi.StepsPanel(steps);

    // Symptom (accent) on the left, what to do on the right; the term column is per card.
    private static void Definitions(float termWidth, params (string Term, string Description)[] items)
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        var term = Math.Min(termWidth * s, width * .4f);
        ImGui.BeginGroup();
        using (DactUi.Gap(10))
        {
            foreach (var (name, description) in items)
            {
                var start = ImGui.GetCursorScreenPos();
                ImGui.BeginGroup();
                DactUi.Wrapped(name, DactTheme.Palette.Accent, DactFonts.Body, term);
                ImGui.EndGroup();
                var bottom = ImGui.GetItemRectMax().Y;
                ImGui.SetCursorScreenPos(start + new Vector2(term + 14 * s, 0));
                ImGui.BeginGroup();
                DactUi.Wrapped(description, null, DactFonts.Body, width - term - 14 * s);
                ImGui.EndGroup();
                bottom = Math.Max(bottom, ImGui.GetItemRectMax().Y);
                ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
                ImGui.Dummy(new Vector2(width, 0));
            }
        }
        ImGui.EndGroup();
    }

    // Ice blue for tips, red for cautions (gold stays reserved for card titles and the brand).
    private void Callout(CalloutKind kind, string value)
    {
        var s = DactUi.S;
        var warning = kind == CalloutKind.Warning;
        var tone = warning ? DactUi.Danger : DactTheme.Palette.Accent;
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(23 * s, 10 * s));
        ImGui.BeginGroup();
        using (DactUi.Gap(2))
        {
            DactUi.Text(warning ? text.Get("注意", "Caution") : text.Get("提示", "Tip"), tone, DactFonts.Small);
            DactUi.Wrapped(value, null, DactFonts.Body, width - 35 * s);
        }
        ImGui.EndGroup();
        var bottom = ImGui.GetItemRectMax().Y + 10 * s;
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, new Vector2(start.X + width, bottom), DactUi.U32(warning ? DactUi.DangerSoft : DactUi.AccentSoft), 8 * s);
        draw.AddRectFilled(start + new Vector2(10 * s, 10 * s), new Vector2(start.X + 13 * s, bottom - 10 * s), DactUi.U32(tone), 2 * s);
        draw.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
        ImGui.Dummy(new Vector2(width, 0));
    }

    // A screenshot framed like the design, scaled to the card width, with its caption under it.
    private void Image(string name, string caption)
    {
        if (ResolveImage(name) is not { } texture) return;
        var wrap = texture.GetWrapOrEmpty();
        // Still loading; the card grows once the texture arrives.
        if (wrap.Width <= 1 || wrap.Height <= 1) return;
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.BeginGroup();
        using (DactUi.Gap(6))
        {
            var shown = Math.Min(width, wrap.Width * s);
            var size = new Vector2(shown, shown * wrap.Height / wrap.Width);
            var min = ImGui.GetCursorScreenPos();
            ImGui.Dummy(size);
            var draw = ImGui.GetWindowDrawList();
            draw.AddImageRounded(wrap.Handle, min, min + size, Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One), 6 * s);
            draw.AddRect(min, min + size, DactUi.U32(DactUi.CardBorder), 6 * s, ImDrawFlags.None, Math.Max(1, s));
            DactUi.Hint(caption);
        }
        ImGui.EndGroup();
    }

    // Screenshots exist per UI language; fall back to English, then Simplified Chinese, and
    // cache the decision so the help does not touch the disk every frame.
    private ISharedImmediateTexture? ResolveImage(string name)
    {
        var key = $"{text.Language}/{name}";
        if (images.TryGetValue(key, out var cached)) return cached;
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

    // Each command: the command in an inset chip, what it does under it, 复制 on the right;
    // rows are separated by dividers.
    private void Commands(params (string Command, string Description)[] commands)
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        var copy = text.Get("复制", "Copy");
        var copied = text.Get("已复制", "Copied");
        var buttonWidth = Math.Max(DactUi.ButtonWidth(copy, icon: "copy"), DactUi.ButtonWidth(copied, icon: "check"));
        for (var index = 0; index < commands.Length; index++)
        {
            var (command, description) = commands[index];
            if (index > 0) DactUi.Divider();
            var start = ImGui.GetCursorScreenPos();
            ImGui.BeginGroup();
            using (DactUi.Gap(4))
            {
                var chipWidth = DactUi.TextWidth(command) + 16 * s;
                var chip = ImGui.GetCursorScreenPos();
                ImGui.Dummy(new Vector2(chipWidth, 24 * s));
                var draw = ImGui.GetWindowDrawList();
                draw.AddRectFilled(chip, chip + new Vector2(chipWidth, 24 * s), DactUi.U32(DactUi.Field), 5 * s);
                draw.AddRect(chip, chip + new Vector2(chipWidth, 24 * s), DactUi.U32(DactUi.FieldBorder), 5 * s, ImDrawFlags.None, Math.Max(1, s));
                DactUi.DrawText(draw, chip + new Vector2(8 * s, 0), 24 * s, command, DactTheme.Palette.Accent);
                DactUi.Wrapped(description, DactUi.HintColor, DactFonts.Small, width - buttonWidth - 12 * s);
            }
            ImGui.EndGroup();
            var bottom = ImGui.GetItemRectMax().Y;
            ImGui.SetCursorScreenPos(new Vector2(start.X + width - buttonWidth, start.Y + (bottom - start.Y - 30 * s) * .5f));
            // 已复制 for a moment after a click, so the player sees it worked.
            var justCopied = copiedCommand == command && ImGui.GetTime() - copiedAt < 1.5;
            if (DactUi.Button((justCopied ? copied : copy) + $"##help-copy-{command}", icon: justCopied ? "check" : "copy", width: buttonWidth))
            {
                ImGui.SetClipboardText(command);
                copiedCommand = command;
                copiedAt = ImGui.GetTime();
            }
            ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
            ImGui.Dummy(new Vector2(width, 0));
        }
    }

    // AI 助手 answers usage questions from the same outline the help search uses; the topic id
    // is what OpenTopic takes.
    internal IReadOnlyList<(string Topic, string Title, string Summary)> Outline()
        => CreateSearchEntries().Select(entry => (entry.Page.ToString(), entry.Title, entry.Summary)).ToArray();

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
            Entry(HelpPage.Account, "help-account-account", "账号", "Account", "账号名、登录有效期、退出登录、修改密码和 5 台设备上限。", "Account name, sign-in expiry, sign out, password and the 5-device limit.", "账号 登录 有效期 退出 密码 设备 电脑 上限 登出 account password sign out device limit"),
            Entry(HelpPage.Account, "help-account-settings", "设置", "Settings", "皮肤、账号、解析器、语言和恢复出厂设置。", "Skins, account, parser, language and factory reset.", "皮肤 赞助 语言 快捷按钮 恢复出厂 skins language"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-status", "先看运行状态", "Check runtime status", "找到第一个不正常的组件。", "Find the first component that is not running.", "/actcompat status 初始化 运行状态"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-restart", "重启对应的组件", "Restart the right component", "解析器、共享、抹茶和通用 Host 各管什么。", "What the parser and each Host are responsible for.", "重启 解析器 Host reload 卫月"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-common", "常见情况", "Common cases", "没数据、触发器不工作、重启 ACT、通知和 FFLogs 差异。", "No data, a trigger not working, restart ACT, notifications and FFLogs gaps.", "没数据 不播报 标点 通知 FFLogs 差异"),
            Entry(HelpPage.Troubleshooting, "help-troubleshooting-report", "反馈问题", "Report a problem", "顶栏反馈、需要的说明、诊断日志和 Network 日志。", "Send feedback, what to include, diagnostic log and Network log.", "反馈 截图 日志 诊断 我的反馈 官方通知 report log feedback"),
            Entry(HelpPage.Commands, "help-commands-common", "常用宏指令", "Common commands", "打开控制中心、统计、历史、日志和状态。", "Open the control center, meter, history, logs, and status.", "/actcompat on meter history logs status copy 复制"),
            Entry(HelpPage.Commands, "help-commands-overlays", "悬浮窗命令", "Overlay commands", "打开 Cactbot 或指定 HTML 模板。", "Open Cactbot or a named HTML template.", "cactbot overlay template 模板"),
            Entry(HelpPage.Commands, "help-commands-maintenance", "维护与诊断命令", "Maintenance commands", "清空、安装、Host、停止和恢复出厂设置。", "Clear, install, Host, stop, and factory reset commands.", "clear install host stop factory-reset sample DLL ZIP"),
            Entry(HelpPage.Copyright, "help-copyright-project", "版权与 GPL-3.0", "Copyright and GPL-3.0", "DACT 自有代码的许可证和第三方边界。", "License for DACT-owned code and third-party boundaries.", "license source 源代码 warranty"),
        ];
    }

    private sealed record HelpSearchEntry(
        HelpPage Page,
        string SectionId,
        string Title,
        string Summary,
        string SearchText);

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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Could not open help URL '{url}'.");
        }
    }
}
