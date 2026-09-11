using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace DalamudActCompat.UI;

internal static class AdministratorBadge
{
    public static void DrawName(ISharedImmediateTexture? icon, string name, bool isAdmin,
        Vector2 position, float width, Vector4 color)
    {
        var badgeSize = ImGui.GetFontSize();
        var hasBadge = isAdmin && icon is not null;
        // Reserve the badge before truncating long names, so unread counts and
        // adjacent actions never cover the server-authorized identity marker.
        var label = FriendsMessagePreview.Ellipsize(name, Math.Max(1, width - (hasBadge ? badgeSize + 4 : 0)),
            value => ImGui.CalcTextSize(value).X);
        var list = ImGui.GetWindowDrawList();
        list.AddText(position, ImGui.GetColorU32(color), label);
        if (!hasBadge) return;
        var start = position + new Vector2(ImGui.CalcTextSize(label).X + 4, 0);
        list.AddImage(icon!.GetWrapOrEmpty().Handle, start, start + new Vector2(badgeSize),
            Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One));
        if (ImGui.IsMouseHoveringRect(start, start + new Vector2(badgeSize))) ImGui.SetTooltip("管理员");
    }

    public static void Text(ISharedImmediateTexture? icon, string name, bool isAdmin, Vector4 color)
    {
        var width = ImGui.GetContentRegionAvail().X;
        DrawName(icon, name, isAdmin, ImGui.GetCursorScreenPos(), width, color);
        ImGui.Dummy(new Vector2(width, ImGui.GetTextLineHeight()));
    }
}
