namespace DalamudActCompat.FflogsParityHarness;

internal enum OffensiveBuffDimension
{
    PercentageDamage,
    CriticalRate,
    DirectHitRate,
    CriticalAndDirectHitRate,
}

internal sealed record OffensiveBuffDefinition(
    string ProviderJob,
    IReadOnlyList<long> ActionIds,
    string ActionName,
    long StatusId,
    OffensiveBuffDimension Dimension,
    double CriticalRateIncrease,
    double DirectHitRateIncrease,
    double? DamageMultiplier,
    string Magnitude,
    string Scope,
    string Targeting,
    bool PartyWide,
    bool SingleTarget,
    bool DebuffOnEnemy,
    bool SelfAlsoAffected,
    string OfficialSource,
    string GameVersion,
    bool CoveredByProduction,
    string IdProvenance,
    string AnalysisNote = "");

internal static class OffensiveBuffRegistry
{
    public const string GameVersion = "FFXIV Patch 7.5 / FFLogs partition 9";

    public static IReadOnlyList<OffensiveBuffDefinition> All { get; } =
    [
        Rate("DRG", [3557], "Battle Litany", 786, OffensiveBuffDimension.CriticalRate,
            0.10, 0, "10% Crit", "self and nearby party", "30y party aura", true, false, false, true,
            "https://na.finalfantasyxiv.com/jobguide/dragoon/", true),
        Rate("SCH", [7436], "Chain Stratagem", 1221, OffensiveBuffDimension.CriticalRate,
            0.10, 0, "10% Crit", "attacks against one enemy", "enemy debuff", false, true, true, true,
            "https://na.finalfantasyxiv.com/jobguide/scholar/", true,
            "SelfAlsoAffected means the SCH's own attacks against the debuffed enemy also receive the game effect."),
        Rate("DNC", [16011], "Devilment", 1825, OffensiveBuffDimension.CriticalAndDirectHitRate,
            0.20, 0.20, "20% Crit + 20% DH", "self and Dance Partner", "self plus one designated partner",
            false, true, false, true, "https://na.finalfantasyxiv.com/jobguide/dancer/", true),
        Rate("BRD", [118], "Battle Voice", 141, OffensiveBuffDimension.DirectHitRate,
            0, 0.20, "20% DH", "self and nearby party", "30y party aura", true, false, false, true,
            "https://na.finalfantasyxiv.com/jobguide/bard/", true),
        Rate("BRD", [3559], "The Wanderer's Minuet", 2216, OffensiveBuffDimension.CriticalRate,
            0.02, 0, "2% Crit", "self and party", "50y song aura", true, false, false, true,
            "https://na.finalfantasyxiv.com/jobguide/bard/", false,
            "Current production rate metadata does not include the Patch 7.5 song status."),
        Rate("BRD", [116], "Army's Paeon", 2218, OffensiveBuffDimension.DirectHitRate,
            0, 0.03, "3% DH", "self and party", "50y song aura", true, false, false, true,
            "https://na.finalfantasyxiv.com/jobguide/bard/", false,
            "Current production rate metadata does not include the Patch 7.5 song status."),

        Percentage("AST", [16552], "Divination", 1878, 1.06, "6%", "self and nearby party",
            "party aura", true, false, "https://na.finalfantasyxiv.com/jobguide/astrologian/", true),
        Percentage("AST", [37023], "The Balance", 3887, 1.06, "6%", "one melee or tank party member",
            "single target", false, true, "https://na.finalfantasyxiv.com/jobguide/astrologian/", true),
        Percentage("AST", [37026], "The Spear", 3889, 1.06, "6%", "one ranged or healer party member",
            "single target", false, true, "https://na.finalfantasyxiv.com/jobguide/astrologian/", true),
        Percentage("MNK", [7396], "Brotherhood", 1185, 1.05, "5%", "self and nearby party",
            "party aura", true, false, "https://na.finalfantasyxiv.com/jobguide/monk/", true),
        Percentage("SMN", [25801], "Searing Light", 2703, 1.05, "5%", "self and nearby party",
            "party aura", true, false, "https://na.finalfantasyxiv.com/jobguide/summoner/", true),
        Percentage("RPR", [24405], "Arcane Circle", 2599, 1.03, "3%", "self and nearby party",
            "party aura", true, false, "https://na.finalfantasyxiv.com/jobguide/reaper/", true),
        Percentage("RDM", [7520], "Embolden", 1297, 1.05, "5%", "self and nearby party",
            "party aura", true, false, "https://na.finalfantasyxiv.com/jobguide/redmage/", true),
        Percentage("PCT", [34675], "Starry Muse", 3685, 1.05, "5%", "self and nearby party",
            "party aura", true, false, "https://na.finalfantasyxiv.com/jobguide/pictomancer/", true),
        Percentage("NIN", [36957], "Dokumori", 3849, 1.05, "5%", "attacks against one enemy",
            "enemy debuff", false, false, "https://na.finalfantasyxiv.com/jobguide/ninja/", true,
            debuffOnEnemy: true),
        Percentage("DNC", [16003, 16191, 16192], "Standard Finish (partner)", 2105, 1.05, "5%", "Dance Partner",
            "self plus one designated partner", false, true, "https://na.finalfantasyxiv.com/jobguide/dancer/", true),
        Percentage("DNC", [16003, 16191, 16192], "Standard Finish (self)", 1821, 1.05, "5%", "self",
            "self", false, true, "https://na.finalfantasyxiv.com/jobguide/dancer/", true,
            analysisNote: "The game emits a separate self-facing status; it affects damage but cannot earn self rDPS contribution."),
        // Technical Finish shares one status across finish ranks. The timeline resolves the
        // 3%/5% multiplier from the nearby finish action instead of trusting this nominal maximum.
        Percentage("DNC", [16004, 16193, 16194, 16195, 16196, 33216, 33217, 33218], "Technical Finish", 1822, 1.05, "1%/2%/3%/5%",
            "self and nearby party", "party aura; rank depends on completed steps", true, false,
            "https://na.finalfantasyxiv.com/jobguide/dancer/", true),
        Percentage("BRD", [114], "Mage's Ballad", 2217, 1.01, "1%", "self and party",
            "50y song aura", true, false, "https://na.finalfantasyxiv.com/jobguide/bard/", false),
        Percentage("BRD", [25785], "Radiant Finale", 2964, null, "2%/4%/6%", "self and nearby party",
            "party aura; magnitude depends on active Coda", true, false,
            "https://na.finalfantasyxiv.com/jobguide/bard/", false,
            analysisNote: "Public cached status events do not encode Coda count; retained in the registry but excluded from fixed-magnitude control residuals."),
    ];

