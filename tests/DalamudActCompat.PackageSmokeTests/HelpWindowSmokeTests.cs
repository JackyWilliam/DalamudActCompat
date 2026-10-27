using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static class HelpWindowSmokeTests
{
    private static readonly Dictionary<string, string[]> PageImages = new(StringComparer.Ordinal)
    {
        ["GettingStarted"] = ["account-login", "account-discord", "control-overview", "meter"],
        ["CombatMeter"] = ["control-meter"],
        ["Overlays"] = ["control-overlays"],
        ["Extensions"] = ["control-extensions"],
        ["Account"] = ["control-cloud", "control-diagnostics"],
    };

    internal static void Run()
    {
        ValidateWrapping();
        ValidateShippedImages();
        if (Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") is { Length: > 0 } library) Native(library);
        Console.WriteLine("Help: CJK-aware wrapping, shipped screenshots for six languages" +
            (Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") is { Length: > 0 } ? " and native page layout" : "") + " passed.");
    }

    private static void ValidateWrapping()
    {
        // One unit per character keeps the expectations readable.
        static float Measure(string value) => value.Length;
        var chinese = HelpWindow.WrapLines("在槽位列表中可分别开关 FFLogs、DPS 和死亡。", 10, Measure);
        Check(chinese.All(line => line.Length <= 10) && chinese[0] == "在槽位列表中可分别开关".Substring(0, 10),
            $"CJK text still breaks only at spaces: {string.Join(" | ", chinese)}");
        var punctuation = HelpWindow.WrapLines("一二三四五六七八九，十一二三「四五」六。", 9, Measure);
        Check(punctuation.All(line => line.Length == 0 || ("，。、）」".IndexOf(line[0]) < 0 && "（「".IndexOf(line[^1]) < 0)),
            $"Line-start or line-end punctuation rule failed: {string.Join(" | ", punctuation)}");
        var english = HelpWindow.WrapLines("Click Restart shared Host in runtime status.", 16, Measure);
        Check(english.SequenceEqual(["Click Restart", "shared Host in", "runtime status."]),
            $"Latin words were split or spacing changed: {string.Join(" | ", english)}");
        var longWord = HelpWindow.WrapLines("https://www.fflogs.com/api/clients", 12, Measure);
        Check(longWord.All(line => line.Length <= 12) && string.Concat(longWord) == "https://www.fflogs.com/api/clients",
            "A word wider than the column was not split losslessly.");
        Check(HelpWindow.WrapLines("第一段\n第二段", 100, Measure).SequenceEqual(["第一段", "第二段"]),
            "Explicit paragraph breaks were lost.");
    }

    private static void ValidateShippedImages()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Assets", "Help");
        foreach (var language in HelpScreenshotGenerator.Languages)
        foreach (var name in PageImages.Values.SelectMany(names => names))
        {
            var path = Path.Combine(root, language, name + ".png");
            Check(File.Exists(path), $"Help screenshot is missing from the package: {language}/{name}.png");
        }
    }

    private static unsafe void Native(string library)
    {
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true);
        NativeLibrary.Load(library);
        var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT");
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0x17f, 0x2000, 0x206f, 0x2190, 0x21ff, 0x2500, 0x27bf, 0x3000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            if (!io.Fonts.Build()) throw new InvalidOperationException("Help font failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            var config = new PluginConfiguration(); var text = new UiText(config);
            var logger = new PluginLogger(EntityServiceProxy.Create<IPluginLog>(new()));
            var textures = new Dictionary<string, HelpImage>(StringComparer.OrdinalIgnoreCase);
            HelpImage Load(string path)
            {
                if (textures.TryGetValue(path, out var existing)) return existing;
                var image = HelpImage.Load(path, 5000UL + (ulong)textures.Count, raster);
                textures[path] = image;
                return image;
            }

            var window = new HelpWindow(text, new SkinSmokeTests.EmptyTexture(),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Help"), path => Load(path), logger, () => { }, () => { }, () => { });
            var pageField = typeof(HelpWindow).GetField("selectedPage", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var heights = (Dictionary<string, float>)typeof(HelpWindow).GetField("cardHeights", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            foreach (var language in HelpScreenshotGenerator.Languages)
            foreach (var (width, height, scale, tag) in new[] { (980f, 720f, 1f, "100"), (980f, 720f, 1.4f, "140"), (820f, 580f, 1f, "min"), (980f, 9000f, 1.4f, "full140") })
            foreach (var page in Enum.GetValues(pageField.FieldType))
            {
                config.UiLanguage = language; io.FontGlobalScale = scale; io.DisplaySize = new(width + 40, height + 40);
                pageField.SetValue(window, page);
                Dictionary<string, float>? previous = null;
                for (var frame = 0; frame < 8; frame++)
                {
                    previous = new Dictionary<string, float>(heights);
                    ImGui.NewFrame();
                    using (DactTheme.PushFrame())
                    {
                        window.PreDraw();
                        ImGui.SetNextWindowPos(new(20, 20)); ImGui.SetNextWindowSize(new(width, height));
                        ImGui.Begin("help-test", window.Flags | ImGuiWindowFlags.NoSavedSettings);
                        window.Draw();
                        ImGui.End();
                        window.PostDraw();
                    }
                    ImGui.Render();
                }

                // Card heights must settle on their content, or text is clipped / space wasted.
                Check(previous!.Count == heights.Count && heights.All(pair => previous.TryGetValue(pair.Key, out var value) && value == pair.Value),
                    $"Help card heights did not converge: {language} {tag} {page}.");
                if (tag == "full140" && PageImages.TryGetValue(page.ToString()!, out var expected))
                {
                    var drawn = DrawnTextures(ImGui.GetDrawData());
                    foreach (var name in expected)
                    {
                        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Help", language, name + ".png");
                        Check(textures.TryGetValue(path, out var image) && drawn.Contains(image.Handle.Handle),
                            $"Help screenshot was not drawn: {language} {page} {name}.");
                    }
                }

                if (output is not null && tag != "full140")
                {
                    Directory.CreateDirectory(output);
                    raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"help-{language}-{tag}-{page}.png"));
                }
            }
        }
        finally
        {
            DactTheme.SetCurrent(new(), false, 0);
            ImGui.DestroyContext(context);
        }
    }

    private static unsafe HashSet<ulong> DrawnTextures(ImDrawDataPtr data)
    {
        var handles = new HashSet<ulong>();
        for (var n = 0; n < data.CmdListsCount; n++)
        {
            var list = new ImDrawListPtr(data.CmdLists[n]);
            for (var c = 0; c < list.CmdBuffer.Size; c++) handles.Add(list.CmdBuffer[c].TextureId.Handle);
        }

        return handles;
    }

    private sealed class HelpImage(ulong handle, int width, int height) : ISharedImmediateTexture, IDalamudTextureWrap
    {
        public ImTextureID Handle => new(handle);
        public int Width => width;
        public int Height => height;
        public Vector2 Size => new(Width, Height);
        public IDalamudTextureWrap GetWrapOrEmpty() => this;
        public IDalamudTextureWrap GetWrapOrDefault(IDalamudTextureWrap? fallback = null) => this;
        public bool TryGetWrap([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDalamudTextureWrap? wrap, out Exception? exception) { wrap = this; exception = null; return true; }
        public Task<IDalamudTextureWrap> RentAsync(CancellationToken ct = default) => Task.FromResult<IDalamudTextureWrap>(this);
        public IDalamudTextureWrap CreateWrapSharingLowLevelResource() => this;
        public void Dispose() { }

        internal static HelpImage Load(string path, ulong handle, NativeUiRasterizer raster)
        {
            using var bitmap = new System.Drawing.Bitmap(path);
            var pixels = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y); var offset = (y * bitmap.Width + x) * 4;
                pixels[offset] = color.R; pixels[offset + 1] = color.G; pixels[offset + 2] = color.B; pixels[offset + 3] = color.A;
            }

            var image = new HelpImage(handle, bitmap.Width, bitmap.Height);
            raster.AddTexture(image.Handle, pixels, bitmap.Width, bitmap.Height);
            return image;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
