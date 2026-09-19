using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace DalamudActCompat.UI;

internal static class AccountIdentityBadge
{
    internal static readonly Vector4 SponsorNameColor = new(.96f, .22f, .28f, 1);

    public static void DrawName(ISharedImmediateTexture? icon, string name, bool isAdmin,
        Vector2 position, float width, Vector4 color, ISharedImmediateTexture? sponsorIcon = null, int sponsorTier = 0)
    {
        if (width <= 0) return;
        var badgeSize = ImGui.GetFontSize();
        var hasBadge = isAdmin && icon is not null;
        var hasSponsor = sponsorTier > 0;
        var tier = $"Lv.{Math.Clamp(sponsorTier, 1, 10)}";
        var sponsorWidth = hasSponsor ? badgeSize + ImGui.CalcTextSize(tier).X + 12 : 0;
        // Reserve the badge before truncating long names, so unread counts and
        // adjacent actions never cover the server-authorized identity marker.
        var label = FriendsMessagePreview.Ellipsize(name, Math.Max(1, width - (hasBadge ? badgeSize + 4 : 0) - (hasSponsor ? sponsorWidth + 4 : 0)),
            value => ImGui.CalcTextSize(value).X);
        var list = ImGui.GetWindowDrawList();
        list.PushClipRect(position - new Vector2(0, 1), position + new Vector2(width, badgeSize + 1), true);
        list.AddText(position, ImGui.GetColorU32(hasSponsor ? SponsorNameColor : color), label);
        var start = position + new Vector2(ImGui.CalcTextSize(label).X + 4, 0);
        if (hasSponsor)
        {
            // Sponsor identity always precedes other markers. Its color comes
            // from the server tier, never from the currently selected UI skin.
            var end = start + new Vector2(sponsorWidth, badgeSize);
            list.AddRectFilled(start - new Vector2(0, 1), end + new Vector2(0, 1), ImGui.GetColorU32(new Vector4(.23f, .12f, .08f, 1)), 3);
            list.AddRect(start - new Vector2(0, 1), end + new Vector2(0, 1), ImGui.GetColorU32(new Vector4(.91f, .75f, .42f, 1)), 3);
            if (sponsorIcon is not null)
                list.AddImage(sponsorIcon.GetWrapOrEmpty().Handle, start + new Vector2(4, 0), start + new Vector2(4 + badgeSize, badgeSize));
            list.AddText(start + new Vector2(badgeSize + 8, 0), ImGui.GetColorU32(new Vector4(1, .86f, .6f, 1)), tier);
            if (ImGui.IsMouseHoveringRect(start, end)) ImGui.SetTooltip($"赞助者 · {sponsorTier} 级 · 永久");
            start.X = end.X + 4;
        }
        if (hasBadge)
        {
            list.AddImage(icon!.GetWrapOrEmpty().Handle, start, start + new Vector2(badgeSize),
                Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One));
            if (ImGui.IsMouseHoveringRect(start, start + new Vector2(badgeSize))) ImGui.SetTooltip("管理员");
        }
        list.PopClipRect();
    }

    public static void Text(ISharedImmediateTexture? icon, string name, bool isAdmin, Vector4 color,
        ISharedImmediateTexture? sponsorIcon = null, int sponsorTier = 0)
    {
        var width = ImGui.GetContentRegionAvail().X;
        DrawName(icon, name, isAdmin, ImGui.GetCursorScreenPos(), width, color, sponsorIcon, sponsorTier);
        ImGui.Dummy(new Vector2(width, ImGui.GetTextLineHeight()));
    }
}
