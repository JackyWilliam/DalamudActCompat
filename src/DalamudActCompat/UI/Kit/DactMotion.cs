using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI.Kit;

// Animation state for the 1.0 UI. Controls keep a value per ImGui ID that eases toward its
// target every frame. Exponential easing is frame-rate independent and can be re-targeted
// mid-flight without a jump, so hover, toggle and selection changes never snap or restart.
internal static class DactMotion
{
    // Response rates (1/s): larger is quicker. 18 settles in ~0.25 s, 10 in ~0.45 s.
    public const float Fast = 22, Normal = 16, Gentle = 10;

    // Durations for one-shot transitions (page enter, dialog in/out).
    public const float PageSeconds = .22f, DialogSeconds = .18f;

    private struct Track
    {
        public Vector4 Value;
        public int Frame;
    }

    private static readonly Dictionary<uint, Track> Tracks = new();
    private static int lastPruneFrame;

    // Lets tests and screenshots render the settled state of every animation.
    internal static bool Disabled { get; set; }

    public static float Float(uint id, float target, float rate = Normal, float? from = null)
        => Advance(id, new Vector4(target, 0, 0, 0), rate, from is { } start ? new Vector4(start, 0, 0, 0) : null).X;

    public static float Float(string id, float target, float rate = Normal, float? from = null)
        => Float(ImGui.GetID(id), target, rate, from);

    public static Vector4 Color(uint id, Vector4 target, float rate = Normal)
        => Advance(id, target, rate, null);

    public static Vector2 Point(uint id, Vector2 target, float rate = Normal)
    {
        var value = Advance(id, new Vector4(target, 0, 0), rate, null);
        return new Vector2(value.X, value.Y);
    }

    // 0 -> 1 eased progress of a one-shot transition that started at startedAt (ImGui time).
    public static float Progress(double startedAt, float seconds)
    {
        if (Disabled || seconds <= 0) return 1;
        return EaseOutCubic((float)Math.Clamp((ImGui.GetTime() - startedAt) / seconds, 0, 1));
    }

    public static float EaseOutCubic(float t)
    {
        t = Math.Clamp(t, 0, 1);
        var inverse = 1 - t;
        return 1 - inverse * inverse * inverse;
    }

    // Slight overshoot for things that pop in (dialogs, toasts); settles exactly at 1.
    public static float EaseOutBack(float t)
    {
        t = Math.Clamp(t, 0, 1);
        const float c1 = 1.20158f, c3 = c1 + 1;
        return 1 + c3 * MathF.Pow(t - 1, 3) + c1 * MathF.Pow(t - 1, 2);
    }

    private static Vector4 Advance(uint id, Vector4 target, float rate, Vector4? from)
    {
        var frame = ImGui.GetFrameCount();
        PruneOccasionally(frame);
        if (Disabled)
        {
            Tracks[id] = new Track { Value = target, Frame = frame };
            return target;
        }

        if (!Tracks.TryGetValue(id, out var track) || frame - track.Frame > 2)
        {
            // A control that was not drawn last frame (another page, scrolled away) shows its
            // settled state; only an explicit start value makes it animate in.
            track.Value = from ?? target;
        }
        else if (track.Frame != frame)
        {
            var step = 1 - MathF.Exp(-rate * Math.Max(0, ImGui.GetIO().DeltaTime));
            track.Value += (target - track.Value) * step;
            if (Vector4.DistanceSquared(track.Value, target) < 1e-7f) track.Value = target;
        }

        track.Frame = frame;
        Tracks[id] = track;
        return track.Value;
    }

    private static void PruneOccasionally(int frame)
    {
        if (frame - lastPruneFrame < 600) return;
        lastPruneFrame = frame;
        foreach (var id in Tracks.Where(pair => frame - pair.Value.Frame > 600).Select(pair => pair.Key).ToList())
            Tracks.Remove(id);
    }
}
