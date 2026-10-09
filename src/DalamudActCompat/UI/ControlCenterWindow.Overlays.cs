using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Parser;

namespace DalamudActCompat.UI;

public sealed partial class ControlCenterWindow
{
    private enum OverlayDetailsPage { General, Hotkeys, Source }
    private enum OverlayCreatorPage { Cactbot, Template, Url }
    private OverlayDetailsPage overlayDetailsPage;
    private OverlayCreatorPage? overlayCreatorPage;
    private string? selectedCreationTemplate;
    private string? editingOverlaySource;
    private string overlaySourceDraft = string.Empty;
    private string? overlaySourceFeedback;
    private const string AddOverlayPopup = "###dact-add-overlay";

    private float OverlayScale => Math.Max(.75f, ImGui.GetFontSize() / 17f);

    // Registration is separate from merely having defaults in the configuration.
    // Removed Cactbot windows must stay out of the list until explicitly reopened.
    internal string[] RegisteredOverlayNames() => configuration.OverlayWindows
        .Where(pair => !SelfHostedActRuntime.IsCactbotOverlayName(pair.Key) || pair.Value.HasBeenOpened)
        .OrderBy(pair => SelfHostedActRuntime.IsCactbotOverlayName(pair.Key) ? 0 : 1)
        .ThenBy(pair => GetCactbotOverlayOrder(pair.Key))
        .ThenBy(pair => ResolveOverlayDisplayName(pair.Key, pair.Value), StringComparer.OrdinalIgnoreCase)
        .Select(pair => pair.Key).ToArray();

    private bool DrawOverlays()
    {
        configuration.OverlayWindows ??= new(StringComparer.OrdinalIgnoreCase);
        var templates = getOverlayTemplates();
        var scale = OverlayScale;
        var headingStart = ImGui.GetCursorPos();
        var pageWidth = ImGui.GetContentRegionAvail().X;
        var addLabel = text.Get("＋ 添加悬浮窗", "+ Add overlay");
        var addWidth = ImGui.CalcTextSize(addLabel).X + 30 * scale;
        var headingWide = pageWidth >= 760 * scale;
        if (headingWide) ImGui.PushTextWrapPos(headingStart.X + pageWidth - addWidth - 18 * scale);
        DrawPageHeader(text.Get("悬浮窗管理", "Overlay manager"),
            text.Get("选择一个悬浮窗，调整它的显示与操作。", "Select an overlay to manage its display and controls."), false);
        if (headingWide) ImGui.PopTextWrapPos();
        var headingEnd = ImGui.GetCursorPosY();
        if (headingWide) ImGui.SetCursorPos(headingStart + new Vector2(pageWidth - addWidth, 0));
        if (DactTheme.Button(addLabel, new(addWidth, 36 * scale)))
        {
            overlayCreatorPage = null;
            ImGui.OpenPopup(AddOverlayPopup);
        }
        if (headingWide) ImGui.SetCursorPosY(Math.Max(headingEnd, ImGui.GetCursorPosY()));
        ImGui.Spacing();
        DrawCactbotManagement();
        ImGui.Spacing();

        var names = RegisteredOverlayNames();
        if (selectedCreatedOverlay is null || !names.Contains(selectedCreatedOverlay, StringComparer.OrdinalIgnoreCase))
            SelectManagedOverlay(names.FirstOrDefault());
        var wide = ImGui.GetContentRegionAvail().X >= 760 * scale;
        var height = Math.Max(360 * scale, ImGui.GetContentRegionAvail().Y);
        var changed = false;
        // Narrow windows stack the panels and retain scrolling rather than
        // shrinking translated controls until they overlap or become unreachable.
        if (ImGui.BeginChild("overlay-list", new(wide ? 252 * scale : -1, wide ? height : 250 * scale), true))
            DrawManagedOverlayList(names, templates, wide);
        ImGui.EndChild();
        if (wide) ImGui.SameLine(); else ImGui.Spacing();
        if (ImGui.BeginChild("overlay-details", new(-1, height), true))
        {
            if (selectedCreatedOverlay is { } name && configuration.OverlayWindows.TryGetValue(name, out var settings))
                changed |= DrawManagedOverlayDetails(name, settings, templates);
            else ImGui.TextWrapped(text.Get("添加一个悬浮窗后，即可在这里设置。", "Add an overlay to configure it here."));
        }
        ImGui.EndChild();
        changed |= DrawAddOverlayDialog(templates);
        return changed;
    }

