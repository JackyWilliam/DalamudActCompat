using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace DalamudActCompat.UI;

internal enum UltimateKind { Bahamut, Weapon, Alexander, Dragonsong, Omega, Futures }

// The six ultimate skins share one structure and differ only in these colours and in
// the small parts drawn by UltimateSkins: emblem, tab mark, button fittings, card mark,
// drawer crown and the ornaments behind the control centre.
internal sealed record UltimateStyle(
    UltimateKind Kind, uint BackingIcon,
    Vector4 Frame, Vector4 FrameHigh, Vector4 HoverRim, Vector4 Glow,
    Vector4 Face, Vector4 FaceLow, Vector4 FaceHover, Vector4 FacePress, Vector4 Groove, Vector4 Panel, Vector4 Line,
    Vector4 Active, Vector4 ActiveHigh, Vector4 ActiveLow, Vector4 ActiveFace, Vector4 ActiveFaceLow, Vector4 ActiveRim, Vector4 ActiveInk,
    Vector4 TitleTop, Vector4 TitleBottom, Vector4 TitleTintLeft, Vector4 TitleTintRight, Vector4 Inlay, float ButtonRounding);

// Plate backings are read from the player's own game files at runtime; the package
// ships only icon IDs, never the artwork.
internal sealed class UltimateSkinAssets(Func<uint, IDalamudTextureWrap?> resolveIcon)
{
    public IDalamudTextureWrap? Icon(uint id) => id == 0 ? null : resolveIcon(id);
}

