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

    public EncounterSnapshot Snapshot => stateStore.GetSnapshot();

    public IReadOnlyList<CombatantRow> GetRows()
    {
        var encounter = Snapshot.Current;
        if (encounter is null)
        {
            return Array.Empty<CombatantRow>();
        }

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
        var duration = Math.Max(1.0, encounter.Duration.TotalSeconds);
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
            combatant.Deaths));

        return MeterSortModeOptions.Normalize(settings.SortMode) switch
        {
            MeterSortMode.Hps => rows.OrderByDescending(static row => row.Hps).ToArray(),
            _ => rows.OrderByDescending(static row => row.Dps).ToArray(),
        };
    }

    private double ResolveDps(Combatant combatant, double encounterDuration)
        => settings.DpsMetric switch
        {
            DpsMetric.Dps when combatant.Dps > 0 => combatant.Dps,
            DpsMetric.ExtDps when combatant.ExtDps > 0 => combatant.ExtDps,
            DpsMetric.EncDps when combatant.EncDps > 0 => combatant.EncDps,
            _ => combatant.TotalDamage / encounterDuration,
        };
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
    int Deaths);
