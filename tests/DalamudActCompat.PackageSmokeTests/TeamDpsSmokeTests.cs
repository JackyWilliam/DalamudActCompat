using DalamudActCompat.Core.Models;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using Newtonsoft.Json;

internal static class TeamDpsSmokeTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run()
    {
        var start = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
        var encounter = new Encounter(Guid.NewGuid(), start, start.AddSeconds(120), "Fixture", "Target",
            [new("one", "Player one", "PLD", true, 60_000, 0, 0, Dps: 2_000),
             new("two", "Player two", "WHM", false, 120_000, 30_000, 0, Dps: 3_000),
             new("lb", "Limit Break", "", false, 30_000, 0, 0, Dps: 500)], [], [], [], [], [])
        { CombatDuration = TimeSpan.FromSeconds(60) };
        Check(MeterSlotPresentation.TeamDps(encounter) == 3_500,
            "Team DPS must divide full encounter damage, including LB, by one effective combat clock.");
        Check(MeterSlotPresentation.TeamDps(encounter with { CombatDuration = null }) == 1_750,
            "Team DPS did not fall back to elapsed fight duration.");
        Check(MeterSlotPresentation.TeamDps(encounter with { EndTime = null, CombatDuration = TimeSpan.FromSeconds(30) }) == 7_000,
            "Live team DPS ignored the parser's current combat duration.");
        Check(MeterSlotPresentation.TeamDps(encounter with { Id = Guid.NewGuid(), Combatants = [] }) == 0,
            "An empty new pull retained the previous team rate.");
        Check(MeterSlotPresentation.TeamDps(encounter with { EndTime = start, CombatDuration = null }) == 210_000 &&
              double.IsFinite(MeterSlotPresentation.TeamDps(encounter with { EndTime = start.AddSeconds(-1), CombatDuration = null })),
            "A just-started or clock-skewed pull produced an invalid rate.");
        Check(MeterSlotPresentation.TeamSummaryValue(MeterSlotMetric.TeamDps, encounter) == $"{3_500:N0}",
            "Team DPS was formatted as a damage total.");
        var slot = new MeterSlotDefinition { Metric = MeterSlotMetric.TeamDps, Visible = true };
        Check(MeterSlotDefaults.EditableMetrics.Contains(MeterSlotMetric.TeamDps) &&
              MeterSlotPresentation.HasTeamSummary([slot]) &&
              !MeterSlotPresentation.TryGetDpsMetric(MeterSlotMetric.TeamDps, out _),
            "Team DPS is missing from the editor, missing its summary, or became a player ranking metric.");
        slot.Visible = false;
        Check(!MeterSlotPresentation.HasTeamSummary([slot]), "A hidden team rate still reserves a footer.");

        // New numeric enum values must not reinterpret anyone's saved ExtDPS slot.
        Check(JsonConvert.DeserializeObject<MeterSlotDefinition>("{\"Metric\":18}")!.Metric == MeterSlotMetric.ExtDps,
            "Adding Team DPS changed a previous numeric metric.");
        var config = new PluginConfiguration();
        foreach (var profile in new[] { config.Meter.ClassicWindow, config.Meter.HorizontalWindow,
                     config.Meter.RoleSplitDamageWindow, config.Meter.RoleSplitHealerWindow })
            profile.Slots.Add(new() { Metric = MeterSlotMetric.TeamDps, Visible = true });
        var restored = JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(config))!;
        foreach (var profile in new[] { restored.Meter.ClassicWindow, restored.Meter.HorizontalWindow,
                     restored.Meter.RoleSplitDamageWindow, restored.Meter.RoleSplitHealerWindow })
        {
            profile.Normalize(MeterSlotDefaults.CreateClassic());
            Check(profile.Slots.Last().Metric == MeterSlotMetric.TeamDps && profile.Slots.Last().Clone().Visible,
                "A saved or cloned template lost its team DPS choice.");
        }
        Console.WriteLine("Team DPS: shared combat clock, LB, live/reset/zero-duration and per-template persistence passed.");
    }
}
