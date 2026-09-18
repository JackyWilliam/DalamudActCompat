using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal sealed class SkinSettingsPanel
{
    public bool IsOpen { get; private set; }
    internal string PreviewSkinId { get; private set; } = SkinCatalog.Default;

    public void Open(UiSkinSettings settings, CloudClientSnapshot account)
    {
        // Browsing is transient: leaving must not apply a highlighted skin.
        PreviewSkinId = ActiveSkin(settings, account);
        IsOpen = true;
    }
    public void Close() => IsOpen = false;

    private static bool SignedIn(CloudClientSnapshot account) => account.IsSignedIn && account.ActiveBan is null;
    private static string ActiveSkin(UiSkinSettings settings, CloudClientSnapshot account)
        => SkinCatalog.Resolve(settings, SignedIn(account), account.Sponsor?.Tier ?? 0);
    private static bool Available(SkinDefinition skin, UiSkinSettings settings, CloudClientSnapshot account)
        => SkinCatalog.IsAvailable(skin.Id, settings, SignedIn(account), account.Sponsor?.Tier ?? 0);
    private static bool Discovered(SkinDefinition skin, UiSkinSettings settings)
        => !skin.EasterEgg || settings.UnlockedEasterEggs.Contains(skin.Id);
    private static string Name(SkinDefinition skin, UiSkinSettings settings, UiText text)
        => Discovered(skin, settings) ? text.Get(skin.ChineseName, skin.EnglishName) : text.Get("神秘配色", "Mystery palette");

    public void DrawEntry(UiSkinSettings settings, CloudClientSnapshot account, UiText text)
    {
        var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        DactTheme.TextColored(DactTheme.Palette.Gold, text.Get("外观与皮肤", "Appearance & skins"));
        var current = SkinCatalog.All.First(skin => skin.Id == ActiveSkin(settings, account));
        if (ImGui.BeginChild("skin-entry", new Vector2(-1, 100 * scale), true, ImGuiWindowFlags.NoScrollbar))
        {
            var start = ImGui.GetCursorScreenPos();
            DrawPreview(current.Id, start, new Vector2(130, 78) * scale, text, true);
            ImGui.SetCursorScreenPos(start + new Vector2(145 * scale, 0));
            ImGui.BeginGroup();
            ImGui.TextUnformatted(text.Get($"当前：{current.ChineseName}", $"Current: {current.EnglishName}"));
            ImGui.TextDisabled(text.Get("预览皮肤、查看权益与探索配色", "Preview skins, benefits and discoveries"));
            if (DactTheme.Button(text.Get("管理皮肤", "Manage skins"), new Vector2(145 * scale, 0))) Open(settings, account);
            ImGui.EndGroup();
        }
        ImGui.EndChild();
    }

    public bool Draw(UiSkinSettings settings, CloudClientSnapshot account, UiText text, Action refresh, Action closeWindow)
    {
        if (!IsOpen) return false;
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) &&
            !ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) &&
            !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            Close();
            return false;
        }

        var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        var pageStart = ImGui.GetCursorScreenPos();
        var pageSize = ImGui.GetContentRegionAvail();
        DactTheme.TextColored(DactTheme.Palette.Gold, text.Get("设置与账号 / 外观与皮肤", "Settings & Account / Appearance & skins"));
        ImGui.TextUnformatted(text.Get("选择喜欢的外观", "Find your look"));
        ImGui.TextDisabled(text.Get("点击卡片查看预览，应用后自动保存。", "Select a card to preview. Applying saves your choice."));
        ImGui.Spacing();

        // Only the catalogue/detail body scrolls. Exit and apply stay reachable
        // when the window is small or the user's font scale is large.
        var footerHeight = ImGui.GetTextLineHeightWithSpacing() + 46 * scale;
        var footerY = Math.Max(ImGui.GetCursorScreenPos().Y + 60 * scale, pageStart.Y + pageSize.Y - footerHeight);
        var bodyHeight = Math.Max(40, footerY - ImGui.GetCursorScreenPos().Y - 8 * scale);
        if (ImGui.BeginChild("skin-browser", new Vector2(-1, bodyHeight), false))
        {
            var width = ImGui.GetContentRegionAvail().X;
            if (width >= 680 * scale)
            {
                if (ImGui.BeginChild("skin-catalogue", new Vector2(width * .45f, -1), false)) DrawCatalogue(settings, account, text, scale);
                ImGui.EndChild();
                ImGui.SameLine(0, 16 * scale);
                if (ImGui.BeginChild("skin-detail", new Vector2(-1, -1), false)) DrawDetail(settings, account, text, refresh, scale);
                ImGui.EndChild();
            }
            else
            {
                // A compact picker keeps every skin reachable without scrolling
                // past a large preview on a narrow window with enlarged text.
                ImGui.SetNextItemWidth(-1);
                var preview = SkinCatalog.All.First(skin => skin.Id == PreviewSkinId);
                if (DactTheme.BeginCombo("##compact-skin-picker", Name(preview, settings, text)))
                {
                    foreach (var skin in SkinCatalog.All)
                    {
                        var label = Name(skin, settings, text) +
                            (Available(skin, settings, account) ? "" : text.Get(" · 未解锁", " · Locked")) + "##" + skin.Id;
                        if (ImGui.Selectable(label, skin.Id == PreviewSkinId)) PreviewSkinId = skin.Id;
                    }
                    ImGui.EndCombo();
                }
                DrawDetail(settings, account, text, refresh, scale, compact: true);
            }
        }
        ImGui.EndChild();

        ImGui.SetCursorScreenPos(new Vector2(pageStart.X, footerY));
        ImGui.Separator();
        var selected = SkinCatalog.All.First(skin => skin.Id == PreviewSkinId);
        var active = ActiveSkin(settings, account);
        var available = Available(selected, settings, account);
        ImGui.TextDisabled(!Discovered(selected, settings)
            ? text.Get("继续探索客户端，揭晓这款配色。", "Explore the client to reveal this palette.")
            : !available
            ? text.Get("此皮肤尚未解锁，可以预览。", "Locked skins can be previewed.")
            : active == PreviewSkinId ? text.Get("当前外观已保存。", "Your current look is saved.")
            : text.Get("当前仅预览；返回或关闭不会应用。", "Preview only. Apply to save."));
        if (DactTheme.Button(text.Get("返回设置", "Back to settings"), new Vector2(150 * scale, 0))) Close();
        ImGui.SameLine();
        ImGui.BeginDisabled(!available || active == PreviewSkinId);
        var changed = false;
        if (DactTheme.Button(active == PreviewSkinId ? text.Get("正在使用", "In use") : text.Get("应用皮肤", "Apply skin"), new Vector2(150 * scale, 0)))
        {
            settings.SelectedSkin = PreviewSkinId;
            changed = true;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (DactTheme.Button(text.Get("关闭窗口", "Close window"), new Vector2(150 * scale, 0)))
        {
            Close();
            closeWindow();
        }
        return changed;
    }

    private void DrawCatalogue(UiSkinSettings settings, CloudClientSnapshot account, UiText text, float scale)
    {
        ImGui.TextDisabled(text.Get("皮肤收藏", "Your collection"));
        var active = ActiveSkin(settings, account);
        foreach (var skin in SkinCatalog.All)
        {
            ImGui.PushID(skin.Id);
            var min = ImGui.GetCursorScreenPos();
            var size = new Vector2(ImGui.GetContentRegionAvail().X, 108 * scale);
            if (ImGui.InvisibleButton("skin-card", size)) PreviewSkinId = skin.Id;
            var draw = ImGui.GetWindowDrawList();
            var selected = PreviewSkinId == skin.Id;
            draw.AddRectFilled(min, min + size, ImGui.GetColorU32(selected || ImGui.IsItemHovered() ? DactTheme.Palette.Hover : DactTheme.Palette.Raised), 8 * scale);
            draw.AddRect(min, min + size, ImGui.GetColorU32(selected || ImGui.IsItemFocused() ? DactTheme.Palette.Accent : DactTheme.Palette.Border), 8 * scale);
            var thumbnail = new Vector2(Math.Min(140 * scale, size.X * .38f), 82 * scale);
            DrawPreview(Discovered(skin, settings) ? skin.Id : null, min + new Vector2(10, 13) * scale, thumbnail, text, true);
            var labelX = thumbnail.X + 22 * scale;
            var labelMin = min + new Vector2(labelX, 14 * scale);
            var labelWidth = Math.Max(1, size.X - labelX - 8 * scale);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), labelMin, ImGui.GetColorU32(DactTheme.Palette.Text), Name(skin, settings, text), labelWidth);
            var status = active == skin.Id ? text.Get("● 正在使用", "● In use")
                : Available(skin, settings, account) ? text.Get("已解锁", "Unlocked")
                : skin.EasterEgg ? text.Get("等待发现", "Undiscovered") : text.Get("赞助 1 级解锁", "Sponsor tier 1");
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * .85f, min + new Vector2(labelX, 65 * scale),
                ImGui.GetColorU32(active == skin.Id ? DactTheme.Palette.Accent : DactTheme.Palette.Muted), status, labelWidth);
            ImGui.PopID();
        }
    }

    private void DrawDetail(UiSkinSettings settings, CloudClientSnapshot account, UiText text, Action refresh, float scale, bool compact = false)
    {
        var skin = SkinCatalog.All.First(item => item.Id == PreviewSkinId);
        if (!compact) DactTheme.TextColored(DactTheme.Palette.Gold, Name(skin, settings, text));
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Math.Min((compact ? 100 : 235) * scale, ImGui.GetContentRegionAvail().X * .6f));
        DrawPreview(Discovered(skin, settings) ? skin.Id : null, ImGui.GetCursorScreenPos(), size, text, false);
        ImGui.Dummy(size);
        ImGui.Spacing();
        ImGui.TextWrapped(skin.SponsorTier > 0
            ? text.Get("浅色面板，沿用游戏原版标签、按钮和图标。", "Light panels with original game tabs, buttons and icons.")
            : skin.EasterEgg ? Discovered(skin, settings)
                ? text.Get("探索所得的配色，保留熟悉的 DACT 布局。", "A discovered palette for the familiar DACT layout.")
                : text.Get("继续探索客户端，发现属于你的隐藏配色。", "Keep exploring the client to discover this hidden palette.")
            : text.Get("熟悉的深色界面，所有用户均可使用。", "The familiar dark interface, available to everyone."));
        if (skin.SponsorTier > 0)
        {
            ImGui.TextWrapped(Available(skin, settings, account)
                ? text.Get("当前账号已永久解锁。", "Permanently unlocked for this account.")
                : text.Get("需要赞助 1 级。赞助后联系管理员核对账号并开通等级。", "Requires sponsor tier 1. Contact the administrator after sponsoring to activate your account benefit."));
            ImGui.BeginDisabled(account.IsBusy || !SignedIn(account));
            if (DactTheme.SmallButton(text.Get("刷新权益", "Refresh benefits"))) refresh();
            ImGui.EndDisabled();
        }
        if (ActiveSkin(settings, account) != settings.SelectedSkin)
            ImGui.TextWrapped(text.Get("原选皮肤暂未获当前账号授权，现使用默认外观。", "Your saved skin is unavailable to this account; the default is active."));
    }

    private static void DrawPreview(string? skinId, Vector2 min, Vector2 size, UiText text, bool miniature)
    {
        var draw = ImGui.GetWindowDrawList();
        var palette = skinId is null ? DactTheme.Palette : DactTheme.For(skinId);
        var max = min + size;
        draw.PushClipRect(min, max, true);
        if (skinId is null)
        {
            // A mystery illustration reveals no undiscovered palette or title.
            var center = min + size * .5f;
            draw.AddRectFilled(min, max, ImGui.GetColorU32(palette.Raised), 7);
            draw.AddCircle(center, size.Y * .29f, ImGui.GetColorU32(palette.Border), 32, 1.5f);
            draw.AddCircle(center, size.Y * .37f, ImGui.GetColorU32(palette.Border with { W = .45f }), 32);
            var fontSize = size.Y * .43f;
            draw.AddText(ImGui.GetFont(), fontSize, center - new Vector2(fontSize * .25f, fontSize * .58f), ImGui.GetColorU32(palette.Accent), "?");
            draw.PopClipRect();
            return;
        }

        var unit = size.Y / 190f;
        var game = palette.Light ? DactTheme.GameAssets : null;
        if (game?.Window(min, max, unit) != true)
        {
            draw.AddRectFilled(min, max, ImGui.GetColorU32(palette.Surface), 7 * unit);
            draw.AddRect(min, max, ImGui.GetColorU32(palette.Border), 7 * unit);
        }
        var pad = 12 * unit;
        var font = ImGui.GetFont();
        var fontSizeNormal = Math.Clamp(14 * unit, 8, ImGui.GetFontSize());
        var titleY = min.Y + 9 * unit;
        draw.AddCircleFilled(new Vector2(min.X + pad + 5 * unit, titleY + 7 * unit), 5 * unit, ImGui.GetColorU32(palette.Accent));
        draw.AddText(font, fontSizeNormal, new Vector2(min.X + pad + 16 * unit, titleY), ImGui.GetColorU32(palette.Text), "DACT");
        var closeMin = new Vector2(max.X - 28 * unit, min.Y + 6 * unit);
        if (game?.Icon(GameSkinIcon.Close, closeMin, 20 * unit) != true)
        {
            draw.AddLine(closeMin + new Vector2(6, 6) * unit, closeMin + new Vector2(14, 14) * unit, ImGui.GetColorU32(palette.Muted), unit);
            draw.AddLine(closeMin + new Vector2(14, 6) * unit, closeMin + new Vector2(6, 14) * unit, ImGui.GetColorU32(palette.Muted), unit);
        }
        var tabY = min.Y + 35 * unit;
        var tabWidth = (size.X - pad * 2) / 3;
        var labels = new[] { text.Get("概览", "Overview"), text.Get("统计", "Meter"), text.Get("设置", "Settings") };
        for (var i = 0; i < 3; i++)
        {
            var tabMin = new Vector2(min.X + pad + i * tabWidth, tabY);
            var tabMax = tabMin + new Vector2(tabWidth - 3 * unit, 23 * unit);
            var textured = game?.Tab(tabMin, tabMax, i == 0, false) == true;
            if (!textured) draw.AddRectFilled(tabMin, tabMax, ImGui.GetColorU32(i == 0 ? palette.Hover : palette.Raised), 3 * unit);
            if (!miniature) CenterPreviewText(labels[i], tabMin, tabMax, fontSizeNormal * .9f, textured ? Vector4.One : palette.Text);
        }
        var rowMin = new Vector2(min.X + pad, min.Y + 70 * unit);
        draw.AddRectFilled(rowMin, new Vector2(max.X - pad, min.Y + 132 * unit), ImGui.GetColorU32(palette.Raised), 5 * unit);
        for (var i = 0; i < 3; i++)
        {
            var p = rowMin + new Vector2(10, 10 + i * 17) * unit;
            draw.AddCircleFilled(p + new Vector2(3 * unit), 3 * unit, ImGui.GetColorU32(i == 0 ? palette.Accent : palette.Muted));
            draw.AddRectFilled(p + new Vector2(14, 0) * unit, p + new Vector2((size.X / unit - 65) * (i == 0 ? .78f : .53f), 5) * unit,
                ImGui.GetColorU32(i == 0 ? palette.Accent : palette.Muted with { W = .55f }), 2 * unit);
        }
        var buttonMin = new Vector2(min.X + pad, min.Y + 150 * unit);
        var buttonMax = buttonMin + new Vector2(Math.Min(size.X - pad * 2, 110 * unit), 28 * unit);
        var gameButton = game?.Button(buttonMin, buttonMax, false, false) == true;
        if (!gameButton) draw.AddRectFilled(buttonMin, buttonMax, ImGui.GetColorU32(palette.Hover), 5 * unit);
        if (!miniature) CenterPreviewText(text.Get("按钮效果", "Button style"), buttonMin, buttonMax - new Vector2(0, gameButton ? 4 * unit : 0), fontSizeNormal * .9f, gameButton ? Vector4.One : palette.Text);
        draw.PopClipRect();
    }

    private static void CenterPreviewText(string label, Vector2 min, Vector2 max, float fontSize, Vector4 color)
    {
        var labelSize = ImGui.CalcTextSize(label) * fontSize / ImGui.GetFontSize();
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), fontSize, (min + max - labelSize) * .5f, ImGui.GetColorU32(color), label);
    }
}
