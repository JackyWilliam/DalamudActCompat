using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

// Reported on 1.0.0.7: the D / T pane always had a scrollbar, on the window and on the list
// inside it. A meter window never scrolls as a whole (its rows scroll in their own list), and
// that list only scrolls when the rows really do not fit. Runs in its own process because
// cimgui can only be copied beside the tests before it is loaded.
internal static class MeterScrollbarNativeSmokeTests
{
    internal static void Run()
    {
        if (Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") is not { Length: > 0 } library) return;
        var nativePath = Path.Combine(AppContext.BaseDirectory, "cimgui.dll");
        try { File.Copy(library, nativePath, true); } catch (IOException) when (File.Exists(nativePath)) { }
        NativeLibrary.Load(nativePath);
        DrawFrames();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static unsafe void DrawFrames()
    {
        var context = ImGui.CreateContext();
        try
        {
            var setup = ImGui.GetIO(); setup.IniFilename = null; setup.LogFilename = null;
            ushort* ranges = stackalloc ushort[] { 0x20, 0x024f, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            setup.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(setup.Fonts.Build(), "Meter scrollbar font atlas failed.");
            var logo = new SkinSmokeTests.EmptyTexture();
            Measure(context, logo);
            // The collapsed heights changed with this fix; the collapse animation, anchors and
            // drag checks must still hold with them.
            SkinSmokeTests.MeterCollapseNative(new NativeUiRasterizer(setup.Fonts), Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT"), logo);
        }
        finally
        {
            ImGui.DestroyContext(context);
        }
    }

    private static unsafe void Measure(ImGuiContextPtr context, SkinSmokeTests.EmptyTexture logo)
    {
        var io = ImGui.GetIO(); io.FontGlobalScale = 1; io.DisplaySize = new(1200, 900); io.DeltaTime = 1f / 60;
        DactTheme.SetCurrent(new(), false, 0);
        var cases = 0;
        var failures = new List<string>();
        foreach (var kind in new[] { "classic", "damage-tank", "healer" })
        foreach (var compact in new[] { false, true })
        foreach (var summary in new[] { true, false })
        foreach (var header in new[] { true, false })
        foreach (var scale in new[] { .65f, 1f, 1.4f, 2f })
        // 1100 px holds every column at each font scale. 640 px does not at the larger ones, so
        // the list gains a horizontal scrollbar that a collapsed meter has to leave room for.
        foreach (var width in compact ? new[] { 1100f, 640f } : new[] { 1100f })
        foreach (var height in compact ? new[] { 420f } : new[] { 900f, 420f, 260f, 190f })
        {
            var config = new PluginConfiguration(); config.ApplyMigrations(); config.Fflogs.Enabled = false;
            var text = new UiText(config); var store = new EncounterStateStore();
            var encounter = (Encounter)typeof(MeterStyleEditorWindow).GetMethod("CreatePreviewEncounter", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [text])!;
            store.UpdateCurrent(encounter with { EndTime = encounter.StartTime.AddMinutes(3) });
            var service = new MeterService(store, config.Meter);
            var icons = new JobIconTextureSet(null!, Path.Combine(Path.GetTempPath(), "dact-missing-preview-icons"));
            var classic = new MeterWindow(service, null!, config, text, icons, logo, logo, logo, (_, name) => name, () => { });
            var isClassic = kind == "classic";
            Window window = isClassic ? classic : new RoleSplitMeterWindow(service, config, text, classic, () => { },
                kind == "healer" ? RoleSplitGroup.Healer : RoleSplitGroup.DamageTank);
            var profile = isClassic ? config.Meter.ClassicWindow : kind == "healer" ? config.Meter.RoleSplitHealerWindow : config.Meter.RoleSplitDamageWindow;
            profile.FontScale = scale; profile.ShowHeader = header;
            if (isClassic) { config.Meter.FontScale = scale; config.Meter.ShowHeader = header; config.Meter.CompactMode = compact; }
            else if (kind == "healer") config.Meter.RoleSplitHealerCompact = compact;
            else config.Meter.RoleSplitDamageCompact = compact;
            foreach (var slot in profile.Slots.Where(slot => MeterSlotPresentation.IsTeamSummaryMetric(slot.Metric))) slot.Visible = summary;
            var label = $"{kind}/compact={compact}/summary={summary}/header={header}/scale={scale}/size={width}x{height}";
            io.AddMouseButtonEvent(0, false); io.AddMousePosEvent(-100, -100);
            // Unique per case: ImGui keeps size and scroll state per window name across frames.
            var name = $"{window.WindowName}-scrollbar-{cases}";
            // Only the first frame sets the size; a collapsed meter then settles to its own height.
            for (var frame = 0; frame < 40; frame++)
            {
                ImGui.NewFrame(); window.PreDraw();
                if (frame == 0) { ImGui.SetNextWindowPos(new(80, 0)); ImGui.SetNextWindowSize(new(width, height)); }
                var constraints = window.SizeConstraints!.Value;
                ImGui.SetNextWindowSizeConstraints(constraints.MinimumSize, constraints.MaximumSize);
                ImGui.Begin(name, window.Flags | ImGuiWindowFlags.NoSavedSettings);
                window.Draw(); ImGui.End(); window.PostDraw(); ImGui.Render();
            }

            ImGuiWindowPtr outer = default, list = default;
            for (var index = 0; index < context.Windows.Size; index++)
            {
                var candidate = context.Windows[index];
                var candidateName = Marshal.PtrToStringUTF8((nint)candidate.Name) ?? "";
                if (candidateName == name) outer = candidate;
                else if (candidate.Active && candidateName.StartsWith(name + "/", StringComparison.Ordinal)) list = candidate;
            }
            cases++;
            if (outer.Handle == null || list.Handle == null)
            {
                failures.Add($"{label}: meter window or its row list was not drawn.");
                continue;
            }
            var state = $"window {outer.Size} content {outer.ContentSize} scroll {outer.ScrollMax}; list {list.Size} content {list.ContentSize} scroll {list.ScrollMax}";
            if (outer.ScrollbarY || outer.ScrollMax.Y != 0)
                failures.Add($"{label}: the meter window itself scrolls ({state}).");
            // A collapsed meter shows one row, so its list has nothing to scroll. An expanded one
            // scrolls its list only by what the rows exceed it (the sample D / T party at 200 %
            // font is taller than 900 px), and its list ends where the window's content ends.
            if (compact ? list.ScrollbarY || list.ScrollMax.Y != 0
                        : list.ScrollbarY != list.ContentSize.Y > list.Size.Y + .5f)
                failures.Add($"{label}: the row list scrolls although its rows fit ({state}).");
            // 900 px holds the whole sample party up to 140 % font in every pane.
            if (!compact && height == 900 && scale <= 1.4f && list.ScrollbarY)
                failures.Add($"{label}: the row list scrolls in a window tall enough for every row ({state}).");
        }
        Check(failures.Count == 0,
            $"{failures.Count} of {cases} meter layouts have a needless scrollbar:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        Console.WriteLine($"Meter scrollbars native: {cases} layouts (classic, D / T, healer; expanded and collapsed; team summary and header on/off; four font scales) passed.");
    }
}
