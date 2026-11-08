using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Parser;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 1.0 control-centre frame (tools/ui-redesign/screens.js: header, sidebar, shell): a 48 px
// header, a 176 px sidebar and the page area. Pages fade and rise in when switched; the
// sidebar's selection slides between items.
public sealed partial class ControlCenterWindow
{
    private sealed record NavEntry(Page? Page, string Icon, string Label, Action? Open = null, int Badge = 0);

    private Page shownPage = Page.Overview;
    private double pageEnteredAt = double.NegativeInfinity;

    private void DrawShell(CloudClientSnapshot cloudSnapshot, Action drawPage)
    {
        DactUi.BeginFrame();
        DactUi.BeginFrameDialogs();
        DactUi.HostMin = ImGui.GetWindowPos();
        DactUi.HostMax = ImGui.GetWindowPos() + ImGui.GetWindowSize();
        var s = DactUi.S;
        var line = Math.Max(1, s);
        var windowMin = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();
        var headerBottom = windowMin.Y + 48 * s;
        DrawShellHeader(cloudSnapshot, windowMin, windowSize.X, 48 * s);
        draw.AddRectFilled(new Vector2(windowMin.X, headerBottom), new Vector2(windowMin.X + windowSize.X, headerBottom + line), DactUi.U32(DactUi.DividerColor));

        var bodyTop = headerBottom + line;
        var bodyHeight = windowMin.Y + windowSize.Y - bodyTop;
        ImGui.SetCursorScreenPos(new Vector2(windowMin.X, bodyTop));
        DrawSidebar(cloudSnapshot, bodyHeight);
        // 艾欧泽亚's game window sprite ends in a 10 px shadow tail, so its metal rim and the paper
        // inside stop 16 px above the window's edge. The page and the divider stop there too;
        // otherwise a long page (账号与云同步's invite list) scrolled over the bottom rim.
        var rimInset = DactTheme.GameFrameBottomInset;
        var separatorX = windowMin.X + 176 * s;
        draw.AddRectFilled(new Vector2(separatorX, bodyTop), new Vector2(separatorX + line, windowMin.Y + windowSize.Y - rimInset), DactUi.U32(DactUi.DividerColor));
        ImGui.SetCursorScreenPos(new Vector2(separatorX + line, bodyTop));
        DrawPageArea(bodyHeight - rimInset, drawPage);
        DrawDiscoveryToast(windowMin, windowSize);
        // Both open from the header, in this window's ID scope; the dropdown lies above the page.
        DrawHeaderSearchDropdown(cloudSnapshot);
        DrawFeedbackDialog(cloudSnapshot);
        DactUi.DrawScrim();
    }