    public static IReadOnlyDictionary<long, OffensiveBuffDefinition> ByStatusId { get; } = All
        .ToDictionary(static item => item.StatusId);

    private static OffensiveBuffDefinition Rate(
        string providerJob,
        IReadOnlyList<long> actionIds,
        string actionName,
        long statusId,
        OffensiveBuffDimension dimension,
        double criticalRate,
        double directRate,
        string magnitude,
        string scope,
        string targeting,
        bool partyWide,
        bool singleTarget,
        bool debuffOnEnemy,
        bool selfAlsoAffected,
        string source,
        bool coveredByProduction,
        string note = "")
        => new(
            providerJob, actionIds, actionName, statusId, dimension, criticalRate, directRate, null,
            magnitude, scope, targeting, partyWide, singleTarget, debuffOnEnemy, selfAlsoAffected,
            source, GameVersion, coveredByProduction,
            "Action IDs: cached FFLogs masterData; status IDs: cached FFLogs events/DamageDone taken[]", note);

    private static OffensiveBuffDefinition Percentage(
        string providerJob,
        IReadOnlyList<long> actionIds,
        string actionName,
        long statusId,
        double? multiplier,
        string magnitude,
        string scope,
        string targeting,
        bool partyWide,
        bool singleTarget,
        string source,
        bool coveredByProduction,
        string analysisNote = "",
        bool debuffOnEnemy = false)
        => new(
            providerJob, actionIds, actionName, statusId, OffensiveBuffDimension.PercentageDamage,
            0, 0, multiplier, magnitude, scope, targeting, partyWide, singleTarget, debuffOnEnemy,
            true, source, GameVersion, coveredByProduction,
            "Action IDs: cached FFLogs masterData; status IDs: cached FFLogs events/DamageDone taken[]",
            analysisNote);
}

internal sealed record GuaranteedHitDefinition(
    string Job,
    IReadOnlyList<long> ActionIds,
    string ActionName,
    ProbeGuaranteedDimensions Dimensions,
    string Condition,
    long? ConditionStatusId,
    string OfficialSource,
    string GameVersion,
    string DetectionSupport,
    bool CoveredByProduction);

