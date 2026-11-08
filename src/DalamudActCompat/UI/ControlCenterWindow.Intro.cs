using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 开屏动画 (Figma 11 开屏动画) and 新版导览 (features.js tour).
// The splash plays when the window opens, as 开屏动画 sets (by default only on the first open of a
// game launch); closing the window ends it. The logo assembles from its SVG parts:
// the three cards rise in one after another, the orbit draws itself around them with a glowing
// head (behind the cards, then in front), the bars grow, the star spins in with a flare and
// D-A-C-T fades in letter by letter. Then the logo flies to where this open lands (header corner
// when signed in, the sign-in page's big logo otherwise) while the backdrop fades and the page
// rises in. A click skips it.
public sealed partial class ControlCenterWindow
{
    private const double SplashSeconds = 1.9;
    // Everything has settled by ~1.32 s; the flight starts here and takes .36 s.
    private const double SplashFlyAt = 1.5;
    private static bool splashPlayedThisSession;
    private double splashStartedAt = -1;
    private double splashRequestedAt;
    // Whether this run assembles the layers; fixed once the clock runs so it never switches style midway.
    private bool splashLayered;
    private bool splashRevealed;
    private (Vector2 Min, float Size) headerLogoSpot;
    private (Vector2 Min, float Size) brandLogoSpot;

    // Assets/dact-logo-layers.png from tools/ui-redesign/logo-layers.js: a 4×3 atlas of 512-px
    // cells, one logo part per cell, each on the full logo canvas. Unset or still loading → the
    // splash grows the flat logo instead.
    internal ISharedImmediateTexture? LogoLayers { get; set; }

    // Cell order is the contract with logo-layers.js.
    private enum LogoLayer { OrbitBack, CardBack, CardMid, CardFront, Bar1, Bar2, Bar3, RingFront, StarGlow, Star }

    // Measured on the 512 logo canvas from the atlas. The orbit is one stroke on an ellipse:
    // OrbitBack covers parameter 10°–180° (behind the cards), RingFront 180°–395° (in front,
    // ending past its own start). Parameter 0° is the ellipse's left end.
    private static readonly Vector2 OrbitCenter = new(248.2f, 255.5f);
    private static readonly Vector2 OrbitMajor = new Vector2(MathF.Cos(2.6763f), MathF.Sin(2.6763f)) * 251.4f;
    private static readonly Vector2 OrbitMinor = new Vector2(-MathF.Sin(2.6763f), MathF.Cos(2.6763f)) * 104.2f;
    private static readonly Vector2 StarCenter = new(416, 135);
    private static readonly (LogoLayer Layer, Vector2 Pivot, double Start)[] SplashCards =
        [(LogoLayer.CardBack, new(146, 251), .10), (LogoLayer.CardMid, new(230, 212), .18), (LogoLayer.CardFront, new(279, 284), .26)];
    private static readonly (LogoLayer Layer, float Left, float Top, float Right, double Start)[] SplashBars =
        [(LogoLayer.Bar1, 223, 281, 263, .62), (LogoLayer.Bar2, 274, 249, 314, .69), (LogoLayer.Bar3, 325, 215, 366, .76)];
    private const float SplashBarBottom = 336;

    // 新版导览 targets, refreshed while the shell draws.
    private readonly Dictionary<Page, (Vector2 Min, Vector2 Max)> navigationSpots = [];
    private (Vector2 Min, Vector2 Max) navigationAreaSpot;
    private (Vector2 Min, Vector2 Max)? searchSpot;
    private (Vector2 Min, Vector2 Max)? feedbackSpot;
    private int tourStep = -1;

    private bool SplashActive => splashStartedAt >= 0;

    // Set when the window opens again (OnOpen); 开屏动画 = 每次打开 plays on it.
    private bool splashReopened;

    public override void OnOpen() => splashReopened = true;

    // The splash runs on ImGui's clock, which keeps going while the window is closed and not
    // drawing, so a run cut short by closing would carry on unseen: the next open resumed it
    // midway, or found it already over and went straight to the page instead of replaying.
    private void EndSplashOnClose() => splashStartedAt = -1;