    private void DrawShellHeader(CloudClientSnapshot cloudSnapshot, Vector2 windowMin, float width, float height)
    {
        var s = DactUi.S;
        var draw = ImGui.GetWindowDrawList();
        var ultimate = DactTheme.Palette.Ultimate;
        if (ultimate is not null)
        {
            // The nameplate's title strip and band run edge to edge under the header.
            var windowMax = windowMin + ImGui.GetWindowSize();
            var rounding = ImGui.GetStyle().WindowRounding;
            draw.PushClipRect(windowMin, windowMax, false);
            UltimateSkins.DrawTitle(draw, windowMin, new Vector2(windowMax.X, windowMin.Y + height), rounding, ultimate);
            UltimateSkins.DrawBand(draw, new Vector2(windowMin.X, windowMin.Y + height), new Vector2(windowMax.X, windowMin.Y + height + 6 * s), ultimate, s);
            draw.AddRect(windowMin, windowMax, ImGui.GetColorU32(ultimate.Frame), rounding);
            draw.PopClipRect();
        }
        else if (DactTheme.Palette.Glass)
            DactTheme.DrawGlassSurface(draw, windowMin + new Vector2(6 * s), new Vector2(windowMin.X + width - 6 * s, windowMin.Y + height - 4 * s), 12 * s, DactTheme.Palette.Raised);

        // Left: logo (also the hidden-skin discovery target) and the product name.
        var logoSize = 26 * s;
        var logoMin = new Vector2(windowMin.X + (ultimate is null ? 14 : 26) * s, windowMin.Y + (height - logoSize) * .5f);
        if (ultimate is not null)
        {
            var viewport = ImGui.GetWindowViewport();
            draw.PushClipRect(viewport.Pos, viewport.Pos + viewport.Size, false);
            UltimateSkins.DrawEmblem(draw, new Vector2(windowMin.X + 1, logoMin.Y + logoSize * .5f), 30 * s, ultimate);
            draw.PopClipRect();
        }
        // While the splash plays, its flying logo lands here; the real one appears when it ends.
        headerLogoSpot = (logoMin, logoSize);
        if (!(SplashActive && cloudSnapshot.CanUseDact))
            draw.AddImageRounded(logoTexture.GetWrapOrEmpty().Handle, logoMin, logoMin + new Vector2(logoSize), Vector2.Zero, Vector2.One,
                ImGui.GetColorU32(Vector4.One), 6 * s);
        ImGui.SetCursorScreenPos(logoMin);
        if (ImGui.InvisibleButton("##dact-logo", new Vector2(logoSize)))
            DiscoverSkin(skinDiscoveries.ClickLogo(configuration.Appearance, Environment.TickCount64));
        var titleX = logoMin.X + logoSize + 10 * s;
        DactUi.DrawText(draw, new Vector2(titleX, windowMin.Y), height, "DACT", DactTheme.Palette.Text);

        // Right: cloud chip, help, close (right to left, 10 px apart, 10 px from the edge).
        searchSpot = null;
        feedbackSpot = null;
        var right = windowMin.X + width - (DactTheme.Palette.Glass ? 16 : 10) * s;
        var buttonTop = windowMin.Y + (height - 28 * s) * .5f;
        right -= 28 * s;
        ImGui.SetCursorScreenPos(new Vector2(right, buttonTop));
        if (DactUi.IconButton("##shell-close", "close", text.Get("关闭窗口", "Close window"))) HideAnimated();
        // Help sits beside close on the sign-in, register and binding pages too (user request);
        // there it opens 快速上手, whose first steps are signing in and linking Discord.
        right -= 38 * s;
        ImGui.SetCursorScreenPos(new Vector2(right, buttonTop));
        if (DactUi.IconButton("##shell-help", "help", text.Get("帮助", "Help")))
        {
            if (cloudSnapshot.CanUseDact || OpenHelpTopic is not { } openTopic) openHelp();
            else openTopic("GettingStarted");
        }
        if (cloudSnapshot.CanUseDact)
        {
            // Manual feedback sits beside help (user decision); replies come back as 官方通知.
            right -= 38 * s;
            ImGui.SetCursorScreenPos(new Vector2(right, buttonTop));
            if (DactUi.IconButton("##shell-feedback", "feedback", text.Get("反馈问题或建议", "Send feedback"))) OpenFeedback();
            feedbackSpot = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        }

        var chipLeft = right;
        if (cloudSnapshot.CanUseDact)
        {
            var (fullLabel, color) = ResolveCloudStatus(cloudSnapshot);
            // Long translations are shortened so the chip never reaches the centred search field.
            string label;
            using (DactFonts.Push(DactFonts.Small)) label = DactUi.Fit(fullLabel, width * .3f);
            var chipWidth = DactUi.TextWidth(label, DactFonts.Small) + 32 * s;
            chipLeft = right - 10 * s - chipWidth;
            var chipMin = new Vector2(chipLeft, windowMin.Y + (height - 26 * s) * .5f);
            ImGui.SetCursorScreenPos(chipMin);
            if (ImGui.InvisibleButton("##shell-cloud-chip", new Vector2(chipWidth, 26 * s)))
            {
                // The status popover hangs under the chip, right edges aligned.
                cloudQuickPopupRequested = true;
                cloudChipAnchor = new Vector2(chipMin.X + chipWidth, chipMin.Y + 33 * s);
            }
            var hovered = ImGui.IsItemHovered();
            if (hovered) DactUi.Tooltip(label == fullLabel ? text.Get("查看云同步状态", "View cloud sync status") : fullLabel);
            var light = DactTheme.Palette.Light;
            var plateChip = DactTheme.Palette.Ultimate;
            var chipMax = chipMin + new Vector2(chipWidth, 26 * s);
            var warm = DactMotion.Float(ImGuiP.GetItemID(), hovered ? 1 : 0, DactMotion.Fast);
            if (light)
            {
                // 艾欧泽亚 (redesigned, user: the chocolate pill was ugly): the game's cream inset with
                // a tan rim that warms to orange on hover, the status lamp with its halo and dark text.
                draw.AddRectFilled(chipMin, chipMax, DactUi.U32(Vector4.Lerp(DactUi.LightCream, DactUi.LightCreamHover, warm)), 13 * s);
                draw.AddRect(chipMin, chipMax, DactUi.U32(Vector4.Lerp(DactUi.LightRim, DactUi.LightGlow, warm)), 13 * s, ImDrawFlags.None, Math.Max(1, s));
                draw.AddCircleFilled(chipMin + new Vector2(13 * s, 13 * s), 6 * s, DactUi.U32(color with { W = .22f }), 16);
                draw.AddCircleFilled(chipMin + new Vector2(13 * s, 13 * s), 3.5f * s, DactUi.U32(color), 12);
                DactUi.DrawText(draw, chipMin + new Vector2(22 * s, 0), 26 * s, label, DactTheme.Palette.Text, DactFonts.Small);
            }
            else
            {
                var baseFill = plateChip?.Groove ?? DactTheme.Palette.Hover;
                draw.AddRectFilled(chipMin, chipMax, DactUi.U32(Vector4.Lerp(baseFill, DactTheme.Palette.Accent, .14f * warm)), 13 * s);
                if (plateChip is not null)
                    draw.AddRect(chipMin, chipMax, DactUi.U32(plateChip.Frame with { W = .9f }), 13 * s, ImDrawFlags.None, Math.Max(1, s));
                draw.AddCircleFilled(chipMin + new Vector2(13 * s, 13 * s), 3 * s, DactUi.U32(color), 12);
                DactUi.DrawText(draw, chipMin + new Vector2(22 * s, 0), 26 * s, label, color, DactFonts.Small);
            }
        }

        // 全局搜索 field, centred on the whole window (user request) unless the chip needs the room;
        // a window too narrow for it keeps the shortcut only.
        var dragLeft = titleX + DactUi.TextWidth("DACT") + 8 * s;
        var dragRight = chipLeft - 8 * s;
        var searchWidth = HeaderSearchWidth;
        var searchLeft = Math.Min(windowMin.X + (width - searchWidth) * .5f, chipLeft - 12 * s - searchWidth);
        if (cloudSnapshot.CanUseDact && searchLeft >= dragLeft + 12 * s)
        {
            var searchMin = new Vector2(searchLeft, windowMin.Y + (height - 30 * s) * .5f);
            searchSpot = (searchMin, searchMin + new Vector2(searchWidth, 30 * s));
            DrawHeaderSearchField(searchMin, searchWidth, 30 * s);
            // The drag area continues on both sides of the field.
            DrawHeaderDrag(dragLeft, searchLeft - 8 * s, windowMin.Y, height, "##shell-drag-left");
            DrawHeaderDrag(searchLeft + searchWidth + 8 * s, dragRight, windowMin.Y, height, "##shell-drag-right");
        }
        else DrawHeaderDrag(dragLeft, dragRight, windowMin.Y, height, "##shell-drag");
        // The search field splits the drag area in two. Both halves report to the controller once
        // per frame: told separately, the idle half cancelled the drag the other had just started,
        // so the header could not move the window at all.
        headerDrag.ObserveHandle(ImGui.GetFrameCount(), ImGui.GetMousePos(), ImGui.GetWindowPos(), headerDragClicked, headerDragActive);
        headerDragClicked = headerDragActive = false;
    }

