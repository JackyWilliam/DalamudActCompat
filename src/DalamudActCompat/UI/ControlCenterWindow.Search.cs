using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 全局搜索: settings, pages and help topics are searched together; Enter (or a click) goes to the
// place and pulses its card, and on/off settings switch right in the list. Two ways in:
// - the header field searches in place, results dropping down under it (no separate box);
// - the shortcut (default Ctrl+Shift+K, SearchHotkey) opens a standalone box anywhere in the game,
//   even with DACT closed (SearchWindow). Neither dims anything. Esc, a click elsewhere or the
//   shortcut again close them.
public sealed partial class ControlCenterWindow
{
    private bool searchFocus;
    private string searchQuery = string.Empty;
    private int searchSelected;
    private bool headerSearchActive;
    private double headerSearchOpenedAt;
    private (Vector2 Min, Vector2 Max) headerSearchField;
    private (Vector2 Min, Vector2 Max)? headerSearchDropdown;

    private string windowSearchQuery = string.Empty;
    private int windowSearchSelected;

    // Opens a help page by its name (HelpWindow.HelpPage); set by the plugin.
    internal Action<string>? OpenHelpTopic { get; set; }

    // The standalone search box and its shortcut; set by the plugin.
    internal Action? OpenSearchWindow { get; set; }
    internal SearchHotkey? SearchShortcut { get; set; }

    private sealed record SearchEntry(int Group, string Icon, string Title, string Where, string Keywords, Action Open,
        Func<bool>? Get = null, Action<bool>? Set = null);

    private string ShortcutLabel => SearchHotkey.Format(configuration.SearchHotkeyKey, configuration.SearchHotkeyModifiers);

    // The header field's idle text; the field widens to show it whole (a cut-off 「Ctrl+S…」
    // would name the wrong shortcut).
    private string HeaderSearchPlaceholder => !configuration.SearchHotkeyActive()
        ? text.Get("搜索设置和功能", "Search settings")
        : text.Format($"搜索设置和功能  {ShortcutLabel}", $"Search settings  {ShortcutLabel}");

    private float HeaderSearchWidth => Math.Clamp(DactUi.TextWidth(HeaderSearchPlaceholder) + 44 * DactUi.S, 196 * DactUi.S, 280 * DactUi.S);

    // query pre-fills the header search (offline design previews); a click on the field opens it empty.
    public void OpenSearch(string? query = null)
    {
        headerSearchActive = true;
        headerSearchOpenedAt = ImGui.GetTime();
        searchQuery = query ?? string.Empty;
        searchSelected = 0;
        searchFocus = true;
    }

    private void CloseHeaderSearch()
    {
        headerSearchActive = false;
        headerSearchDropdown = null;
        searchQuery = string.Empty;
        searchSelected = 0;
    }

    // Opens the standalone box fresh (the shortcut, or the search field of a window without one).
    internal void ResetWindowSearch()
    {
        windowSearchQuery = string.Empty;
        windowSearchSelected = 0;
    }

