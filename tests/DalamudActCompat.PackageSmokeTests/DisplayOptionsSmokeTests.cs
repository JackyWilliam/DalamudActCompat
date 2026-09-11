using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using Newtonsoft.Json;

internal static class DisplayOptionsSmokeTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        var row = new CombatantRow("player", "Player", "SAM", false, 0, 0, 0, 0, 0, null, null, null, 0,
            HighestDamageAction: "Midare", HighestDamage: 128_456);
        Check(MeterWindow.FormatHighestDamage(row) == "Midare 128.5k", "Legacy max-hit format changed.");
        Check(MeterWindow.FormatHighestDamage(row, false) == "Midare 128456", "Full max hit lost digits or retained separators.");
        Check(MeterSlotPresentation.Value(MeterSlotMetric.HighestDamage, row, row.Name, false) == "128456",
            "Separate horizontal amount ignored full-number format.");
        Check(MeterSlotPresentation.Value(MeterSlotMetric.HighestDamageAction, row, row.Name, false, true) == "Midare 128456" &&
              MeterSlotPresentation.Value(MeterSlotMetric.HighestDamageAction, row, row.Name, false) == "Midare",
            "Horizontal combined/split max-hit slots did not preserve their different layouts.");
        Check(MeterWindow.FormatHighestDamageAmount(1_234_567, false) == "1234567" &&
              MeterWindow.FormatHighestDamage(row with { HighestDamage = 0 }, false) == "--",
            "Full-number max hit mishandled large or unavailable values.");

        var config = new PluginConfiguration();
        var compact = JsonConvert.DeserializeObject<MeterSlotDefinition>("{\"Metric\":9}")!;
        Check(compact.UseCompactHighestDamage, "Old saved slots no longer default to compact.");
        config.Meter.ClassicWindow.Slots.Add(new() { Metric = MeterSlotMetric.HighestDamageAction, UseCompactHighestDamage = false });
        config.GetOverlayWindowSettings("one").AutoHideOutOfCombat = true;
        config.GetOverlayWindowSettings("two").OpenOnStartup = true;
        var restored = JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(config))!;
        var savedSlot = restored.Meter.ClassicWindow.Slots.Last();
        Check(!savedSlot.UseCompactHighestDamage && !savedSlot.Clone().UseCompactHighestDamage,
            "Save/reload or profile cloning lost full-number selection.");
        Check(restored.Meter.HorizontalWindow.Slots.All(slot => slot.UseCompactHighestDamage) &&
              restored.GetOverlayWindowSettings("one").AutoHideOutOfCombat &&
              !restored.GetOverlayWindowSettings("two").AutoHideOutOfCombat,
            "Per-window preferences leaked to another window.");
        Check(!JsonConvert.DeserializeObject<HtmlOverlayWindowSettings>("{}")!.AutoHideOutOfCombat,
            "Existing HTML overlays were hidden without opting in.");
        restored.GetOverlayWindowSettings("one").ResetRegistration();
        Check(!restored.GetOverlayWindowSettings("one").AutoHideOutOfCombat, "Reset retained removed overlay preferences.");

        // Exercise the real WinForms visibility queue without WebView startup, mouse
        // input or foreground activation. Native visibility is distinct from saved open state.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { ValidateOverlayVisibility(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("Display options: max-hit formats, saved per-window isolation and native HTML combat/edit/close visibility passed.");
    }

    private static void ValidateOverlayVisibility()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
        using var first = new OverlayFixture(true);
        using var second = new OverlayFixture(false);
        first.Overlay.Show(); second.Overlay.Show(); Pump();
        Check(!first.Form.Visible && second.Form.Visible && first.Settings.IsVisible,
            "Opening out of combat affected the wrong overlay or discarded its open state.");
        first.Overlay.SetCombatState(true); second.Overlay.SetCombatState(true); Pump();
        Check(first.Form.Visible && second.Form.Visible, "Entering combat did not restore the selected overlay.");
        first.Overlay.SetCombatState(false); second.Overlay.SetCombatState(false); Pump();
        Check(!first.Form.Visible && second.Form.Visible && first.Settings.OpenOnStartup,
            "Leaving combat changed startup settings or hid the unselected overlay.");
        first.Settings.SetEditing(true); first.Overlay.ApplySettings(); Pump();
        Check(first.Form.Visible, "An out-of-combat overlay cannot be edited.");
        first.Overlay.SetTemporarilyHidden(true); Pump();
        Check(!first.Form.Visible, "Editing bypassed existing foreground suppression.");
        first.Overlay.SetTemporarilyHidden(false); Pump();
        Check(first.Form.Visible, "Clearing foreground suppression failed to restore editing.");
        first.Settings.SetEditing(false); first.Overlay.ApplySettings(); Pump();
        Check(!first.Form.Visible, "Finishing editing did not restore combat hiding.");
        first.Settings.AutoHideOutOfCombat = false; first.Overlay.ApplySettings(); Pump();
        Check(first.Form.Visible, "Disabling combat hiding did not restore the open overlay.");
        first.Settings.AutoHideOutOfCombat = true; first.Overlay.ApplySettings();
        first.Overlay.SetCombatState(true); first.Overlay.SetCombatState(false); Pump();
        Check(!first.Form.Visible, "Queued combat changes replayed stale visibility.");
        first.Overlay.Hide(); first.Overlay.SetCombatState(true); Pump();
        Check(!first.Form.Visible && !first.Settings.IsVisible,
            "Entering combat reopened a manually closed overlay.");
    }

    private static void Pump() => Application.DoEvents();

    private sealed class PassiveForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    private sealed class OverlayFixture : IDisposable
    {
        public HtmlOverlayWindowSettings Settings { get; }
        public Form Form { get; } = new PassiveForm
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000),
            Size = new Size(140, 90),
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
        };
        public HtmlOverlayForm Overlay { get; }
        public OverlayFixture(bool autoHide)
        {
            Settings = new() { AutoHideOutOfCombat = autoHide, OpenOnStartup = true };
            Overlay = new(new Uri("about:blank"), "", "", "Combat visibility test", true, Settings, new Size(140, 90), false,
                DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>());
            _ = Form.Handle;
            Set("form", Form);
            Set("uiThread", Thread.CurrentThread);
        }
        private void Set(string name, object? value) => typeof(HtmlOverlayForm)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Overlay, value);
        public void Dispose()
        {
            Overlay.Hide(); Pump();
            Set("form", null); Set("uiThread", null);
            Form.Dispose(); Overlay.Dispose();
        }
    }
}
