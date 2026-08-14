using System.Globalization;

namespace DalamudActCompat.Fflogs;

internal sealed record CurrentFflogsEncounter(
    uint TerritoryId,
    int Phase,
    int EncounterId,
    string EncounterName,
    int Difficulty);

internal static class CurrentFflogsEncounterTable
{
    // FFLogs zone 73 is the currently supported AAC Heavyweight ranking tier.
    // Keeping the active tier in one table makes a tier rollover an explicit,
    // reviewable data change and prevents stale cached encounters from opening.
    internal const int ZoneId = 73;
    internal const string ZoneName = "AAC Heavyweight";
    // CN is one partition behind the global default for the current tier. Keep
    // these values beside the explicit duty table so a tier rollover updates
    // the ranking population and encounter mapping together.
    internal const string RankingRegion = "CN";
    internal const int RankingPartition = 9;
    internal const string RankingMetric = "dps";

    private static readonly IReadOnlyDictionary<uint, DutyEntry> Duties =
        new Dictionary<uint, DutyEntry>
        {
            [1320] = new(101, "Vamp Fatale", 100),
            [1321] = new(101, "Vamp Fatale", 101),
            [1322] = new(102, "Red Hot and Deep Blue", 100),
            [1323] = new(102, "Red Hot and Deep Blue", 101),
            [1324] = new(103, "The Tyrant", 100),
            [1325] = new(103, "The Tyrant", 101),
            [1326] = new(104, "Lindwurm", 100),
            [1327] = new(104, "Lindwurm", 101, 105, "Lindwurm II"),
        };

    private static readonly HashSet<int> EncounterIds =
        Duties.Values
            .SelectMany(static duty => duty.PhaseTwoEncounterId is int phaseTwoId
                ? new[] { duty.PhaseOneEncounterId, phaseTwoId }
                : new[] { duty.PhaseOneEncounterId })
            .ToHashSet();

    private static readonly HashSet<(int EncounterId, int Difficulty)> Rankings =
        Duties.Values
            .SelectMany(static duty => duty.PhaseTwoEncounterId is int phaseTwoId
                ? new[]
                {
                    (duty.PhaseOneEncounterId, duty.Difficulty),
                    (phaseTwoId, duty.Difficulty),
                }
                : new[] { (duty.PhaseOneEncounterId, duty.Difficulty) })
            .ToHashSet();

    private static readonly HashSet<uint> LindwurmPhaseTwoActions =
    [
        0xBBD8,
        0xBBD9,
        0xBBDA,
        0xBBDB,
        0xBBDC,
        0xBBDD,
        0xBBDE,
        0xBBDF,
        0xBBE1,
        0xBCF3,
    ];

    internal static bool IsSupportedEncounter(int encounterId)
        => EncounterIds.Contains(encounterId);

    internal static bool IsSupportedRanking(int encounterId, int difficulty)
        => Rankings.Contains((encounterId, difficulty));

    internal static bool TryResolve(uint territoryId, int phase, out CurrentFflogsEncounter encounter)
    {
        if (!Duties.TryGetValue(territoryId, out var duty))
        {
            encounter = null!;
            return false;
        }

        var usePhaseTwo = phase >= 2 && duty.PhaseTwoEncounterId is not null;
        encounter = new CurrentFflogsEncounter(
            territoryId,
            usePhaseTwo ? 2 : 1,
            usePhaseTwo ? duty.PhaseTwoEncounterId!.Value : duty.PhaseOneEncounterId,
            usePhaseTwo ? duty.PhaseTwoEncounterName! : duty.PhaseOneEncounterName,
            duty.Difficulty);
        return true;
    }

    internal static int ObservePhase(uint territoryId, int currentPhase, string actLine)
    {
        if (territoryId != 1327 || currentPhase >= 2 || string.IsNullOrWhiteSpace(actLine))
        {
            return currentPhase;
        }

        var fields = actLine.Split('|');
        if (fields.Length <= 4 || (fields[0] != "20" && fields[0] != "21"))
        {
            return currentPhase;
        }

        return uint.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var actionId) &&
               LindwurmPhaseTwoActions.Contains(actionId)
            ? 2
            : currentPhase;
    }

    private sealed record DutyEntry(
        int PhaseOneEncounterId,
        string PhaseOneEncounterName,
        int Difficulty,
        int? PhaseTwoEncounterId = null,
        string? PhaseTwoEncounterName = null);
}