    private List<SearchEntry> BuildSearchIndex(CloudClientSnapshot snapshot)
    {
        var entries = new List<SearchEntry>();
        string T(string zh, string en) => text.Get(zh, en);
        void AddPage(string icon, string zh, string en, Page page, Action? extra = null)
            => entries.Add(new(1, icon, T(zh, en), T("页面", "Page"), zh + " " + en, () => { selectedPage = page; extra?.Invoke(); }));
        void Setting(string icon, string zh, string en, string whereZh, string whereEn, Page page, string? card, Action? extra = null,
            Func<bool>? get = null, Action<bool>? set = null)
            => entries.Add(new(0, icon, T(zh, en), T(whereZh, whereEn), zh + " " + en + " " + whereZh + " " + whereEn, () =>
            {
                selectedPage = page;
                extra?.Invoke();
                if (card is not null) DactUi.HighlightCard(card);
            }, get, set));
        void Help(string zh, string en, string page)
            => entries.Add(new(2, "help", T(zh, en), T("帮助", "Help"), zh + " " + en, () =>
            {
                if (OpenHelpTopic is { } open) open(page);
                else openHelp();
            }));

        AddPage("home", "主页", "Home", Page.Overview);
        AddPage("meter", "战斗统计", "Combat Meter", Page.Meter);
        AddPage("history", "战斗记录", "Encounter history", Page.History, () => historyTab = 0);
        AddPage("target", "开荒进度", "Progression", Page.History, () => historyTab = 1);
        AddPage("folder", "日志文件", "Log files", Page.History, () => historyTab = 2);
        AddPage("layers", "悬浮窗", "Overlays", Page.Overlays);
        AddPage("plug", "扩展", "Extensions", Page.Extensions);
        if (snapshot.CanUseDact && Friends is not null)
        {
            AddPage("users", "好友", "Friends", Page.Friends, () => friendNotificationsPage = false);
            AddPage("users", "来信提醒", "Message alerts", Page.Friends, () => friendNotificationsPage = true);
        }
        AddPage("cloud", "账号与云同步", "Account & Cloud", Page.Cloud);
        AddPage("gear", "设置", "Settings", Page.Diagnostics, () => scenesPage = false);
        AddPage("layers", "场景方案", "Scene presets", Page.Diagnostics, () => scenesPage = true);

        var home = Page.Overview;
        Setting("pulse", "启用解析", "Enable parsing", "主页 · 基础设置", "Home · Basics", home, "home-general",
            get: () => configuration.EnableParsing, set: value => { configuration.EnableParsing = value; saveConfiguration(); });
        Setting("pulse", "自动启动解析器", "Auto start parser", "主页 · 基础设置", "Home · Basics", home, "home-general",
            get: () => configuration.AutoStartParser, set: value => { configuration.AutoStartParser = value; saveConfiguration(); });
        Setting("home", "精简模式", "Simplified mode", "主页 · 基础设置", "Home · Basics", home, "home-general",
            get: () => configuration.SimplifiedModeEnabled, set: value => setSimplifiedMode(value));
        Setting("layers", "游戏失焦时隐藏网页悬浮窗", "Hide web overlays when the game is unfocused", "主页 · 基础设置", "Home · Basics", home, "home-general",
            get: () => configuration.HideHtmlOverlaysWhenGameUnfocused, set: value => setHideHtmlOverlaysWhenUnfocused(value));
        Setting("cloud", "游戏区域（国服 / 国际服 / 台服）", "Game region", "主页 · 基础设置", "Home · Basics", home, "home-general");
        Setting("users", "游戏解析范围", "Parse scope", "主页 · 基础设置", "Home · Basics", home, "home-general");

        var meter = Page.Meter;
        Setting("meter", "榜单模板（经典榜 / 横版 / 职能分栏）", "Meter template", "战斗统计 · 榜单", "Combat Meter · Ranking", meter, "meter-ranking");
        Setting("meter", "统计重置", "Encounter reset", "战斗统计 · 榜单", "Combat Meter · Ranking", meter, "meter-ranking");
        Setting("meter", "刷新间隔", "Refresh interval", "战斗统计 · 榜单", "Combat Meter · Ranking", meter, "meter-ranking");
        Setting("users", "玩家 ID 与代称", "Player names and aliases", "战斗统计 · 榜单", "Combat Meter · Ranking", meter, "meter-ranking");
        Setting("meter", "启用 FFLogs 在线估算", "Enable FFLogs online estimate", "战斗统计 · FFLogs", "Combat Meter · FFLogs", meter, "meter-fflogs",
            get: () => configuration.Fflogs.Enabled, set: value => { configuration.Fflogs.Enabled = value; saveConfiguration(); });
        Setting("meter", "FFLogs Client ID / Client Secret", "FFLogs client ID / secret", "战斗统计 · FFLogs", "Combat Meter · FFLogs", meter, "meter-fflogs");
        Setting("edit", "自定义榜单样式", "Customize meter style", "战斗统计 · 榜单模板", "Combat Meter · Template", meter, "meter-ranking", openMeterStyleEditor);

        var overlays = Page.Overlays;
        Setting("layers", "Cactbot 设置 / 安装与更新", "Cactbot settings / install", "悬浮窗 · Cactbot", "Overlays · Cactbot", overlays, "cactbot-management");
        Setting("plus", "添加悬浮窗", "Add overlay", "悬浮窗", "Overlays", overlays, null);

        var extensions = Page.Extensions;
        Setting("plug", "系统插件（FFXIV_ACT_Plugin、OverlayPlugin）", "System plugins", "扩展", "Extensions", extensions, "ext-system");
        Setting("plug", "兼容扩展与权限", "Compatible extensions and permissions", "扩展", "Extensions", extensions, "ext-compat");

        var cloudPage = Page.Cloud;
        Setting("cloud", "打开游戏后自动同步配置", "Sync configuration after game launch", "账号与云同步 · 同步", "Account & Cloud · Sync", cloudPage, "cloud-summary",
            get: () => configuration.AutoCloudSyncEnabled, set: value => { configuration.AutoCloudSyncEnabled = value; saveConfiguration(); });
        Setting("upload", "上传当前配置", "Upload current settings", "账号与云同步 · 同步", "Account & Cloud · Sync", cloudPage, "cloud-summary");
        Setting("refresh", "备份与恢复", "Backups and restore", "账号与云同步", "Account & Cloud", cloudPage, "cloud-backup");
        Setting("shield", "Discord 账号找回", "Discord account recovery", "账号与云同步", "Account & Cloud", cloudPage, "cloud-discord");
        Setting("users", "邀请好友 / 邀请码", "Invite friends", "账号与云同步", "Account & Cloud", cloudPage, "cloud-invitations");
        Setting("edit", "修改密码 / 退出登录", "Change password / sign out", "账号与云同步 · 账号", "Account & Cloud · Account", cloudPage, "cloud-account");

        var settings = Page.Diagnostics;
        Setting("palette", "外观与皮肤", "Appearance and skins", "设置", "Settings", settings, null, () => scenesPage = false);
        Setting("gear", "界面语言", "Interface language", "设置 · 通用", "Settings · General", settings, "settings-general", () => scenesPage = false);
        Setting("home", "显示 ACT 快捷按钮", "Show ACT quick button", "设置 · 通用", "Settings · General", settings, "settings-general", () => scenesPage = false,
            () => configuration.ShowLauncherButton, value => { configuration.ShowLauncherButton = value; saveConfiguration(); });
        Setting("wrench", "调试模式", "Debug mode", "设置 · 通用", "Settings · General", settings, "settings-general", () => scenesPage = false,
            () => configuration.DebugMode, value => { configuration.DebugMode = value; saveConfiguration(); });
        Setting("history", "历史记录上限", "History limit", "设置 · 数据与日志", "Settings · Data and logs", settings, "settings-data", () => scenesPage = false);
        Setting("folder", "FFLogs 上传日志目录", "FFLogs upload log folder", "设置 · 数据与日志", "Settings · Data and logs", settings, "settings-data", () => scenesPage = false);
        Setting("copy", "复制诊断日志", "Copy diagnostic log", "设置 · 数据与日志", "Settings · Data and logs", settings, "settings-data", () => scenesPage = false);
        Setting("heart", "支持 DACT（爱发电）", "Support DACT", "设置", "Settings", settings, "settings-support", () => scenesPage = false);
        Setting("refresh", "重启解析器 / 运行状态", "Restart parser / runtime status", "设置 · 解析器与诊断", "Settings · Parser", settings, "settings-parser", () => scenesPage = false);
        Setting("layers", "按内容类型自动切换（场景方案）", "Switch by content type (scene presets)", "设置 · 场景方案", "Settings · Scenes", settings, "settings-scenes",
            () => scenesPage = false, () => configuration.Scenes.Enabled, value => { configuration.Scenes.Enabled = value; ScenePresets?.RequestApply(); saveConfiguration(); });
        Setting("reset", "恢复出厂设置", "Factory reset", "设置", "Settings", settings, null, () => scenesPage = false);
        if (snapshot.CanUseDact && Friends is not null)
        {
            var friends = Page.Friends;
            Setting("users", "显示来信气泡", "Show incoming message bubbles", "好友 · 来信提醒", "Friends · Message alerts", friends, "friend-bubbles",
                () => friendNotificationsPage = true, () => configuration.FriendNotificationsEnabled,
                value => { configuration.FriendNotificationsEnabled = value; saveConfiguration(); });
            Setting("users", "播放新消息提示音", "Play a sound for new messages", "好友 · 来信提醒", "Friends · Message alerts", friends, "friend-sound",
                () => friendNotificationsPage = true, () => configuration.FriendNotificationSoundEnabled,
                value => { configuration.FriendNotificationSoundEnabled = value; saveConfiguration(); });
        }

        Help("使用须知", "Usage notice", "UsageNotice");
        Help("快速开始", "Getting started", "GettingStarted");
        Help("战斗统计与 FFLogs DPS Parse 预估", "Combat meter and FFLogs estimates", "CombatMeter");
        Help("悬浮窗与 Cactbot", "Overlays and Cactbot", "Overlays");
        Help("ACT 扩展与权限", "ACT extensions and permissions", "Extensions");
        Help("账号、云同步与找回", "Account, cloud sync and recovery", "Account");
        Help("常见问题与排查", "Troubleshooting", "Troubleshooting");
        Help("聊天命令", "Chat commands", "Commands");
        return entries;
    }

