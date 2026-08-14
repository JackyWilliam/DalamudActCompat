namespace DalamudActCompat.FflogsParityHarness;

internal static class PercentageIdentificationCandidates
{
    public const string OraclePercentageFirst = "Oracle.PercentageFirst";
    public const string OracleRateFirst = "Oracle.RateFirst";
    public const string OracleSharedShapley = "Oracle.SharedShapley2";
    public const string OracleSharedBaseLog = "Oracle.SharedBaseLog";
    public const string OracleSharedShapley3 = "Oracle.SharedShapley3";
    public const string NominalPercentageFirst = "Nominal.PercentageFirst";
    public const string NominalSharedBaseLog = "Nominal.SharedBaseLog";
    public const string CausalGracePercentageFirst = "CausalGrace2s.PercentageFirst";
    public const string CausalGraceSharedBaseLog = "CausalGrace2s.SharedBaseLog";
    public const string CausalCohortPercentageFirst = "CausalCohort2s.PercentageFirst";
    public const string CausalCohortSharedBaseLog = "CausalCohort2s.SharedBaseLog";

    public static IReadOnlyList<string> OwnershipCandidates { get; } =
    [
        OraclePercentageFirst,
        OracleRateFirst,
        OracleSharedShapley,
        OracleSharedBaseLog,
        OracleSharedShapley3,
    ];

    public static IReadOnlyList<string> SharedCandidates { get; } =
    [
        OracleSharedShapley,
        OracleSharedBaseLog,
        OracleSharedShapley3,
    ];
}

internal readonly record struct PercentageInteractionDecomposition(
    double BaseDamage,
    double PercentageMain,
    double CriticalMain,
    double DirectMain,
    double PercentageCritical,
    double PercentageDirect,
    double CriticalDirect,
    double PercentageCriticalDirect,
    double PercentageFirst,
    double RateFirst,
    double SharedShapley2,
    double SharedBaseLog,
    double SharedShapley3,
    double CriticalRateContribution,
    double DirectRateContribution);

internal sealed record PercentageIdentificationConstraintRow(
    string Report,
    int FightId,
    int EncounterId,
    string Encounter,
    string PartyComposition,
    int ProviderActorId,
    string ProviderActor,
    string ProviderJob,
    int RecipientActorId,
    string RecipientActor,
    string RecipientJob,
    long BuffStatusId,
    string BuffName,
    double FflogsPercentageReference,
    double FflogsRecipientRateReference,
    double NominalPercentageFirst,
    double NominalSharedBaseLog,
    double OraclePercentageFirst,
    double OracleRateFirst,
    double OracleSharedShapley,
    double OracleSharedBaseLog,
    double OracleSharedShapley3,
    double CausalGracePercentageFirst,
    double CausalGraceSharedBaseLog,
    double CausalCohortPercentageFirst,
    double CausalCohortSharedBaseLog,
    int EventCount,
    int DirectEventCount,
    int PeriodicEventCount,
    int RateOverlapEventCount,
    int GuaranteedCriticalEventCount,
    int GuaranteedDirectHitEventCount,
    int GuaranteedCriticalDirectHitEventCount,
    int PetEventCount,
    int AmbiguousPetEventCount,
    int DeathResetBoundaryEventCount,
    int UnknownMagnitudeEventCount,
    int MetadataMismatchEventCount,
    long RawDamage,
    long EffectiveDamage,
    long RateOverlapDamage,
    double DamageWeightedPercentageMultiplier,
    double DamageWeightedPercentageProviderCount,
    double DamageWeightedCriticalRateTotal,
    double DamageWeightedDirectRateTotal,
    double DamageWeightedCriticalProviderCount,
    double DamageWeightedDirectProviderCount,
    int MaximumCriticalProviderCount,
    int MaximumDirectProviderCount,
    bool HasSeparateCriticalDirectProviders,
    double PercentageMainInteraction,
    double CriticalMainInteraction,
    double DirectMainInteraction,
    double PercentageCriticalInteraction,
    double PercentageDirectInteraction,
    double CriticalDirectInteraction,
    double PercentageCriticalDirectInteraction,
    double MeanBuffWindowAgeMilliseconds,
    double MeanDistanceToApplyMilliseconds,
    double MeanDistanceToRemoveMilliseconds,
    int SameTimestampStatusActivityCount,
    long MinimumPacketSequence,
    long MaximumPacketSequence,
    string PercentageComposition,
    string CriticalComposition,
    string DirectComposition,
    string RateDimension,
    string DominantActionFamily,
    int ActionFamilyCount,
    string StatusStateSources,
    bool IsCleanDirectNormal,
    string EligibilityExclusions,
    string SourceCache)
{
    public double ResolvePrediction(string candidate)
        => candidate switch
        {
            PercentageIdentificationCandidates.OraclePercentageFirst => OraclePercentageFirst,
            PercentageIdentificationCandidates.OracleRateFirst => OracleRateFirst,
            PercentageIdentificationCandidates.OracleSharedShapley => OracleSharedShapley,
            PercentageIdentificationCandidates.OracleSharedBaseLog => OracleSharedBaseLog,
            PercentageIdentificationCandidates.OracleSharedShapley3 => OracleSharedShapley3,
            PercentageIdentificationCandidates.NominalPercentageFirst => NominalPercentageFirst,
            PercentageIdentificationCandidates.NominalSharedBaseLog => NominalSharedBaseLog,
            PercentageIdentificationCandidates.CausalGracePercentageFirst => CausalGracePercentageFirst,
            PercentageIdentificationCandidates.CausalGraceSharedBaseLog => CausalGraceSharedBaseLog,
            PercentageIdentificationCandidates.CausalCohortPercentageFirst => CausalCohortPercentageFirst,
            PercentageIdentificationCandidates.CausalCohortSharedBaseLog => CausalCohortSharedBaseLog,
            _ => throw new ArgumentOutOfRangeException(nameof(candidate), candidate, "Unknown candidate."),
        };
}