    private void SelectManagedOverlay(string? name)
    {
        selectedCreatedOverlay = name;
        overlayDetailsPage = OverlayDetailsPage.General;
        CancelOverlayRename();
        editingOverlaySource = null;
        overlaySourceFeedback = null;
    }

    private void DrawCactbotManagement()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(10, 9) * OverlayScale);
        if (!ImGui.BeginTable("cactbot-management", 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.SizingStretchSame))
        { ImGui.PopStyleVar(); return; }
        ImGui.TableNextRow(); ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(NavyRaised)); ImGui.TableNextColumn();
        var labels = new[] { text.Get("Cactbot 设置", "Cactbot settings"), text.Get("安装 / 更新", "Install / update"), text.Get("官方项目页", "Official project") };
        var actionsWidth = labels.Sum(s => ImGui.CalcTextSize(s).X + ImGui.GetStyle().FramePadding.X * 2) + ImGui.GetStyle().ItemSpacing.X * 3;
        var columns = ImGui.GetContentRegionAvail().X - actionsWidth > 330 * OverlayScale ? 2 : 1;
        if (ImGui.BeginTable("cactbot-management-header", columns, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("status", ImGuiTableColumnFlags.WidthStretch);
            if (columns == 2) ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthFixed, actionsWidth);
            ImGui.TableNextColumn();
            DactTheme.TextColored(Gold, "Cactbot");
            OverlaySameLineIfFits(ImGui.CalcTextSize(LocalizeState(parserStatus.State)).X + 35 * OverlayScale);
            DactTheme.TextColored(parserStatus.State == ParserState.Running ? IceBlue : DactTheme.Palette.Muted,
                text.Format($"解析器：{LocalizeState(parserStatus.State)}", $"Parser: {LocalizeState(parserStatus.State)}"));
            ImGui.TextWrapped(FormatCactbotStatus());
            ImGui.TableNextColumn();
            if (DactTheme.Button(labels[0])) openCactbotSettings();
            OverlaySameLineIfFits(ImGui.CalcTextSize(labels[1]).X + 24 * OverlayScale);
            if (DactTheme.Button(labels[1])) selectCactbotPackage();
            OverlaySameLineIfFits(ImGui.CalcTextSize(labels[2]).X + 24 * OverlayScale);
            if (DactTheme.Button(labels[2])) OpenUrl("https://github.com/OverlayPlugin/cactbot");
            ImGui.EndTable();
        }
        ImGui.TextWrapped(text.Get(
            "提示中的玩家默认显示职业全称；可在 Cactbot 设置的“默认玩家代称”中修改。",
            "Player callouts default to full job names; change this under Default Player Label in Cactbot settings."));
        ImGui.EndTable(); ImGui.PopStyleVar();
    }

    private void OverlaySameLineIfFits(float width)
    {
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <=
            ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X)
            ImGui.SameLine();
    }

    private string OverlayDisplayName(string name) => SelfHostedActRuntime.IsCactbotOverlayName(name)
        ? FormatCactbotOverlayName(name) : ResolveOverlayDisplayName(name, configuration.OverlayWindows[name]);

    private string OverlayKind(string name, ActOverlayTemplate? template) => SelfHostedActRuntime.IsCactbotOverlayName(name)
        ? "Cactbot" : template is not null ? text.Get("本地模板", "Local template") : text.Get("自定义网址", "Custom URL");

    internal static bool CanOpenManagedOverlay(string name, HtmlOverlayWindowSettings settings, IReadOnlyList<ActOverlayTemplate> templates)
        => SelfHostedActRuntime.IsCactbotOverlayName(name)
            ? templates.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            : !string.IsNullOrWhiteSpace(settings.SourceUrl) || templates.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private string ManagedOverlayStatus(string name, HtmlOverlayWindowSettings settings, IReadOnlyList<ActOverlayTemplate> templates)
    {
        if (settings.IsVisible) return settings.IsUserHidden ? text.Get("已隐藏", "Hidden") : text.Get("已打开", "Open");
        if (parserStatus.State != ParserState.Running) return text.Get("解析器未运行", "Parser stopped");
        if (!CanOpenManagedOverlay(name, settings, templates)) return text.Get("本地资源不可用", "Local asset unavailable");
        return text.Get("已关闭", "Closed");
    }

    private void DrawManagedOverlayList(string[] names, IReadOnlyList<ActOverlayTemplate> templates, bool wide)
    {
        ImGui.TextUnformatted(text.Get("我的悬浮窗", "My overlays"));
        var rowHeight = ImGui.GetTextLineHeightWithSpacing() * 2 + 12 * OverlayScale;
        var listHeight = Math.Max(70 * OverlayScale, ImGui.GetContentRegionAvail().Y - (wide ? 112 : 85) * OverlayScale);
        if (ImGui.BeginChild("overlay-rows", new(-1, listHeight), false))
        {
            if (names.Length == 0) ImGui.TextWrapped(text.Get("还没有创建悬浮窗。", "No overlays have been created yet."));
            foreach (var name in names)
            {
                var settings = configuration.OverlayWindows[name];
                var template = templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                ImGui.PushID(name);
                var start = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
                if (ImGui.InvisibleButton("select-overlay", new(width, rowHeight))) SelectManagedOverlay(name);
                var draw = ImGui.GetWindowDrawList();
                var selected = string.Equals(name, selectedCreatedOverlay, StringComparison.OrdinalIgnoreCase);
                draw.AddRectFilled(start, start + new Vector2(width, rowHeight), ImGui.GetColorU32(selected ? NavyHover : NavyRaised), 6);
                if (selected) draw.AddRect(start, start + new Vector2(width, rowHeight), ImGui.GetColorU32(IceBlue), 6);
                draw.PushClipRect(start + new Vector2(8, 0), start + new Vector2(width - 8, rowHeight), true);
                draw.AddText(start + new Vector2(9, 7), ImGui.GetColorU32(DactTheme.Palette.Text),
                    FriendsMessagePreview.Ellipsize(OverlayDisplayName(name), Math.Max(1, width - 18), s => ImGui.CalcTextSize(s).X));
                var status = OverlayKind(name, template) + " · " + ManagedOverlayStatus(name, settings, templates);
                draw.AddText(start + new Vector2(9, 7 + ImGui.GetTextLineHeightWithSpacing()),
                    ImGui.GetColorU32(DactTheme.Palette.Muted), FriendsMessagePreview.Ellipsize(status, Math.Max(1, width - 18), s => ImGui.CalcTextSize(s).X));
                draw.PopClipRect();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(OverlayDisplayName(name) + "\n" + status);
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.Separator();
        ImGui.TextDisabled(text.Get("全部悬浮窗", "All overlays"));
        var hide = configuration.HideHtmlOverlaysWhenGameUnfocused;
        if (DactTheme.Checkbox(text.Get("游戏失焦时隐藏", "Hide when game loses focus"), ref hide)) setHideHtmlOverlaysWhenUnfocused(hide);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text.Get(
            "仅临时隐藏网页悬浮窗，不改变各悬浮窗保存的开启状态。",
            "Temporarily hides web overlays without changing their saved open state."));
    }

    private bool DrawManagedOverlayDetails(string name, HtmlOverlayWindowSettings settings, IReadOnlyList<ActOverlayTemplate> templates)
    {
        var changed = false;
        var cactbot = SelfHostedActRuntime.IsCactbotOverlayName(name);
        var template = templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        var available = CanOpenManagedOverlay(name, settings, templates);
        ImGui.PushID(name);
        DactTheme.TextColored(IceBlue, OverlayDisplayName(name));
        ImGui.TextWrapped(OverlayKind(name, template) + " · " + ManagedOverlayStatus(name, settings, templates));
        ImGui.BeginDisabled(!settings.IsVisible && (!available || parserStatus.State != ParserState.Running));
        if (DactTheme.Button(settings.IsVisible ? text.Get("关闭", "Close") : text.Get("打开", "Open")))
        { if (settings.IsVisible) closeHtmlOverlay(name); else openHtmlOverlay(name); }
        ImGui.EndDisabled();
        OverlaySameLineIfFits(95 * OverlayScale);
        ImGui.BeginDisabled(!settings.IsVisible);
        if (DactTheme.Button(settings.IsUserHidden ? text.Get("显示", "Show") : text.Get("隐藏", "Hide")))
        { settings.IsUserHidden = !settings.IsUserHidden; changed = true; }
        ImGui.EndDisabled();
        OverlaySameLineIfFits(95 * OverlayScale);
        if (DactTheme.Button(text.Get("更多", "More"))) ImGui.OpenPopup("overlay-actions");
        var removed = false;
        if (ImGui.BeginPopup("overlay-actions"))
        {
            if (!cactbot && ImGui.MenuItem(text.Get("重命名", "Rename")))
            { overlayBeingRenamed = name; overlayRenameValue = OverlayDisplayName(name); overlayRenameFeedback = null; }
            if (ImGui.MenuItem(cactbot ? text.Get("移除并重置", "Remove and reset") : text.Get("删除悬浮窗", "Delete overlay")))
            { deleteHtmlOverlay(name); SelectManagedOverlay(null); removed = true; }
            ImGui.TextWrapped(cactbot ? text.Get(
                "关闭并移除记录，清除窗口设置；保留本地模板。", "Close and remove this entry, clearing window settings while keeping the local template.")
                : text.Get("关闭悬浮窗并删除它保存的网址、位置、大小与显示设置。", "Close the overlay and delete its saved URL, position, size, and display settings."));
            ImGui.EndPopup();
        }
        if (removed) { ImGui.PopID(); return changed; }
        if (overlayBeingRenamed == name) changed |= DrawOverlayRenameEditor(name, settings);
        ImGui.Spacing();
        overlayDetailsPage = (OverlayDetailsPage)BrandedWindowChrome.DrawNavigationRail("overlay-detail-tabs",
            [text.Get("常规", "General"), text.Get("快捷键", "Hotkeys"), text.Get("来源", "Source")], (int)overlayDetailsPage, 36 * OverlayScale);
        ImGui.Spacing();
        if (cactbot && (!available || parserStatus.State != ParserState.Running))
        {
            ImGui.TextWrapped(parserStatus.State != ParserState.Running
                ? text.Get("启动解析器后才能打开该悬浮窗。", "Start the parser before opening this overlay.")
                : text.Get("当前 Cactbot 包缺少该页面，不会回退到远程地址。", "The current Cactbot package does not contain this page; no online fallback will be used."));
            if (settings.OpenOnStartup && DactTheme.Button(text.Get("停止自动打开", "Disable startup")))
            { settings.OpenOnStartup = false; changed = true; }
            if (DactTheme.Button(text.Get("移除并重置", "Remove and reset")))
            { deleteHtmlOverlay(name); SelectManagedOverlay(null); ImGui.PopID(); return changed; }
            ImGui.Separator();
        }
        switch (overlayDetailsPage)
        {
            case OverlayDetailsPage.General: changed |= DrawManagedOverlayGeneral(name, settings, available); break;
            case OverlayDetailsPage.Hotkeys: changed |= HotkeyEditor?.Draw(name, settings, text) ?? false; break;
            case OverlayDetailsPage.Source: changed |= DrawManagedOverlaySource(name, settings, template); break;
        }
        if (changed) applyOverlayWindowSettings(name);
        ImGui.PopID();
        return changed;
    }

    private bool DrawManagedOverlayGeneral(string name, HtmlOverlayWindowSettings settings, bool available)
    {
        ImGui.TextUnformatted(text.Get("显示与启动", "Display and startup"));
        var changed = Checkbox(text.Get("启动时自动打开", "Open on startup"), settings.OpenOnStartup, v => settings.OpenOnStartup = v);
        changed |= Checkbox(text.Get("脱战隐藏", "Hide out of combat"), settings.AutoHideOutOfCombat, v => settings.AutoHideOutOfCombat = v);
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        ImGui.TextUnformatted(text.Get("位置、缩放、穿透与锁定", "Position, scale, click-through, and lock"));
        ImGui.BeginDisabled(!available && !settings.IsVisible);
        if (DactTheme.Button(settings.IsEditing ? text.Get("完成位置编辑", "Finish position editing") : text.Get("编辑位置和大小", "Edit position and size")))
        {
            var edit = !settings.IsEditing;
            if (!edit || settings.IsVisible || openHtmlOverlay(name)) { settings.SetEditing(edit); changed = true; }
        }
        ImGui.EndDisabled();
        changed |= Checkbox(text.Get("鼠标穿透", "Click-through"), settings.IsClickThrough, settings.SetClickThrough);
        OverlaySameLineIfFits(ImGui.CalcTextSize(text.Get("锁定位置和大小", "Lock position and size")).X + 45 * OverlayScale);
        changed |= Checkbox(text.Get("锁定位置和大小", "Lock position and size"), settings.IsLocked, settings.SetLocked);
        ImGui.SetNextItemWidth(Math.Min(300 * OverlayScale, Math.Max(80 * OverlayScale, ImGui.GetContentRegionAvail().X - 130 * OverlayScale)));
        changed |= SliderFloat(text.Get("页面缩放", "Page zoom"), settings.ZoomFactor, .5f, 2, v => settings.ZoomFactor = v);
        ImGui.TextWrapped(settings.IsEditing ? text.Get(
            "单击可操作网页；按住并拖动可移动，拖动右下角斜纹可缩放。",
            "Click to use the page; hold and drag to move, or drag the striped bottom-right grip to resize.") : text.Get(
            "位置编辑时会暂时关闭穿透与锁定；完成后会恢复。",
            "Position editing temporarily disables click-through and locking; finishing restores them."));
        return changed;
    }

    internal static string ManagedOverlaySource(ActOverlayTemplate? template, HtmlOverlayWindowSettings settings)
    {
        if (template is null) return settings.SourceUrl;
        if (!template.IsCactbot || !Uri.TryCreate(template.Uri, UriKind.Absolute, out var uri)) return template.Uri;
        if (uri.IsFile) return uri.LocalPath;
        var marker = uri.AbsolutePath.IndexOf("/cactbot/", StringComparison.OrdinalIgnoreCase);
        // The catalog stores upstream URLs, but Cactbot opens local files. Never
        // present that upstream URL as the active source or as a fallback action.
        return marker < 0 ? string.Empty : "cactbot/" + Uri.UnescapeDataString(uri.AbsolutePath[(marker + 9)..]);
    }

    private bool DrawManagedOverlaySource(string name, HtmlOverlayWindowSettings settings, ActOverlayTemplate? template)
    {
        var changed = false;
        var cactbot = SelfHostedActRuntime.IsCactbotOverlayName(name);
        var custom = !cactbot && template is null;
        ImGui.TextUnformatted(text.Get("页面来源", "Page source"));
        ImGui.TextWrapped(OverlayKind(name, template));
        var address = ManagedOverlaySource(template, settings);
        if (address.Length > 0)
        {
            ImGui.TextDisabled(cactbot ? text.Get("本地页面", "Local page") : text.Get("页面网址", "Page URL"));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##source-address", ref address, Math.Max(2048, address.Length + 1), ImGuiInputTextFlags.ReadOnly);
        }
        else ImGui.TextWrapped(text.Get("本地资源不可用", "Local asset unavailable"));
        if (cactbot) ImGui.TextWrapped(text.Get("此页面随 Cactbot 一起安装和更新。", "This page is installed and updated with Cactbot."));
        if (custom)
        {
            if (DactTheme.Button(text.Get("修改网址", "Edit URL")))
            { editingOverlaySource = name; overlaySourceDraft = settings.SourceUrl; overlaySourceFeedback = null; }
            if (editingOverlaySource == name)
            {
                ImGui.SetNextItemWidth(-1); ImGui.InputText("##edit-source", ref overlaySourceDraft, 2048);
                if (DactTheme.Button(text.Get("保存网址", "Save URL"))) changed |= SaveManagedOverlaySource(name, overlaySourceDraft);
                OverlaySameLineIfFits(110 * OverlayScale);
                if (DactTheme.Button(text.Get("取消", "Cancel"))) { editingOverlaySource = null; overlaySourceFeedback = null; }
            }
            if (overlaySourceFeedback is not null) ImGui.TextWrapped(overlaySourceFeedback);
        }
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        ImGui.TextUnformatted(text.Get("数据连接", "Data connection"));
        // Only custom pages currently report a detected WebSocket connection.
        // Window visibility is not evidence that a page is receiving game data.
        if (!settings.IsVisible) ImGui.TextWrapped(custom ? text.Get("打开后自动检测连接", "Connection will be detected when opened") : text.Get("已关闭", "Closed"));
        else if (!custom) ImGui.TextWrapped(text.Get("页面已打开；此页面没有独立的连接检测结果。", "The page is open; no separate connection detection result is available for this page."));
        else ImGui.TextWrapped(string.IsNullOrWhiteSpace(settings.ConnectionStateDetail)
            ? text.Get("打开后自动检测连接", "Connection will be detected when opened") : text.SystemMessage(settings.ConnectionStateDetail));
        if (custom && ImGui.TreeNode(text.Get("连接高级设置", "Advanced connection settings")))
        {
            var reconnect = false;
            ImGui.SetNextItemWidth(Math.Min(280 * OverlayScale, ImGui.GetContentRegionAvail().X));
            if (DactTheme.BeginCombo("##connection-mode", GetOverlayConnectionModeLabel(settings.ConnectionMode)))
            {
                foreach (var mode in Enum.GetValues<OverlayConnectionMode>())
                    if (ImGui.Selectable(GetOverlayConnectionModeLabel(mode), settings.ConnectionMode == mode))
                    { settings.ConnectionMode = mode; reconnect = true; }
                ImGui.EndCombo();
            }
            if (DactTheme.Button(text.Get("重新检测", "Detect again"))) { settings.ConnectionMode = OverlayConnectionMode.Auto; reconnect = true; }
            ImGui.TextWrapped(text.Get("默认自动检测；手动模式只用于检测失败时微调。", "Automatic detection is the default. Manual modes are only for troubleshooting."));
            if (reconnect) { ReconnectManagedOverlay(name, settings); changed = true; }
            ImGui.TreePop();
        }
        return changed;
    }

    internal bool SaveManagedOverlaySource(string name, string value)
    {
        if (SelfHostedActRuntime.IsCactbotOverlayName(name) || getOverlayTemplates().Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ||
            !configuration.OverlayWindows.TryGetValue(name, out var settings)) return false;
        if (!SelfHostedActRuntime.TryNormalizeCustomOverlayUri(value, out var uri))
        { overlaySourceFeedback = text.Get("网址无效；请使用完整的 http、https 或 file 地址。", "Invalid URL. Use a complete http, https, or file URL."); return false; }
        settings.SourceUrl = uri.AbsoluteUri;
        var opened = ReconnectManagedOverlay(name, settings);
        editingOverlaySource = null;
        overlaySourceFeedback = opened ? null : text.Get("网址已保存，窗口未能重新打开；请检查解析器状态。", "URL saved, but the overlay could not reopen; check the parser status.");
        return true;
    }

    private bool ReconnectManagedOverlay(string name, HtmlOverlayWindowSettings settings)
    {
        settings.ResetConnectionDetection();
        if (!settings.IsVisible) return true;
        var hidden = settings.IsUserHidden; var startup = settings.OpenOnStartup;
        // Explicit Open clears hiding and opts into startup restoration. Changing
        // a source/protocol is a refresh, so preserve those independent preferences.
        try { return openHtmlOverlay(name); }
        finally { settings.IsUserHidden = hidden; settings.OpenOnStartup = startup; applyOverlayWindowSettings(name); }
    }

    private bool DrawAddOverlayDialog(IReadOnlyList<ActOverlayTemplate> templates)
    {
        var width = Math.Min(650 * OverlayScale, ImGui.GetMainViewport().WorkSize.X - 40 * OverlayScale);
        // A modal can outlive viewport resizing. Refit it before drawing so all
        // creation controls and the cancel action remain inside the viewport.
        DactTheme.PreparePopupPosition(AddOverlayPopup);
        ImGui.SetNextWindowSize(new(width, 0));
        if (!ImGui.BeginPopupModal(text.Get("添加悬浮窗", "Add overlay") + AddOverlayPopup,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) return false;
        var changed = false;
        if (overlayCreatorPage is null)
        {
            ImGui.TextWrapped(text.Get("选择悬浮窗来源", "Choose an overlay source"));
            foreach (var page in Enum.GetValues<OverlayCreatorPage>())
            {
                var label = page switch { OverlayCreatorPage.Cactbot => "Cactbot", OverlayCreatorPage.Template => text.Get("从模板创建", "Create from template"), _ => text.Get("从网址创建", "Create from URL") };
                if (DactTheme.Button(label, new(-1, 42 * OverlayScale))) { overlayCreatorPage = page; selectedCreationTemplate = null; customOverlayFeedback = null; }
            }
        }
        else
        {
            if (overlayCreatorPage == OverlayCreatorPage.Url)
            {
                changed |= DrawCustomHtmlOverlayCreator();
                if (changed) { overlayDetailsPage = OverlayDetailsPage.General; ImGui.CloseCurrentPopup(); }
            }
            else
            {
                var cactbot = overlayCreatorPage == OverlayCreatorPage.Cactbot;
                var choices = templates.Where(t => t.IsCactbot == cactbot).OrderBy(t => GetCactbotOverlayOrder(t.Name)).ToArray();
                if (choices.Length == 0) ImGui.TextWrapped(templates.Count == 0
                    ? text.Get("启动解析器后可选择悬浮窗模板。", "Start the parser to select an overlay template.")
                    : text.Get("没有可用的本地模板。", "No local templates are available."));
                else
                {
                    if (!choices.Any(t => t.Name == selectedCreationTemplate))
                    {
                        var previous = cactbot ? configuration.SelectedCactbotOverlay : configuration.SelectedOverlayTemplate;
                        selectedCreationTemplate = choices.FirstOrDefault(t => string.Equals(t.Name, previous, StringComparison.OrdinalIgnoreCase))?.Name ?? choices[0].Name;
                    }
                    ImGui.SetNextItemWidth(-1);
                    if (DactTheme.BeginCombo("##creation-template", cactbot ? FormatCactbotOverlayName(selectedCreationTemplate!) : selectedCreationTemplate!))
                    {
                        foreach (var item in choices)
                            if (ImGui.Selectable(cactbot ? FormatCactbotOverlayName(item.Name) : item.Name, item.Name == selectedCreationTemplate)) selectedCreationTemplate = item.Name;
                        ImGui.EndCombo();
                    }
                    var already = RegisteredOverlayNames().Contains(selectedCreationTemplate!, StringComparer.OrdinalIgnoreCase);
                    if (DactTheme.Button(already ? text.Get("查看已有悬浮窗", "View existing overlay") : text.Get("添加并打开", "Add and open")))
                    {
                        // Slash commands and the advanced settings still use the
                        // persisted template selection; keep both entry points in sync.
                        if (cactbot) configuration.SelectedCactbotOverlay = selectedCreationTemplate!;
                        else configuration.SelectedOverlayTemplate = selectedCreationTemplate!;
                        changed = true;
                        if (already || openHtmlOverlay(selectedCreationTemplate!)) { SelectManagedOverlay(selectedCreationTemplate); ImGui.CloseCurrentPopup(); }
                    }
                    if (already) ImGui.TextWrapped(text.Get("此模板已在列表中，可直接查看和打开。", "This template is already listed; select it to view or open it."));
                }
                if (cactbot) ImGui.TextWrapped(text.Get(
                    "文字提醒和时间轴可以同时打开；它们与旧版组合窗口互斥。其他 Cactbot 窗口可自由多开。",
                    "Alerts and timeline can be open together; both conflict with the legacy combined window. Other Cactbot overlays can be opened together freely."));
            }
            if (DactTheme.Button(text.Get("返回", "Back"))) overlayCreatorPage = null;
            ImGui.SameLine();
        }
        if (DactTheme.Button(text.Get("取消", "Cancel"))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        return changed;
    }
}
