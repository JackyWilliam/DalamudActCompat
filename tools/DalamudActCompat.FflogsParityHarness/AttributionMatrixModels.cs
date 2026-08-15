namespace DalamudActCompat.FflogsParityHarness;

internal enum RecipientHitType
{
    Normal,
    GuaranteedCritical,
    GuaranteedDirectHit,
    GuaranteedCriticalDirectHit,
}

internal sealed record MatrixConstraintResult(
    string Report,
    int FightId,
    int EncounterId,
    string Encounter,
    string Partition,
    string PartyComposition,
    int ProviderActorId,
    string ProviderActor,
    string ProviderJob,
    int RecipientActorId,
    string RecipientActor,
    string RecipientJob,
    long BuffStatusId,
    string BuffName,
    OffensiveBuffDimension BuffDimension,
    string Magnitude,
    double FflogsReference,
    string ReferenceAvailability,
    int EventCount,
    long RawDamage,
    int NormalEventCount,
    long NormalRawDamage,
    int GuaranteedCriticalEventCount,
    long GuaranteedCriticalRawDamage,
    int GuaranteedDirectEventCount,
    long GuaranteedDirectRawDamage,
    int GuaranteedCriticalDirectEventCount,
    long GuaranteedCriticalDirectRawDamage,
    double CriticalChanceProxy,
    double DirectChanceProxy,
    IReadOnlyDictionary<string, double> CandidateTotals,
    IReadOnlyDictionary<string, double> CandidateResiduals,
    IReadOnlyList<string> ActiveRateComposition,
    string SourceCache,
    IReadOnlyList<string> Warnings);

internal sealed record AttributionMatrixCell(
    OffensiveBuffDimension BuffDimension,
    RecipientHitType HitType,
    int ConstraintCount,
    int FightCount,
    int EventCount,
    long RawDamage,
    int ProviderCount,
    int RecipientJobCount,
    int ActorCount,
    int EncounterCount,
    string Providers,
    string RecipientJobs,
    string ReferenceQuality,
    IReadOnlyDictionary<string, MatrixResidualStatistics> CandidateStatistics);

internal sealed record MatrixResidualStatistics(
    int ConstraintCount,
    double MeanResidual,
    double MedianResidual,
    double MeanAbsoluteResidual,
    double RootMeanSquareResidual,
    double MaximumAbsoluteResidual,
    int NegativeCount,
    int ZeroCount,
    int PositiveCount);

internal sealed record MatrixCandidateScopeResult(
    string Candidate,
    string ScopeDimension,
    string ScopeValue,
    MatrixResidualStatistics Statistics);

internal sealed record MatchedActorFight(
    string Report,
    int FightId,
    int EncounterId,
    string Encounter,
    string PartyComposition,
    string RateComposition,
    double CriticalChanceProxy,
    double DirectChanceProxy,
    IReadOnlyDictionary<string, double> CandidateResiduals);

internal sealed record MatchedActorGroup(
    string Actor,
    string Job,
    string Partition,
    string MatchQuality,
    string QualityReason,
    RecipientHitType GuaranteedHitType,
    IReadOnlyList<MatchedActorFight> Fights,
    string BuffDifference,
    IReadOnlyDictionary<string, double> CandidateResidualRange);

internal sealed record AttributionMatrixCandidateRanking(
    string Candidate,
    MatrixResidualStatistics Overall,
    IReadOnlyList<MatrixCandidateScopeResult> Scopes,
    string Verdict,
    string VerdictReason);

internal sealed record PercentageControlResult(
    string Status,
    MatrixResidualStatistics PublishedMathStatistics,
    string Reason);

internal sealed record AttributionMatrixReport(
    DateTimeOffset GeneratedAt,
    string Scope,
    string EquationStatus,
    string EquationStatusReason,
    IReadOnlyList<OffensiveBuffDefinition> OffensiveBuffRegistry,
    IReadOnlyList<GuaranteedHitDefinition> GuaranteedHitRegistry,
    IReadOnlyList<AttributionMatrixCell> Matrix,
    IReadOnlyList<MatrixConstraintResult> Constraints,
    IReadOnlyList<MatchedActorGroup> MatchedGroups,
    IReadOnlyList<AttributionMatrixCandidateRanking> CandidateRankings,
    PercentageControlResult PercentageControl,
    IReadOnlyList<string> CandidatesRejected,
    IReadOnlyList<string> CrossProviderFindings,
    IReadOnlyList<string> RemainingUnknowns,
    IReadOnlyList<string> MinimumDataNeeds,
    IReadOnlyList<string> KnownBoundaries,
    int ExistingCachedFightCount,
    int TargetedCachedFightCount,
    int NewlyMinedFightCount);

internal sealed record AttributionMatrixReportPaths(
    string JsonPath,
    string MarkdownPath,
    string MatrixCsvPath,
    string ConstraintsCsvPath,
    string MatchedPairsCsvPath,
    string CandidateCsvPath,
    string OffensiveBuffRegistryCsvPath,
    string GuaranteedHitRegistryCsvPath);
