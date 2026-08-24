using Dalamud.Bindings.ImGui;
using DalamudActCompat.Core.Models;
using DalamudActCompat.UI;

namespace DalamudActCompat.Meter;

internal static class MeterSlotPresentation
{
    public static string Label(MeterSlotMetric metric, UiText text)
        => metric switch
        {
            MeterSlotMetric.Rank => text.Get("排名", "Rank"),
            MeterSlotMetric.Job => text.Get("职能", "Job"),
            MeterSlotMetric.PlayerName => text.Get("玩家", "Player"),
            MeterSlotMetric.Dps => "DPS",
            MeterSlotMetric.Rdps => "rDPS",
            MeterSlotMetric.Hps => "HPS",
            MeterSlotMetric.DamagePercent => text.Get("伤害占比", "Damage %"),
            MeterSlotMetric.TotalDamage => text.Get("总伤害", "Damage"),
            MeterSlotMetric.TotalHealing => text.Get("总治疗", "Healing"),
            MeterSlotMetric.HighestDamageAction => text.Get("最高伤害技能", "Max hit skill"),
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