internal static class GuaranteedHitRegistry
{
    public static IReadOnlyList<GuaranteedHitDefinition> All { get; } =
    [
        Stable("SAM", [7487], "Midare Setsugekka", ProbeGuaranteedDimensions.Critical,
            "intrinsic", "https://na.finalfantasyxiv.com/jobguide/samurai/", true),
        Stable("SAM", [16486], "Kaeshi: Setsugekka", ProbeGuaranteedDimensions.Critical,
            "intrinsic", "https://na.finalfantasyxiv.com/jobguide/samurai/", true),
        Stable("SAM", [25781], "Ogi Namikiri", ProbeGuaranteedDimensions.Critical,
            "intrinsic", "https://na.finalfantasyxiv.com/jobguide/samurai/", true),
        Stable("SAM", [25782], "Kaeshi: Namikiri", ProbeGuaranteedDimensions.Critical,
            "intrinsic", "https://na.finalfantasyxiv.com/jobguide/samurai/", true),
        Stable("SAM", [36966], "Tendo Setsugekka", ProbeGuaranteedDimensions.Critical,
            "intrinsic", "https://na.finalfantasyxiv.com/jobguide/samurai/", true),
        Stable("SAM", [36968], "Tendo Kaeshi Setsugekka", ProbeGuaranteedDimensions.Critical,
            "intrinsic", "https://na.finalfantasyxiv.com/jobguide/samurai/", true),
        Contextual("DRG", [], "Life Surge weaponskill", ProbeGuaranteedDimensions.Critical,
            "first eligible weaponskill while Life Surge is active", 116,
            "https://na.finalfantasyxiv.com/jobguide/dragoon/", "resolved from status consumption", true),
        Contextual("MNK", [53, 25767, 36945], "Opo-opo form weaponskills", ProbeGuaranteedDimensions.Critical,
            "Opo-opo Form or Formless Fist bonus", null,
            "https://na.finalfantasyxiv.com/jobguide/monk/", "registry only; form state is not yet normalized", false),
        Stable("DNC", [25792], "Starfall Dance",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "intrinsic", "https://na.finalfantasyxiv.com/jobguide/dancer/", true),
        Stable("MCH", [36982], "Full Metal Field",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "intrinsic", "https://na.finalfantasyxiv.com/jobguide/machinist/", true),
        Contextual("MCH", [16498, 16499, 16500, 25788, 36981], "Reassemble weaponskill",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "next weaponskill while Reassemble is active; DoT component excluded", 851,
            "https://na.finalfantasyxiv.com/jobguide/machinist/", "resolved for cached production weaponskill IDs", true),
        Stable("PCT", [34678], "Hammer Stamp",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "Hammer Time", "https://na.finalfantasyxiv.com/jobguide/pictomancer/", false),
        Stable("PCT", [34679], "Hammer Brush",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "Hammer Time combo", "https://na.finalfantasyxiv.com/jobguide/pictomancer/", false),
        Stable("PCT", [34680], "Polishing Hammer",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "Hammer Time combo", "https://na.finalfantasyxiv.com/jobguide/pictomancer/", false),
        Stable("WAR", [16463], "Chaotic Cyclone",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "intrinsic while Nascent Chaos action is available", "https://na.finalfantasyxiv.com/jobguide/warrior/", false),
        Stable("WAR", [16465], "Inner Chaos",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "intrinsic while Nascent Chaos action is available", "https://na.finalfantasyxiv.com/jobguide/warrior/", false),
        Stable("WAR", [25753], "Primal Rend",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "intrinsic", "https://na.finalfantasyxiv.com/jobguide/warrior/", false),
        Stable("WAR", [36925], "Primal Ruination",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "intrinsic", "https://na.finalfantasyxiv.com/jobguide/warrior/", false),
        Contextual("WAR", [3549, 3550], "Inner Release Fell Cleave / Decimate",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
            "Fell Cleave or Decimate consumes an Inner Release stack", 1177,
            "https://na.finalfantasyxiv.com/jobguide/warrior/", "resolved from active Inner Release status", false),
    ];

    public static IReadOnlyDictionary<long, GuaranteedHitDefinition> StableByActionId { get; } = All
        .Where(static item => item.ConditionStatusId is null &&
                              !item.DetectionSupport.StartsWith("registry only", StringComparison.Ordinal))
        .SelectMany(static item => item.ActionIds.Select(actionId => (actionId, item)))
        .ToDictionary(static pair => pair.actionId, static pair => pair.item);

    public static bool HasGuaranteedDirectOnly => All.Any(static item =>
        item.Dimensions == ProbeGuaranteedDimensions.DirectHit);

    private static GuaranteedHitDefinition Stable(
        string job,
        IReadOnlyList<long> actionIds,
        string actionName,
        ProbeGuaranteedDimensions dimensions,
        string condition,
        string source,
        bool coveredByProduction)
        => new(job, actionIds, actionName, dimensions, condition, null, source,
            OffensiveBuffRegistry.GameVersion, "stable action ID", coveredByProduction);

    private static GuaranteedHitDefinition Contextual(
        string job,
        IReadOnlyList<long> actionIds,
        string actionName,
        ProbeGuaranteedDimensions dimensions,
        string condition,
        long? statusId,
        string source,
        string detectionSupport,
        bool coveredByProduction)
        => new(job, actionIds, actionName, dimensions, condition, statusId, source,
            OffensiveBuffRegistry.GameVersion, detectionSupport, coveredByProduction);
}
