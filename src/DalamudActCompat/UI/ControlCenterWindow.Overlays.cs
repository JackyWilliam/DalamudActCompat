using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Parser;
using DalamudActCompat.UI.Kit;

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

    // 悬浮窗 (screens.js overlays): Cactbot strip, then the overlay list beside its details.
    private bool DrawOverlays()
    {
        configuration.OverlayWindows ??= new(StringComparer.OrdinalIgnoreCase);
        var templates = getOverlayTemplates();
        var s = DactUi.S;
        var addLabel = text.Get("添加悬浮窗", "Add overlay");
        DactUi.PageHeader(text.Get("悬浮窗", "Overlays"),
            text.Get("选择一个悬浮窗，调整它的显示与操作。", "Select an overlay to manage its display and controls."),
            DactUi.ButtonWidth(addLabel, ButtonKind.Primary, "plus"),
            () =>
            {
                if (DactUi.Button(addLabel + "##overlay-add", ButtonKind.Primary, "plus"))
                {
                    overlayCreatorPage = null;
                    DactUi.OpenDialog(AddOverlayPopup);
                }
            });
        DrawCactbotManagement();

        var names = RegisteredOverlayNames();
        if (selectedCreatedOverlay is null || !names.Contains(selectedCreatedOverlay, StringComparer.OrdinalIgnoreCase))
            SelectManagedOverlay(names.FirstOrDefault());
        // Side by side when the details still get a usable width; narrow windows stack them
        // and keep scrolling rather than squeezing translated controls.
        var wide = ImGui.GetContentRegionAvail().X >= 560 * s;
        var height = Math.Max(330 * s, ImGui.GetContentRegionAvail().Y);
        var changed = false;
        if (DactUi.BeginPanel("overlay-list", new Vector2(wide ? 210 * s : -1, wide ? height : 230 * s), 10, 4))
            DrawManagedOverlayList(names, templates, wide);
        DactUi.EndPanel();
        if (wide) ImGui.SameLine(0, 14 * s);
        if (DactUi.BeginPanel("overlay-details", new Vector2(-1, height)))
        {
            if (selectedCreatedOverlay is { } name && configuration.OverlayWindows.TryGetValue(name, out var settings))
                changed |= DrawManagedOverlayDetails(name, settings, templates);
            else DactUi.Wrapped(text.Get("添加一个悬浮窗后，即可在这里设置。", "Add an overlay to configure it here."), DactTheme.Palette.Muted);
        }
        DactUi.EndPanel();
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
        var s = DactUi.S;
        var labels = new[] { text.Get("Cactbot 设置", "Cactbot settings"), text.Get("安装 / 更新", "Install / update"), text.Get("官方项目页", "Official project") };
        var actionsWidth = DactUi.ButtonWidth(labels[0]) + DactUi.ButtonWidth(labels[1]) + DactUi.ButtonWidth(labels[2], ButtonKind.Ghost, "external") + 24 * s;
        DactUi.BeginCard("cactbot-management", null, padding: 12, gap: 8);
        var inline = ImGui.GetContentRegionAvail().X - actionsWidth > 300 * s;
        var top = ImGui.GetCursorPos();
        var infoWidth = inline ? ImGui.GetContentRegionAvail().X - actionsWidth - 12 * s : ImGui.GetContentRegionAvail().X;
        ImGui.BeginGroup();
        using (DactUi.Gap(4))
        {
            var rowStart = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(infoWidth, 22 * s));
            var draw = ImGui.GetWindowDrawList();
            DactUi.DrawText(draw, rowStart, 22 * s, "Cactbot", DactTheme.Palette.Gold);
            var x = DactUi.TextWidth("Cactbot") + 8 * s;
            ImGui.SetCursorScreenPos(rowStart + new Vector2(x, 0));
            DactUi.Pill(FormatCactbotStatus(), CactbotStatusTone(), maxWidth: infoWidth - x);
            x += ImGui.GetItemRectSize().X + 8 * s;
            var parserLabel = text.Format($"解析器：{LocalizeState(parserStatus.State)}", $"Parser: {LocalizeState(parserStatus.State)}");
            if (x + DactUi.TextWidth(parserLabel, DactFonts.Small) <= infoWidth)
                DactUi.DrawText(draw, rowStart + new Vector2(x, 0), 22 * s, parserLabel,
                    parserStatus.State == ParserState.Running ? DactTheme.Palette.Muted : DactTheme.Palette.Gold, DactFonts.Small);
            ImGui.SetCursorScreenPos(rowStart + new Vector2(0, 26 * s));
            DactUi.Wrapped(text.Get(
                "提示中的玩家默认显示职业全称；可在 Cactbot 设置的“默认玩家代称”中修改。",
                "Player callouts default to full job names; change this under Default Player Label in Cactbot settings."),
                DactUi.HintColor, DactFonts.Small, infoWidth);
        }
        ImGui.EndGroup();
        var infoHeight = ImGui.GetItemRectSize().Y;
        if (inline) ImGui.SetCursorPos(new Vector2(top.X + infoWidth + 12 * s, top.Y + Math.Max(0, (infoHeight - 30 * s) * .5f)));
        if (DactUi.Button(labels[0] + "##cactbot-settings")) openCactbotSettings();
        // Long translations at large font sizes cannot fit both buttons on one line.
        DactUi.SameLineIfFits(DactUi.ButtonWidth(labels[1]));
        if (DactUi.Button(labels[1] + "##cactbot-install")) selectCactbotPackage();
        DactUi.SameLineIfFits(DactUi.ButtonWidth(labels[2], ButtonKind.Ghost, "external"));
        if (DactUi.Button(labels[2] + "##cactbot-project", ButtonKind.Ghost, "external")) OpenUrl("https://github.com/OverlayPlugin/cactbot");
        // Beside a taller description the buttons end early; the card must still clear the text.
        if (inline) ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), top.Y + infoHeight + ImGui.GetStyle().ItemSpacing.Y));
        DactUi.EndCard();
    }

    private PillTone CactbotStatusTone()
    {
        var status = getCactbotOperationStatus();
        return status.State switch
        {
            CactbotOperationState.Checking or CactbotOperationState.Installing => PillTone.Warn,
            CactbotOperationState.Error => PillTone.Error,
            _ when isCactbotInstalled() => PillTone.Ok,
            _ => PillTone.Off,
        };
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
        if (settings.IsVisible) return settings.IsUserHidden ? text.Get("已隐藏", "Hidden") : text.Get("已打开", "Opened");
        if (parserStatus.State != ParserState.Running) return text.Get("解析器未运行", "Parser stopped");
        if (!CanOpenManagedOverlay(name, settings, templates)) return text.Get("本地资源不可用", "Local asset unavailable");
        return text.Get("已关闭", "Closed");
    }

    private PillTone ManagedOverlayTone(string name, HtmlOverlayWindowSettings settings, IReadOnlyList<ActOverlayTemplate> templates)
    {
        if (settings.IsVisible) return settings.IsUserHidden ? PillTone.Warn : PillTone.Ok;
        if (parserStatus.State == ParserState.Running && !CanOpenManagedOverlay(name, settings, templates)) return PillTone.Error;
        return PillTone.Off;
    }

    private void DrawManagedOverlayList(string[] names, IReadOnlyList<ActOverlayTemplate> templates, bool wide)
    {
        var s = DactUi.S;
        DactUi.Text(text.Get("我的悬浮窗", "My overlays"), DactTheme.Palette.Muted, DactFonts.Small);
        // The rows scroll on their own; the all-overlays switch stays pinned below them.
        // gap + divider + gap + label + gap + switch row (4 + 1 + 4 + 16 + 4 + 24).
        var footerHeight = 53 * s;
        var listHeight = Math.Max(60 * s, ImGui.GetContentRegionAvail().Y - footerHeight);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 4) * s);
        if (ImGui.BeginChild("overlay-rows", new Vector2(-1, listHeight), false))
        {
            if (names.Length == 0) DactUi.Wrapped(text.Get("还没有创建悬浮窗。", "No overlays have been created yet."), DactTheme.Palette.Muted, DactFonts.Small);
            foreach (var name in names)
            {
                var settings = configuration.OverlayWindows[name];
                var template = templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                ImGui.PushID(name);
                var start = ImGui.GetCursorScreenPos();
                var width = ImGui.GetContentRegionAvail().X;
                var rowHeight = 18 * s + 16 * s + 2 * s + 18 * s;
                if (ImGui.InvisibleButton("select-overlay", new Vector2(width, rowHeight))) SelectManagedOverlay(name);
                var hovered = ImGui.IsItemHovered();
                var selected = string.Equals(name, selectedCreatedOverlay, StringComparison.OrdinalIgnoreCase);
                var draw = ImGui.GetWindowDrawList();
                var fill = DactMotion.Color(ImGuiP.GetItemID(), DactTheme.Palette.Hover with { W = selected ? 1 : hovered ? .5f : 0 }, DactMotion.Fast);
                if (fill.W > 0) draw.AddRectFilled(start, start + new Vector2(width, rowHeight), DactUi.U32(fill), 8 * s);
                if (selected)
                    draw.AddRect(start, start + new Vector2(width, rowHeight), DactUi.U32(DactTheme.Palette.Accent with { W = .55f }), 8 * s, ImDrawFlags.None, Math.Max(1, s));
                var status = OverlayKind(name, template) + " · " + ManagedOverlayStatus(name, settings, templates);
                var title = DactUi.Fit(OverlayDisplayName(name), width - 20 * s);
                DactUi.DrawText(draw, start + new Vector2(10 * s, 9 * s), 18 * s, title, selected ? DactTheme.Palette.Text : DactTheme.Palette.Muted);
                string line;
                using (DactFonts.Push(DactFonts.Small)) line = DactUi.Fit(status, width - 20 * s);
                DactUi.DrawText(draw, start + new Vector2(10 * s, 29 * s), 16 * s, line, DactTheme.Palette.Muted, DactFonts.Small);
                if (hovered && (title != OverlayDisplayName(name) || line != status)) DactUi.Tooltip(OverlayDisplayName(name) + "\n" + status);
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        DactUi.Divider();
        DactUi.Text(text.Get("全部悬浮窗", "All overlays"), DactTheme.Palette.Muted, DactFonts.Small);
        var hide = configuration.HideHtmlOverlaysWhenGameUnfocused;
        if (DactUi.ToggleRow(text.Get("游戏失焦时隐藏", "Hide when game loses focus"), ref hide)) setHideHtmlOverlaysWhenUnfocused(hide);
        if (ImGui.IsItemHovered()) DactUi.Tooltip(text.Get(
            "仅临时隐藏网页悬浮窗，不改变各悬浮窗保存的开启状态。",
            "Temporarily hides web overlays without changing their saved open state."));
    }

    private bool DrawManagedOverlayDetails(string name, HtmlOverlayWindowSettings settings, IReadOnlyList<ActOverlayTemplate> templates)
    {
        var s = DactUi.S;
        var changed = false;
        var cactbot = SelfHostedActRuntime.IsCactbotOverlayName(name);
        var template = templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        var available = CanOpenManagedOverlay(name, settings, templates);
        ImGui.PushID(name);

        // Title row: name, state, source, then 打开/关闭 · 隐藏/显示 · more, right-aligned.
        var openLabel = settings.IsVisible ? text.Get("关闭", "Close") : text.Get("打开", "Open");
        var hideLabel = settings.IsUserHidden ? text.Get("显示", "Show") : text.Get("隐藏", "Hide");
        var openKind = settings.IsVisible ? ButtonKind.Secondary : ButtonKind.Primary;
        var actionsWidth = DactUi.ButtonWidth(openLabel, openKind) + DactUi.ButtonWidth(hideLabel) + 28 * s + 16 * s;
        var width = ImGui.GetContentRegionAvail().X;
        var rowStart = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var titleRoom = Math.Max(60 * s, width - actionsWidth - 12 * s);
        var x = 0f;
        string title;
        using (DactFonts.Push(DactFonts.H2)) title = DactUi.Fit(OverlayDisplayName(name), titleRoom * .55f);
        DactUi.DrawText(draw, rowStart, 30 * s, title, DactTheme.Palette.Text, DactFonts.H2);
        using (DactFonts.Push(DactFonts.H2)) x += ImGui.CalcTextSize(title).X + 8 * s;
        ImGui.SetCursorScreenPos(rowStart + new Vector2(x, 4 * s));
        DactUi.Pill(ManagedOverlayStatus(name, settings, templates), ManagedOverlayTone(name, settings, templates), maxWidth: Math.Max(40 * s, titleRoom - x));
        x += ImGui.GetItemRectSize().X + 8 * s;
        var origin = text.Format($"来自 {OverlayKind(name, template)}", $"From {OverlayKind(name, template)}");
        if (x + DactUi.TextWidth(origin, DactFonts.Small) <= titleRoom)
            DactUi.DrawText(draw, rowStart + new Vector2(x, 0), 30 * s, origin, DactTheme.Palette.Muted, DactFonts.Small);

        ImGui.SetCursorScreenPos(rowStart + new Vector2(width - actionsWidth, 0));
        var canOpen = settings.IsVisible || (available && parserStatus.State == ParserState.Running);
        if (DactUi.Button(openLabel + "##overlay-open", openKind, disabled: !canOpen))
        { if (settings.IsVisible) closeHtmlOverlay(name); else openHtmlOverlay(name); }
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(hideLabel + "##overlay-hide", disabled: !settings.IsVisible))
        { settings.IsUserHidden = !settings.IsUserHidden; changed = true; }
        ImGui.SameLine(0, 8 * s);
        if (DactUi.IconButton("##overlay-more", "more", text.Get("更多", "More"), DactTheme.Palette.Hover)) ImGui.OpenPopup("overlay-actions");
        var removed = false;
        if (DactUi.BeginMenuPopup("overlay-actions"))
        {
            if (!cactbot && DactUi.MenuRow(text.Get("重命名", "Rename"), width: 220 * s))
            {
                overlayBeingRenamed = name; overlayRenameValue = OverlayDisplayName(name); overlayRenameFeedback = null;
                ImGui.CloseCurrentPopup();
            }
            if (DactUi.MenuRow(cactbot ? text.Get("移除并重置", "Remove and reset") : text.Get("删除悬浮窗", "Delete overlay"), danger: true, width: 220 * s))
            { deleteHtmlOverlay(name); SelectManagedOverlay(null); removed = true; ImGui.CloseCurrentPopup(); }
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 212 * s);
            DactUi.Hint(cactbot ? text.Get(
                "关闭并移除记录，清除窗口设置；保留本地模板。", "Close and remove this entry, clearing window settings while keeping the local template.")
                : text.Get("关闭悬浮窗并删除它保存的网址、位置、大小与显示设置。", "Close the overlay and delete its saved URL, position, size, and display settings."));
            ImGui.PopTextWrapPos();
            ImGui.EndPopup();
        }
        ImGui.SetCursorScreenPos(rowStart);
        ImGui.Dummy(new Vector2(width, 30 * s));
        if (removed) { ImGui.PopID(); return changed; }
        if (overlayBeingRenamed == name) changed |= DrawOverlayRenameEditor(name, settings);

        var page = (int)overlayDetailsPage;
        if (DactUi.Tabs("overlay-detail-tabs", [text.Get("常规", "General"), text.Get("快捷键", "Hotkeys"), text.Get("来源", "Source")], ref page))
            overlayDetailsPage = (OverlayDetailsPage)page;
        if (cactbot && (!available || parserStatus.State != ParserState.Running))
        {
            DactUi.Wrapped(parserStatus.State != ParserState.Running
                ? text.Get("启动解析器后才能打开该悬浮窗。", "Start the parser before opening this overlay.")
                : text.Get("当前 Cactbot 包缺少该页面，不会回退到远程地址。", "The current Cactbot package does not contain this page; no online fallback will be used."),
                DactTheme.Palette.Gold, DactFonts.Small);
            if (settings.OpenOnStartup && DactUi.Button(text.Get("停止自动打开", "Disable startup") + "##overlay-stop-startup"))
            { settings.OpenOnStartup = false; changed = true; }
            if (settings.OpenOnStartup) ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(text.Get("移除并重置", "Remove and reset") + "##overlay-reset", ButtonKind.Danger))
            { deleteHtmlOverlay(name); SelectManagedOverlay(null); ImGui.PopID(); return changed; }
            DactUi.Divider();
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
        var s = DactUi.S;
        var changed = false;
        if (DactUi.BeginGrid("overlay-general", 20))
        {
            using (DactUi.Gap(10))
            {
                DactUi.Text(text.Get("显示与启动", "Display and startup"), DactTheme.Palette.Muted, DactFonts.Small);
                var startup = settings.OpenOnStartup;
                if (DactUi.ToggleRow(text.Get("启动时自动打开", "Open on startup"), ref startup)) { settings.OpenOnStartup = startup; changed = true; }
                var autoHide = settings.AutoHideOutOfCombat;
                if (DactUi.ToggleRow(text.Get("脱战隐藏", "Hide out of combat"), ref autoHide)) { settings.AutoHideOutOfCombat = autoHide; changed = true; }
            }
            DactUi.NextColumn();
            using (DactUi.Gap(10))
            {
                DactUi.Text(text.Get("位置、缩放、穿透与锁定", "Position, scale, click-through, and lock"), DactTheme.Palette.Muted, DactFonts.Small);
                var clickThrough = settings.IsClickThrough;
                if (DactUi.ToggleRow(text.Get("鼠标穿透", "Click-through"), ref clickThrough)) { settings.SetClickThrough(clickThrough); changed = true; }
                var locked = settings.IsLocked;
                if (DactUi.ToggleRow(text.Get("锁定位置和大小", "Lock position and size"), ref locked)) { settings.SetLocked(locked); changed = true; }
            }
            DactUi.EndGrid();
        }

        DactUi.BeginField(text.Get("页面缩放", "Page zoom"));
        var zoom = settings.ZoomFactor;
        if (DactUi.SliderFloat("overlay-zoom", ref zoom, .5f, 2)) { settings.ZoomFactor = zoom; changed = true; }
        DactUi.EndField();

        var editLabel = settings.IsEditing ? text.Get("完成位置编辑", "Finish position editing") : text.Get("编辑位置和大小", "Edit position and size");
        if (DactUi.Button(editLabel + "##overlay-edit", settings.IsEditing ? ButtonKind.Primary : ButtonKind.Secondary, "target",
                disabled: !available && !settings.IsVisible))
        {
            var edit = !settings.IsEditing;
            if (!edit || settings.IsVisible || openHtmlOverlay(name)) { settings.SetEditing(edit); changed = true; }
        }
        var note = settings.IsEditing ? text.Get(
            "单击可操作网页；按住并拖动可移动，拖动右下角斜纹可缩放。",
            "Click to use the page; hold and drag to move, or drag the striped bottom-right grip to resize.") : text.Get(
            "位置编辑时会暂时关闭穿透与锁定；完成后会恢复。",
            "Position editing temporarily disables click-through and locking; finishing restores them.");
        // The note sits beside the button when it fits, as in the design, otherwise below it.
        var room = ImGui.GetWindowContentRegionMax().X - ImGui.GetItemRectMax().X + ImGui.GetWindowPos().X - 10 * s;
        if (room > 160 * s)
        {
            ImGui.SameLine(0, 10 * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (30 * s - ImGui.GetTextLineHeight()) * .5f);
            DactUi.Wrapped(note, DactUi.HintColor, DactFonts.Small, room);
        }
        else DactUi.Hint(note);
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
        var s = DactUi.S;
        var changed = false;
        var cactbot = SelfHostedActRuntime.IsCactbotOverlayName(name);
        var custom = !cactbot && template is null;
        DactUi.Text(text.Get("页面来源", "Page source"), DactTheme.Palette.Muted, DactFonts.Small);
        DactUi.Text(OverlayKind(name, template));
        var address = ManagedOverlaySource(template, settings);
        if (address.Length > 0)
        {
            DactUi.BeginField(cactbot ? text.Get("本地页面", "Local page") : text.Get("页面网址", "Page URL"));
            DactUi.Input("source-address", ref address, "", Math.Max(2048, address.Length + 1), readOnly: true);
            DactUi.EndField();
        }
        else DactUi.Wrapped(text.Get("本地资源不可用", "Local asset unavailable"), DactTheme.Palette.Gold, DactFonts.Small);
        if (cactbot) DactUi.Hint(text.Get("此页面随 Cactbot 一起安装和更新。", "This page is installed and updated with Cactbot."));
        if (custom)
        {
            if (editingOverlaySource != name && DactUi.Button(text.Get("修改网址", "Edit URL") + "##overlay-edit-url"))
            { editingOverlaySource = name; overlaySourceDraft = settings.SourceUrl; overlaySourceFeedback = null; }
            if (editingOverlaySource == name)
            {
                DactUi.Input("edit-source", ref overlaySourceDraft, "https://", 2048);
                if (DactUi.Button(text.Get("保存网址", "Save URL") + "##overlay-save-url", ButtonKind.Primary)) changed |= SaveManagedOverlaySource(name, overlaySourceDraft);
                ImGui.SameLine(0, 8 * s);
                if (DactUi.Button(text.Get("取消", "Cancel") + "##overlay-cancel-url")) { editingOverlaySource = null; overlaySourceFeedback = null; }
            }
            if (overlaySourceFeedback is not null) DactUi.Wrapped(overlaySourceFeedback, DactUi.Danger, DactFonts.Small);
        }

        DactUi.Divider();
        DactUi.Text(text.Get("数据连接", "Data connection"), DactTheme.Palette.Muted, DactFonts.Small);
        // Only custom pages currently report a detected WebSocket connection.
        // Window visibility is not evidence that a page is receiving game data.
        var connection = !settings.IsVisible
            ? custom ? text.Get("打开后自动检测连接", "Connection will be detected when opened") : text.Get("已关闭", "Closed")
            : !custom ? text.Get("页面已打开；此页面没有独立的连接检测结果。", "The page is open; no separate connection detection result is available for this page.")
            : string.IsNullOrWhiteSpace(settings.ConnectionStateDetail)
                ? text.Get("打开后自动检测连接", "Connection will be detected when opened") : text.SystemMessage(settings.ConnectionStateDetail);
        DactUi.Wrapped(connection);
        if (custom)
        {
            DactUi.Disclosure(text.Get("连接高级设置", "Advanced connection settings"), ref overlayConnectionAdvancedOpen);
            if (overlayConnectionAdvancedOpen)
            {
                var reconnect = false;
                var modes = Enum.GetValues<OverlayConnectionMode>();
                var index = Array.IndexOf(modes, settings.ConnectionMode);
                if (DactUi.Select("connection-mode", modes.Select(GetOverlayConnectionModeLabel).ToArray(), ref index, 280 * s) && index >= 0)
                { settings.ConnectionMode = modes[index]; reconnect = true; }
                ImGui.SameLine(0, 8 * s);
                if (DactUi.Button(text.Get("重新检测", "Detect again") + "##overlay-detect")) { settings.ConnectionMode = OverlayConnectionMode.Auto; reconnect = true; }
                DactUi.Hint(text.Get("默认自动检测；手动模式只用于检测失败时微调。", "Automatic detection is the default. Manual modes are only for troubleshooting."));
                if (reconnect) { ReconnectManagedOverlay(name, settings); changed = true; }
            }
        }
        return changed;
    }

    private bool overlayConnectionAdvancedOpen;

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
        // The dialog centres on the DACT window; a narrow window keeps it 24 px inside each edge.
        var hostWidth = (DactUi.HostMax.X - DactUi.HostMin.X) / DactUi.S;
        var width = hostWidth > 0 ? Math.Clamp(hostWidth - 48, 320, 560) : 560;
        if (!DactUi.BeginDialog(AddOverlayPopup, width)) return false;
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var changed = false;
        DactUi.Text(text.Get("添加悬浮窗", "Add overlay"), size: DactFonts.H2);
        if (overlayCreatorPage is null)
        {
            DactUi.Hint(text.Get("选择悬浮窗来源", "Choose an overlay source"));
            foreach (var page in Enum.GetValues<OverlayCreatorPage>())
            {
                var label = page switch { OverlayCreatorPage.Cactbot => "Cactbot", OverlayCreatorPage.Template => text.Get("从模板创建", "Create from template"), _ => text.Get("从网址创建", "Create from URL") };
                if (DactUi.Button(label + "##overlay-source-" + page, width: ImGui.GetContentRegionAvail().X))
                {
                    overlayCreatorPage = page;
                    selectedCreationTemplate = null;
                    customOverlayFeedback = null;
                }
            }
        }
        else if (overlayCreatorPage == OverlayCreatorPage.Url)
        {
            changed |= DrawCustomHtmlOverlayCreator();
            if (changed)
            {
                overlayDetailsPage = OverlayDetailsPage.General;
                DactUi.CloseDialog(AddOverlayPopup);
            }
        }
        else
        {
            var cactbot = overlayCreatorPage == OverlayCreatorPage.Cactbot;
            var choices = templates.Where(t => t.IsCactbot == cactbot).OrderBy(t => GetCactbotOverlayOrder(t.Name)).ToArray();
            if (choices.Length == 0)
                DactUi.Wrapped(templates.Count == 0
                    ? text.Get("启动解析器后可选择悬浮窗模板。", "Start the parser to select an overlay template.")
                    : text.Get("没有可用的本地模板。", "No local templates are available."), palette.Muted);
            else
            {
                if (!choices.Any(t => t.Name == selectedCreationTemplate))
                {
                    var previous = cactbot ? configuration.SelectedCactbotOverlay : configuration.SelectedOverlayTemplate;
                    selectedCreationTemplate = choices.FirstOrDefault(t => string.Equals(t.Name, previous, StringComparison.OrdinalIgnoreCase))?.Name ?? choices[0].Name;
                }
                var labels = choices.Select(t => cactbot ? FormatCactbotOverlayName(t.Name) : t.Name).ToArray();
                var index = Array.FindIndex(choices, t => t.Name == selectedCreationTemplate);
                DactUi.BeginField(text.Get("模板", "Template"));
                if (DactUi.Select("creation-template", labels, ref index)) selectedCreationTemplate = choices[index].Name;
                DactUi.EndField();
                var already = RegisteredOverlayNames().Contains(selectedCreationTemplate!, StringComparer.OrdinalIgnoreCase);
                if (DactUi.Button((already ? text.Get("查看已有悬浮窗", "View existing overlay") : text.Get("添加并打开", "Add and open")) + "##creation-add",
                        ButtonKind.Primary))
                {
                    // Slash commands and the advanced settings still use the
                    // persisted template selection; keep both entry points in sync.
                    if (cactbot) configuration.SelectedCactbotOverlay = selectedCreationTemplate!;
                    else configuration.SelectedOverlayTemplate = selectedCreationTemplate!;
                    changed = true;
                    if (already || openHtmlOverlay(selectedCreationTemplate!))
                    {
                        SelectManagedOverlay(selectedCreationTemplate);
                        DactUi.CloseDialog(AddOverlayPopup);
                    }
                }
                if (already) DactUi.Hint(text.Get("此模板已在列表中，可直接查看和打开。", "This template is already listed; select it to view or open it."));
            }
            if (cactbot) DactUi.Hint(text.Get(
                "文字提醒和时间轴可以同时打开；它们与旧版组合窗口互斥。其他 Cactbot 窗口可自由多开。",
                "Alerts and timeline can be open together; both conflict with the legacy combined window. Other Cactbot overlays can be opened together freely."));
        }
        DactUi.Divider();
        // 返回 sits on the left of a sub-page; 取消 always closes from the right.
        if (overlayCreatorPage is not null && DactUi.Button(text.Get("返回", "Back") + "##overlay-add-back", ButtonKind.Ghost))
            overlayCreatorPage = null;
        var cancel = text.Get("取消", "Cancel");
        if (overlayCreatorPage is not null) ImGui.SameLine(0, 8 * s);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - DactUi.ButtonWidth(cancel)));
        if (DactUi.Button(cancel + "##overlay-add-cancel")) DactUi.CloseDialog(AddOverlayPopup);
        DactUi.EndDialog(AddOverlayPopup);
        return changed;
    }
}