    private bool headerDragClicked;
    private bool headerDragActive;

    // Everything between the title and the chip (except the search field) drags the window.
    private void DrawHeaderDrag(float left, float right, float top, float height, string id)
    {
        if (right <= left) return;
        ImGui.SetCursorScreenPos(new Vector2(left, top));
        ImGui.InvisibleButton(id, new Vector2(right - left, height));
        headerDragClicked |= ImGui.IsItemClicked(ImGuiMouseButton.Left);
        headerDragActive |= ImGui.IsItemActive();
    }

    private NavEntry?[] NavigationEntries(CloudClientSnapshot cloudSnapshot)
    {
        var friends = cloudSnapshot.CanUseDact ? Friends : null;
        var unread = 0;
        if (friends?.Snapshot is { } chat)
        {
            unread = chat.Conversations.Values.Where(c => c.Unread).Sum(c => Math.Max(1, c.UnreadCount)) +
                     (chat.Friends?.Requests.Count(r => r.Direction == "incoming") ?? 0);
        }

        return
        [
            new NavEntry(Page.Overview, "home", text.Get("主页", "Home")),
            new NavEntry(Page.Meter, "meter", text.Get("战斗统计", "Combat Meter")),
            new NavEntry(Page.History, "history", text.Get("战斗记录", "Encounter history")),
            new NavEntry(Page.Overlays, "layers", text.Get("悬浮窗", "Overlays")),
            new NavEntry(Page.Extensions, "plug", text.Get("扩展", "Extensions")),
            friends is null ? null : new NavEntry(Page.Friends, "users", text.Get("好友", "Friends"), Badge: unread),
            // Stays in the rail while AI is off; the page then explains how to switch it on.
            new NavEntry(Page.Ai, "sparkle", text.Get("AI 助手", "AI assistant")),
            null,
            new NavEntry(Page.Cloud, "cloud", text.Get("账号与云同步", "Account & Cloud")),
            new NavEntry(Page.Diagnostics, "gear", text.Get("设置", "Settings")),
        ];
    }

