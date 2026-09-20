using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.Meter;

public sealed class MeterService
{
    private readonly EncounterStateStore stateStore;
    private readonly Func<MeterSettings> getSettings;
    private MeterSettings Settings => getSettings();
    private readonly Func<ParserScope> parserScope;
    private Encounter? projectedSource;
    private Encounter? projectedEncounter;
    private MeterDisplayScope projectedMode;
    private ParserScope projectedScope;
    private readonly object cacheLock = new();
    private readonly Dictionary<string, HitRateSnapshot> lastKnownHitRates =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CombatantRow> cachedRows = Array.Empty<CombatantRow>();
    private Guid cachedEncounterId;
    private DateTimeOffset nextRefresh;

    public MeterService(EncounterStateStore stateStore, MeterSettings settings, Func<ParserScope>? parserScope = null)
        : this(stateStore, () => settings, parserScope)
    {
    }

    public MeterService(EncounterStateStore stateStore, Func<MeterSettings> getSettings, Func<ParserScope>? parserScope = null)
    {
        this.stateStore = stateStore;
        // Cloud restore and factory reset replace MeterSettings. Resolve the live
        // instance so the display policy and the window controls cannot diverge.
        this.getSettings = getSettings;
        this.parserScope = parserScope ?? (() => ParserScope.All);
    }

    public Encounter? DisplayEncounter => Project(stateStore.GetDisplayEncounter(Settings.DisplayScope == MeterDisplayScope.ParserScope));

    private Encounter? Project(Encounter? encounter)
    {
        lock (cacheLock)
        {
            var settings = Settings;
            var selected = parserScope();
            if (selected == ParserScope.Auto) selected = encounter?.ParserContext?.AutoScope ?? ParserScope.All;
            if (!Enum.IsDefined(selected)) selected = ParserScope.All;
            if (settings.DisplayScope != projectedMode || selected != projectedScope)
                nextRefresh = DateTimeOffset.MinValue;
            if (ReferenceEquals(encounter, projectedSource) && settings.DisplayScope == projectedMode && selected == projectedScope)
                return projectedEncounter;
            projectedSource = encounter; projectedMode = settings.DisplayScope; projectedScope = selected;
            if (encounter is null || projectedMode != MeterDisplayScope.ParserScope)
                return projectedEncounter = encounter;

            var players = SelectParsedPlayers(encounter, selected);
            // Project the encounter as well as rows, so totals and damage shares
            // use the same scope. The stored fight remains intact when switching back.
            return projectedEncounter = encounter with
            {
                Combatants = players,
                JobSummaries = players.Where(player => !string.IsNullOrWhiteSpace(player.Job))
                    .GroupBy(player => player.Job, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new JobSummary(group.Key, group.Sum(player => player.TotalDamage),
                        group.Sum(player => player.TotalHealing), group.Count())).ToArray(),
            };
        }
    }

    internal static IReadOnlyList<Combatant> SelectParsedPlayers(Encounter encounter, ParserScope scope)
    {
        var players = encounter.ParsedPlayers ?? encounter.Combatants;
        var context = encounter.ParserContext;
        if (scope == ParserScope.Auto) scope = context?.AutoScope ?? ParserScope.All;
        if (scope == ParserScope.All) return players;
        var partyIds = (context?.PartyMemberIds ?? encounter.Combatants.Select(player => player.Id).ToArray())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localGroup = context?.LocalPartyGroup ?? encounter.Combatants.FirstOrDefault(player => player.IsLocalPlayer)?.PartyGroup ?? 0;
        return players.Where(player => player.IsLocalPlayer || (scope != ParserScope.Self &&
            (IsLimitBreak(player) || partyIds.Contains(player.Id) &&
                (scope == ParserScope.Alliance || player.PartyGroup == localGroup)))).ToArray();
    }

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
            if (cachedEncounterId != encounter.Id)
            {
                // Never carry a player's final percentages into the next pull.
                lastKnownHitRates.Clear();
            }
            if (cachedEncounterId == encounter.Id && now < nextRefresh)
            {
                return cachedRows;
            }

