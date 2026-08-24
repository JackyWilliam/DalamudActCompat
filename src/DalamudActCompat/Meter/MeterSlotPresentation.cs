using Dalamud.Bindings.ImGui;
using DalamudActCompat.Core.Models;
using DalamudActCompat.UI;
using System.Numerics;

namespace DalamudActCompat.Meter;

internal static class MeterSlotPresentation
{
    public const float TeamSummaryHeight = 34;

    public static string Label(MeterSlotMetric metric, UiText text)
        => metric switch
        {
            MeterSlotMetric.Rank => text.Get("排名", "Rank"),
            MeterSlotMetric.Job => text.Get("职能", "Job"),
            MeterSlotMetric.PlayerName => text.Get("玩家", "Player"),
            MeterSlotMetric.PlayerIdentity => text.Get("职业 / ID", "Job / ID"),
            MeterSlotMetric.Fflogs => "FFLogs",
            MeterSlotMetric.Dps => "DPS",
            MeterSlotMetric.Rdps => "rDPS",
            MeterSlotMetric.Hps => "HPS",
            MeterSlotMetric.DamagePercent => text.Get("伤害占比", "Damage %"),
            MeterSlotMetric.TotalDamage => text.Get("全队总伤害", "Team damage"),
            MeterSlotMetric.TotalHealing => text.Get("全队总治疗", "Team healing"),
            MeterSlotMetric.HighestDamageAction => text.Get("最高伤害", "Max hit"),
            MeterSlotMetric.HighestDamage => text.Get("最高伤害", "Max hit"),
            MeterSlotMetric.Deaths => text.Get("死亡", "Deaths"),
            MeterSlotMetric.CriticalHitPercent => text.Get("暴击率", "Critical %"),
            MeterSlotMetric.DirectHitPercent => text.Get("直击率", "Direct %"),
            MeterSlotMetric.CriticalDirectHitPercent => text.Get("直暴率", "Crit/direct %"),
            _ => metric.ToString(),
        };

    public static string Value(
        MeterSlotMetric metric,
        CombatantRow row,
        string displayName)
        => metric switch
        {
            MeterSlotMetric.Rank => row.Rank?.ToString() ?? "--",
            MeterSlotMetric.Job => JobDisplayFormatter.NormalizeJobCode(row.Job),
            MeterSlotMetric.PlayerName => displayName,
            MeterSlotMetric.PlayerIdentity => displayName,
            MeterSlotMetric.Fflogs => "--",
            MeterSlotMetric.Dps => $"{row.PersonalDps:N0}",
            MeterSlotMetric.Rdps => $"{row.Rdps:N0}",
            MeterSlotMetric.Hps => $"{row.Hps:N0}",
            MeterSlotMetric.DamagePercent => $"{row.DamagePercent:N1}%",
            MeterSlotMetric.TotalDamage => MeterWindow.FormatCompactNumber(row.TotalDamage),
            MeterSlotMetric.TotalHealing => MeterWindow.FormatCompactNumber(row.TotalHealing),
            MeterSlotMetric.HighestDamageAction => string.IsNullOrWhiteSpace(row.HighestDamageAction)
                ? "--"
                : row.HighestDamageAction,
            MeterSlotMetric.HighestDamage => MeterWindow.FormatCompactNumber(row.HighestDamage),
            MeterSlotMetric.Deaths => row.Deaths.ToString(),
            MeterSlotMetric.CriticalHitPercent => FormatPercent(row.CriticalHitPercent),
            MeterSlotMetric.DirectHitPercent => FormatPercent(row.DirectHitPercent),
            MeterSlotMetric.CriticalDirectHitPercent => FormatPercent(row.CriticalDirectHitPercent),
            _ => string.Empty,
        };