internal sealed record ResidualFeatureAnalysisRow(
    string Dataset,
    string Candidate,
    string Feature,
    string Analysis,
    string Group,
    int N,
    double FeatureMinimum,
    double FeatureMaximum,
    double FeatureMean,
    double Pearson,
    double Spearman,
    double ZeroInterceptSlope,
    double OriginSlopeExplainedFraction,
    MatrixResidualStatistics Residuals);

internal sealed record OwnershipValidationRow(
    string Dataset,
    string Dimension,
    string Value,
    string Candidate,
    MatrixResidualStatistics Statistics);

internal sealed record OwnershipIdentifiabilityRow(
    string Dataset,
    string CandidateA,
    string CandidateB,
    int ConstraintCount,
    int DistinguishableConstraintCount,
    double MeanAbsolutePredictionDifference,
    double MaximumAbsolutePredictionDifference,
    bool ObservationallyEquivalent,
    string Conclusion);

internal sealed record OwnershipDiscriminatorRow(
    string Dataset,
    string CandidateA,
    string CandidateB,
    string Report,
    int FightId,
    string Encounter,
    string Provider,
    string ProviderJob,
    string Recipient,
    string RecipientJob,
    string Buff,
    string RateDimension,
    double FflogsReference,
    double PredictionA,
    double PredictionB,
    double PredictionDifference,
    double AbsoluteResidualA,
    double AbsoluteResidualB,
    string ReferenceSupports);

internal sealed record MatchedInteractionControlRow(
    string Quality,
    string Report,
    int RecipientActorId,
    string Recipient,
    string RecipientJob,
    int ProviderActorId,
    string Provider,
    string ProviderJob,
    string Encounter,
    string Buff,
    string ActionFamily,
    string ControlA,
    string ControlB,
    int FightA,
    int FightB,
    double ResidualA,
    double ResidualB,
    double ResidualShift,
    double InteractionShift,
    string ReferenceAvailability);

internal sealed record StatusWindowMetricRow(
    string Scope,
    string Value,
    string Strategy,
    int IntervalCount,
    int ExactEndpointCount,
    int EarlyExpiryCount,
    int LateExpiryCount,
    int DamageIncorrectlyIncludedCount,
    long DamageIncorrectlyIncluded,
    int DamageIncorrectlyExcludedCount,
    long DamageIncorrectlyExcluded,
    int FallbackOnlyMismatchCount,
    long FallbackOnlyMismatchDamage,
    double MaximumLatenessMilliseconds,
    string Causality,
    string Bound);

internal sealed record StatusContributionMetricRow(
    string Dataset,
    string Ownership,
    string StateStrategy,
    string Candidate,
    MatrixResidualStatistics Statistics,
    double MeanAbsolutePredictionGapFromOracle);

internal sealed record PercentageIdentificationReport(
    DateTimeOffset GeneratedAt,
    int FightCount,
    int RateOverlapConstraintCount,
    int CleanDirectNormalConstraintCount,
    IReadOnlyList<string> InteractionDecomposition,
    IReadOnlyList<string> OwnershipAllocation,
    IReadOnlyList<string> StateMachine,
    IReadOnlyList<string> FallbackStrategies,
    IReadOnlyList<PercentageIdentificationConstraintRow> Constraints,
    IReadOnlyList<ResidualFeatureAnalysisRow> ResidualFeatures,
    IReadOnlyList<OwnershipValidationRow> OwnershipValidation,
    IReadOnlyList<OwnershipIdentifiabilityRow> Identifiability,
    IReadOnlyList<OwnershipDiscriminatorRow> Discriminators,
    IReadOnlyList<MatchedInteractionControlRow> MatchedControls,
    IReadOnlyList<StatusWindowMetricRow> WindowMetrics,
    IReadOnlyList<StatusContributionMetricRow> StatusContributionMetrics,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> EvidenceBoundaries);

internal sealed record PercentageIdentificationReportPaths(
    string JsonPath,
    string MarkdownPath,
    string CoreCsvPath,
    string ResidualFeaturesCsvPath,
    string OwnershipValidationCsvPath,
    string IdentifiabilityCsvPath,
    string DiscriminatorsCsvPath,
    string MatchedControlsCsvPath,
    string WindowMetricsCsvPath,
    string StatusContributionCsvPath);