    private sealed class NavSweepState
    {
        public double StartedAt = double.NegativeInfinity;
        public bool Lit;
        public float Shown;
    }

    private readonly Dictionary<int, NavSweepState> navSweeps = new();

    // How far a rail item's tab mark is lit, 0..1 from the left. Lighting up (hover, or a click
    // even on the current page) sweeps it in anew; going dark draws it back quickly. Items that
    // are lit when the window first draws them show lit without a sweep.
    private float NavSweep(int index, bool lit, bool clicked)
    {
        if (!navSweeps.TryGetValue(index, out var sweep))
            navSweeps[index] = sweep = new NavSweepState { Lit = lit, Shown = lit ? 1 : 0 };
        if (clicked || lit && !sweep.Lit) sweep.StartedAt = ImGui.GetTime();
        sweep.Lit = lit;
        sweep.Shown = lit
            ? DactMotion.Progress(sweep.StartedAt, .32f)
            : DactMotion.Disabled ? 0 : sweep.Shown * MathF.Exp(-ImGui.GetIO().DeltaTime * 18);
        return sweep.Shown;
    }

    private readonly Dictionary<int, double> navPressedAt = new();

    // The RCloud site's rail icons, in every skin (user request): hovering tilts the icon -12°
    // about a point just below its centre and lifts it 1 px; a click squeezes it to 85 % and back
    // (190 ms) and throws six sparks outward (280–400 ms). The icon's own vertices are turned, so
    // any icon works. Sparks take the skin's accent on 艾欧泽亚, where white would vanish.
    private void NavIconMotion(ImDrawListPtr draw, int firstVertex, Vector2 pivot, float tilt, int index, bool clicked)
    {
        var s = DactUi.S;
        var now = ImGui.GetTime();
        if (clicked) navPressedAt[index] = now;
        var elapsed = now - navPressedAt.GetValueOrDefault(index, double.NegativeInfinity);
        var squeeze = 1f;
        if (!DactMotion.Disabled && elapsed is >= 0 and < .19)
        {
            var t = (float)(elapsed / .19);
            squeeze = t < .4f ? 1 - .15f * t / .4f : .85f + .15f * (t - .4f) / .6f;
        }
        if (tilt > .001f || squeeze < 1)
        {
            var angle = -12f * MathF.PI / 180 * tilt;
            var cos = MathF.Cos(angle) * squeeze;
            var sin = MathF.Sin(angle) * squeeze;
            var lift = new Vector2(0, -s * tilt);
            for (var v = firstVertex; v < draw.VtxBuffer.Size; v++)
            {
                var vertex = draw.VtxBuffer[v];
                var d = vertex.Pos - pivot;
                vertex.Pos = pivot + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos) + lift;
                draw.VtxBuffer[v] = vertex;
            }
        }
        if (DactMotion.Disabled || elapsed is < 0 or >= .41) return;
        var palette = DactTheme.Palette;
        for (var i = 0; i < 6; i++)
        {
            var progress = (float)(elapsed / (.28 + i * .024));
            if (progress >= 1) continue;
            var eased = DactMotion.EaseOutCubic(progress);
            var direction = (i * 60 + 15) * MathF.PI / 180;
            var center = pivot + new Vector2(MathF.Cos(direction), MathF.Sin(direction)) * (15 + i % 3 * 3) * s * eased;
            var half = (i % 2 == 1 ? 1f : 1.5f) * s * (1 - .75f * eased);
            var alpha = .9f * (1 - eased);
            var spark = palette.Light ? palette.Accent : i % 2 == 1 ? new Vector4(.875f, .914f, 1, 1) : new Vector4(.973f, .984f, 1, 1);
            var glow = palette.Light ? palette.Gold : new Vector4(.733f, .816f, 1, 1);
            draw.AddRectFilled(center - new Vector2(half + 1.5f * s), center + new Vector2(half + 1.5f * s), DactUi.U32(glow with { W = .2f * alpha }), 2 * s);
            draw.AddRectFilled(center - new Vector2(half), center + new Vector2(half), DactUi.U32(spark with { W = alpha }), s);
        }
    }

    private void DrawSidebar(CloudClientSnapshot cloudSnapshot, float height)
    {
        var s = DactUi.S;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 12) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2) * s);
        ImGui.BeginChild("control-center-sidebar", new Vector2(176 * s, height), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysUseWindowPadding);
        var draw = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        var itemHeight = 34 * s;
        var origin = ImGui.GetCursorScreenPos();
        var entries = NavigationEntries(cloudSnapshot);

        // Selection background first, so it slides under the labels.
        var offsets = new float[entries.Length];
        var y = 0f;
        var activeOffset = 0f;
        for (var i = 0; i < entries.Length; i++)
        {
            offsets[i] = y;
            if (entries[i]?.Page == selectedPage) activeOffset = y;
            y += (entries[i] is null ? 13 * s : itemHeight) + 2 * s;
        }
        navigationSpots.Clear();
        navigationAreaSpot = (origin, origin + new Vector2(width, y - 2 * s));
        var highlight = DactMotion.Float("##nav-highlight", activeOffset, DactMotion.Normal);
        if (DactTheme.Palette.Light)
        {
            // 艾欧泽亚: the game's golden selection glow, strongest on the left.
            var left = origin + new Vector2(0, highlight);
            draw.AddRectFilledMultiColor(left, left + new Vector2(width, itemHeight),
                DactUi.U32(new Vector4(.99f, .83f, .55f, .95f)), DactUi.U32(new Vector4(.99f, .83f, .55f, 0)),
                DactUi.U32(new Vector4(.99f, .83f, .55f, 0)), DactUi.U32(new Vector4(.99f, .83f, .55f, .95f)));
        }
        else draw.AddRectFilled(origin + new Vector2(0, highlight), origin + new Vector2(width, highlight + itemHeight),
            DactUi.U32(DactTheme.Palette.Hover), 7 * s);

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            ImGui.SetCursorScreenPos(origin + new Vector2(0, offsets[i]));
            if (entry is null)
            {
                ImGui.Dummy(new Vector2(width, 13 * s));
                draw.AddRectFilled(origin + new Vector2(4 * s, offsets[i] + 6 * s), origin + new Vector2(width - 4 * s, offsets[i] + 6 * s + Math.Max(1, s)),
                    DactUi.U32(DactUi.DividerColor));
                continue;
            }

            var active = entry.Page == selectedPage;
            var clickedItem = ImGui.InvisibleButton($"##nav-{i}", new Vector2(width, itemHeight));
            if (clickedItem)
            {
                if (entry.Page is { } page) selectedPage = page;
                else entry.Open?.Invoke();
            }
            var id = ImGuiP.GetItemID();
            var hovered = ImGui.IsItemHovered();
            var min = ImGui.GetItemRectMin();
            if (entry.Page is { } spotPage) navigationSpots[spotPage] = (min, min + new Vector2(width, itemHeight));
            var hover = DactMotion.Float(id, hovered && !active ? 1 : 0, DactMotion.Fast);
            if (hover > 0) draw.AddRectFilled(min, min + new Vector2(width, itemHeight), DactUi.U32(DactTheme.Palette.Hover with { W = .55f * hover }), 7 * s);
            var iconColor = DactMotion.Color(id + 1, active ? DactTheme.Palette.Accent : hovered ? DactTheme.Palette.Text : DactTheme.Palette.Muted, DactMotion.Fast);
            var labelColor = DactMotion.Color(id + 2, active || hovered ? DactTheme.Palette.Text : DactTheme.Palette.Muted, DactMotion.Fast);
            var iconMin = min + new Vector2(10 * s, (itemHeight - 16 * s) * .5f);
            var iconVertex = draw.VtxBuffer.Size;
            DactIcons.Draw(draw, entry.Icon, iconMin, 16 * s, iconColor);
            NavIconMotion(draw, iconVertex, iconMin + new Vector2(8 * s, 8.8f * s), DactMotion.Float(id + 3, hovered ? 1 : 0, DactMotion.Fast), i,
                clickedItem);
            // Long translations end in an ellipsis inside the 176 px rail; the tooltip has the full name.
            var labelRoom = width - 46 * s - (entry.Badge > 0 ? 30 * s : 0);
            var label = DactUi.Fit(entry.Label, labelRoom);
            if (label != entry.Label && hovered) DactUi.Tooltip(entry.Label);
            DactUi.DrawText(draw, min + new Vector2(36 * s, 0), itemHeight, label, labelColor);
            // Nameplate skins give every rail item its tab mark under the label. The lit mark
            // sweeps in from left to right on hover, and once more on a click (user request); the
            // current page's stays lit.
            if (DactTheme.Palette.Ultimate is { } plate)
            {
                var mark = min + new Vector2(36 * s + 31 * s, 28 * s);
                UltimateSkins.DrawTabMark(draw, mark, false, false, plate, s);
                var lit = NavSweep(i, hovered || active, clickedItem);
                if (lit > .002f)
                {
                    var half = 36 * s;
                    draw.PushClipRect(new Vector2(mark.X - half, mark.Y - 10 * s), new Vector2(mark.X - half + 2 * half * lit, mark.Y + 10 * s), true);
                    UltimateSkins.DrawTabMark(draw, mark, true, true, plate, s);
                    draw.PopClipRect();
                }
            }
            if (entry.Badge > 0) DactUi.Badge(draw, min + new Vector2(width - 10 * s, itemHeight * .5f), entry.Badge);
        }

        DrawSidebarFooter(origin, width, height);
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
    }

    // Parser state and version pinned to the sidebar bottom; the version keeps its
    // hidden-skin discovery click, "运行状态" toggles the status window.
    private void DrawSidebarFooter(Vector2 origin, float width, float height)
    {
        var s = DactUi.S;
        var draw = ImGui.GetWindowDrawList();
        // The footer entry keeps reading 运行状态 while the window is open (user: it must not turn
        // into 关闭运行状态); a second click still closes it.
        var statusLabel = text.Get("运行状态", "Runtime status");
        var buttonWidth = DactUi.ButtonWidth(statusLabel, ButtonKind.Ghost);
        var versionWidth = DactUi.TextWidth(VersionLabel, DactFonts.Small);
        // Longer translations of 运行状态 do not fit beside the version; they take a row of their own.
        var stacked = versionWidth + 8 * s + buttonWidth > width - 16 * s;
        var top = origin.Y - 12 * s + height - 12 * s - (stacked ? 94 : 64) * s;
        var running = parserStatus.State == ParserState.Running;
        var stateColor = running ? DactTheme.Palette.Accent : DactTheme.Palette.Muted;
        var statusMin = new Vector2(origin.X + 8 * s, top + 10 * s);
        // Nameplate skins show the running lamp gauge in place of the dot.
        var labelX = 12 * s;
        if (DactTheme.Palette.Ultimate is { } plate)
            labelX = UltimateSkins.DrawStatusGauge(draw, statusMin, 16 * s, running, plate, s, ImGui.GetTime()) + 6 * s;
        else draw.AddCircleFilled(statusMin + new Vector2(3 * s, 8 * s), 3 * s, DactUi.U32(stateColor), 12);
        DactUi.DrawText(draw, statusMin + new Vector2(labelX, 0), 16 * s,
            text.Format($"解析器{LocalizeState(parserStatus.State)}", $"Parser {LocalizeState(parserStatus.State).ToLowerInvariant()}"), stateColor, DactFonts.Small);

        var rowTop = statusMin.Y + 22 * s;
        ImGui.SetCursorScreenPos(new Vector2(statusMin.X, rowTop));
        if (ImGui.InvisibleButton("##sidebar-version", new Vector2(versionWidth, 30 * s)))
            DiscoverSkin(skinDiscoveries.ClickVersion(configuration.Appearance, Environment.TickCount64));
        DactUi.DrawText(draw, new Vector2(statusMin.X, rowTop), 30 * s, VersionLabel, DactTheme.Palette.Muted, DactFonts.Small);

        ImGui.SetCursorScreenPos(stacked
            ? new Vector2(statusMin.X - 6 * s, rowTop + 30 * s)
            : new Vector2(origin.X + width - 8 * s - buttonWidth, rowTop));
        if (DactUi.Button(statusLabel + "##sidebar-status", ButtonKind.Ghost)) setStatusVisible(!isStatusVisible());
    }

    private void DrawPageArea(float height, Action drawPage)
    {
        var s = DactUi.S;
        if (selectedPage != shownPage)
        {
            shownPage = selectedPage;
            pageEnteredAt = ImGui.GetTime();
            resetSettingsScroll = true;
        }

        var progress = DactMotion.Progress(pageEnteredAt, DactMotion.PageSeconds);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * progress);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 18) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 14) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 10 * s);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, DactTheme.Palette.Muted with { W = .28f });
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, DactTheme.Palette.Muted with { W = .45f });
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, DactTheme.Palette.Muted with { W = .6f });
        var flags = ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysUseWindowPadding;
        if (skinSettings.IsOpen) flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.BeginChild("control-center-page-content", new Vector2(-1, height), false, flags))
        {
            if (resetSettingsScroll) { ImGui.SetScrollY(0); resetSettingsScroll = false; }
            // New pages rise 10 px into place while fading in.
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (1 - progress) * 10 * s);
            drawPage();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(4);
    }

    private void DrawDiscoveryToast(Vector2 windowMin, Vector2 windowSize)
    {
        DiscoverSkin(skinDiscoveries.VisitPage(configuration.Appearance, (int)selectedPage));
        var remaining = discoveryNoticeUntil - Environment.TickCount64;
        if (discoveredSkin is not { } id || remaining <= 0) return;
        var s = DactUi.S;
        var skin = SkinCatalog.All.First(item => item.Id == id);
        var message = text.Format($"发现隐藏配色「{skin.ChineseName}」！可在设置中使用。", $"Discovered {text.Get(skin.ChineseName, skin.EnglishName)}! Apply it in Settings.");
        // Eases in over the first 200 ms and out over the last 300 ms of its 8 s.
        var shown = Math.Min(Math.Clamp((8000 - remaining) / 200f, 0, 1), Math.Clamp(remaining / 300f, 0, 1));
        var eased = DactMotion.EaseOutCubic(shown);
        var size = new Vector2(DactUi.TextWidth(message) + 36 * s, 36 * s);
        var min = new Vector2(windowMin.X + (windowSize.X - size.X) * .5f, windowMin.Y + windowSize.Y - size.Y - 18 * s + (1 - eased) * 12 * s);
        var draw = ImGui.GetForegroundDrawList();
        draw.AddRectFilled(min, min + size, DactUi.U32(DactTheme.Palette.Raised with { W = eased }), 10 * s);
        draw.AddRect(min, min + size, DactUi.U32(DactTheme.Palette.Accent with { W = .55f * eased }), 10 * s);
        DactIcons.Draw(draw, "sparkle", min + new Vector2(12 * s, 10 * s), 16 * s, DactTheme.Palette.Accent with { W = eased });
        DactUi.DrawText(draw, min + new Vector2(32 * s, 0), size.Y, message, DactTheme.Palette.Accent with { W = eased });
    }
}
