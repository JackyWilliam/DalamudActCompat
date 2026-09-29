using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Newtonsoft.Json;

internal static partial class SkinSmokeTests
{
    private static void MeterCollapseConfiguration()
    {
        var config = JsonConvert.DeserializeObject<PluginConfiguration>("""{"Version":17,"Meter":{}}""")!;
        config.ApplyMigrations();
        Check(Profiles(config).All(profile => profile.CollapseDirection == MeterCollapseDirection.Upward),
            "Old configuration changed its default collapse direction.");
        config.Meter.ClassicWindow.CollapseDirection = MeterCollapseDirection.Downward;
        config.Meter.RoleSplitHealerWindow.CollapseDirection = MeterCollapseDirection.Downward;
        var restored = JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(config))!;
        restored.ApplyMigrations();
        var snapshot = new PluginConfiguration(); snapshot.RestoreFrom(restored.CreateSnapshot()); snapshot.ApplyMigrations();
        Check(snapshot.Meter.ClassicWindow.CollapseDirection == MeterCollapseDirection.Downward &&
            snapshot.Meter.RoleSplitDamageWindow.CollapseDirection == MeterCollapseDirection.Upward &&
            snapshot.Meter.RoleSplitHealerWindow.CollapseDirection == MeterCollapseDirection.Downward,
            "Reload or cloud snapshot lost independent collapse directions.");
        snapshot.Meter.ClassicWindow.CollapseDirection = (MeterCollapseDirection)99;
        snapshot.ApplyMigrations();
        Check(snapshot.Meter.ClassicWindow.CollapseDirection == MeterCollapseDirection.Upward && !snapshot.ApplyMigrations(),
            "Invalid collapse direction was not normalized to the compatible default.");
        Console.WriteLine("Meter collapse configuration: old default, independent profiles, reload, snapshot and invalid value passed.");

