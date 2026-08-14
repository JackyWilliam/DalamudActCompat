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
    double ProviderCriticalRateIncrease,
    double ProviderDirectRateIncrease,
    ProbeGuaranteedDimensions Dimensions,
    double SelfCriticalRateIncrease = 0,
    double SelfDirectRateIncrease = 0);

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
    double ResidualVsRateBuffOverlapCorrelation,
    double ResidualVsGuaranteedDamageRatioCorrelation,
    double ResidualVsTendoDamageRatioCorrelation,
    double ResidualVsTendoKaeshiDamageRatioCorrelation,
    double ResidualVsOgiDamageRatioCorrelation,
    double ResidualVsSelfRateExposureCorrelation,
    double ResidualVsExternalCriticalOverlapCorrelation,
    double ResidualVsExternalDirectOverlapCorrelation,
    double ActorEtaSquared,
    double EncounterEtaSquared);

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

internal sealed record GuaranteedHitResidualDecomposition(
    string Report,
    int FightId,
    string Actor,
    string PartnerActor,
    string Encounter,
    int EncounterId,
    string Cohort,
    double Duration,
    string PartyComposition,
    long PartnerTotalRawDamage,
    long GuaranteedRawDamage,
    double GuaranteedTotalRawRatio,
    double GuaranteedDevilmentWindowRatio,
    long MidareDamage,
    long TendoDamage,
    long TendoKaeshiDamage,
    long OgiDamage,
    long KaeshiNamikiriDamage,
    long KaeshiSetsugekkaDamage,
    double CriticalChanceProxy,
    double DirectChanceProxy,
    double CriticalChanceMinimum,
    double CriticalChanceMaximum,
    double DirectChanceMinimum,
    double DirectChanceMaximum,
    string CriticalRateBuffComposition,
    string DirectRateBuffComposition,
    double SelfRateExposureFraction,
    double ExternalRateExposureFraction,
    double ExternalCriticalOverlapFraction,
    double ExternalDirectOverlapFraction,
    double RawWeightedSelfCriticalRate,
    double RawWeightedSelfDirectRate,
    double RawWeightedExternalCriticalRate,
    double RawWeightedExternalDirectRate,
    double FflogsDevilmentTotal,
    double ProductionDevilmentTotal,
    double CurrentProductionResidual,
    double ObservedHitRegularResidual,
    double UnscaledObservedHitResidual,
    IReadOnlyDictionary<string, double> CandidateResiduals);

internal sealed record GuaranteedHitCandidateScopeValidation(
    string Candidate,
    string Scope,
    string Unit,
    GuaranteedHitResidualStatistics Statistics);

internal sealed record GuaranteedHitActorAnalysis(
    string Actor,
    int FightCount,
    int EncounterCount,
    string Encounters,
    double CurrentResidualMean,
    double ObservedResidualMean,
    double UnscaledResidualMean,
    double CriticalChanceMinimum,
    double CriticalChanceMaximum,
    double DirectChanceMinimum,
    double DirectChanceMaximum,
    double GuaranteedDamageRatioMean,
    double TendoRatioMean,
    double ExternalCriticalOverlapMean,
    double ExternalDirectOverlapMean,
    double SelfRateExposureMean,
    string RateBuffComposition,
    IReadOnlyDictionary<string, double> CandidateResidualMeans);

internal sealed record GuaranteedHitActorStability(
    string Candidate,
    int MultiFightActorCount,
    int MultiEncounterActorCount,
    int StableSignActorCount,
    double MeanWithinActorStandardDeviation,
    double ActorEtaSquared);

internal sealed record GuaranteedHitCohortFeatureDistribution(
    string Cohort,
    string Feature,
    int FightCount,
    double Mean,
    double Median,
    double FirstQuartile,
    double ThirdQuartile,
    double Minimum,
    double Maximum);

internal sealed record GuaranteedHitCohortCategoryDistribution(
    string Cohort,
    string Dimension,
    string Value,
    int FightCount,
    double Fraction);

internal sealed record GuaranteedHitPartialCorrelation(
    string Candidate,
    string Scope,
    string Variable,
    int FightCount,
    int WithinActorObservationCount,
    int WithinEncounterObservationCount,
    double RawCorrelation,
    double ControllingGuaranteedDamageRatio,
    double ControllingNumericInputs,
    double WithinActorCorrelation,
    double WithinEncounterCorrelation,
    double FullControlsCorrelation);

internal sealed record GuaranteedHitCounterfactualStatistics(
    string Candidate,
    string PartnerJob,
    int SampleCount,
    double MeanDelta,
    double MedianDelta,
    double MeanAbsoluteDelta,
    double RootMeanSquareDelta,
    double MaximumAbsoluteDelta,
    int NegativeCount,
    int ZeroCount,
    int PositiveCount);

internal sealed record RateBuffDenominatorAudit(
    long AbilityId,
    string Buff,
    double CriticalRate,
    double DirectRate,
    string SourceAndTarget,
    string ExternalTargetProduction,
    string SelfTargetProduction,
    bool AllowsSelfContribution,
    string OtherProviderDenominator,
    string FflogsPublicRule);

internal sealed record GuaranteedHitAcceptanceCheck(
    string Candidate,
    string Check,
    bool Passed,
    string Evidence);

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
    IReadOnlyList<string> EvidenceBoundaries,
    IReadOnlyList<GuaranteedHitResidualDecomposition> ResidualDecomposition,
    IReadOnlyList<GuaranteedHitCandidateScopeValidation> CandidateScopeValidation,
    IReadOnlyList<GuaranteedHitActorAnalysis> ActorAnalysis,
    IReadOnlyList<GuaranteedHitActorStability> ActorStability,
    IReadOnlyList<GuaranteedHitCohortFeatureDistribution> CohortFeatureDistributions,
    IReadOnlyList<GuaranteedHitCohortCategoryDistribution> CohortCategoryDistributions,
    IReadOnlyList<GuaranteedHitPartialCorrelation> PartialCorrelations,
    IReadOnlyList<GuaranteedHitCounterfactualStatistics> AllCandidateCounterfactuals,
    IReadOnlyList<RateBuffDenominatorAudit> RateBuffDenominatorAudit,
    IReadOnlyList<GuaranteedHitAcceptanceCheck> AcceptanceChecks,
    IReadOnlyList<string> ResidualFindings);

internal sealed record GuaranteedHitExperimentReportPaths(
    string JsonPath,
    string FightCandidatesCsvPath,
    string CandidateSummaryCsvPath,
    string CohortValidationCsvPath,
    string ActionFamilyCsvPath,
    string BuffConditionCsvPath,
    string FullReplayCsvPath,
    string ResidualDecompositionCsvPath,
    string CandidateScopeCsvPath,
    string ActorAnalysisCsvPath,
    string CohortFeatureCsvPath,
    string CohortCategoryCsvPath,
    string PartialCorrelationCsvPath,
    string AllCandidateCounterfactualCsvPath,
    string RateBuffAuditCsvPath,
    string MarkdownPath);
