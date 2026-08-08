using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;

namespace DalamudActCompat.Meter;

public sealed class MeterService
{
    private readonly EncounterStateStore stateStore;
    private readonly MeterSettings settings;
    private readonly object cacheLock = new();
    private IReadOnlyList<CombatantRow> cachedRows = Array.Empty<CombatantRow>();
    private Guid cachedEncounterId;
    private DateTimeOffset nextRefresh;

    public MeterService(EncounterStateStore stateStore, MeterSettings settings)
    {
        this.stateStore = stateStore;
        this.settings = settings;
    }

    public Encounter? DisplayEncounter => stateStore.GetDisplayEncounter();

    public IReadOnlyList<CombatantRow> GetRows()
    {
        var encounter = DisplayEncounter;
        if (encounter is null)
        {
            return Array.Empty<CombatantRow>();
        }

        return GetRows(encounter);
    }

    public IReadOnlyList<CombatantRow> GetRows(Encounter encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        lock (cacheLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (cachedEncounterId == encounter.Id && now < nextRefresh)
            {
                return cachedRows;
            }

            cachedEncounterId = encounter.Id;
            nextRefresh = now.AddMilliseconds(Math.Clamp(settings.RefreshIntervalMs, 250, 2000));
            cachedRows = BuildRows(encounter);
            return cachedRows;
        }
    }

    private IReadOnlyList<CombatantRow> BuildRows(Encounter encounter)
    {
        var duration = Math.Max(1.0, encounter.EffectiveDuration.TotalSeconds);
        var totalDamage = Math.Max(1, encounter.TotalDamage);
        var rows = encounter.Combatants.Select(combatant => new CombatantRow(
            combatant.Id,
            combatant.Name,
            combatant.Job,
            combatant.IsLocalPlayer,
            ResolveDps(combatant, duration),
            combatant.TotalHealing / duration,
            combatant.TotalDamage,
            combatant.TotalHealing,
            combatant.TotalDamage * 100.0 / totalDamage,
            CalculateHitRate(combatant.CriticalHits, combatant.DamageHits),
            CalculateHitRate(combatant.CriticalDirectHits, combatant.DamageHits),
            combatant.Deaths));

        var ordered = MeterSortModeOptions.Normalize(settings.SortMode) switch
        {
            MeterSortMode.Hps => rows
                .OrderBy(static row => IsLimitBreak(row.Id, row.Name))
                .ThenByDescending(static row => row.Hps),
            _ => rows
                .OrderBy(static row => IsLimitBreak(row.Id, row.Name))
                .ThenByDescending(static row => row.Dps),
        };

        var playerRank = 0;
        var ranked = new List<CombatantRow>();
        foreach (var row in ordered)
        {
            ranked.Add(row with
            {
                Rank = NextPlayerRank(IsLimitBreak(row.Id, row.Name), ref playerRank),
            });
        }

        return ranked;
    }

    internal static bool IsLimitBreak(Combatant combatant)
        => IsLimitBreak(combatant.Id, combatant.Name);

    internal static bool IsLimitBreak(string id, string name)
        => string.Equals(id, "Limit Break", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "Limit Break", StringComparison.OrdinalIgnoreCase);

    internal static int? NextPlayerRank(bool isLimitBreak, ref int playerRank)
        => isLimitBreak ? null : ++playerRank;

    private double ResolveDps(Combatant combatant, double encounterDuration)
        => settings.DpsMetric switch
        {
            DpsMetric.Dps when combatant.Dps > 0 => combatant.Dps,
            DpsMetric.ExtDps when combatant.ExtDps > 0 => combatant.ExtDps,
            DpsMetric.EncDps when combatant.EncDps > 0 => combatant.EncDps,
            _ => combatant.TotalDamage / encounterDuration,
        };

    internal static double? CalculateHitRate(int matchingHits, int damageHits)
        => damageHits > 0
            ? Math.Clamp(matchingHits, 0, damageHits) * 100.0 / damageHits
            : null;
}

public sealed record CombatantRow(
    string Id,
    string Name,
    string Job,
    bool IsLocalPlayer,
    double Dps,
    double Hps,
    long TotalDamage,
    long TotalHealing,
    double DamagePercent,
    double? CriticalHitPercent,
    double? CriticalDirectHitPercent,
    int Deaths,
    int? Rank = null);
