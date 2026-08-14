namespace DalamudActCompat.FflogsParityHarness;

internal static class MatchedOwnershipCandidates
{
    public const string CurrentProduction = "CurrentProduction";
    public const string RateFirst = "RateFirst";
    public const string SharedBaseLog = "SharedBaseLog";
    public const string SharedShapley = "SharedShapley";
    public const string SharedShapley3 = "SharedShapley3";

    public static IReadOnlyList<string> All { get; } =
    [
        CurrentProduction,
        RateFirst,
        SharedBaseLog,
        SharedShapley,
        SharedShapley3,
    ];
}

internal sealed record MatchedActorIdentity(
    string Key,
    int? CanonicalId,
    string CharacterName,
    int ServerId,
    string ServerName,
    string Region,
    string Job,
    int Partition,
    string ResolutionSource,
    bool ReportServerVerified);

internal sealed record MatchedRankingFight(
    RankingSeed Seed,
    string Job,
    int ServerId,
    string ServerName,
    string Region,
    int LodestoneId,
    long AbsoluteStartTime,
    int Partition,
    string DiscoveryPath);

internal sealed record MatchedFightPreflight(
    MatchedRankingFight Ranking,
    string MetadataPath,
    int RecipientActorId,
    string PartyComposition,
    string RateDimension,
    string RateComposition,
    string PercentageComposition,
    int CriticalProviderCount,
    int DirectHitProviderCount,
    bool SeparateCriticalDirectProviders,
    bool HasFixedPercentageReference,
    bool HasUnknownPercentageMagnitude,
    long RawDamage,
    double FightStart,
    double FightEnd,
    bool ReportServerVerified);

internal sealed record MatchedOwnershipSample(
    MatchedActorIdentity Identity,
    MatchedFightPreflight Preflight,
    IReadOnlyList<string> EventPaths,
    string WhyUseful);

internal sealed record MatchedOwnershipManifest(
    DateTimeOffset GeneratedAt,
    int ApiCandidatesScanned,
    int RankingPagesRead,
    int MetadataPreflights,
    int ImportedCacheHits,
    int ApiCacheHits,
    int NewApiRequests,
    int UniqueCachedApiResponses,
    IReadOnlyList<MatchedOwnershipSample> Samples,
    IReadOnlyList<string> Failures);

internal sealed record MatchedOwnershipFightResult(
    string IdentityKey,
    int? CanonicalId,
    string Actor,
    string Job,
    string World,
    string Region,
    int Partition,
    string Report,
    int FightId,
    int EncounterId,
    string Encounter,
    long AbsoluteStartTime,
    string PartyComposition,
    string ExposureDimension,
    string RateComposition,
    string PercentageComposition,
    int CriticalProviderCount,
    int DirectHitProviderCount,
    bool SeparateCriticalDirectProviders,
    double FflogsPercentageReference,
    IReadOnlyDictionary<string, double> CandidatePredictions,
    IReadOnlyDictionary<string, double> CandidateResiduals,
    int ConstraintCount,
    int EventCount,
    int DirectEventCount,
    int PeriodicEventCount,
    int GuaranteedCriticalEventCount,
    int GuaranteedDirectHitEventCount,
    int GuaranteedCriticalDirectHitEventCount,
    bool IsNormalDirect,
    string CombatantInfoEvidence,
    string WhyUseful);

internal sealed record MatchedOwnershipPairResult(
    string IdentityKey,
    string Actor,
    string Job,
    string World,
    int Partition,
    string FightA,
    string FightB,
    int EncounterA,
    int EncounterB,
    string ExposureA,
    string ExposureB,
    string ChangedDimension,
    double DaysBetween,
    bool SameReport,
    bool SameEncounter,
    bool SamePercentageComposition,
    int MatchScore,
    string Grade,
    double FflogsObservedShift,
    IReadOnlyDictionary<string, double> CandidatePredictionShifts,
    IReadOnlyDictionary<string, double> CandidateResidualShifts,
    string MaximumDiscriminatorPair,
    double MaximumCandidateSeparation,
    string Winner,
    string Confidence,
    bool IsNormalDirect,
    bool GuaranteedEquationConfounded);

internal sealed record MatchedOwnershipComponentResult(
    string IdentityKey,
    string Actor,
    string Job,
    string World,
    int Partition,
    string Report,
    int FightId,
    int EncounterId,
    string Encounter,
    long AbsoluteStartTime,
    string ProviderJob,
    long BuffStatusId,
    string BuffName,
    string RateDimension,
    string PercentageComposition,
    int CriticalProviderCount,
    int DirectHitProviderCount,
    bool SeparateCriticalDirectProviders,
    double FflogsReference,
    IReadOnlyDictionary<string, double> CandidatePredictions,
    IReadOnlyDictionary<string, double> CandidateResiduals,
    int EventCount,
    int PeriodicEventCount,
    int GuaranteedEventCount,
    bool IsCleanDirectNormal);

internal sealed record MatchedOwnershipComponentPairResult(
    string IdentityKey,
    string Actor,
    string Job,
    string World,
    int Partition,
    string ProviderJob,
    long BuffStatusId,
    string BuffName,
    string FightA,
    string FightB,
    int EncounterA,
    int EncounterB,
    string ExposureA,
    string ExposureB,
    string ChangedDimension,
    double DaysBetween,
    bool SameReport,
    bool SameEncounter,
    bool SamePercentageComposition,
    int MatchScore,
    string Grade,
    double FflogsObservedShift,
    IReadOnlyDictionary<string, double> CandidatePredictionShifts,
    IReadOnlyDictionary<string, double> CandidateResidualShifts,
    string MaximumDiscriminatorPair,
    double MaximumCandidateSeparation,
    string Winner,
    string Confidence,
    bool IsCleanDirectNormal,
    bool GuaranteedEquationConfounded,
    bool MultipleProviderExposure);

internal sealed record MatchedOwnershipCandidateStatistics(
    string Scope,
    string Candidate,
    int N,
    double Mean,
    double Median,
    double MeanAbsolute,
    double RootMeanSquare,
    double MaximumAbsolute,
    int NegativeCount,
    int ZeroCount,
    int PositiveCount,
    int Rank);

internal sealed record MatchedOwnershipActorSummary(
    string IdentityKey,
    int? CanonicalId,
    string Actor,
    string Job,
    string World,
    string Region,
    int Partition,
    int FightCount,
    string ExposureDimensions,
    string BestMatchGrade,
    string IdentitySource,
    bool ReportServerVerified,
    double MaximumDaysBetween);

internal sealed record MatchedOwnershipReport(
    DateTimeOffset GeneratedAt,
    MatchedOwnershipManifest Mining,
    IReadOnlyList<MatchedOwnershipActorSummary> Actors,
    IReadOnlyList<MatchedOwnershipFightResult> Fights,
    IReadOnlyList<MatchedOwnershipPairResult> Pairs,
    IReadOnlyList<MatchedOwnershipComponentResult> Components,
    IReadOnlyList<MatchedOwnershipComponentPairResult> MatchedGroups,
    IReadOnlyList<MatchedOwnershipCandidateStatistics> Rankings,
    int GradeAGroupCount,
    int GradeBGroupCount,
    int GradeCGroupCount,
    bool HasEnoughDhOnlyEvidence,
    bool HasEnoughCriticalDirectEvidence,
    bool SharedBaseLogVsShapley3Identifiable,
    string OwnershipStatus,
    string MinimumGap,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Limitations);