            cachedEncounterId = encounter.Id;
            nextRefresh = now.AddMilliseconds(Math.Clamp(Settings.RefreshIntervalMs, 250, 2000));
            cachedRows = BuildRows(encounter);
            return cachedRows;
        }
    }

    private IReadOnlyList<CombatantRow> BuildRows(Encounter encounter)
    {
        var damageDuration = Math.Max(1.0, encounter.EffectiveDuration.TotalSeconds);
        var healingDuration = Math.Max(1.0, encounter.Duration.TotalSeconds);
        var totalDamage = Math.Max(1, encounter.TotalDamage);
        var rows = encounter.Combatants.Select(combatant =>
        {
            var hitRates = ResolveHitRates(combatant);
            return new CombatantRow(
                combatant.Id,
                combatant.Name,
                combatant.Job,
                combatant.IsLocalPlayer,
                ResolveDps(combatant, damageDuration),
                combatant.TotalHealing / healingDuration,
                combatant.TotalDamage,
                combatant.TotalHealing,
                combatant.TotalDamage * 100.0 / totalDamage,
                hitRates.CriticalHitPercent,
                hitRates.DirectHitPercent,
                hitRates.CriticalDirectHitPercent,
                combatant.Deaths,
                HighestDamageAction: combatant.HighestDamageAction,
                HighestDamage: combatant.HighestDamage,
                PartyGroup: combatant.PartyGroup,
                PersonalDps: ResolvePersonalDps(combatant, damageDuration),
                Rdps: ResolveRdps(combatant, damageDuration),
                EncDps: ResolveEncounterDps(combatant, healingDuration),
                ExtDps: ResolveExternalDps(combatant, healingDuration));
        });

        var ordered = MeterSortModeOptions.Normalize(Settings.SortMode) switch
        {
            MeterSortMode.Hps => rows
                .OrderBy(static row => IsLimitBreak(row.Id, row.Name))
                .ThenByDescending(static row => row.Hps),
            _ => rows
                .OrderBy(static row => IsLimitBreak(row.Id, row.Name))
                .ThenByDescending(static row => row.PersonalDps),
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
        => Settings.DpsMetric switch
        {
            DpsMetric.Rdps when combatant.Rdps > 0 => combatant.Rdps,
            DpsMetric.Dps when combatant.Dps > 0 => combatant.Dps,
            DpsMetric.ExtDps when combatant.ExtDps > 0 => combatant.ExtDps,
            DpsMetric.EncDps when combatant.EncDps > 0 => combatant.EncDps,
            _ => combatant.TotalDamage / encounterDuration,
        };

    private static double ResolvePersonalDps(Combatant combatant, double encounterDuration)
        => combatant.Dps > 0
            ? combatant.Dps
            : combatant.TotalDamage / encounterDuration;

    private static double ResolveRdps(Combatant combatant, double encounterDuration)
        => combatant.Rdps > 0
            ? combatant.Rdps
            : ResolvePersonalDps(combatant, encounterDuration);

    private static double ResolveEncounterDps(Combatant combatant, double encounterDuration)
        => combatant.EncDps > 0
            ? combatant.EncDps
            : combatant.TotalDamage / encounterDuration;

    private static double ResolveExternalDps(Combatant combatant, double encounterDuration)
        => combatant.ExtDps > 0
            ? combatant.ExtDps
            : ResolveEncounterDps(combatant, encounterDuration);

    internal static double? CalculateHitRate(int matchingHits, int damageHits)
        => damageHits > 0
            ? Math.Clamp(matchingHits, 0, damageHits) * 100.0 / damageHits
            : null;

    private HitRateSnapshot ResolveHitRates(Combatant combatant)
    {
        var key = string.IsNullOrWhiteSpace(combatant.Id)
            ? combatant.Name
            : combatant.Id;
        var current = new HitRateSnapshot(
            CalculateHitRate(combatant.CriticalHits, combatant.DamageHits),
            CalculateHitRate(combatant.DirectHits, combatant.DamageHits),
            CalculateHitRate(combatant.CriticalDirectHits, combatant.DamageHits));
        if (current.CriticalHitPercent is not null)
        {
            lastKnownHitRates[key] = current;
            return current;
        }

        // ACT can briefly publish a zero-hit snapshot while rebuilding live totals.
        // Keep the last valid display value so a refresh cannot insert "--" between numbers.
        return lastKnownHitRates.GetValueOrDefault(key, current);
    }

    private readonly record struct HitRateSnapshot(
        double? CriticalHitPercent,
        double? DirectHitPercent,
        double? CriticalDirectHitPercent);
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
    double? DirectHitPercent,
    double? CriticalDirectHitPercent,
    int Deaths,
    int? Rank = null,
    string HighestDamageAction = "",
    long HighestDamage = 0,
    int PartyGroup = 0,
    double PersonalDps = 0,
    double Rdps = 0,
    double EncDps = 0,
    double ExtDps = 0);
