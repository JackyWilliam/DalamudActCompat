namespace DalamudActCompat.FflogsParityHarness;

internal sealed record GuaranteedHitCandidateDefinition(
    string Name,
    string Family,
    string Equation,
    string ApplicableConditions);

internal readonly record struct GuaranteedHitCandidateInput(
    double DamageAfterPercentageRemoval,
    bool IsCritical,
    bool IsDirectHit,
    double UnbuffedCriticalChance,
    double UnbuffedDirectChance,
    double CriticalRateIncrease,
    double DirectRateIncrease,
    double DancerCriticalRateIncrease,
    double DancerDirectRateIncrease,
    ProbeGuaranteedDimensions Dimensions);

internal sealed record GuaranteedHitExperimentSelection(
    string Report,
    int FightId,
    string Actor,
    string Encounter,
    int EncounterId,
    double Duration,
    string PartyComposition,
    long GuaranteedRawDamage,
    int GuaranteedEventCount,
    double GuaranteedDamageShare,
    double CriticalChanceProxy,
    double DirectChanceProxy,
    double RateBuffOverlapFraction,
    string SelectionReason);

internal sealed record GuaranteedHitCandidateFightResult(
    string Report,
    int FightId,
    string Actor,
    string Encounter,
    int EncounterId,
    string PartnerJob,
    double Duration,
    string PartyComposition,
    double FflogsDevilmentTotal,
    double ProductionDevilmentTotal,
    double ProductionResidual,
    string Candidate,
    double CandidateDevilmentTotal,
    double CandidateResidual,
    double CandidateFinalRdpsDelta,
    long GuaranteedRawDamage,
    int GuaranteedEventCount,
    double GuaranteedDamageShare,
    double CriticalChanceProxy,
    double DirectChanceProxy,
    double RateBuffOverlapFraction,
    string BuffConditions);

internal sealed record GuaranteedHitResidualStatistics(
    int FightCount,
    double MeanResidual,
    double MedianResidual,
    double MeanAbsoluteResidual,
    double RootMeanSquareResidual,
    double MaximumAbsoluteResidual,
    int NegativeCount,
    int ZeroCount,
    int PositiveCount,
    double ResidualVsDurationCorrelation,
    double ResidualVsGuaranteedDamageCorrelation,
    double ResidualVsCriticalProxyCorrelation,
    double ResidualVsDirectProxyCorrelation,
    double ResidualVsRateBuffOverlapCorrelation);

internal sealed record GuaranteedHitCandidateRanking(
    string Candidate,
    string Family,
    GuaranteedHitResidualStatistics Statistics,
    string SystematicBias,
    string Verdict);

internal sealed record GuaranteedHitActionFamilyValidation(
    string Candidate,
    string ActionFamily,
    long[] ActionIds,
    int ObservedFightCount,
    long RawDamage,
    int HeavyFightCount,
    GuaranteedHitResidualStatistics HeavyGroup,
    GuaranteedHitResidualStatistics RemainingGroup,
    double ResidualVsFamilyDamageCorrelation);

internal sealed record GuaranteedHitCohortValidation(
    string Candidate,
    string Cohort,
    GuaranteedHitResidualStatistics Statistics);

internal sealed record GuaranteedHitBuffConditionValidation(
    string Candidate,
    string Condition,
    GuaranteedHitResidualStatistics Statistics);

internal sealed record GuaranteedHitDimensionEvidence(
    string Dimension,
    int FightCount,
    int EventCount,
    long RawDamage,
    string EvidenceVerdict);

internal sealed record GuaranteedHitFullReplayComparison(
    string PartnerJob,
    int SampleCount,
    double CurrentMeanDelta,
    double CurrentMedianDelta,
    double CurrentMeanAbsoluteDelta,
    double CurrentMaxAbsoluteDelta,
    double CandidateMeanDelta,
    double CandidateMedianDelta,
    double CandidateMeanAbsoluteDelta,
    double CandidateMaxAbsoluteDelta);

internal sealed record GuaranteedHitCalibrationResult(
    int EventCount,
    double MaximumAbsoluteEventResidual,
    double MaximumAbsoluteFightResidual,
    bool Passed);

internal sealed record GuaranteedHitAttributionExperimentReport(
    DateTimeOffset GeneratedAt,
    int CachedSampleCount,
    int EligibleSamFightCount,
    int SelectedSamFightCount,
    int UniqueDancerCount,
    int EncounterCount,
    IReadOnlyList<GuaranteedHitCandidateDefinition> Candidates,
    GuaranteedHitCalibrationResult CurrentProductionCalibration,
    IReadOnlyList<GuaranteedHitExperimentSelection> Selections,
    IReadOnlyList<GuaranteedHitCandidateFightResult> FightResults,
    IReadOnlyList<GuaranteedHitCandidateRanking> Rankings,
    IReadOnlyList<GuaranteedHitCohortValidation> CohortValidation,
    IReadOnlyList<GuaranteedHitActionFamilyValidation> ActionFamilyValidation,
    IReadOnlyList<GuaranteedHitBuffConditionValidation> BuffConditionValidation,
    IReadOnlyList<GuaranteedHitDimensionEvidence> DimensionEvidence,
    string BestCandidate,
    string EquationStatus,
    string EquationStatusReason,
    IReadOnlyList<GuaranteedHitFullReplayComparison> FullReplay,
    IReadOnlyList<string> RemainingUnknowns,
    IReadOnlyList<string> EvidenceBoundaries);

internal sealed record GuaranteedHitExperimentReportPaths(
    string JsonPath,
    string FightCandidatesCsvPath,
    string CandidateSummaryCsvPath,
    string CohortValidationCsvPath,
    string ActionFamilyCsvPath,
    string BuffConditionCsvPath,
    string FullReplayCsvPath,
    string MarkdownPath);