    public static string DisplayName(
        CombatantRow row,
        Encounter encounter,
        MeterSettings settings,
        UiText text)
    {
        var combatant = encounter.Combatants.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, row.Id, StringComparison.OrdinalIgnoreCase));
        return combatant is null
            ? row.Name
            : PlayerIdentityFormatter.Format(combatant, encounter.Combatants, settings, text);
    }

    public static IReadOnlyList<CombatantRow> SortAndRank(
        IEnumerable<CombatantRow> rows,
        MeterSortMode sortMode)
    {
        var ordered = MeterSortModeOptions.Normalize(sortMode) == MeterSortMode.Hps
            ? rows.OrderBy(static row => MeterService.IsLimitBreak(row.Id, row.Name))
                .ThenByDescending(static row => row.Hps)
            : rows.OrderBy(static row => MeterService.IsLimitBreak(row.Id, row.Name))
                .ThenByDescending(static row => row.PersonalDps);
        var rank = 0;
        return ordered.Select(row => row with
        {
            Rank = MeterService.NextPlayerRank(
                MeterService.IsLimitBreak(row.Id, row.Name),
                ref rank),
        }).ToArray();
    }

    public static bool ReplacePrimaryMetric(MeterWindowProfile profile, MeterSortMode mode)
    {
        var targetMetric = mode == MeterSortMode.Hps
            ? MeterSlotMetric.Hps
            : MeterSlotMetric.Dps;
        var source = profile.Slots.FirstOrDefault(slot =>
            slot.Visible &&
            (mode == MeterSortMode.Hps
                ? slot.Metric is MeterSlotMetric.Dps or MeterSlotMetric.Rdps
                : slot.Metric == MeterSlotMetric.Hps));
        var existingTarget = profile.Slots.FirstOrDefault(slot =>
            slot.Metric == targetMetric && !ReferenceEquals(slot, source));
        if (source is not null)
        {
            // Swap with an existing target instead of creating duplicates. This keeps
            // every user-defined position while the leading rate follows the ranking mode.
            var sourceMetric = source.Metric;
            source.Metric = targetMetric;
            source.Visible = true;
            if (existingTarget is not null)
            {
                existingTarget.Metric = sourceMetric;
                // The source now owns the active rate. Keeping the displaced target
                // visible would render a duplicate DPS/HPS value after a mode switch.
                existingTarget.Visible = false;
            }
            return true;
        }

        if (existingTarget is not null)
        {
            if (existingTarget.Visible)
            {
                return false;
            }
            existingTarget.Visible = true;
            return true;
        }

        profile.Slots.Add(new MeterSlotDefinition(
            targetMetric,
            0,
            0,
            4,
            2,
            MeterSlotAlignment.Left));
        return true;
    }

    public static bool HasTeamSummary(IEnumerable<MeterSlotDefinition> slots)
        => slots.Any(static slot =>
            slot.Visible &&
            slot.Metric is MeterSlotMetric.TotalDamage or MeterSlotMetric.TotalHealing);

    public static bool IsAlliance(Encounter encounter, IEnumerable<CombatantRow> rows)
        => encounter.PartyCapacity > 8 ||
           rows.Where(static row => row.PartyGroup > 0)
               .Select(static row => row.PartyGroup)
               .Distinct()
               .Skip(1)
               .Any();

    public static int ResolveLocalPartyGroup(IEnumerable<CombatantRow> rows)
    {
        var localGroup = rows.FirstOrDefault(static row => row.IsLocalPlayer)?.PartyGroup ?? 0;
        return localGroup is > 0 and <= 3
            ? localGroup
            : rows.Select(static row => row.PartyGroup)
                .FirstOrDefault(static group => group is > 0 and <= 3);
    }

    public static IReadOnlyList<CombatantRow> SelectParty(
        IEnumerable<CombatantRow> rows,
        int partyGroup,
        int maximumPlayers = 8)
    {
        var materialized = rows.Where(static row => !MeterService.IsLimitBreak(row.Id, row.Name))
            .ToArray();
        var selected = partyGroup is > 0 and <= 3
            ? materialized.Where(row => row.PartyGroup == partyGroup).ToArray()
            : materialized.Where(static row => row.PartyGroup == 0).ToArray();
        if (selected.Length == 0)
        {
            selected = materialized;
        }
        return selected.Take(Math.Max(1, maximumPlayers)).ToArray();
    }

    public static void DrawTeamSummary(
        string id,
        Encounter encounter,
        IEnumerable<MeterSlotDefinition> slots,
        UiText text,
        Vector4 labelColor,
        Vector4 valueColor)
    {
        var summaries = slots.Where(static slot =>
                slot.Visible &&
                slot.Metric is MeterSlotMetric.TotalDamage or MeterSlotMetric.TotalHealing)
            .GroupBy(static slot => slot.Metric)
            .Select(static group => group.First())
            .ToArray();
        if (summaries.Length == 0)
        {
            return;
        }

        var start = ImGui.GetCursorScreenPos();
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        ImGui.InvisibleButton($"team-summary-{id}", new Vector2(width, TeamSummaryHeight));
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddLine(
            start,
            start + new Vector2(width, 0),
            ImGui.GetColorU32(new Vector4(labelColor.X, labelColor.Y, labelColor.Z, 0.45f)));
        var cellWidth = width / summaries.Length;
        for (var index = 0; index < summaries.Length; index++)
        {
            var metric = summaries[index].Metric;
            var label = Label(metric, text);
            var value = metric == MeterSlotMetric.TotalHealing
                ? MeterWindow.FormatCompactNumber(encounter.TotalHealing)
                : MeterWindow.FormatCompactNumber(encounter.TotalDamage);
            var cellStart = start + new Vector2(index * cellWidth, 8);
            drawList.AddText(cellStart, ImGui.GetColorU32(labelColor), label);
            var valueSize = ImGui.CalcTextSize(value);
            drawList.AddText(
                new Vector2(cellStart.X + cellWidth - valueSize.X - 6, cellStart.Y),
                ImGui.GetColorU32(valueColor),
                value);
        }
    }

    public static string TrimToWidth(string value, float maximumWidth)
    {
        if (string.IsNullOrEmpty(value) || ImGui.CalcTextSize(value).X <= maximumWidth)
        {
            return value;
        }

        const string ellipsis = "…";
        var length = value.Length;
        while (length > 1 && ImGui.CalcTextSize(value[..length] + ellipsis).X > maximumWidth)
        {
            length--;
        }

        return value[..length] + ellipsis;
    }

    private static string FormatPercent(double? value)
        => value is null ? "--" : $"{value:N1}%";
}

internal readonly struct MeterFontScaleScope : IDisposable
{
    public MeterFontScaleScope(float scale)
    {
        ImGui.SetWindowFontScale(Math.Clamp(scale, 0.75f, 1.8f));
    }

    public void Dispose() => ImGui.SetWindowFontScale(1);
}