internal static class UltimateSkins
{
    private static Vector4 Rgb(uint rgb, float alpha = 1) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);
    private static readonly Vector4 Clear = Vector4.Zero;
    private static readonly Vector4 CloseFace = Rgb(0x5A2626), CloseFaceLow = Rgb(0x461C1C), CloseRim = Rgb(0xC9675C), CloseLamp = Rgb(0xF29A8E);

    private static readonly Dictionary<string, (SkinPalette Palette, UltimateStyle Style)> Skins = new(StringComparer.Ordinal)
    {
        [SkinCatalog.UltimateBahamut] = Make(UltimateKind.Bahamut, 195701,
            gun: 0x171110, panel: 0x1D1514, groove: 0x0E0A0A, line: 0x35272A, face: 0x2D211F, faceLow: 0x221816, faceHover: 0x382926, facePress: 0x160F0E,
            frame: 0x9C8558, frameHigh: 0xC9AE74, head: 0xE2BB62, hover: 0xE8C981, glow: Rgb(0xF0C86E, .5f),
            active: 0x36C8E6, activeHigh: 0xC2F3FF, activeLow: 0x127C98, activeFace: 0x183C46, activeFaceLow: 0x112C34, activeRim: 0x3F98AB, activeInk: 0x04222B,
            text: 0xF1E6DA, muted: 0xB0A194, titleTop: 0x2B1E22, titleBottom: 0x1A1313,
            tintLeft: Rgb(0x703470, .32f), tintRight: Rgb(0xC44838, .14f), inlay: Rgb(0x36C8E6), rounding: 3),
        [SkinCatalog.UltimateWeapon] = Make(UltimateKind.Weapon, 195702,
            gun: 0x101920, panel: 0x14202A, groove: 0x0A1117, line: 0x263844, face: 0x1E2D39, faceLow: 0x16232D, faceHover: 0x253848, facePress: 0x0F1820,
            frame: 0xAAB6C0, frameHigh: 0xE6EEF4, head: 0x86D3D8, hover: 0xEEF6FB, glow: Rgb(0xC8F0FF, .6f),
            active: 0xF2D774, activeHigh: 0xFFF7D6, activeLow: 0xA9852A, activeFace: 0x3B3720, activeFaceLow: 0x2B2917, activeRim: 0xC8AF64, activeInk: 0x3A2D05,
            text: 0xE8F0F4, muted: 0x9DB0BC, titleTop: 0x223340, titleBottom: 0x121D26,
            tintLeft: Clear, tintRight: Rgb(0x82D2E1, .07f), inlay: Rgb(0x41B9C4), rounding: 3),
        [SkinCatalog.UltimateAlexander] = Make(UltimateKind.Alexander, 0,
            gun: 0x121715, panel: 0x161D19, groove: 0x0B0F0D, line: 0x28312C, face: 0x24302A, faceLow: 0x1B241F, faceHover: 0x2B3931, facePress: 0x131A16,
            frame: 0x8E6B3E, frameHigh: 0xB88A4F, head: 0xD4AF62, hover: 0xD4AF62, glow: Rgb(0xF2C45C, .55f),
            active: 0x3DCB62, activeHigh: 0xB6F6C0, activeLow: 0x167234, activeFace: 0x22492F, activeFaceLow: 0x183925, activeRim: 0x5E9A6A, activeInk: 0x0A2412,
            text: 0xEFE8D6, muted: 0xA8A18F, titleTop: 0x25302A, titleBottom: 0x161D19,
            tintLeft: Clear, tintRight: Clear, inlay: Rgb(0xF2ECDA), rounding: 4),
        [SkinCatalog.UltimateDragonsong] = Make(UltimateKind.Dragonsong, 195704,
            gun: 0x131315, panel: 0x18181C, groove: 0x0B0B0D, line: 0x2E2E34, face: 0x25252B, faceLow: 0x1B1B20, faceHover: 0x2F2F37, facePress: 0x121215,
            frame: 0x707079, frameHigh: 0xA6A6B1, head: 0xD9B26A, hover: 0xD6D6E0, glow: Rgb(0xFFA05A, .45f),
            active: 0xF08A3C, activeHigh: 0xFFD6AE, activeLow: 0xA1461A, activeFace: 0x4B2817, activeFaceLow: 0x381D11, activeRim: 0xB96B3D, activeInk: 0x3B1406,
            text: 0xECE8E2, muted: 0xA6A19B, titleTop: 0x1E2536, titleBottom: 0x131620,
            tintLeft: Rgb(0xE2622A, .26f), tintRight: Rgb(0x6EAAE6, .16f), inlay: Rgb(0x7FB6E8), rounding: 2),
        [SkinCatalog.UltimateOmega] = Make(UltimateKind.Omega, 195705,
            gun: 0x111218, panel: 0x15161E, groove: 0x0A0B10, line: 0x272A36, face: 0x20222D, faceLow: 0x181A23, faceHover: 0x282B39, facePress: 0x101118,
            frame: 0x8F96A6, frameHigh: 0xD4D9E3, head: 0x9FE0FF, hover: 0xBDEBFF, glow: Rgb(0x82DCFF, .55f),
            active: 0xA66DFF, activeHigh: 0xE4D0FF, activeLow: 0x6A35C9, activeFace: 0x2F2252, activeFaceLow: 0x22183E, activeRim: 0x8B64D8, activeInk: 0x1A0838,
            text: 0xE8EAF2, muted: 0x9FA4B6, titleTop: 0x23262F, titleBottom: 0x15161E,
            tintLeft: Clear, tintRight: Clear, inlay: Rgb(0x9FE3FF), rounding: 999),
        [SkinCatalog.UltimateFutures] = Make(UltimateKind.Futures, 195716,
            gun: 0x16121E, panel: 0x1B1624, groove: 0x0E0B13, line: 0x30283A, face: 0x2A2034, faceLow: 0x20192A, faceHover: 0x332840, facePress: 0x140F1A,
            frame: 0xB2ADBF, frameHigh: 0xECE8F6, head: 0xD7B8FF, hover: 0xF3EAFF, glow: Rgb(0xE6BEFF, .55f),
            active: 0xF28DB2, activeHigh: 0xFFDCE8, activeLow: 0xB04A75, activeFace: 0x4B2337, activeFaceLow: 0x381A2A, activeRim: 0xCA7190, activeInk: 0x45102A,
            text: 0xF1ECF6, muted: 0xAAA0B7, titleTop: 0x281F33, titleBottom: 0x18131F,
            tintLeft: Rgb(0xB082FF, .42f), tintRight: Rgb(0xFFD28C, .25f), inlay: Rgb(0xF3CF7A), rounding: 4),
    };

    private static (SkinPalette, UltimateStyle) Make(UltimateKind kind, uint backing, uint gun, uint panel, uint groove, uint line,
        uint face, uint faceLow, uint faceHover, uint facePress, uint frame, uint frameHigh, uint head, uint hover, Vector4 glow,
        uint active, uint activeHigh, uint activeLow, uint activeFace, uint activeFaceLow, uint activeRim, uint activeInk,
        uint text, uint muted, uint titleTop, uint titleBottom, Vector4 tintLeft, Vector4 tintRight, Vector4 inlay, float rounding)
    {
        var style = new UltimateStyle(kind, backing, Rgb(frame), Rgb(frameHigh), Rgb(hover), glow,
            Rgb(face), Rgb(faceLow), Rgb(faceHover), Rgb(facePress), Rgb(groove), Rgb(panel), Rgb(line),
            Rgb(active), Rgb(activeHigh), Rgb(activeLow), Rgb(activeFace), Rgb(activeFaceLow), Rgb(activeRim), Rgb(activeInk),
            Rgb(titleTop), Rgb(titleBottom), tintLeft, tintRight, inlay, rounding);
        // Raised is the button/card body, Hover its lit state, Border the metal rim and
        // Gold the heading colour, matching how the other solid skins use these slots.
        return (new SkinPalette(Rgb(gun), Rgb(panel), Rgb(faceHover), Rgb(head), Rgb(active), Rgb(text), Rgb(muted), Rgb(frame), Ultimate: style), style);
    }

    public static SkinPalette? Palette(string id) => Skins.TryGetValue(id, out var skin) ? skin.Palette : null;

    private static uint Col(Vector4 color, float alpha = 1) => ImGui.GetColorU32(color with { W = color.W * alpha });
    private static Vector2 Polar(Vector2 center, float radius, float angle) => center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;

    // ── window chrome ──────────────────────────────────────────────────────────

    public static void DrawTitle(ImDrawListPtr draw, Vector2 min, Vector2 max, float rounding, UltimateStyle u)
    {
        draw.AddRectFilled(min, max, Col(u.TitleBottom), rounding, ImDrawFlags.RoundCornersTop);
        // MultiColor fills ignore rounding, so the vertical gradient starts below the corner radius.
        draw.AddRectFilledMultiColor(new(min.X, min.Y + rounding), max, Col(u.TitleTop), Col(u.TitleTop), Col(u.TitleBottom), Col(u.TitleBottom));
        draw.AddRectFilled(min, new(max.X, min.Y + rounding + 1), Col(u.TitleTop), rounding, ImDrawFlags.RoundCornersTop);
        if (u.TitleTintLeft.W > 0 || u.TitleTintRight.W > 0)
        {
            var middle = new Vector2((min.X + max.X) * .5f, max.Y);
            draw.AddRectFilledMultiColor(new(min.X, min.Y + rounding), middle, Col(u.TitleTintLeft), Col(u.TitleTintLeft with { W = 0 }),
                Col(u.TitleTintLeft with { W = 0 }), Col(u.TitleTintLeft));
            draw.AddRectFilledMultiColor(new(middle.X, min.Y + rounding), max, Col(u.TitleTintRight with { W = 0 }), Col(u.TitleTintRight),
                Col(u.TitleTintRight), Col(u.TitleTintRight with { W = 0 }));
        }
        draw.AddLine(new(min.X + rounding, min.Y + 1), new(max.X - rounding, min.Y + 1), Col(Vector4.One, .09f));
        draw.AddLine(new(min.X, max.Y - 1), new(max.X, max.Y - 1), Col(u.Frame));
    }

    // The 6px strip between the title bar and the tabs carries each plate's frame texture.
    public static void DrawBand(ImDrawListPtr draw, Vector2 min, Vector2 max, UltimateStyle u, float scale)
    {
        draw.AddRectFilled(min, max, Col(u.Groove));
        var mid = (min.Y + max.Y) * .5f;
        switch (u.Kind)
        {
            case UltimateKind.Alexander:
                for (var x = min.X; x < max.X; x += 5 * scale)
                    draw.AddRectFilled(new(x, mid - 1.5f * scale), new(x + 2 * scale, mid + 1.5f * scale), Col(u.FrameHigh, .5f));
                break;
            case UltimateKind.Bahamut:
                for (var x = min.X; x < max.X; x += 28 * scale)
                {
                    draw.AddRectFilled(new(x, mid - scale), new(x + 19 * scale, mid + scale), Col(u.Frame));
                    draw.AddRectFilled(new(x + 21 * scale, mid - scale), new(x + 24 * scale, mid + scale), Col(u.Active));
                }
                break;
            case UltimateKind.Weapon:
                draw.AddLine(new(min.X, mid - 2 * scale), new(max.X, mid - 2 * scale), Col(u.Frame));
                draw.AddLine(new(min.X, mid + 2 * scale), new(max.X, mid + 2 * scale), Col(u.Frame));
                for (var x = min.X; x < max.X; x += 3.5f * scale)
                    draw.AddLine(new(x, mid + scale), new(x + 1.5f * scale, mid - scale), Col(u.Inlay), scale);
                break;
            case UltimateKind.Dragonsong:
                draw.AddLine(new(min.X, max.Y - 2 * scale), new(max.X, max.Y - 2 * scale), Col(u.Frame));
                for (var x = min.X + 4 * scale; x < max.X; x += 8 * scale)
                    draw.AddLine(new(x, min.Y), new(x, max.Y - 2 * scale), Col(u.FrameHigh, .7f));
                break;
            case UltimateKind.Omega:
                for (var x = min.X + 4 * scale; x < max.X; x += 9 * scale)
                {
                    draw.AddCircleFilled(new(x, mid), 2 * scale, Col(u.Inlay, .25f), 8);
                    draw.AddCircleFilled(new(x, mid), scale, Col(u.Inlay), 6);
                }
                break;
            case UltimateKind.Futures:
                var third = (max.X - min.X) / 2;
                draw.AddRectFilledMultiColor(new(min.X, mid - .5f), new(min.X + third, mid + .5f), Col(Rgb(0xBE96FF, .6f)), Col(Rgb(0xFFA0C8, .6f)), Col(Rgb(0xFFA0C8, .6f)), Col(Rgb(0xBE96FF, .6f)));
                draw.AddRectFilledMultiColor(new(min.X + third, mid - .5f), new(max.X, mid + .5f), Col(Rgb(0xFFA0C8, .6f)), Col(Rgb(0xFFD28C, .6f)), Col(Rgb(0xFFD28C, .6f)), Col(Rgb(0xFFA0C8, .6f)));
                for (var x = min.X + 8 * scale; x < max.X; x += 16 * scale) Sparkle(draw, new(x, mid), 2.2f * scale, Col(u.Inlay));
                break;
        }
    }

    // End-cap emblem at the window's top-left corner, half outside like the plate's.
    public static void DrawEmblem(ImDrawListPtr draw, Vector2 center, float size, UltimateStyle u)
    {
        var k = size / 30f;
        Vector2 P(float x, float y) => center + new Vector2(x - 15, y - 15) * k;
        draw.AddCircleFilled(center + new Vector2(0, 2 * k), 15 * k, Col(Vector4.Zero with { W = .45f }), 32);
        switch (u.Kind)
        {
            case UltimateKind.Alexander:
                draw.AddCircleFilled(center, 14 * k, Col(Rgb(0x8E6B3E)), 40);
                draw.AddCircle(center, 13.4f * k, Col(Rgb(0xD9AE6A), .8f), 40, 1.2f * k);
                draw.AddCircleFilled(center, 11.6f * k, Col(Rgb(0x2DAF4F)), 40);
                draw.AddCircleFilled(P(13.6f, 13.2f), 8.4f * k, Col(Rgb(0x6DE08A), .75f), 32);
                draw.AddCircle(center, 11.6f * k, Col(Rgb(0x0E3A1B)), 40, .8f * k);
                draw.AddCircle(center, 5.2f * k, Col(Rgb(0xE4FFE8), .85f), 24, 1.1f * k);
                foreach (var (a, b) in new[] { (P(15, 5.6f), P(15, 9.8f)), (P(15, 20.2f), P(15, 24.4f)), (P(5.6f, 15), P(9.8f, 15)), (P(20.2f, 15), P(24.4f, 15)) })
                    draw.AddLine(a, b, Col(Rgb(0xE4FFE8), .85f), 1.1f * k);
                draw.AddCircleFilled(center, 1.3f * k, Col(Rgb(0xE4FFE8)), 12);
                draw.AddCircleFilled(P(11.4f, 9.6f), 2.2f * k, Col(Vector4.One, .35f), 12);
                break;
            case UltimateKind.Bahamut:
                draw.AddTriangleFilled(P(21, 7), P(29.5f, 2), P(24.9f, 8.6f), Col(Rgb(0xC9AE74)));
                draw.AddTriangleFilled(P(9, 7), P(.5f, 2), P(5.1f, 8.6f), Col(Rgb(0xC9AE74)));
                draw.AddCircleFilled(P(15, 16), 12.4f * k, Col(Rgb(0x9C8558)), 40);
                draw.AddCircle(P(15, 16), 11.8f * k, Col(Rgb(0xEAD6A0), .6f), 40, 1.1f * k);
                draw.AddCircleFilled(P(15, 16), 10 * k, Col(Rgb(0x2E8FCB)), 40);
                draw.AddCircleFilled(P(13.6f, 14.4f), 7.4f * k, Col(Rgb(0x4FB8EC), .85f), 32);
                draw.AddLine(P(8, 21.5f), P(22, 10.5f), Col(Rgb(0xE8FBFF), .75f), 1.7f * k);
                draw.AddLine(P(9.2f, 23), P(23.2f, 12), Col(Rgb(0x0E4F7A), .55f), .8f * k);
                draw.AddCircleFilled(P(11.6f, 11.6f), 2.4f * k, Col(Vector4.One, .45f), 12);
                break;
            case UltimateKind.Weapon:
            {
                Span<Vector2> star = stackalloc Vector2[16];
                for (var i = 0; i < 16; i++)
                {
                    var radius = i % 2 == 1 ? 3.2f : i % 4 == 0 ? 14.5f : 7.5f;
                    star[i] = Polar(center, radius * k, -MathF.PI / 2 + i * MathF.PI / 8);
                }
                FillFan(draw, center, star, Col(Rgb(0xC9D3DC)));
                draw.AddPolyline(ref star[0], star.Length, Col(Rgb(0x3A4652)), ImDrawFlags.Closed, .7f * k);
                draw.AddCircleFilled(center, 4 * k, Col(Rgb(0xF4F8FC)), 20);
                draw.AddCircle(center, 4 * k, Col(Rgb(0x8A97A2)), 20, .6f * k);
                break;
            }
            case UltimateKind.Dragonsong:
                draw.AddCircleFilled(center, 13.8f * k, Col(Rgb(0x1E1E24)), 40);
                draw.AddCircle(center, 13.8f * k, Col(Rgb(0xA6A6B1)), 40, 1.1f * k);
                for (var i = 0; i < 6; i++) draw.AddCircleFilled(Polar(center, 9.6f * k, -MathF.PI / 2 + i * MathF.PI / 3), 2.3f * k, Col(Rgb(0xF2EEE6)), 12);
                draw.AddCircleFilled(center, 5.3f * k, Col(Rgb(0xC9541E)), 24);
                draw.AddCircleFilled(center - new Vector2(0, .6f * k), 3.8f * k, Col(Rgb(0xFFB060)), 20);
                draw.AddQuadFilled(center + new Vector2(0, -4.3f * k), center + new Vector2(1.2f * k, 0), center + new Vector2(0, 4.3f * k),
                    center - new Vector2(1.2f * k, 0), Col(Rgb(0x2A0805)));
                break;
            case UltimateKind.Omega:
                draw.AddCircleFilled(center, 13.8f * k, Col(Rgb(0x1B1D25)), 40);
                draw.AddCircle(center, 13.8f * k, Col(Rgb(0xD4D9E3)), 40, 1.1f * k);
                for (var i = 0; i < 20; i++) draw.AddCircleFilled(Polar(center, 10.6f * k, i * MathF.Tau / 20), .7f * k, Col(Rgb(0x8F96A6)), 6);
                draw.AddCircleFilled(center, 7.6f * k, Col(Rgb(0x0B0C11)), 32);
                draw.AddCircleFilled(center, 4.4f * k, Col(Rgb(0x2C6C9A)), 24);
                draw.AddCircleFilled(center, 3.1f * k, Col(Rgb(0xA9E8FF)), 20);
                draw.AddCircleFilled(center, 1.6f * k, Col(Vector4.One), 12);
                break;
            case UltimateKind.Futures:
                Feather(draw, P(15, 27), P(15, 1), 3.4f * k);
                Feather(draw, P(15, 27), P(3.5f, 5.5f), 3 * k);
                Feather(draw, P(15, 27), P(26.5f, 5.5f), 3 * k);
                draw.AddCircleFilled(P(11.4f, 24.6f), 3 * k, Col(Rgb(0xF28DB2)), 16);
                draw.AddCircleFilled(P(11.4f, 24.6f), 1.2f * k, Col(Rgb(0xFFD6E5)), 10);
                draw.AddCircleFilled(P(18.7f, 25.1f), 2.5f * k, Col(Rgb(0x7FB0F0)), 16);
                draw.AddCircleFilled(P(18.7f, 25.1f), k, Col(Rgb(0xDDEBFF)), 10);
                break;
        }
    }

    private static void Feather(ImDrawListPtr draw, Vector2 root, Vector2 tip, float width)
    {
        var axis = tip - root; var length = axis.Length(); var d = axis / length; var n = new Vector2(-d.Y, d.X);
        Span<Vector2> leaf = stackalloc Vector2[10];
        for (var i = 0; i < 5; i++)
        {
            // A leaf bulging at 45% of its length, narrowing to both ends.
            var t = (i + 1) / 6f; var w = width * MathF.Sin(MathF.PI * MathF.Pow(t, .8f));
            leaf[i] = root + d * length * t + n * w;
            leaf[9 - i] = root + d * length * t - n * w;
        }
        var center = root + axis * .45f;
        // The outline runs root → left side → tip → right side.
        Span<Vector2> outline = stackalloc Vector2[12];
        outline[0] = root; for (var i = 0; i < 5; i++) outline[1 + i] = leaf[i]; outline[6] = tip; for (var i = 0; i < 5; i++) outline[7 + i] = leaf[5 + i];
        FillFan(draw, center, outline, Col(Rgb(0xCAC5D7)));
        draw.AddPolyline(ref outline[0], outline.Length, Col(Rgb(0x5E5870)), ImDrawFlags.Closed, .6f);
        draw.AddLine(root, tip, Col(Rgb(0x7D7790)), .6f);
    }

    private static void FillFan(ImDrawListPtr draw, Vector2 center, ReadOnlySpan<Vector2> points, uint color)
    {
        // Gears, stars and leaves are star-shaped around their centre, so a fan from the
        // centre fills them exactly even though they are concave.
        for (var i = 0; i < points.Length; i++) draw.AddTriangleFilled(center, points[i], points[(i + 1) % points.Length], color);
    }

    private static void Sparkle(ImDrawListPtr draw, Vector2 center, float radius, uint color)
    {
        var inner = radius * .24f;
        draw.AddQuadFilled(center + new Vector2(0, -radius), center + new Vector2(inner, 0), center + new Vector2(0, radius), center - new Vector2(inner, 0), color);
        draw.AddQuadFilled(center + new Vector2(-radius, 0), center + new Vector2(0, -inner), center + new Vector2(radius, 0), center + new Vector2(0, inner), color);
    }

    public static float StatusGaugeWidth(UltimateStyle u, float scale) => (u.Kind == UltimateKind.Alexander ? 34 : 47) * scale;

    // Small running-state gauge before the title's centre label.
    public static float DrawStatusGauge(ImDrawListPtr draw, Vector2 min, float height, bool running, UltimateStyle u, float scale, double time)
    {
        var bar = u.Kind == UltimateKind.Alexander;
        var cell = bar ? new Vector2(3, 9) * scale : new Vector2(4, 4) * scale;
        var gap = (bar ? 2 : 3) * scale; var pad = (bar ? 3 : 4) * scale;
        var width = StatusGaugeWidth(u, scale);
        var groove = new Vector2(width, cell.Y + 4 * scale);
        var top = min.Y + (height - groove.Y) * .5f;
        draw.AddRectFilled(new(min.X, top), new(min.X + width, top + groove.Y), Col(u.Groove), 4 * scale);
        draw.AddRect(new(min.X, top), new(min.X + width, top + groove.Y), Col(u.Frame, .6f), 4 * scale);
        var lamp = u.Kind == UltimateKind.Omega ? u.Inlay : u.Active;
        for (var i = 0; i < 6; i++)
        {
            // A light runs left to right once every 1.6 s while the parser is running.
            var phase = running ? (float)((time / 1.6 - i * .125) % 1 + 1) % 1 : 1;
            var strength = running ? MathF.Max(.3f, 1 - phase * 2.2f) : .18f;
            var color = Vector4.Lerp(u.Groove, Vector4.Lerp(lamp, u.ActiveHigh, strength > .9f ? .5f : 0), strength);
            var p = new Vector2(min.X + pad + i * (cell.X + gap), top + 2 * scale);
            if (bar) draw.AddRectFilled(p, p + cell, Col(color), scale);
            else draw.AddCircleFilled(p + cell * .5f, cell.X * .5f, Col(color), 10);
        }
        return width;
    }

    // Marker under each tab label. Every tab owns one; the selected one is lit.
    public static float TabMarkHeight(float scale) => 7 * scale;

    public static void DrawTabMark(ImDrawListPtr draw, Vector2 center, bool selected, bool hovered, UltimateStyle u, float scale)
    {
        var w = 62 * scale; var x0 = center.X - w * .5f; var x1 = center.X + w * .5f; var y0 = center.Y - 3.5f * scale;
        switch (u.Kind)
        {
            case UltimateKind.Alexander:
            {
                var cell = new Vector2(6, 3) * scale; var total = 8 * cell.X + 7 * scale + 2 * scale;
                var min = new Vector2(center.X - total * .5f, center.Y - cell.Y * .5f - scale);
                draw.AddRectFilled(min, min + new Vector2(total, cell.Y + 2 * scale), Col(Rgb(0x070A09)), 2 * scale);
                draw.AddRect(min, min + new Vector2(total, cell.Y + 2 * scale), Col(selected ? u.FrameHigh : u.Frame, selected ? 1 : .45f), 2 * scale);
                for (var i = 0; i < 8; i++)
                {
                    var p = min + new Vector2(scale + i * (cell.X + scale), scale);
                    if (selected) draw.AddRectFilled(p - new Vector2(scale), p + cell + new Vector2(scale), Col(u.Glow, .35f));
                    draw.AddRectFilled(p, p + cell, Col(selected ? u.Inlay : hovered ? Rgb(0x64675B) : Rgb(0x29312C)));
                }
                break;
            }
            case UltimateKind.Bahamut:
            {
                var color = selected ? u.Active : u.Frame; var alpha = selected ? 1 : hovered ? .85f : .55f;
                var step = x0 + w * .46f;
                if (selected) draw.AddLine(new(x0, y0 + 1 * scale), new(step, y0 + scale), Col(u.Active, .25f), 3 * scale);
                draw.AddLine(new(x0, y0 + scale), new(step, y0 + scale), Col(color, alpha), scale);
                draw.AddLine(new(step, y0 + scale), new(step, y0 + 5 * scale), Col(color, alpha), scale);
                draw.AddLine(new(step, y0 + 5 * scale), new(x1 - 6 * scale, y0 + 5 * scale), Col(color, alpha), scale);
                draw.AddCircleFilled(new(x1 - 3 * scale, y0 + 5 * scale), 3 * scale, Col(selected ? u.ActiveHigh : u.Groove), 12);
                draw.AddCircle(new(x1 - 3 * scale, y0 + 5 * scale), 3 * scale, Col(color, alpha), 12, scale);
                break;
            }
            case UltimateKind.Weapon:
            {
                var color = selected ? u.Active : u.Inlay; var alpha = selected ? 1 : hovered ? .85f : .5f;
                if (selected) draw.AddRectFilled(new(x0, y0 + 1.5f * scale), new(x1, y0 + 5.5f * scale), Col(u.Active, .18f), 2 * scale);
                for (var x = x0; x < x1; x += 3.5f * scale) draw.AddLine(new(x, y0 + 5 * scale), new(x + 1.5f * scale, y0 + 2 * scale), Col(color, alpha), 1.2f * scale);
                break;
            }
            case UltimateKind.Dragonsong:
            {
                var alpha = selected ? 1 : hovered ? .6f : .3f; var y = center.Y;
                var ember = Rgb(0xE2622A, alpha); var mid = Rgb(0xC98A5A, alpha); var ice = Rgb(0x7FB6E8, alpha);
                if (selected) draw.AddRectFilled(new(x0, y - 2.5f * scale), new(x1, y + 2.5f * scale), Col(u.Glow, .25f), 2 * scale);
                draw.AddRectFilledMultiColor(new(x0, y - scale), new(center.X, y + scale), Col(ember), Col(mid), Col(mid), Col(ember));
                draw.AddRectFilledMultiColor(new(center.X, y - scale), new(x1, y + scale), Col(mid), Col(ice), Col(ice), Col(mid));
                var r = 3 * scale;
                draw.AddQuadFilled(center + new Vector2(0, -r), center + new Vector2(r, 0), center + new Vector2(0, r), center - new Vector2(r, 0),
                    Col(selected ? u.ActiveHigh : u.FrameHigh, selected ? 1 : .8f));
                break;
            }
            case UltimateKind.Omega:
                for (var i = 0; i < 8; i++)
                {
                    var p = new Vector2(center.X - 24.5f * scale + i * 7 * scale, center.Y);
                    if (selected) draw.AddCircleFilled(p, 3 * scale, Col(u.Inlay, .3f), 10);
                    draw.AddCircleFilled(p, 1.6f * scale, Col(selected ? u.Inlay : hovered ? Rgb(0x596079) : Rgb(0x2A2E3B)), 8);
                }
                break;
            case UltimateKind.Futures:
            {
                var alpha = selected ? 1 : hovered ? .55f : .25f; var y = center.Y;
                if (selected) draw.AddRectFilled(new(x0 - 2 * scale, y - 4 * scale), new(x1 + 2 * scale, y + 4 * scale), Col(Rgb(0xE6AAFF), .22f), 4 * scale);
                var a = Rgb(0xB98CFF, alpha); var b = Rgb(0xFF9EC6, alpha); var c = Rgb(0xFFD58A, alpha);
                draw.AddRectFilledMultiColor(new(x0, y - 2 * scale), new(center.X, y + 2 * scale), Col(a), Col(b), Col(b), Col(a));
                draw.AddRectFilledMultiColor(new(center.X, y - 2 * scale), new(x1, y + 2 * scale), Col(b), Col(c), Col(c), Col(b));
                draw.AddLine(new(x0, y - 1.5f * scale), new(x1, y - 1.5f * scale), Col(Vector4.One, .3f * alpha));
                break;
            }
        }
    }

    // ── controls ───────────────────────────────────────────────────────────────

    internal enum ButtonLook { Normal, Open, Danger }

    public static void DrawButton(ImDrawListPtr draw, Vector2 min, Vector2 max, ButtonLook look, bool hovered, bool held, bool decorated,
        UltimateStyle u, float scale)
    {
        var height = max.Y - min.Y;
        var rounding = Math.Min(u.ButtonRounding * (u.ButtonRounding > 100 ? 1 : scale), height * .5f);
        var closing = look == ButtonLook.Open && hovered;
        var (top, bottom, rim) = look switch
        {
            ButtonLook.Danger => (Rgb(0x6A2828), Rgb(0x521E1E), Rgb(0xA9584F)),
            _ when closing => (CloseFace, CloseFaceLow, CloseRim),
            ButtonLook.Open => (u.ActiveFace, u.ActiveFaceLow, u.ActiveRim),
            _ when held => (u.FacePress, u.FacePress, u.FrameHigh),
            _ when hovered => (u.FaceHover, u.FaceLow, u.HoverRim),
            _ => (u.Face, u.FaceLow, u.Frame),
        };
        if (hovered && !held && look == ButtonLook.Normal) draw.AddRect(min - new Vector2(scale), max + new Vector2(scale), Col(u.Glow, .35f), rounding + scale, ImDrawFlags.None, 2 * scale);
        draw.AddRectFilled(min, max, Col(Vector4.Lerp(top, bottom, .5f)), rounding);
        draw.AddRectFilled(min, new(max.X, min.Y + height * .5f), Col(top, .55f), rounding, ImDrawFlags.RoundCornersTop);
        if (!held) draw.AddLine(new(min.X + rounding, min.Y + 1), new(max.X - rounding, min.Y + 1), Col(Vector4.One, u.Kind == UltimateKind.Futures ? .22f : .08f));
        if (u.Kind == UltimateKind.Futures && !held)
            draw.AddRectFilled(min + new Vector2(1), new(max.X - 1, min.Y + height * .45f), Col(Vector4.One, .06f), rounding, ImDrawFlags.RoundCornersTop);
        draw.AddRect(min, max, Col(rim), rounding, ImDrawFlags.None, scale);
        if (!decorated) return;
        var compact = height < 30 * scale;
        var mid = (min.Y + max.Y) * .5f;
        var lamp = closing ? CloseLamp : look == ButtonLook.Open ? u.ActiveHigh : hovered ? u.HoverRim : u.FrameHigh;
        var lampAlpha = look == ButtonLook.Open || hovered ? 1 : .7f;
        switch (u.Kind)
        {
            case UltimateKind.Alexander:
            {
                // Bronze end caps, like the fittings at both ends of the plate's gauge.
                var inset = (compact ? 4 : 5) * scale; var width = (compact ? 2 : 3) * scale;
                var left = new Vector2(min.X + 4 * scale, min.Y + inset); var right = new Vector2(max.X - 4 * scale - width, min.Y + inset);
                var leftColor = look == ButtonLook.Open ? (closing ? CloseLamp : u.Active) : lamp;
                if (look == ButtonLook.Open || hovered) draw.AddRectFilled(left - new Vector2(scale), new Vector2(left.X + width, max.Y - inset) + new Vector2(scale), Col(look == ButtonLook.Open ? leftColor : u.Glow, .35f));
                draw.AddRectFilled(left, new(left.X + width + (look == ButtonLook.Open ? scale : 0), max.Y - inset), Col(leftColor, lampAlpha), scale);
                draw.AddRectFilled(right, new(right.X + width, max.Y - inset), Col(look == ButtonLook.Open ? (closing ? CloseRim : u.FrameHigh) : lamp, look == ButtonLook.Open ? .55f : lampAlpha), scale);
                break;
            }
            case UltimateKind.Bahamut:
            {
                draw.AddLine(new(min.X + 3 * scale, min.Y + 1), new(max.X - 3 * scale, min.Y + 1), Col(Rgb(0xFFE296), .4f));
                var y = max.Y - (compact ? 3 : 4) * scale; var from = min.X + (compact ? 9 : 12) * scale; var to = max.X - (compact ? 9 : 12) * scale;
                if (look == ButtonLook.Open)
                {
                    draw.AddLine(new(from, y), new(to, y), Col(closing ? CloseLamp : u.Active, .35f), 3 * scale);
                    draw.AddLine(new(from, y), new(to, y), Col(closing ? CloseLamp : u.ActiveHigh), 1.5f * scale);
                }
                else for (var x = from; x < to; x += 15 * scale)
                    draw.AddLine(new(x, y), new(Math.Min(to, x + 12 * scale), y), Col(u.Active, hovered ? .85f : .4f), scale);
                break;
            }
            case UltimateKind.Weapon:
            {
                var color = closing ? CloseRim : look == ButtonLook.Open ? u.Active : hovered ? Rgb(0x9FF2F6) : u.Inlay;
                var y = min.Y + (compact ? 2 : 3) * scale;
                for (var x = min.X + 8 * scale; x < max.X - 8 * scale; x += 3.5f * scale)
                    draw.AddLine(new(x, y + 2 * scale), new(x + 1.5f * scale, y), Col(color, look == ButtonLook.Normal && !hovered ? .85f : 1), scale);
                break;
            }
            case UltimateKind.Dragonsong:
            {
                draw.AddRect(min + new Vector2(3 * scale), max - new Vector2(3 * scale), Col(look == ButtonLook.Open ? u.Active : u.FrameHigh, look == ButtonLook.Open ? .45f : .32f), Math.Max(0, rounding - 2 * scale));
                var inset = (compact ? 3 : 4) * scale; var knob = (compact ? 2 : 2.5f) * scale;
                foreach (var x in new[] { min.X + 8 * scale, max.X - 8 * scale })
                {
                    // A wrought-iron railing post with a diamond knot; it must not read as an arrow.
                    draw.AddLine(new(x, min.Y + inset), new(x, max.Y - inset), Col(lamp, lampAlpha), 1.4f * scale);
                    draw.AddQuadFilled(new(x, mid - knob), new(x + knob, mid), new(x, mid + knob), new(x - knob, mid), Col(lamp, lampAlpha));
                }
                break;
            }
            case UltimateKind.Omega:
            {
                var center = new Vector2(min.X + (compact ? 10 : 13) * scale, mid);
                var color = closing ? CloseLamp : look == ButtonLook.Open ? u.ActiveHigh : hovered ? u.Inlay : Rgb(0x3A3F50);
                if (look == ButtonLook.Open || hovered) draw.AddNgonFilled(center, (compact ? 5 : 7) * scale, Col(look == ButtonLook.Open ? u.Active : u.Inlay, .3f), 6);
                draw.AddNgonFilled(center, (compact ? 3.5f : 4.6f) * scale, Col(color), 6);
                break;
            }
            case UltimateKind.Futures:
            {
                var length = (compact ? 8 : 11) * scale; var width = (compact ? 2 : 2.5f) * scale;
                Leaf(draw, new(min.X + 7 * scale, mid), length, width, -.38f, Col(closing ? CloseLamp : look == ButtonLook.Open ? u.Active : lamp, lampAlpha));
                Leaf(draw, new(max.X - 7 * scale, mid), length, width, .38f, Col(closing ? CloseLamp : look == ButtonLook.Open ? u.Active : lamp, lampAlpha));
                break;
            }
        }
    }

    private static void Leaf(ImDrawListPtr draw, Vector2 center, float length, float width, float angle, uint color)
    {
        var d = new Vector2(MathF.Sin(angle), -MathF.Cos(angle)); var n = new Vector2(-d.Y, d.X);
        Span<Vector2> points = stackalloc Vector2[12];
        for (var i = 0; i < 12; i++)
        {
            var t = i / 12f * MathF.Tau;
            points[i] = center + d * MathF.Cos(t) * length * .5f + n * MathF.Sin(t) * width * (MathF.Cos(t) > 0 ? .8f : 1);
        }
        draw.AddConvexPolyFilled(ref points[0], points.Length, color);
    }

    public static void DrawCheckbox(ImDrawListPtr draw, Vector2 min, float size, bool value, bool hovered, UltimateStyle u, float scale)
    {
        var max = min + new Vector2(size);
        var rounding = (u.Kind == UltimateKind.Omega ? 5 : 3) * scale;
        draw.AddRectFilled(min, max, Col(value ? u.Active : u.Groove), rounding);
        if (value)
        {
            draw.AddRectFilledMultiColor(min + new Vector2(1), max - new Vector2(1), Col(u.ActiveHigh, .55f), Col(u.ActiveHigh, .15f), Col(u.ActiveLow, .6f), Col(u.ActiveHigh, .15f));
            var k = size / 14f;
            Span<Vector2> check = [min + new Vector2(3.2f, 7.4f) * k, min + new Vector2(6, 10.2f) * k, min + new Vector2(11, 4.1f) * k];
            draw.AddPolyline(ref check[0], 3, Col(u.ActiveInk), ImDrawFlags.None, 2.2f * k);
        }
        draw.AddRect(min, max, Col(value ? u.ActiveRim : hovered ? u.HoverRim : u.Frame), rounding, ImDrawFlags.None, scale);
    }

    // Card mark sits on the card's left rim at the title line, so card layouts stay unchanged.
    public static void DrawCardMark(ImDrawListPtr draw, Vector2 center, float scale, UltimateStyle u)
    {
        var r = 5 * scale;
        switch (u.Kind)
        {
            case UltimateKind.Alexander:
                draw.AddCircleFilled(center, r, Col(Rgb(0x2C2619)), 16);
                draw.AddCircle(center, r, Col(u.FrameHigh), 16, scale);
                draw.AddLine(center - new Vector2(0, 3.2f * scale), center + new Vector2(0, 3.2f * scale), Col(Rgb(0xD4AF62)), .9f * scale);
                draw.AddLine(center - new Vector2(3.2f * scale, 0), center + new Vector2(3.2f * scale, 0), Col(Rgb(0xD4AF62)), .9f * scale);
                break;
            case UltimateKind.Bahamut:
                draw.AddQuadFilled(center + new Vector2(0, -r), center + new Vector2(r, 0), center + new Vector2(0, r), center - new Vector2(r, 0), Col(Rgb(0x1A1212)));
                draw.AddQuad(center + new Vector2(0, -r), center + new Vector2(r, 0), center + new Vector2(0, r), center - new Vector2(r, 0), Col(u.FrameHigh), scale);
                draw.AddLine(center - new Vector2(2.4f * scale, 0), center + new Vector2(2.4f * scale, 0), Col(u.Active), 1.3f * scale);
                break;
            case UltimateKind.Weapon:
                Sparkle(draw, center, r * 1.1f, Col(u.FrameHigh));
                break;
            case UltimateKind.Dragonsong:
                draw.AddQuadFilled(center + new Vector2(0, -r), center + new Vector2(r * .8f, 0), center + new Vector2(0, r), center - new Vector2(r * .8f, 0), Col(Rgb(0x26262C)));
                draw.AddQuad(center + new Vector2(0, -r), center + new Vector2(r * .8f, 0), center + new Vector2(0, r), center - new Vector2(r * .8f, 0), Col(u.FrameHigh), .9f * scale);
                draw.AddQuadFilled(center + new Vector2(0, -2 * scale), center + new Vector2(1.5f * scale, 0), center + new Vector2(0, 2 * scale), center - new Vector2(1.5f * scale, 0), Col(u.Active));
                break;
            case UltimateKind.Omega:
                draw.AddNgonFilled(center, r, Col(Rgb(0x1C1E27)), 6);
                draw.AddNgon(center, r, Col(u.FrameHigh), 6, .9f * scale);
                draw.AddCircleFilled(center, 1.5f * scale, Col(u.Inlay), 8);
                break;
            case UltimateKind.Futures:
                Sparkle(draw, center, r * 1.15f, Col(u.Inlay));
                draw.AddCircleFilled(center, 1.4f * scale, Col(u.Active), 8);
                break;
        }
    }

    // ── friends drawer crown ──────────────────────────────────────────────────

    public static float CrownHeight(float scale) => 30 * scale;

    public static void DrawCrown(ImDrawListPtr draw, Vector2 min, float width, float scale, UltimateStyle u)
    {
        var h = CrownHeight(scale); var x0 = min.X + 4 * scale; var x1 = min.X + width - 4 * scale; var cx = (x0 + x1) * .5f; var y0 = min.Y + 2 * scale; var y1 = min.Y + h + 10 * scale;
        var frame = Col(u.Frame); var inner = Col(u.FrameHigh, .4f);
        void Arch(float inset, uint color, float shoulder, float apex)
        {
            var a = x0 + inset; var b = x1 - inset; var top = y0 + inset;
            draw.AddLine(new(a, y1), new(a, top + shoulder), color);
            draw.AddBezierCubic(new(a, top + shoulder), new(a, top + shoulder * .35f), new(cx - (cx - a) * apex, top), new(cx, top), color, scale);
            draw.AddBezierCubic(new(cx, top), new(cx + (b - cx) * apex, top), new(b, top + shoulder * .35f), new(b, top + shoulder), color, scale);
            draw.AddLine(new(b, top + shoulder), new(b, y1), color);
        }
        switch (u.Kind)
        {
            case UltimateKind.Alexander:
                draw.AddCircleFilled(new(cx, y0 + 10 * scale), 46 * scale, Col(u.FrameHigh, .05f), 40);
                Arch(0, frame, 22 * scale, .55f); Arch(6 * scale, inner, 18 * scale, .55f);
                break;
            case UltimateKind.Bahamut:
            {
                Span<Vector2> outline = [new(x0, y1), new(x0, y0 + 14 * scale), new(x0 + 14 * scale, y0), new(x1 - 14 * scale, y0), new(x1, y0 + 14 * scale), new(x1, y1)];
                draw.AddPolyline(ref outline[0], outline.Length, frame, ImDrawFlags.None, 1.2f * scale);
                var moon = new Vector2(cx, y0 + 12 * scale);
                draw.AddCircleFilled(moon, 14 * scale, Col(Rgb(0xC4483A), .15f), 24);
                draw.AddCircleFilled(moon, 9 * scale, Col(Rgb(0x8E1E12)), 24);
                draw.AddCircleFilled(moon - new Vector2(2, 2) * scale, 6 * scale, Col(Rgb(0xD84A30)), 20);
                foreach (var (a, b) in new[] { (new Vector2(-4, -6), new Vector2(-1, -1)), (new Vector2(3, -5), new Vector2(1, 2)), (new Vector2(-3, 4), new Vector2(2, 3)) })
                    draw.AddLine(moon + a * scale, moon + b * scale, Col(Rgb(0x2A0705)), .9f * scale);
                draw.AddLine(moon + new Vector2(-26, 5) * scale, moon + new Vector2(26, -5) * scale, Col(Rgb(0xFF5A44), .35f), 3 * scale);
                draw.AddLine(moon + new Vector2(-26, 5) * scale, moon + new Vector2(26, -5) * scale, Col(Rgb(0xFF5A44)), 1.4f * scale);
                break;
            }
            case UltimateKind.Weapon:
            {
                Arch(0, frame, 12 * scale, .78f);
                var top = new Vector2(cx, y0 + 2 * scale);
                Span<Vector2> horns = [top + new Vector2(0, 12) * scale, top + new Vector2(-12, 9) * scale, top + new Vector2(-24, 0) * scale, top + new Vector2(-10, 4) * scale,
                    top + new Vector2(0, -3) * scale, top + new Vector2(10, 4) * scale, top + new Vector2(24, 0) * scale, top + new Vector2(12, 9) * scale];
                FillFan(draw, top + new Vector2(0, 5) * scale, horns, Col(Rgb(0xC9D3DC)));
                draw.AddPolyline(ref horns[0], horns.Length, Col(Rgb(0x3A4652)), ImDrawFlags.Closed, .6f * scale);
                draw.AddCircleFilled(top + new Vector2(0, 15) * scale, 2.2f * scale, Col(u.Active), 10);
                break;
            }
            case UltimateKind.Dragonsong:
            {
                Arch(0, frame, 26 * scale, .3f); Arch(6 * scale, inner, 22 * scale, .3f);
                var eye = new Vector2(cx, y0 + 15 * scale);
                draw.AddCircleFilled(eye, 15 * scale, Col(Rgb(0xFFB45A), .12f), 24);
                draw.AddCircleFilled(eye, 11 * scale, Col(Rgb(0xFFB45A), .2f), 24);
                draw.AddCircleFilled(eye, 8 * scale, Col(Rgb(0xC9541E)), 24);
                draw.AddCircleFilled(eye - new Vector2(0, scale), 5.5f * scale, Col(Rgb(0xFFB060)), 20);
                draw.AddQuadFilled(eye + new Vector2(0, -6.5f) * scale, eye + new Vector2(1.5f, 0) * scale, eye + new Vector2(0, 6.5f) * scale, eye - new Vector2(1.5f, 0) * scale, Col(Rgb(0x2A0805)));
                draw.AddRectFilledMultiColor(eye + new Vector2(-1, 9) * scale, eye + new Vector2(1, 19) * scale, Col(Vector4.One), Col(Vector4.One), Col(Vector4.One, 0), Col(Vector4.One, 0));
                break;
            }
            case UltimateKind.Omega:
            {
                draw.AddRect(new(x0, y0), new(x1, y1 + 20 * scale), frame, 10 * scale, ImDrawFlags.RoundCornersTop, 1.2f * scale);
                var seed = 7u;
                float Next() { seed = seed * 1664525 + 1013904223; return (seed >> 8) / 16777216f; }
                for (var x = x0 + 12 * scale; x < x1 - 8 * scale; x += 12 * scale)
                for (var y = y0 + 6 * scale + Next() * 6 * scale; y < y1 - 4 * scale;)
                {
                    var length = (3 + Next() * 6) * scale;
                    draw.AddRectFilled(new(x, y), new(x + 3 * scale, y + length), Col(Next() > .7f ? u.Inlay : u.Active, .15f + (1 - (y - y0) / (y1 - y0)) * .45f));
                    y += length + (3 + Next() * 8) * scale;
                }
                break;
            }
            case UltimateKind.Futures:
            {
                draw.AddRect(new(x0, y0), new(x1, y1 + 20 * scale), frame, 18 * scale, ImDrawFlags.RoundCornersTop, 1.2f * scale);
                var c = new Vector2(cx, y0 + 18 * scale);
                draw.PathArcTo(c, 12 * scale, MathF.PI * .8f, MathF.PI * 1.5f, 16); draw.PathStroke(Col(Rgb(0xFF8A3A), .35f), ImDrawFlags.None, 6 * scale);
                draw.PathArcTo(c, 12 * scale, MathF.PI * .8f, MathF.PI * 1.5f, 16); draw.PathStroke(Col(Rgb(0xFF8A3A)), ImDrawFlags.None, 3 * scale);
                draw.PathArcTo(c, 12 * scale, MathF.PI * 1.5f, MathF.PI * 2.2f, 16); draw.PathStroke(Col(Rgb(0x6FC3FF), .35f), ImDrawFlags.None, 6 * scale);
                draw.PathArcTo(c, 12 * scale, MathF.PI * 1.5f, MathF.PI * 2.2f, 16); draw.PathStroke(Col(Rgb(0x6FC3FF)), ImDrawFlags.None, 3 * scale);
                break;
            }
        }
    }

    // ── ornaments behind the control centre ────────────────────────────────────

    // Plate geometry measured from the hr1 assets: the 1920×1040 frame canvas sits centred
    // in the 2560×1440 backing canvas, and the frame's outer edge spans x 175–1745, y 85–1008.
    private const float PlateLeft = 495 / 2560f, PlateRight = 2065 / 2560f, PlateTop = 285 / 1440f, PlateHeight = (1208 - 285) / 1440f;

    public static void DrawOrnaments(ImDrawListPtr draw, Vector2 windowMin, Vector2 windowMax, UltimateStyle u, float scale, double time, UltimateSkinAssets? assets)
    {
        if (u.Kind == UltimateKind.Alexander)
        {
            DrawGears(draw, windowMin, windowMax, scale, time);
            return;
        }
        if (assets?.Icon(u.BackingIcon) is not { } texture) return;
        // The plate is wider than the DACT window. Keep the plate's proportions and anchor
        // each half of the backing to its own window edge, split at the window centre.
        var height = (windowMax.Y - windowMin.Y) / PlateHeight;
        var width = height * texture.Width / Math.Max(1, texture.Height);
        var top = windowMin.Y - PlateTop * height;
        var centre = (windowMin.X + windowMax.X) * .5f;
        // The two halves come from plate columns that no longer meet, so a hard cut leaves a
        // visible seam above and below the window. Fade each half out before it reaches the
        // centre (or its own inner edge when the window is wider than the plate) instead.
        var fade = MathF.Min(140 * scale, (windowMax.X - windowMin.X) * .15f);
        var left = windowMin.X - PlateLeft * width;
        var leftEnd = MathF.Min(centre, left + width * .5f);
        PlateSpan(draw, texture, left, width, top, height, left, leftEnd - fade, 1, 1);
        PlateSpan(draw, texture, left, width, top, height, leftEnd - fade, leftEnd, 1, 0);
        var right = windowMax.X - PlateRight * width;
        var rightStart = MathF.Max(centre, right + width * .5f);
        PlateSpan(draw, texture, right, width, top, height, rightStart, rightStart + fade, 0, 1);
        PlateSpan(draw, texture, right, width, top, height, rightStart + fade, right + width, 1, 1);
    }

    // Draws screen columns [from, to) of a plate placed at plateX, with opacity moving from
    // `alphaFrom` to `alphaTo`. ImGui images take one tint, so a fade is drawn as thin slices.
    private static void PlateSpan(ImDrawListPtr draw, IDalamudTextureWrap texture, float plateX, float width, float top, float height,
        float from, float to, float alphaFrom, float alphaTo)
    {
        if (to <= from) return;
        var steps = alphaFrom == alphaTo ? 1 : Math.Max(1, (int)MathF.Ceiling((to - from) / 4));
        for (var i = 0; i < steps; i++)
        {
            var x0 = from + (to - from) * i / steps; var x1 = from + (to - from) * (i + 1) / steps;
            var t = (i + .5f) / steps; var alpha = alphaFrom + (alphaTo - alphaFrom) * t * t * (3 - 2 * t);
            draw.AddImage(texture.Handle, new(x0, top), new(x1, top + height),
                new((x0 - plateX) / width, 0), new((x1 - plateX) / width, 1), Col(Vector4.One, alpha));
        }
    }

    private static readonly Vector4 Gold = Rgb(0xFFD46A);

    private static void DrawGears(ImDrawListPtr draw, Vector2 min, Vector2 max, float scale, double time)
    {
        // Two clusters of luminous gears break out of the top-right and bottom-left corners.
        (Vector2 Center, float Radius, int Direction)[] gears =
        [
            (new(max.X - 26 * scale, min.Y + 12 * scale), 44 * scale, 1), (new(max.X + 28 * scale, min.Y - 8 * scale), 22 * scale, -1),
            (new(max.X + 22 * scale, min.Y + 54 * scale), 25 * scale, -1),
            (new(min.X + 6 * scale, max.Y - 12 * scale), 40 * scale, -1), (new(min.X - 30 * scale, max.Y - 56 * scale), 20 * scale, 1),
        ];
        foreach (var cluster in new[] { gears[0].Center + new Vector2(14, 10) * scale, gears[3].Center + new Vector2(-10, -12) * scale })
            for (var i = 4; i >= 1; i--) draw.AddCircleFilled(cluster, (34 + i * 16) * scale, Col(Gold, .028f), 48);
        foreach (var (center, radius, direction) in gears)
            Gear(draw, center, radius, (float)(direction * time * MathF.Tau / (direction > 0 ? 80 : 56)), scale);
        Sparks(draw, gears[0].Center + new Vector2(10, 6) * scale, 70 * scale, scale, time, 0);
        Sparks(draw, gears[3].Center + new Vector2(-8, -10) * scale, 60 * scale, scale, time, 17);
    }

    private static void Gear(ImDrawListPtr draw, Vector2 center, float radius, float angle, float scale)
    {
        var teeth = Math.Max(14, (int)MathF.Round(radius / scale * .62f)); var depth = MathF.Max(3 * scale, radius * .12f);
        Span<Vector2> outline = stackalloc Vector2[teeth * 4];
        var step = MathF.Tau / teeth;
        for (var i = 0; i < teeth; i++)
        {
            var a = angle + i * step;
            outline[i * 4] = Polar(center, radius - depth, a + step * .06f);
            outline[i * 4 + 1] = Polar(center, radius, a + step * .26f);
            outline[i * 4 + 2] = Polar(center, radius, a + step * .54f);
            outline[i * 4 + 3] = Polar(center, radius - depth, a + step * .74f);
        }
        FillFan(draw, center, outline, Col(Gold, .14f));
        // A wide faint stroke under the line reads as the plate's soft glow.
        draw.AddPolyline(ref outline[0], outline.Length, Col(Gold, .12f), ImDrawFlags.Closed, 6 * scale);
        draw.AddPolyline(ref outline[0], outline.Length, Col(Gold, .22f), ImDrawFlags.Closed, 3.2f * scale);
        draw.AddPolyline(ref outline[0], outline.Length, Col(Gold, .95f), ImDrawFlags.Closed, 1.5f * scale);
        draw.AddCircle(center, radius * .58f, Col(Gold, .7f), 40, scale);
        for (var i = 0; i < 28; i++) draw.AddCircleFilled(Polar(center, radius * .72f, angle + i * MathF.Tau / 28), .8f * scale, Col(Gold, .55f), 6);
        draw.AddCircle(Polar(center, radius * .25f, angle - .5f), radius * .26f, Col(Gold, .6f), 24, scale);
        draw.AddCircle(Polar(center, radius * .31f, angle + 2.4f), radius * .18f, Col(Gold, .5f), 20, scale);
        draw.AddCircleFilled(center, radius * .1f, Col(Gold, .7f), 12);
    }

    private static void Sparks(ImDrawListPtr draw, Vector2 center, float spread, float scale, double time, int seed)
    {
        for (var i = 0; i < 12; i++)
        {
            var n = i + seed;
            // Deterministic drift: each spark rises, fades in and out, then restarts elsewhere.
            var period = 3.5 + n % 5 * .8;
            var phase = (float)((time / period + n * .37) % 1);
            var angle = n * 2.399f; var distance = spread * (.35f + (n * 37 % 100) / 160f);
            var position = Polar(center, distance, angle) + new Vector2(MathF.Sin(phase * MathF.Tau + n) * 4 * scale, -phase * 26 * scale);
            var alpha = MathF.Sin(phase * MathF.PI);
            var size = (1 + n % 3 * .5f) * scale;
            draw.AddCircleFilled(position, size * 3, Col(Gold, .12f * alpha), 10);
            draw.AddCircleFilled(position, size, Col(Rgb(0xFFF1C0), .9f * alpha), 8);
        }
    }
}