    private void BeginSplashIfFirstOpen()
    {
        // The first open of this game launch is used up whatever the setting, so switching to
        // 启动游戏后首次 later in the session does not replay it on the next open.
        var firstOpen = !splashPlayedThisSession;
        var reopened = splashReopened;
        splashPlayedThisSession = true;
        splashReopened = false;
        var play = configuration.SplashAnimation switch
        {
            SplashAnimationMode.EveryOpen => firstOpen || reopened,
            SplashAnimationMode.Off => false,
            _ => firstOpen,
        };
        if (!play || DactMotion.Disabled || SplashActive) return;
        splashStartedAt = splashRequestedAt = ImGui.GetTime();
        splashRevealed = false;
    }

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.05f, c3 = c1 + 1;
        return 1 + c3 * MathF.Pow(t - 1, 3) + c1 * MathF.Pow(t - 1, 2);
    }

    private static float EaseOutCubic(float t) => 1 - MathF.Pow(1 - t, 3);

    private static float EaseInOutCubic(float t) => t < .5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2;

    private static float Phase(double t, double start, double length) => (float)Math.Clamp((t - start) / length, 0, 1);

    private void DrawSplashOverlay(bool signedIn)
    {
        if (!SplashActive) return;
        var now = ImGui.GetTime();
        var layers = LogoLayers is { } atlas && atlas.TryGetWrap(out var wrap, out _) ? wrap : null;
        // The atlas loads on first use: hold the first frame (backdrop only) until it is ready,
        // for at most half a second, then run with whichever logo is available.
        if (now - splashStartedAt < .05)
        {
            if (layers is null && LogoLayers is not null && now - splashRequestedAt < .5) splashStartedAt = now;
            splashLayered = layers is not null;
        }
        var t = now - splashStartedAt;
        var skip = ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows);
        if (t >= SplashSeconds || skip)
        {
            splashStartedAt = -1;
            if (!splashRevealed) pageEnteredAt = ImGui.GetTime();
            // The page already drew this frame without its logo (the splash owned it), so the
            // landed logo is drawn once more here instead of blinking out for a frame.
            var landing = signedIn ? headerLogoSpot : brandLogoSpot;
            if (landing.Size > 0)
            {
                var windowMin = ImGui.GetWindowPos();
                var foreground = ImGui.GetForegroundDrawList();
                foreground.PushClipRect(windowMin, windowMin + ImGui.GetWindowSize(), true);
                foreground.AddImage(logoTexture.GetWrapOrEmpty().Handle, landing.Min, landing.Min + new Vector2(landing.Size));
                foreground.PopClipRect();
            }
            return;
        }
        // From the flight on, the page underneath plays its own rise-in while the backdrop fades.
        if (t >= SplashFlyAt && !splashRevealed)
        {
            splashRevealed = true;
            pageEnteredAt = ImGui.GetTime();
        }

        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        // Child windows draw after the parent, so the overlay uses the foreground list, clipped to us.
        var draw = ImGui.GetForegroundDrawList();
        draw.PushClipRect(min, max, true);
        var fly = EaseInOutCubic(Phase(t, SplashFlyAt, .36));
        var backdrop = 1 - Phase(t, SplashFlyAt, .4);
        if (backdrop > 0) draw.AddRectFilled(min, max, DactUi.U32(palette.Surface with { W = backdrop }), ImGui.GetStyle().WindowRounding);

        var center = (min + max) * .5f - new Vector2(0, 24 * s);
        // Accent glow: a soft disc that swells once as the star lands.
        var swell = MathF.Sin(Phase(t, .85, .6) * MathF.PI);
        var glow = (.35f + .3f * swell) * Phase(t, 0, .3) * backdrop;
        // Many faint discs instead of a few strong ones keep the falloff free of visible bands.
        for (var ring = 0; ring < 24; ring++)
        {
            var radius = (170 - ring * 7) * s * (1 + .06f * swell);
            draw.AddCircleFilled(center, radius, DactUi.U32(palette.Accent with { W = glow * .025f }), 64);
        }

        var baseSize = 156 * s;
        var logoMin = center - new Vector2(baseSize * .5f);
        var target = signedIn ? headerLogoSpot : brandLogoSpot;
        if (fly > 0)
        {
            // The assembled layers and the flat logo are the same picture; the flat one lands.
            var size = baseSize;
            if (target.Size > 0)
            {
                size = baseSize + (target.Size - baseSize) * fly;
                logoMin = Vector2.Lerp(logoMin, target.Min, fly);
            }
            var alpha = target.Size > 0 ? 1 : backdrop;
            draw.AddImage(logoTexture.GetWrapOrEmpty().Handle, logoMin, logoMin + new Vector2(size), Vector2.Zero, Vector2.One,
                DactUi.U32(new Vector4(1, 1, 1, alpha)));
        }
        else if (splashLayered && layers is not null)
        {
            DrawLogoAssembly(draw, layers.Handle, logoMin, baseSize, t);
        }
        else
        {
            // Flat fallback: 60 % → 100 % with a slight overshoot and a glint at the star.
            var grow = Phase(t, .3, .32);
            var size = baseSize * (.6f + .4f * EaseOutBack(grow));
            var flatMin = center - new Vector2(size * .5f);
            if (grow > 0)
                draw.AddImage(logoTexture.GetWrapOrEmpty().Handle, flatMin, flatMin + new Vector2(size), Vector2.Zero, Vector2.One,
                    DactUi.U32(new Vector4(1, 1, 1, grow)));
            DrawStarFlare(draw, logoMin + StarCenter * (baseSize / 512), baseSize / 512, Phase(t, .95, .3));
        }

        // D A C T: each letter 60 ms after the previous, spacing 18 → 6, gone as the logo flies.
        const string word = "DACT";
        var spacing = (18 - 12 * EaseInOutCubic(Phase(t, 1.0, .35))) * s;
        var fontSize = 30 * s;
        var font = ImGui.GetFont();
        var widths = word.Select(letter => ImGui.CalcTextSize(letter.ToString()).X * fontSize / ImGui.GetFontSize()).ToArray();
        var total = widths.Sum() + spacing * (word.Length - 1);
        var x = center.X - total * .5f;
        var letterY = center.Y + baseSize * .5f + 18 * s;
        for (var index = 0; index < word.Length; index++)
        {
            var alpha = Phase(t, 1.0 + index * .06, .18) * (1 - Phase(t, SplashFlyAt, .15));
            if (alpha > 0)
                draw.AddText(font, fontSize, new Vector2(x, letterY + (1 - alpha) * 6 * s), DactUi.U32(palette.Text with { W = alpha }), word[index].ToString());
            x += widths[index] + spacing;
        }
        draw.PopClipRect();
    }

    // Draws the logo parts at time t on the 512 canvas placed at origin with the given size.
    // Layer z-order follows the cells; the orbit's head is drawn behind the cards while it runs
    // along the back half and in front of them along the front half.
    private static void DrawLogoAssembly(ImDrawListPtr draw, ImTextureID atlas, Vector2 origin, float size, double t)
    {
        var k = size / 512;
        Vector2 Screen(Vector2 canvas) => origin + canvas * k;
        static Vector2 Uv(LogoLayer layer, Vector2 canvas) =>
            new(((int)layer % 4 + canvas.X / 512) / 4, ((int)layer / 4 + canvas.Y / 512) / 3);
        static uint White(float alpha) => DactUi.U32(new Vector4(1, 1, 1, alpha));
        // Canvas rectangle [from, to] of a layer drawn at the canvas rectangle [destFrom, destTo].
        void Part(LogoLayer layer, Vector2 from, Vector2 to, Vector2 destFrom, Vector2 destTo, float alpha) =>
            draw.AddImage(atlas, Screen(destFrom), Screen(destTo), Uv(layer, from), Uv(layer, to), White(alpha));
        Vector2 OrbitPoint(float degrees, float reach = 1)
        {
            var angle = degrees * MathF.PI / 180;
            return OrbitCenter + (OrbitMajor * MathF.Cos(angle) + OrbitMinor * MathF.Sin(angle)) * reach;
        }
        // Reveals the part of a ring layer between two orbit parameters as a fan of wedges from
        // the ellipse centre. The wedges overshoot the canvas, so drawing is clipped to it; that
        // also keeps the samples inside this layer's atlas cell.
        void Sweep(LogoLayer layer, float from, float to, float alpha)
        {
            if (to <= from || alpha <= 0) return;
            draw.PushClipRect(Screen(Vector2.Zero), Screen(new Vector2(512)), true);
            var steps = (int)MathF.Ceiling((to - from) / 4);
            for (var step = 0; step < steps; step++)
            {
                var a = OrbitPoint(from + (to - from) * step / steps, 1.6f);
                var b = OrbitPoint(from + (to - from) * (step + 1) / steps, 1.6f);
                draw.AddImageQuad(atlas, Screen(OrbitCenter), Screen(a), Screen(b), Screen(b),
                    Uv(layer, OrbitCenter), Uv(layer, a), Uv(layer, b), Uv(layer, b), White(alpha));
            }
            draw.PopClipRect();
        }
        void Comet(float degrees, float strength)
        {
            if (strength <= 0) return;
            // A tapering tail along the orbit, then a white core inside a lavender halo.
            for (var tail = 12; tail >= 1; tail--)
            {
                var point = Screen(OrbitPoint(degrees - tail * 3.5f));
                var fade = 1 - tail / 13f;
                draw.AddCircleFilled(point, (4 + 10 * fade) * k, DactUi.U32(new Vector4(.8f, .76f, 1, strength * fade * .45f)), 16);
            }
            var head = Screen(OrbitPoint(degrees));
            for (var halo = 0; halo < 8; halo++)
                draw.AddCircleFilled(head, (64 - halo * 7) * k, DactUi.U32(new Vector4(.72f, .68f, 1, strength * .09f)), 32);
            draw.AddCircleFilled(head, 13 * k, DactUi.U32(new Vector4(1, 1, 1, strength)), 20);
        }

        // The orbit runs from its tail behind the cards (10°) once around to its end in front (395°).
        var sweep = Phase(t, .30, .78);
        var orbit = 10 + 385 * EaseInOutCubic(sweep);
        var head = MathF.Sin(sweep * MathF.PI);
        var orbitAlpha = Phase(t, .30, .08);
        Sweep(LogoLayer.OrbitBack, 10, Math.Min(orbit, 182), orbitAlpha);
        if (orbit < 180) Comet(orbit, head * .7f);

        // Cards rise from below one after another, settling with a slight overshoot.
        foreach (var (layer, pivot, start) in SplashCards)
        {
            var progress = Phase(t, start, .42);
            if (progress <= 0) continue;
            var settle = EaseOutBack(progress);
            var scale = .86f + .14f * settle;
            var lift = new Vector2(0, (1 - settle) * 36);
            Part(layer, Vector2.Zero, new Vector2(512), pivot - pivot * scale + lift, pivot + (new Vector2(512) - pivot) * scale + lift,
                Phase(t, start, .2));
        }

        // Bars rise out of the card's floor, rounded top first, overshooting slightly and settling.
        foreach (var (layer, left, top, right, start) in SplashBars)
        {
            var progress = Phase(t, start, .34);
            if (progress <= 0) continue;
            var shown = (SplashBarBottom - top) * EaseOutBack(progress);
            Part(layer, new Vector2(left, top), new Vector2(right, top + shown),
                new Vector2(left, SplashBarBottom - shown), new Vector2(right, SplashBarBottom), Phase(t, start, .08));
        }

        Sweep(LogoLayer.RingFront, 178, orbit, orbitAlpha);
        // When the orbit closes, the front ring flashes once.
        var flash = MathF.Sin(Phase(t, 1.04, .3) * MathF.PI);
        if (flash > 0) Part(LogoLayer.RingFront, Vector2.Zero, new Vector2(512), Vector2.Zero, new Vector2(512), flash * .6f);
        if (orbit >= 180) Comet(orbit, head);

        // The star's glow blooms in from wider, then the star spins a quarter turn into place.
        var bloom = Phase(t, 1.0, .25);
        if (bloom > 0)
        {
            var spread = 1 + .35f * (1 - EaseOutCubic(bloom));
            Part(LogoLayer.StarGlow, Vector2.Zero, new Vector2(512), StarCenter - StarCenter * spread,
                StarCenter + (new Vector2(512) - StarCenter) * spread, bloom);
        }
        var pop = Phase(t, 1.02, .30);
        if (pop > 0)
        {
            var scale = EaseOutBack(pop);
            var turn = -(1 - EaseOutCubic(pop)) * MathF.PI / 2;
            var (sin, cos) = MathF.SinCos(turn);
            Vector2 Turned(Vector2 canvas)
            {
                var offset = (canvas - StarCenter) * scale;
                return Screen(StarCenter + new Vector2(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos));
            }
            // The star's bounds plus a margin for its soft edge.
            Vector2 a = new(343, 42), b = new(481, 42), c = new(481, 225), d = new(343, 225);
            draw.AddImageQuad(atlas, Turned(a), Turned(b), Turned(c), Turned(d),
                Uv(LogoLayer.Star, a), Uv(LogoLayer.Star, b), Uv(LogoLayer.Star, c), Uv(LogoLayer.Star, d), White(Phase(t, 1.02, .1)));
        }
        DrawStarFlare(draw, Screen(StarCenter), k, Phase(t, 1.14, .34));

        // Six small sparkles burst out from the star as it lands.
        var burst = Phase(t, 1.12, .4);
        if (burst is > 0 and < 1)
        {
            var travel = EaseOutCubic(burst);
            for (var spark = 0; spark < 6; spark++)
            {
                var angle = (spark * 60 + 15) * MathF.PI / 180;
                var at = Screen(StarCenter + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (34 + 92 * travel));
                var arm = (14 - 10 * travel) * k;
                var ink = DactUi.U32(new Vector4(1, 1, 1, (1 - burst) * .9f));
                draw.AddQuadFilled(at - new Vector2(arm, 0), at - new Vector2(0, arm * .25f), at + new Vector2(arm, 0), at + new Vector2(0, arm * .25f), ink);
                draw.AddQuadFilled(at - new Vector2(0, arm), at + new Vector2(arm * .25f, 0), at + new Vector2(0, arm), at - new Vector2(arm * .25f, 0), ink);
            }
        }
    }

    // A thin four-point flare with an expanding ring, k = screen pixels per logo-canvas pixel.
    private static void DrawStarFlare(ImDrawListPtr draw, Vector2 center, float k, float progress)
    {
        if (progress is <= 0 or >= 1) return;
        var strength = MathF.Sin(progress * MathF.PI);
        var arm = 96 * k * strength;
        var waist = 5 * k;
        var ink = DactUi.U32(new Vector4(1, 1, 1, strength * .9f));
        draw.AddQuadFilled(center - new Vector2(arm, 0), center - new Vector2(0, waist), center + new Vector2(arm, 0), center + new Vector2(0, waist), ink);
        draw.AddQuadFilled(center - new Vector2(0, arm), center + new Vector2(waist, 0), center + new Vector2(0, arm), center - new Vector2(waist, 0), ink);
        var ring = EaseOutCubic(progress);
        draw.AddCircle(center, (20 + 70 * ring) * k, DactUi.U32(new Vector4(.85f, .82f, 1, (1 - ring) * .6f)), 48, Math.Max(1, 3 * k));
    }

    // ---- 新版导览

    private sealed record TourStep(string Title, string Body, Func<(Vector2 Min, Vector2 Max)?> Target);

    public void StartTour()
    {
        tourStep = 0;
        selectedPage = Page.Overview;
    }

    private List<TourStep> TourSteps() =>
    [
        new(text.Get("左侧导航", "Navigation on the left"),
            text.Get("所有功能都在左边：主页、战斗统计、战斗记录、悬浮窗、扩展、好友和 AI 助手；账号与云同步、设置在分隔线下面。",
                "Every page lives on the left: home, meter, history, overlays, extensions, friends and AI; account and settings sit below the divider."),
            () => navigationAreaSpot.Max.X > navigationAreaSpot.Min.X ? navigationAreaSpot : null),
        new(text.Get("全局搜索", "Search everything"),
            !configuration.SearchHotkeyActive()
                ? text.Get("点这里直接在下方搜索：设置项、页面和帮助一起搜；回车直接跳过去，开关可以直接切换。",
                    "Click here to search right below it: settings, pages and help together; Enter jumps there and switches toggle in place.")
                : text.Format($"点这里直接在下方搜索；在游戏里随时按 {ShortcutLabel} 也能弹出搜索框。设置项、页面和帮助一起搜，回车直接跳过去，开关可以直接切换。",
                    $"Click here to search right below it, or press {ShortcutLabel} anywhere in the game for the search box. Settings, pages and help are searched together; Enter jumps there and switches toggle in place."),
            () => searchSpot),
        new(text.Get("好友搬到了侧栏", "Friends moved to the sidebar"),
            text.Get("以前在顶栏右上角，现在和其他页面放在一起；有未读消息时右边会显示数字。",
                "It used to sit in the top-right corner; now it is a page like the others, with the unread count beside it."),
            () => navigationSpots.TryGetValue(Page.Friends, out var spot) ? spot : null),
        new(text.Get("战斗记录变成了页面", "Encounter history is a page now"),
            text.Get("原来的独立窗口搬进了控制中心，还多了「开荒进度」：每次打到哪里、最常见的团灭点都会记下来。",
                "The old window moved in here and gained Progression: how far each pull got and where the party usually wipes."),
            () => navigationSpots.TryGetValue(Page.History, out var spot) ? spot : null),
        new(text.Get("直接反馈问题", "Report problems directly"),
            text.Get("遇到问题点这里，开发者会在 好友 ·「官方通知」里回复你。", "Run into a problem? Send it from here; the developer replies in Friends · DACT announcements."),
            () => feedbackSpot),
        new(text.Get("设置更好找了", "Settings, tidied up"),
            text.Get("外观与皮肤、场景方案在设置里；来信提醒移到了好友，AI 设置在 AI 助手下。",
                "Appearance and scene presets live in Settings; message alerts moved to Friends and AI settings to the AI assistant."),
            () => navigationSpots.TryGetValue(Page.Diagnostics, out var spot) ? spot : null),
        new(text.Get("AI 助手", "AI assistant"),
            text.Get("可以帮你排查问题、复盘战斗、回答使用问题。默认关闭，需要你自己的 API Key，Key 只存在本机。",
                "It can troubleshoot, review your fights and answer questions. It is off by default and uses your own API key, kept on this PC only."),
            () => navigationSpots.TryGetValue(Page.Ai, out var spot) ? spot : null),
    ];

    private void EndTour()
    {
        tourStep = -1;
        if (configuration.ShowNewVersionTour)
        {
            configuration.ShowNewVersionTour = false;
            saveConfiguration();
        }
    }

    private void DrawTour(CloudClientSnapshot snapshot)
    {
        if (tourStep < 0 && configuration.ShowNewVersionTour && snapshot.CanUseDact && !SplashActive) StartTour();
        if (tourStep < 0 || SplashActive) return;
        var steps = TourSteps();
        // Steps whose target is not on screen (no friends while signed out, …) are skipped.
        while (tourStep < steps.Count && steps[tourStep].Target() is null) tourStep++;
        if (tourStep >= steps.Count)
        {
            EndTour();
            return;
        }
        var step = steps[tourStep];
        var spot = step.Target()!.Value;
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var hole = (Min: spot.Min - new Vector2(4 * s), Max: spot.Max + new Vector2(4 * s));

        // Four scrim pieces leave a hole around the target, which gets an accent ring. They are
        // drawn in a child of this window submitted last: above the page, but below the tour card,
        // which is a window of its own (the foreground list would cover the card as well).
        ImGui.SetCursorPos(Vector2.Zero);
        ImGui.BeginChild("##dact-tour-scrim", ImGui.GetWindowSize(), false,
            ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(min, max, false);
        var scrim = DactUi.U32(new Vector4(.016f, .024f, .035f, .62f));
        var rounding = ImGui.GetStyle().WindowRounding;
        draw.AddRectFilled(min, new Vector2(max.X, hole.Min.Y), scrim, rounding, ImDrawFlags.RoundCornersTop);
        draw.AddRectFilled(new Vector2(min.X, hole.Max.Y), max, scrim, rounding, ImDrawFlags.RoundCornersBottom);
        draw.AddRectFilled(new Vector2(min.X, hole.Min.Y), new Vector2(hole.Min.X, hole.Max.Y), scrim);
        draw.AddRectFilled(new Vector2(hole.Max.X, hole.Min.Y), new Vector2(max.X, hole.Max.Y), scrim);
        draw.AddRect(hole.Min, hole.Max, DactUi.U32(palette.Accent), 9 * s, ImDrawFlags.None, 2 * s);
        draw.PopClipRect();
        ImGui.EndChild();

        // The card sits beside sidebar targets and under header targets, kept inside the window.
        var width = 320 * s;
        var inHeader = spot.Max.Y <= min.Y + 60 * s;
        var position = inHeader
            ? new Vector2(Math.Clamp(spot.Min.X - 40 * s, min.X + 12 * s, max.X - width - 12 * s), hole.Max.Y + 12 * s)
            : new Vector2(hole.Max.X + 16 * s, Math.Clamp((spot.Min.Y + spot.Max.Y) * .5f - 60 * s, min.Y + 56 * s, max.Y - 220 * s));
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16 * s));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, Math.Max(1, s));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, palette.Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, palette.Accent);
        var open = ImGui.Begin("##dact-tour", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                                              ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        if (open)
        {
            ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
            using (DactUi.Gap(10))
            {
                var row = ImGui.GetCursorScreenPos();
                var inner = ImGui.GetContentRegionAvail().X;
                var counter = $"{tourStep + 1} / {steps.Count}";
                var cardDraw = ImGui.GetWindowDrawList();
                DactUi.DrawText(cardDraw, row, 16 * s, text.Get("1.0 新版导览", "What's new in 1.0"), palette.Accent, DactFonts.Small);
                DactUi.DrawText(cardDraw, new Vector2(row.X + inner - DactUi.TextWidth(counter, DactFonts.Small), row.Y), 16 * s, counter, palette.Muted, DactFonts.Small);
                ImGui.Dummy(new Vector2(inner, 16 * s));
                DactUi.Text(step.Title, size: DactFonts.H2);
                DactUi.Wrapped(step.Body, palette.Text, DactFonts.Body, inner);
                var dots = ImGui.GetCursorScreenPos();
                for (var index = 0; index < steps.Count; index++)
                {
                    var active = index == tourStep;
                    var dotWidth = (active ? 16 : 6) * s;
                    cardDraw.AddRectFilled(dots, dots + new Vector2(dotWidth, 6 * s), DactUi.U32(active ? palette.Accent : palette.Hover), 3 * s);
                    dots.X += dotWidth + 5 * s;
                }
                ImGui.Dummy(new Vector2(inner, 6 * s));
                if (DactUi.Button(text.Get("跳过导览", "Skip tour") + "##tour-skip", ButtonKind.Ghost)) EndTour();
                var last = tourStep == steps.Count - 1;
                var next = last ? text.Get("完成", "Done") : text.Get("下一步", "Next");
                var previous = text.Get("上一步", "Back");
                // Captured before the click changes tourStep, so this frame's layout stays on one line.
                var hasBack = tourStep > 0;
                var right = DactUi.ButtonWidth(next, ButtonKind.Primary) + (hasBack ? DactUi.ButtonWidth(previous) + 8 * s : 0);
                ImGui.SameLine(inner - right + ImGui.GetStyle().WindowPadding.X);
                if (hasBack && DactUi.Button(previous + "##tour-back"))
                {
                    tourStep--;
                    // Stepping back skips targets that are not on screen, the same way forward does.
                    while (tourStep > 0 && steps[tourStep].Target() is null) tourStep--;
                }
                if (hasBack) ImGui.SameLine(0, 8 * s);
                if (DactUi.Button(next + "##tour-next", ButtonKind.Primary))
                {
                    if (last) EndTour();
                    else tourStep++;
                }
            }
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) EndTour();
        }
        ImGui.End();
    }
}