        static MeterWindowProfile[] Profiles(PluginConfiguration configuration) =>
            [configuration.Meter.ClassicWindow, configuration.Meter.HorizontalWindow, configuration.Meter.RoleSplitWindow,
             configuration.Meter.RoleSplitDamageWindow, configuration.Meter.RoleSplitHealerWindow];
    }

    private static unsafe void MeterCollapseNative(NativeUiRasterizer raster, string? output, EmptyTexture logo)
    {
        var io = ImGui.GetIO(); io.FontGlobalScale = 1; io.DisplaySize = new(1200, 900); io.DeltaTime = 1f / 60;
        DactTheme.SetCurrent(new(), false, 0);
        var cases = 0;
        foreach (var direction in Enum.GetValues<MeterCollapseDirection>())
        foreach (var kind in new[] { "classic", "alliance", "damage-tank", "healer" })
        foreach (var empty in new[] { false, true })
        foreach (var scale in new[] { .65f, 1f, 2f })
        {
            var config = new PluginConfiguration(); config.ApplyMigrations(); config.Fflogs.Enabled = false;
            config.Meter.ClassicAllianceView = kind == "alliance";
            var text = new UiText(config); var store = new EncounterStateStore();
            if (!empty)
            {
                var encounter = (Encounter)typeof(MeterStyleEditorWindow).GetMethod("CreatePreviewEncounter", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [text])!;
                store.UpdateCurrent(encounter with { EndTime = encounter.StartTime.AddMinutes(3) });
            }
            var service = new MeterService(store, config.Meter);
            var icons = new JobIconTextureSet(null!, Path.Combine(Path.GetTempPath(), "dact-missing-preview-icons"));
            var classic = new MeterWindow(service, null!, config, text, icons, logo, logo, logo, (_, name) => name, () => { });
            var isClassic = kind is "classic" or "alliance";
            Window window = isClassic ? classic : new RoleSplitMeterWindow(service, config, text, classic, () => { },
                kind == "healer" ? RoleSplitGroup.Healer : RoleSplitGroup.DamageTank);
            var profile = isClassic ? config.Meter.ClassicWindow : kind == "healer" ? config.Meter.RoleSplitHealerWindow : config.Meter.RoleSplitDamageWindow;
            profile.CollapseDirection = direction; profile.FontScale = scale;
            var position = new Vector2(80, 400); var size = new Vector2(1040, 420); var toggle = Vector2.Zero;
            var label = $"{kind}/{direction}/empty={empty}/scale={scale}";
            io.AddMouseButtonEvent(0, false); io.AddMousePosEvent(-100, -100);
            Frame(new(80, 400), new(1040, 420)); Frame(); Frame();
            for (var cycle = 0; cycle < 3; cycle++)
            {
                Toggle(); Check(IsCompact(), $"{label}: actual collapse button did not respond.");
                Settle(direction == MeterCollapseDirection.Downward ? 820 : 400);
                Check(size.Y < 420, $"{label}: compact height did not shrink.");
                if (cycle == 0 && scale == 1 && !empty && direction == MeterCollapseDirection.Downward && output is not null)
                    raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"meter-collapse-down-{kind}.png"));
                Toggle(); Check(!IsCompact(), $"{label}: actual expand button did not respond.");
                Settle(direction == MeterCollapseDirection.Downward ? 820 : 400);
                Check(position == new Vector2(80, 400) && size == new Vector2(1040, 420),
                    $"{label}: repeated toggle drifted to {position}/{size}.");
            }
            if (direction == MeterCollapseDirection.Downward)
            {
                Toggle(); Settle(820);
                var grab = position + new Vector2(80, 25);
                io.AddMousePosEvent(grab.X, grab.Y); Frame();
                io.AddMouseButtonEvent(0, true); Frame();
                io.AddMousePosEvent(grab.X - 20, grab.Y - 60); Frame(); Frame();
                io.AddMouseButtonEvent(0, false); Frame();
                Check(position.X == 60 && position.Y + size.Y == 760, $"{label}: compact header drag lost its anchor.");
                Toggle(); Settle(760);
                Check(position == new Vector2(60, 340) && size.Y == 420, $"{label}: expansion forgot the dragged position.");
                Toggle(); Settle(760);
                // Moving a compact window must establish a new bottom edge, and a
                // later expansion must keep the header inside the viewport.
                Frame(new(80, 10)); Frame();
                Toggle();
                for (var frame = 0; frame < 24; frame++) Frame();
                Check(position.Y == 0 && size.Y == 420, $"{label}: expansion escaped the top boundary: {position}/{size}.");
                Toggle(); Frame();
                if (isClassic) classic.LocateOnNextDraw(); else ((RoleSplitMeterWindow)window).LocateOnNextDraw();
                Frame();
                Check(position.Y > 0, $"{label}: a pending anchor overrode Locate.");
                for (var frame = 0; frame < 24; frame++) Frame();
                // Locate intentionally centers H to the right. Bring this unusually
                // wide fixture fully onscreen before testing its next mouse click.
                Frame(new(80, position.Y)); Frame();
            }
            else
            {
                Toggle(); Settle(400);
            }
            var fixedEdge = direction == MeterCollapseDirection.Downward ? position.Y + size.Y : position.Y;
            profile.IsLocked = true; profile.ShowHeader = false; Settle(fixedEdge);
            profile.ShowHeader = true; Settle(fixedEdge);
            Toggle(); Check(!IsCompact(), $"{label}: locked window's expand button did not respond.");
            Settle(fixedEdge);
            cases++;

            bool IsCompact() => isClassic ? config.Meter.CompactMode : kind == "healer" ? config.Meter.RoleSplitHealerCompact : config.Meter.RoleSplitDamageCompact;
            void Toggle()
            {
                io.AddMousePosEvent(toggle.X, toggle.Y); Frame();
                io.AddMouseButtonEvent(0, true); Frame();
                io.AddMouseButtonEvent(0, false); Frame();
                io.AddMousePosEvent(-100, -100);
            }
            void Settle(float edge)
            {
                for (var frame = 0; frame < 24; frame++)
                {
                    Frame();
                    var actual = direction == MeterCollapseDirection.Downward ? position.Y + size.Y : position.Y;
                    Check(Math.Abs(actual - edge) < .1f, $"{label}: animation moved its fixed edge from {edge} to {actual}.");
                }
            }
            void Frame(Vector2? move = null, Vector2? resize = null)
            {
                ImGui.NewFrame(); window.PreDraw();
                if (move is { } nextPosition) ImGui.SetNextWindowPos(nextPosition);
                if (resize is { } nextSize) ImGui.SetNextWindowSize(nextSize);
                var constraints = window.SizeConstraints!.Value;
                ImGui.SetNextWindowSizeConstraints(constraints.MinimumSize, constraints.MaximumSize);
                ImGui.Begin(window.WindowName, window.Flags | ImGuiWindowFlags.NoSavedSettings);
                position = ImGui.GetWindowPos(); size = ImGui.GetWindowSize();
                var cursor = ImGui.GetCursorScreenPos(); var available = ImGui.GetContentRegionAvail().X;
                toggle = cursor + (isClassic ? new Vector2(available - 19, empty ? 21 : 22) : new Vector2(available - 17, 17));
                window.Draw(); ImGui.End(); window.PostDraw(); ImGui.Render();
            }
        }
        Console.WriteLine($"Meter collapse native: {cases} cases, actual buttons, repeated cycles, every-frame anchors, font scales, empty/hidden headers, lock, drag, top boundary and Locate passed.");
    }
}
