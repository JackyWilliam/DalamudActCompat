using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace DalamudActCompat.UI.Kit;

// Type scale of the 1.0 design. Sizes are the Figma values; 13 is the body text, which is
// Dalamud's default font, and every other size is built from the same default font so CJK
// coverage matches the rest of the UI. Scaling the bitmap of the 17 px font instead would
// blur titles.
internal static class DactFonts
{
    public const int Title = 18, H2 = 15, Body = 13, Small = 12, Tiny = 11;

    private static readonly Dictionary<int, IFontHandle> Handles = new();

    public static void Initialize(IUiBuilder ui)
    {
        foreach (var size in new[] { Title, H2, Small, Tiny })
        {
            var pixels = ui.FontDefaultSizePx * size / Body;
            Handles[size] = ui.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(pixels)));
        }
    }

    public static void Dispose()
    {
        foreach (var handle in Handles.Values) handle.Dispose();
        Handles.Clear();
    }

    // Until the atlas has built the size (or outside Dalamud, in screenshot tests) the body
    // font is scaled instead, so layout is right from the first frame.
    public static Scope Push(int size)
    {
        if (size == Body) return default;
        if (Handles.TryGetValue(size, out var handle) && handle.Available) return new Scope(handle.Push(), false);
        ImGui.SetWindowFontScale(size / (float)Body);
        return new Scope(null, true);
    }

    public readonly struct Scope(IDisposable? pushed, bool scaled) : IDisposable
    {
        public void Dispose()
        {
            pushed?.Dispose();
            if (scaled) ImGui.SetWindowFontScale(1);
        }
    }
}
