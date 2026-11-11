using System.Text.Json;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Meter;

// 「脱战隐藏」 with a delay: the shared rule HTML overlays and combat meters use, and the meter
// migrations that copy window settings, so a chosen delay is never dropped on the way.
internal static class CombatAutoHideSmokeTests
{
    public static void Run()
    {
        Policy();
        MeterSettingsKeepTheDelay();
        OverlaySettingsKeepTheDelay();
        Console.WriteLine("Combat auto-hide delay: hide-at-once default, 30 s grace, combat cancel, clamping and settings migration passed.");
    }

    private static void Policy()
    {
        const long now = 1_000_000;
        Assert(!CombatAutoHidePolicy.IsHidden(false, 0, false, null, now), "Auto-hide off still hid the window.");
        Assert(CombatAutoHidePolicy.IsHidden(true, 0, false, now - 1, now), "A 0-second delay did not hide at once (the old behaviour).");
        Assert(!CombatAutoHidePolicy.IsHidden(true, 0, true, null, now), "The window hid during combat.");
        Assert(CombatAutoHidePolicy.IsHidden(true, 30, false, null, now), "Without any combat yet, the window waited for a delay that never started.");
        Assert(!CombatAutoHidePolicy.IsHidden(true, 30, false, now - 10_000, now), "The window hid 10 s into a 30 s delay.");
        Assert(CombatAutoHidePolicy.IsHidden(true, 30, false, now - 30_000, now), "The window stayed up after its 30 s delay.");
        Assert(!CombatAutoHidePolicy.IsHidden(true, 30, true, now - 60_000, now), "Re-entering combat did not keep the window up.");
        Assert(CombatAutoHidePolicy.RemainingMilliseconds(60, now - 15_000, now) == 45_000, "The remaining delay is wrong.");
        Assert(CombatAutoHidePolicy.ClampDelay(-5) == 0 && CombatAutoHidePolicy.ClampDelay(99_999) == CombatAutoHidePolicy.MaximumDelaySeconds,
            "Delays outside 0..600 s were not clamped.");
        Assert(!CombatAutoHidePolicy.IsHidden(true, 99_999, false, now - 599_000, now) && CombatAutoHidePolicy.IsHidden(true, 99_999, false, now - 600_000, now),
            "An oversized saved delay was not limited to 600 s.");
    }

    private static void MeterSettingsKeepTheDelay()
    {
        // The single pre-1.0 window owned the setting; migration copies it into every window.
        var legacy = new MeterSettings { AutoHideOutOfCombat = true, AutoHideDelaySeconds = 30 };
        legacy.MigrateIndependentWindows();
        Assert(new[] { legacy.ClassicWindow, legacy.HorizontalWindow, legacy.RoleSplitWindow }
                   .All(profile => profile.AutoHideOutOfCombat && profile.AutoHideDelaySeconds == 30),
            "Migrating the single meter window dropped the auto-hide delay.");

        var split = new MeterSettings();
        split.RoleSplitWindow.AutoHideDelaySeconds = 45;
        split.MigrateIndependentRoleSplitWindows();
        Assert(split.RoleSplitDamageWindow.AutoHideDelaySeconds == 45 && split.RoleSplitHealerWindow.AutoHideDelaySeconds == 45,
            "Splitting the D/T and H windows dropped the auto-hide delay.");
        split.RoleSplitDamageWindow.AutoHideDelaySeconds = 60;
        split.SynchronizeLegacyRoleSplitWindow();
        Assert(split.RoleSplitWindow.AutoHideDelaySeconds == 60, "The legacy D/T mirror lost the auto-hide delay.");

        var saved = JsonSerializer.Deserialize<MeterWindowProfile>(JsonSerializer.Serialize(new MeterWindowProfile { AutoHideDelaySeconds = 30 }))!;
        var old = JsonSerializer.Deserialize<MeterWindowProfile>("""{"AutoHideOutOfCombat":true}""")!;
        Assert(saved.AutoHideDelaySeconds == 30 && old.AutoHideOutOfCombat && old.AutoHideDelaySeconds == 0,
            "A saved delay did not round-trip, or an older configuration did not default to hiding at once.");
    }

    private static void OverlaySettingsKeepTheDelay()
    {
        var settings = new HtmlOverlayWindowSettings { AutoHideOutOfCombat = true, AutoHideDelaySeconds = 30 };
        var saved = JsonSerializer.Deserialize<HtmlOverlayWindowSettings>(JsonSerializer.Serialize(settings))!;
        Assert(saved.AutoHideDelaySeconds == 30, "An overlay's auto-hide delay did not round-trip.");
        settings.ResetRegistration();
        Assert(settings.AutoHideDelaySeconds == 0 && !settings.AutoHideOutOfCombat, "Resetting an overlay kept its auto-hide delay.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
