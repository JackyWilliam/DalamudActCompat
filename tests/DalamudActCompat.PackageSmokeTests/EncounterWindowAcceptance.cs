using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

// The combat history window draws its list cards and rows by hand, so only a native
// frame per skin/language/width/font size can show text escaping a card or panel.
internal static class EncounterWindowAcceptance
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    internal static unsafe void Run(bool nativeLibraryLoaded = false)
    {
        // The combined suite already loaded cimgui for overlay acceptance. Windows
        // locks that DLL until process exit, so only the standalone entry may copy it.
        if (!nativeLibraryLoaded)
        {
            var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") ?? throw new InvalidOperationException("Native ImGui is required.");
            File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true); NativeLibrary.Load(library);
        }
        var context = ImGui.CreateContext();
        var output = Path.Combine(Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT")!, "history");
        Directory.CreateDirectory(output);
        var root = Path.Combine(Path.GetTempPath(), $"dact-history-acceptance-{Guid.NewGuid():N}");
        var previousUltimateUnlock = SkinCatalog.UltimateUnlocked;
        SkinCatalog.UltimateUnlocked = () => true;
        var results = new List<object>(); var failures = new List<string>();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0x024f, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(io.Fonts.Build(), "History acceptance font atlas failed.");
            var raster = new NativeUiRasterizer(io.Fonts); SkinSmokeTests.LoadGameTextures(raster);

            var config = new PluginConfiguration(); config.Fflogs.Enabled = false;
            config.Appearance.UnlockedEasterEggs.UnionWith(SkinCatalog.All.Where(skin => skin.EasterEgg).Select(skin => skin.Id));
            var text = new UiText(config);
            var sample = SampleEncounterFactory.Create(DateTimeOffset.Now);
            Encounter Pull(int index) => sample with { Id = Guid.NewGuid(), StartTime = sample.StartTime.AddMinutes(index * 6) };
            var folder = sample with { Id = Guid.NewGuid(), SegmentRecords = [Pull(0), Pull(1)] };
            var store = new EncounterStateStore(); store.UpdateRecent([folder, sample]);
            var paths = new PluginPaths(root);
            Directory.CreateDirectory(paths.EncounterLogDirectory);
            File.WriteAllText(Path.Combine(paths.EncounterLogDirectory, "20261030-193519-sample.json"), JsonSerializer.Serialize(sample));
            using var logo = new SkinSmokeTests.EmptyTexture();
            var icons = new JobIconTextureSet(null!, Path.Combine(root, "missing-icons"));
            var window = new EncounterWindow(store, paths, config, text, icons, logo, (_, name) => name, () => { });
            var page = typeof(EncounterWindow).GetField("selectedPage", Private)!;
            var selectedRecent = typeof(EncounterWindow).GetField("selectedRecentId", Private)!;
            var expanded = (HashSet<Guid>)typeof(EncounterWindow).GetField("expandedRecentFolderIds", Private)!.GetValue(window)!;
            var size = new Vector2(980, 620);
            void Frame()
            {
                io.DisplaySize = size + new Vector2(40);
                ImGui.NewFrame();
                DactTheme.SetCurrent(config.Appearance, true, 3);
                using (DactTheme.PushFrame())
                {
                    window.PreDraw();
                    ImGui.SetNextWindowPos(new(20)); ImGui.SetNextWindowSize(size);
                    ImGui.Begin(window.WindowName, window.Flags | ImGuiWindowFlags.NoSavedSettings);
                    window.Draw();
                    ImGui.End();
                    window.PostDraw();
                }
                ImGui.Render();
                Check(context.ColorStack.Size == 0 && context.StyleVarStack.Size == 0, "History acceptance leaked theme scopes.");
            }

            foreach (var skin in SkinCatalog.All)
            {
                config.Appearance.SelectedSkin = skin.Id;
                foreach (var language in UiLanguages.All)
                foreach (var width in new[] { 980, 780 })
                foreach (var scale in new[] { 1f, 1.4f, 2f })
                foreach (var scenario in new[] { "pull", "single", "logs" })
                {
                    config.UiLanguage = language.Id; io.FontGlobalScale = scale; size = new(width, 620);
                    page.SetValue(window, Enum.ToObject(page.FieldType, scenario == "logs" ? 1 : 0));
                    expanded.Clear();
                    if (scenario == "pull") expanded.Add(folder.Id);
                    selectedRecent.SetValue(window, scenario == "pull" ? folder.SegmentRecords[1].Id : sample.Id);
                    for (var frame = 0; frame < 5; frame++) Frame();

                    var errors = new List<string>();
                    for (var i = 0; i < context.Windows.Size; i++)
                    {
                        var native = context.Windows[i]; if (!native.Active) continue;
                        var name = Marshal.PtrToStringUTF8((nint)native.Name) ?? "";
                        if (!name.Contains("DalamudActCompatHistory", StringComparison.Ordinal)) continue;
                        var available = native.Size.X - native.WindowPadding.X * 2 - native.ScrollbarSizes.X;
                        if (native.ContentSize.X > available + 2)
                            errors.Add($"Horizontal overflow {name}: {native.ContentSize.X:0.0} > {available:0.0}");
                        // Summary cells are fixed-height children: a taller label and value
                        // would scroll inside the card instead of being visible.
                        if (name.Contains("/summary-", StringComparison.Ordinal) &&
                            native.ContentSize.Y > native.Size.Y - native.WindowPadding.Y * 2 + 2)
                            errors.Add($"Clipped summary {name}: {native.ContentSize.Y:0.0} > {native.Size.Y - native.WindowPadding.Y * 2:0.0}");
                    }
                    var key = $"{skin.Id}_{language.Id}_{width}_{scale * 100:0}_{scenario}";
                    var capture = Environment.GetEnvironmentVariable("DACT_ACCEPTANCE_NO_CAPTURE") != "1" &&
                        (skin.Id == SkinCatalog.Default || (language.Id == "zh-CN" && width == 980 && scale == 1) || errors.Count > 0);
                    if (capture) raster.Save(ImGui.GetDrawData(), Path.Combine(output, key + ".png"));
                    results.Add(new { key, skin = skin.Id, language = language.Id, width, fontPercent = scale * 100, scenario, errors, screenshot = capture ? key + ".png" : null });
                    failures.AddRange(errors.Select(error => key + ": " + error));
                }
                Console.WriteLine($"History acceptance {skin.Id}: {results.Count} cases, {failures.Count} layout violations.");
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "matrix.json"), JsonSerializer.Serialize(new { cases = results.Count, failures, results }, new JsonSerializerOptions { WriteIndented = true }));
            DactTheme.SetCurrent(new(), false, 0); ImGui.DestroyContext(context);
            SkinCatalog.UltimateUnlocked = previousUltimateUnlock;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Check(failures.Count == 0, $"History acceptance found {failures.Count} layout violations; see history/matrix.json.");
        Console.WriteLine($"PASS history acceptance: {results.Count} native skin/language/layout/page combinations.");
    }
}
