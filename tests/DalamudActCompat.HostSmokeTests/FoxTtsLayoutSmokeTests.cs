using System.Drawing;
using System.Runtime.Loader;
using System.Windows.Forms;
using DalamudActCompat.Host;

internal static class FoxTtsLayoutSmokeTests
{
    public static void Run(string assemblyPath, string? screenshotDirectory = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Verify(assemblyPath, screenshotDirectory); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("FoxTTS layout regression.", failure);
    }

    private static void Verify(string assemblyPath, string? screenshots)
    {
        // Use the shipped controls without initializing audio/network workers or loading user settings.
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var localization = assembly.GetType("ACT.FoxTTS.localization.Localization", true)!;
        foreach (var language in new[] { "zh-CN", "en-US" })
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            using var form = new Form
            {
                ClientSize = new(944, 680), ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000),
            };
            using var root = (Control)Activator.CreateInstance(assembly.GetType("ACT.FoxTTS.FoxTTSTabControl", true)!)!;
            var panel = root.Controls.Find("panelTTSEngineSettings", true).Single();
            localization.GetMethod("ConfigLocalization")!.Invoke(null, [language]);
            root.GetType().GetMethod("DoLocalization")!.Invoke(root, null);
            root.Scale(new SizeF(scale, scale));
            root.Dock = DockStyle.Fill;
            form.Controls.Add(root);
            FoxTtsLayoutCompatibility.Apply(root);
            FoxTtsLayoutCompatibility.Apply(root); // Reloading a view must not wrap it again.
            form.Show();

            // This is how upstream replaces an engine's controls after initialization.
            foreach (var engineName in new[] { "cafepro.CafePro", "cafe.Cafe", "edge.Edge", "cafepro.CafePro" })
            {
                using var engine = (Control)Activator.CreateInstance(assembly.GetType($"ACT.FoxTTS.engine.{engineName}TTSSettingsControl", true)!)!;
                // Edge's full localization method also requires a live engine; translate
                // its controls directly to keep this UI test independent of voice services.
                var localizer = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("ACT.FoxCommon.localization.LocalizationBase")).First(t => t is not null)!;
                localizer.GetMethod("TranslateControls")!.Invoke(null, [engine]);
                engine.Scale(new SizeF(scale, scale));
                engine.Dock = DockStyle.Top;
                panel.Controls.Add(engine);
                var sliders = Descendants(root).OfType<TrackBar>().ToArray();
                var values = sliders.Select(s => s.Value).ToArray();
                foreach (var width in new[] { 944, 700, 1400, 700, 944 })
                {
                    form.ClientSize = new(width, 680);
                    Application.DoEvents();
                    var page = root.Controls.Find("tabPageGeneralSettings", true).OfType<TabPage>().Single();
                    Assert(!page.HorizontalScroll.Visible, $"{language}/{scale}/{engineName}/{width}: horizontal scrolling required.");
                    foreach (var slider in sliders)
                    {
                        page.ScrollControlIntoView(slider);
                        Application.DoEvents();
                        var bounds = page.RectangleToClient(slider.RectangleToScreen(slider.ClientRectangle));
                        Assert(bounds.Left >= 0 && bounds.Right <= page.ClientSize.Width && slider.Width >= 100,
                            $"{language}/{scale}/{engineName}/{width}: {slider.Name} is clipped or unusable: {bounds}.");
                    }
                    Assert(values.SequenceEqual(sliders.Select(s => s.Value)), "Layout changed a TTS setting.");
                    if (screenshots is not null && language == "zh-CN" && scale == 1.5f && width == 700 && engineName == "cafepro.CafePro")
                    {
                        Directory.CreateDirectory(screenshots);
                        page.AutoScrollPosition = Point.Empty;
                        Application.DoEvents();
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(screenshots, "foxtts-150-percent-700px.png"));
                    }
                }
                panel.Controls.Remove(engine);
            }
        }
        Console.WriteLine("FoxTTS actual controls: 2 languages, 4 scale factors, engine replacement, resize/scroll, and slider values passed.");
    }

    private static IEnumerable<Control> Descendants(Control root)
        => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