    private static bool SearchMatches(SearchEntry entry, string query)
        => query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(part => entry.Title.Contains(part, StringComparison.OrdinalIgnoreCase) ||
                         entry.Where.Contains(part, StringComparison.OrdinalIgnoreCase) ||
                         entry.Keywords.Contains(part, StringComparison.OrdinalIgnoreCase));

    private List<SearchEntry> SearchResults(CloudClientSnapshot snapshot, string query)
    {
        query = query.Trim();
        return BuildSearchIndex(snapshot)
            .Where(entry => query.Length == 0 ? entry.Group == 1 : SearchMatches(entry, query))
            .OrderBy(entry => entry.Group)
            .Take(40)
            .ToList();
    }

    // The grouped result list shared by both search surfaces. Arrow keys and Enter arrive only
    // while the search field is typing (Dalamud forwards keys to ImGui just then), which is the
    // only time they should act. Returns the entry chosen this frame.
    private SearchEntry? DrawSearchResultList(List<SearchEntry> results, ref int selected, float width, float maxHeight, string id)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) selected = Math.Min(results.Count - 1, selected + 1);
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) selected = Math.Max(0, selected - 1);
        selected = Math.Clamp(selected, 0, Math.Max(0, results.Count - 1));
        SearchEntry? activate = null;
        if ((ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) && results.Count > 0) activate = results[selected];
        var groups = results.Select(entry => entry.Group).Distinct().Count();
        var listHeight = Math.Min(maxHeight, Math.Max(44 * s, results.Count * 46 * s + groups * 26 * s + 4 * s));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 6 * s);
        if (ImGui.BeginChild(id, new Vector2(width, listHeight), false))
        {
            var rowWidth = ImGui.GetContentRegionAvail().X;
            if (results.Count == 0)
                DactUi.Wrapped(text.Get("没有找到相关的设置、页面或帮助。", "Nothing matches in settings, pages or help."), palette.Muted, DactFonts.Small);
            var group = -1;
            for (var index = 0; index < results.Count; index++)
            {
                var entry = results[index];
                if (entry.Group != group)
                {
                    group = entry.Group;
                    var label = group switch { 0 => text.Get("设置项", "Settings"), 1 => text.Get("页面", "Pages"), _ => text.Get("帮助", "Help") };
                    var groupStart = ImGui.GetCursorScreenPos();
                    ImGui.Dummy(new Vector2(rowWidth, 24 * s));
                    DactUi.DrawText(ImGui.GetWindowDrawList(), groupStart + new Vector2(12 * s, 4 * s), 18 * s, label, palette.Muted, DactFonts.Small);
                }
                if (DrawSearchResult(entry, index == selected, rowWidth) is { } clicked) activate = clicked;
                if (index == selected && (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.IsKeyPressed(ImGuiKey.UpArrow))) ImGui.SetScrollHereY(.5f);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
        return activate;
    }

    // Header field. Idle it shows the placeholder with the shortcut; a click turns it into a
    // real text field in place and the results drop down under it.
    private void DrawHeaderSearchField(Vector2 min, float width, float height)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var draw = ImGui.GetWindowDrawList();
        var max = min + new Vector2(width, height);
        headerSearchField = (min, max);
        ImGui.SetCursorScreenPos(min);
        if (!headerSearchActive)
        {
            if (ImGui.InvisibleButton("##shell-search", new Vector2(width, height))) OpenSearch();
            var hovered = ImGui.IsItemHovered();
            var border = DactMotion.Color(ImGuiP.GetItemID(), hovered ? palette.Accent with { W = .8f } : DactUi.FieldBorder, DactMotion.Fast);
            draw.AddRectFilled(min, max, DactUi.U32(DactUi.Field), 6 * s);
            draw.AddRect(min, max, DactUi.U32(border), 6 * s, ImDrawFlags.None, Math.Max(1, s));
            DactUi.DrawText(draw, min + new Vector2(10 * s, 0), height, DactUi.Fit(HeaderSearchPlaceholder, width - 44 * s), palette.Muted);
            DactIcons.Draw(draw, "search", min + new Vector2(width - 24 * s, (height - 14 * s) * .5f), 14 * s, palette.Muted);
            return;
        }

        draw.AddRectFilled(min, max, DactUi.U32(DactUi.Field), 6 * s);
        draw.AddRect(min, max, DactUi.U32(palette.Accent), 6 * s, ImDrawFlags.None, Math.Max(1, s));
        DactIcons.Draw(draw, "search", min + new Vector2(width - 24 * s, (height - 14 * s) * .5f), 14 * s, palette.Accent);
        // Esc is checked before the field runs: once the field takes it, it is locked for the frame.
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            CloseHeaderSearch();
            return;
        }
        ImGui.SetCursorScreenPos(min + new Vector2(4 * s, (height - ImGui.GetFrameHeight()) * .5f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        ImGui.SetNextItemWidth(width - 34 * s);
        if (searchFocus)
        {
            ImGui.SetKeyboardFocusHere();
            searchFocus = false;
        }
        if (ImGui.InputTextWithHint("##header-search-query", text.Get("搜索设置、页面和帮助", "Search settings, pages and help"), ref searchQuery, 80))
            searchSelected = 0;
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    // The header's result dropdown: a child of this window drawn after the page so it lies on
    // top. It hangs under the field, centred on it, kept inside the window.
    private void DrawHeaderSearchDropdown(CloudClientSnapshot snapshot)
    {
        if (!headerSearchActive) return;
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var windowMin = ImGui.GetWindowPos();
        var windowMax = windowMin + ImGui.GetWindowSize();
        var (fieldMin, fieldMax) = headerSearchField;
        // A click anywhere else in DACT, or DACT losing focus (a click in the game), closes it.
        var mouse = ImGui.GetMousePos();
        bool Inside((Vector2 Min, Vector2 Max) rect) => mouse.X >= rect.Min.X && mouse.Y >= rect.Min.Y && mouse.X < rect.Max.X && mouse.Y < rect.Max.Y;
        var clickedAway = ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !Inside(headerSearchField) &&
                          !(headerSearchDropdown is { } open && Inside(open));
        var settled = ImGui.GetTime() - headerSearchOpenedAt > .1;
        if (clickedAway || settled && !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            CloseHeaderSearch();
            return;
        }

        var results = SearchResults(snapshot, searchQuery);
        var width = Math.Min(460 * s, windowMax.X - windowMin.X - 24 * s);
        var x = Math.Clamp((fieldMin.X + fieldMax.X - width) * .5f, windowMin.X + 12 * s, windowMax.X - 12 * s - width);
        var progress = DactMotion.Progress(headerSearchOpenedAt, .16f);
        var top = fieldMax.Y + 6 * s - (1 - progress) * 6 * s;
        var padding = 6 * s;
        var maxList = Math.Max(120 * s, Math.Min(380 * s, windowMax.Y - top - 12 * s - padding * 2 - 30 * s));
        ImGui.SetCursorScreenPos(new Vector2(x, top));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * Math.Max(.01f, progress));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, Math.Max(1, s));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, DactUi.PaneFill(palette.Raised));
        ImGui.PushStyleColor(ImGuiCol.Border, DactUi.PaneBorder(palette.Border));
        var groups = results.Select(entry => entry.Group).Distinct().Count();
        var listHeight = Math.Min(maxList, Math.Max(44 * s, results.Count * 46 * s + groups * 26 * s + 4 * s));
        var size = new Vector2(width, listHeight + padding * 2 + 30 * s);
        headerSearchDropdown = (new Vector2(x, top), new Vector2(x, top) + size);
        SearchEntry? activate = null;
        if (ImGui.BeginChild("##header-search-dropdown", size, true, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar))
        {
            DactUi.DrawSkinPane();
            activate =DrawSearchResultList(results, ref searchSelected, ImGui.GetContentRegionAvail().X, maxList, "##header-search-results");
            var footer = ImGui.GetCursorScreenPos() + new Vector2(8 * s, 2 * s);
            DactUi.DrawText(ImGui.GetWindowDrawList(), footer, 22 * s,
                text.Get("↑ ↓ 选择 · Enter 跳过去 · 开关可直接切换 · Esc 收起", "↑ ↓ choose · Enter jumps there · switches toggle here · Esc closes"),
                DactUi.HintColor, DactFonts.Small);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
        if (activate is null) return;
        CloseHeaderSearch();
        activate.Open();
    }

    // The standalone box's contents (SearchWindow draws the frame). Returns true when it should close.
    internal bool DrawStandaloneSearch(bool focusField)
    {
        var snapshot = cloud.GetSnapshot();
        if (!snapshot.CanUseDact) return true;
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var width = ImGui.GetContentRegionAvail().X;
        // Esc is checked before the field runs: once the field takes it, it is locked for the frame.
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) return true;

        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, 40 * s), DactUi.U32(DactUi.Field), 8 * s);
        draw.AddRect(start, start + new Vector2(width, 40 * s), DactUi.U32(palette.Accent), 8 * s, ImDrawFlags.None, Math.Max(1, s));
        DactIcons.Draw(draw, "search", start + new Vector2(12 * s, 12 * s), 16 * s, palette.Accent);
        var shortcut = !configuration.SearchHotkeyActive() ? "Esc" : $"Esc · {ShortcutLabel}";
        var keysWidth = DactUi.PillWidth(shortcut, false);
        DactUi.DrawPill(draw, start + new Vector2(width - 10 * s - keysWidth, 9 * s), shortcut, PillTone.Off, false);
        ImGui.SetCursorScreenPos(start + new Vector2(38 * s, (40 * s - ImGui.GetFrameHeight()) * .5f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        ImGui.SetNextItemWidth(width - 38 * s - keysWidth - 20 * s);
        if (focusField) ImGui.SetKeyboardFocusHere();
        if (ImGui.InputTextWithHint("##window-search-query", text.Get("搜索设置、页面和帮助", "Search settings, pages and help"), ref windowSearchQuery, 80))
            windowSearchSelected = 0;
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        ImGui.SetCursorScreenPos(start + new Vector2(0, 46 * s));

        var activate = DrawSearchResultList(SearchResults(snapshot, windowSearchQuery), ref windowSearchSelected, width, 360 * s, "##window-search-results");
        DactUi.Divider();
        var footer = ImGui.GetCursorScreenPos() + new Vector2(12 * s, 0);
        foreach (var hint in new[]
                 {
                     text.Get("↑ ↓ 选择", "↑ ↓ to choose"),
                     text.Get("Enter 打开 DACT 并跳到该位置", "Enter opens DACT there"),
                     text.Get("开关可直接切换", "Switches toggle right here"),
                 })
        {
            DactUi.DrawText(ImGui.GetWindowDrawList(), footer, 18 * s, hint, DactUi.HintColor, DactFonts.Small);
            footer.X += DactUi.TextWidth(hint, DactFonts.Small) + 16 * s;
        }
        ImGui.Dummy(new Vector2(width, 22 * s));
        if (activate is null) return false;
        // A result opens the control center at that place (help topics open the help window
        // instead); a switch was already toggled in its row.
        activate.Open();
        if (activate.Group == 2) return true;
        if (!IsOpen || visibilityTransition == VisibilityTransition.Closing) ShowAnimated();
        else focusOnNextDraw = true;
        return true;
    }

    // One result row (dsl result): icon, title and where it lives; switches toggle in place.
    private SearchEntry? DrawSearchResult(SearchEntry entry, bool selected, float width)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        ImGui.PushID(entry.Title + entry.Where);
        var start = ImGui.GetCursorScreenPos();
        var height = 44 * s;
        var toggleWidth = entry.Get is null ? 0 : 34 * s + 12 * s;
        var clicked = ImGui.InvisibleButton("##result", new Vector2(width - toggleWidth, height));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var fill = selected ? DactUi.AccentSoft : hovered ? palette.Hover : Vector4.Zero;
        if (fill.W > 0) draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(fill), 8 * s);
        DactIcons.Draw(draw, entry.Icon, start + new Vector2(12 * s, 14 * s), 16 * s, selected ? palette.Accent : palette.Muted);
        var textWidth = width - 40 * s - 12 * s - toggleWidth;
        DactUi.DrawText(draw, start + new Vector2(40 * s, 5 * s), 18 * s, DactUi.Fit(entry.Title, textWidth), selected ? palette.Accent : palette.Text);
        string where;
        using (DactFonts.Push(DactFonts.Small)) where = DactUi.Fit(entry.Where, textWidth);
        DactUi.DrawText(draw, start + new Vector2(40 * s, 24 * s), 16 * s, where, palette.Muted, DactFonts.Small);
        if (entry.Get is { } get && entry.Set is { } set)
        {
            ImGui.SameLine(0, 0);
            ImGui.SetCursorScreenPos(new Vector2(start.X + width - 12 * s - 34 * s, start.Y + 12 * s));
            var value = get();
            if (DactUi.Toggle("##result-toggle", ref value)) set(value);
        }
        ImGui.SetCursorScreenPos(start + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 0));
        ImGui.PopID();
        return clicked ? entry : null;
    }
}
