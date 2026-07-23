using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;

namespace DalamudActCompat.Meter;

public sealed class MeterService
{
    private readonly EncounterStateStore stateStore;
    private readonly MeterSettings settings;

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

        var duration = Math.Max(1.0, encounter.Duration.TotalSeconds);
        var totalDamage = Math.Max(1, encounter.TotalDamage);
        var rows = encounter.Combatants.Select(combatant => new CombatantRow(
            combatant.Name,
            combatant.Job,
            combatant.IsLocalPlayer,
            combatant.TotalDamage / duration,
            combatant.TotalHealing / duration,
            combatant.TotalDamage,
            combatant.TotalHealing,
            combatant.TotalDamage * 100.0 / totalDamage,
            combatant.Deaths));

        return settings.SortMode switch
        {
            MeterSortMode.Hps => rows.OrderByDescending(static row => row.Hps).ToArray(),
            MeterSortMode.Damage => rows.OrderByDescending(static row => row.TotalDamage).ToArray(),
            MeterSortMode.Deaths => rows.OrderByDescending(static row => row.Deaths).ToArray(),
            _ => rows.OrderByDescending(static row => row.Dps).ToArray(),
        };
    }
}

public sealed record CombatantRow(
    string Name,
    string Job,
    bool IsLocalPlayer,
    double Dps,
    double Hps,
    long TotalDamage,
    long TotalHealing,
    double DamagePercent,
    int Deaths);
