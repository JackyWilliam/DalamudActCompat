using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Newtonsoft.Json;

// Requested after 1.0.0.8: every player module of the horizontal meter had a fixed width, so the
// only way to see the whole party was a longer window, and the team totals stretched across the
// whole window with the label and its number at opposite ends.
internal static class HorizontalMeterLayoutSmokeTests
{
    private const float Spacing = HorizontalMeterWindow.ItemSpacing;

    internal static void Run()
    {
        // Off, which is every saved layout: the configured width whatever the window does.
        foreach (var body in new[] { 200f, 700f, 1350f, 4000f })
            Check(HorizontalMeterWindow.ResolveItemWidth(body, 8, 250, false, 1) == 250, "Fitting changed a layout that has it off.");

        // On: eight players share a 1350 px row exactly, and the row has nothing left to scroll.
        var shared = HorizontalMeterWindow.ResolveItemWidth(1350, 8, 250, true, 1);
        Check(shared == 153 && (8 * shared) + (7 * Spacing) <= 1350, $"Eight players did not fit a 1350 px row ({shared}).");
        // Room to spare never stretches a module past its configured width.
        Check(HorizontalMeterWindow.ResolveItemWidth(4000, 8, 250, true, 1) == 250 &&
              HorizontalMeterWindow.ResolveItemWidth(1350, 1, 250, true, 1) == 250 &&
              HorizontalMeterWindow.ResolveItemWidth(1350, 0, 250, true, 1) == 250,
            "A wide window stretched the modules.");
        // Too narrow to stay readable: stop at the minimum and scroll, as the carousel always did.
        var floor = HorizontalMeterWindow.ResolveItemWidth(600, 8, 250, true, 1);
        Check(floor == HorizontalMeterWindow.MinimumFittedItemWidth && (8 * floor) + (7 * Spacing) > 600,
            $"A cramped row went below the readable minimum ({floor}).");
        // Larger text needs a wider minimum, but never more than the user's own module width.
        Check(HorizontalMeterWindow.ResolveItemWidth(600, 8, 250, true, 1.5f) == HorizontalMeterWindow.MinimumFittedItemWidth * 1.5f &&
              HorizontalMeterWindow.ResolveItemWidth(600, 8, 140, true, 1.8f) == 140,
            "The readable minimum ignored the font scale or exceeded the configured width.");

        // Team totals: the vertical meters keep splitting the whole row...
        Check(MeterSlotPresentation.ResolveTeamSummaryCells(900, 3, null, 0) == (300, 300) &&
              MeterSlotPresentation.ResolveTeamSummaryCells(900, 1, null, 18) == (900, 900),
            "Team totals of the vertical meters no longer share the whole row.");
        // ...the horizontal one caps each at a module and lines them up from the left...
        Check(MeterSlotPresentation.ResolveTeamSummaryCells(1350, 2, 250, Spacing) == (250, 250 + Spacing) &&
              MeterSlotPresentation.ResolveTeamSummaryCells(4000, 2, 250, Spacing) == (250, 250 + Spacing),
            "Horizontal team totals still grow with the window.");
        // ...and shares what there is when even that does not fit.
        Check(MeterSlotPresentation.ResolveTeamSummaryCells(300, 2, 250, Spacing) == (141, 141 + Spacing),
            "Horizontal team totals overflow a narrow window.");
        Check(HorizontalMeterWindow.ResolveSummaryCellWidth(250, 1) == 250 &&
              HorizontalMeterWindow.ResolveSummaryCellWidth(140, 1) == 200 &&
              HorizontalMeterWindow.ResolveSummaryCellWidth(140, 1.5f) == 300,
            "A team total is narrower than its label and number need.");

        // The switch is off by default and for every configuration saved before it existed,
        // and it survives the snapshot that cloud sync and backups use.
        var config = new PluginConfiguration(); config.ApplyMigrations();
        Check(!config.Meter.HorizontalWindow.FitItemsToWindow, "Fitting is on by default.");
        var legacy = JsonConvert.DeserializeObject<PluginConfiguration>(
            "{\"Version\":19,\"Meter\":{\"HorizontalWindow\":{\"IsEnabled\":true,\"ItemWidth\":300}}}")!;
        legacy.ApplyMigrations();
        Check(!legacy.Meter.HorizontalWindow.FitItemsToWindow && legacy.Meter.HorizontalWindow.ItemWidth == 300,
            "A saved horizontal layout changed on load.");
        config.Meter.HorizontalWindow.FitItemsToWindow = true;
        var snapshot = config.CreateSnapshot();
        config.ResetToDefaults("logs");
        Check(!config.Meter.HorizontalWindow.FitItemsToWindow, "Reset kept the fitting switch on.");
        config.RestoreFrom(snapshot);
        Check(config.Meter.HorizontalWindow.FitItemsToWindow, "Snapshot restore lost the fitting switch.");
    }

    // The real window, drawn with the game's ImGui. Runs in its own process because cimgui can
    // only be copied beside the tests before it is loaded.
    internal static void RunNative()
    {
        if (Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") is not { Length: > 0 } library) return;
        var nativePath = Path.Combine(AppContext.BaseDirectory, "cimgui.dll");
        try { File.Copy(library, nativePath, true); } catch (IOException) when (File.Exists(nativePath)) { }
        NativeLibrary.Load(nativePath);
        DrawFrames();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static unsafe void DrawFrames()
    {
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null;
            ushort* ranges = stackalloc ushort[] { 0x20, 0x024f, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(io.Fonts.Build(), "Horizontal meter font atlas failed.");
            io.FontGlobalScale = 1; io.DisplaySize = new(2400, 400); io.DeltaTime = 1f / 60;
            io.AddMouseButtonEvent(0, false); io.AddMousePosEvent(-100, -100);
            DactTheme.SetCurrent(new(), false, 0);
            var raster = new NativeUiRasterizer(io.Fonts);
            var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT");
            if (output is not null) Directory.CreateDirectory(output);

            var config = new PluginConfiguration(); config.ApplyMigrations(); config.Fflogs.Enabled = false;
            var text = new UiText(config); var store = new EncounterStateStore();
            var encounter = (Encounter)typeof(MeterStyleEditorWindow).GetMethod("CreatePreviewEncounter", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [text])!;
            store.UpdateCurrent(encounter with { EndTime = encounter.StartTime.AddMinutes(3) });
            var service = new MeterService(store, config.Meter);
            var icons = new JobIconTextureSet(null!, Path.Combine(Path.GetTempPath(), "dact-missing-horizontal-icons"));
            var window = new HorizontalMeterWindow(service, config, text, icons, () => { });
            var profile = config.Meter.HorizontalWindow;
            profile.IsEnabled = true; profile.BackgroundOpacity = .85f;
            foreach (var slot in profile.Slots.Where(slot => MeterSlotPresentation.IsTeamSummaryMetric(slot.Metric))) slot.Visible = true;
            var players = MeterSlotPresentation.SelectParty(service.GetRows(encounter), 0).Count;
            Check(players >= 4, $"The sample party has only {players} players.");
            var offset = typeof(HorizontalMeterWindow).GetField("scrollOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var cases = 0;
            foreach (var fit in new[] { false, true })
            foreach (var scale in new[] { 1f, 1.5f })
            foreach (var width in new[] { 520f, 760f, 1000f, 1350f, 2300f })
            {
                profile.FitItemsToWindow = fit; profile.FontScale = scale;
                // Scrolled as far as it goes, the offset left after a frame is the scroll range.
                offset.SetValue(window, 1_000_000f);
                var name = $"{window.WindowName}-layout-{cases++}";
                for (var frame = 0; frame < 6; frame++)
                {
                    ImGui.NewFrame(); window.PreDraw();
                    ImGui.SetNextWindowPos(new(20, 20)); ImGui.SetNextWindowSize(new(width, 150 * scale));
                    ImGui.Begin(name, window.Flags | ImGuiWindowFlags.NoSavedSettings);
                    window.Draw(); ImGui.End(); window.PostDraw(); ImGui.Render();
                    Check(context.ColorStack.Size == 0 && context.StyleVarStack.Size == 0, "The horizontal meter leaked style state.");
                }

                // The window pads its content by 5 px on each side.
                var body = width - 10;
                var item = HorizontalMeterWindow.ResolveItemWidth(body, players, profile.ItemWidth, fit, scale);
                var expected = Math.Max(0, (players * item) + ((players - 1) * Spacing) - body);
                var range = (float)offset.GetValue(window)!;
                var label = $"fit={fit}/scale={scale}/width={width}";
                Check(Math.Abs(range - expected) < 1, $"{label}: the row scrolls by {range} px, expected {expected} (module {item} px).");
                if (!fit) Check(item == profile.ItemWidth, $"{label}: a layout with fitting off changed its module width.");
                // Wide enough for every module at its readable minimum: nothing is cut off.
                if (fit && body >= (players * HorizontalMeterWindow.MinimumFittedItemWidth * scale) + ((players - 1) * Spacing))
                    Check(range == 0, $"{label}: players are still cut off although the window can hold them all.");
                Check(ImGui.GetDrawData().TotalVtxCount > 100, $"{label}: the horizontal meter rendered no content.");
                if (output is not null && scale == 1f)
                    raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"horizontal-{(fit ? "fit" : "fixed")}-{width}.png"));
            }
            Console.WriteLine($"Horizontal meter native: {cases} layouts ({players} players; fitting on/off; two font scales; five window widths) passed.");
        }
        finally
        {
            DactTheme.SetCurrent(new(), false, 0);
            ImGui.DestroyContext(context);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
