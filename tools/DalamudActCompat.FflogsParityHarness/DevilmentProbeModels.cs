namespace DalamudActCompat.FflogsParityHarness;

internal sealed record DevilmentProbeSampleSelection(
    string Report,
    int FightId,
    string PartnerJob,
    string SelectionReason,
    double FirstRoundDeltaRdps);

internal sealed record DevilmentActionProbeResult
{
    public required string Report { get; init; }

    public required int FightId { get; init; }

    public required string SamplePartnerJob { get; init; }

    public required string PartnerJob { get; init; }

    public required double Timestamp { get; init; }

    public required int SourceActorId { get; init; }

    public required string SourceActor { get; init; }

    public required int OwnerActorId { get; init; }

    public required string OwnerActor { get; init; }

    public required long ActionId { get; init; }

    public required string ActionName { get; init; }

    public required long RawDamage { get; init; }

    public required long EffectiveDamage { get; init; }

    public required bool IsPeriodic { get; init; }

    public required bool IsCrit { get; init; }

    public required bool IsDirectHit { get; init; }

    public required bool? GuaranteedCrit { get; init; }

    public required bool? GuaranteedDirectHit { get; init; }

    public required bool? GuaranteedCritDh { get; init; }

    public required string GuaranteeCategory { get; init; }

    public required string GuaranteeSource { get; init; }

    public required string CurrentDactMetadata { get; init; }

    public required bool DevilmentActive { get; init; }

    public required string DevilmentAttributionTiming { get; init; }

    public int? DevilmentWindow { get; init; }

    public required double DactInferredCu { get; init; }

    public required double DactInferredDu { get; init; }

    public required double CritRateBeforeBuff { get; init; }

    public required double CritRateAfterBuff { get; init; }

    public required double DhRateBeforeBuff { get; init; }

    public required double DhRateAfterBuff { get; init; }

    public double? ReferenceCu { get; init; }

    public double? ReferenceDu { get; init; }

    public double? FflogsDevilmentContribution { get; init; }

    public required string FflogsContributionAvailability { get; init; }

    public required double DactCriticalContribution { get; init; }

    public required double DactDirectHitContribution { get; init; }

    public required double DactDevilmentContribution { get; init; }

    public double? DeltaContribution { get; init; }

    public required double AnalyticalProductionContribution { get; init; }

    public required double AnalyticalCalibrationResidual { get; init; }

    public required double PublishedRegularPathContribution { get; init; }

    public required double ProductionGuaranteedPathContribution { get; init; }

    public required double ScenarioContributionCorrection { get; init; }
}

internal sealed record DevilmentCategoryProbeResult(
    string PartnerJob,
    string Category,
    int EventCount,
    long TotalRawDamage,
    double? FflogsDevilmentContribution,
    string FflogsContributionAvailability,
    double DactDevilmentContribution,
    double? TotalDelta,
    double? MeanDeltaPerEvent,
    double? MeanDeltaPer100kDamage,
    double ScenarioContributionCorrection,
    double MeanScenarioCorrectionPerEvent,
    double MeanScenarioCorrectionPer100kDamage);

internal sealed record DevilmentActionCoverageResult(
    long ActionId,
    string ActionName,
    string Job,
    string ExpectedBehavior,
    string CurrentDactMetadata,
    string ObservedFflogsBehavior,
    int EventCount,
    int CriticalCount,
    int DirectHitCount,
    long RawDamage,
    string Covered,
    string Mismatch,
    string EvidenceSource);

internal sealed record DevilmentWindowProbeResult(
    string Report,
    int FightId,
    string PartnerJob,
    int Window,
    double Start,
    double End,
    string Partner,
    int ActionCount,
    long RawDamage,
    double? FflogsContribution,
    string FflogsContributionAvailability,
    double DactContribution,
    double? Delta,
    double ScenarioContributionCorrection);

internal sealed record DevilmentSensitivityProbeResult(
    string Report,
    int FightId,
    string PartnerJob,
    string Input,
    double ShiftPercentagePoints,
    double DactBaselineContribution,
    double ShiftedContribution,
    double ContributionChange,
    double BaselineFinalRdpsDelta,
    double ShiftedFinalRdpsDelta);

internal sealed record ComponentParityProbeResult(
    string Report,
    int FightId,
    string PartnerJob,
    string Component,
    double? Fflogs,
    double? Dact,
    double? Delta,
    string Availability);

internal sealed record DevilmentSampleProbeResult(
    DevilmentProbeSampleSelection Selection,
    string Actor,
    string Encounter,
    double Duration,
    string PartyComposition,
    int PartnerActorId,
    string PartnerActor,
    double FflogsRdps,
    double DactRdps,
    double DeltaRdps,
    double FflogsDevilmentContribution,
    double DactDevilmentContribution,
    double DevilmentContributionDelta,
    double FinalDactInferredCu,
    double FinalDactInferredDu,
    double? ReferenceCu,
    double? ReferenceDu,
    string ReferenceBaselineAvailability,
    double ActionContributionCoverageDelta,
    double AnalyticalCalibrationResidual,
    double ScenarioContributionCorrection,
    double ScenarioResidualToFflogs,
    IReadOnlyList<ComponentParityProbeResult> Components);

internal sealed record DevilmentTopActionProbeResult(
    long ActionId,
    string ActionName,
    string Job,
    string Category,
    int EventCount,
    long RawDamage,
    double DactContribution,
    double ScenarioContributionCorrection,
    string RankingBoundary);

internal sealed record PartnerParityComparison(
    string PartnerJob,
    int SampleCount,
    double MeanFinalRdpsDelta,
    double MeanExternalReceivedDelta,
    double MeanOwnGivenDelta,
    double MeanTechnicalStandardDelta,
    double MeanDevilmentDelta);

internal sealed record DevilmentProbeReport(
    DateTimeOffset GeneratedAt,
    int SelectedSampleCount,
    int ActionCount,
    IReadOnlyList<DevilmentProbeSampleSelection> Selections,
    IReadOnlyList<PartnerParityComparison> FirstRoundPartnerComparison,
    IReadOnlyList<DevilmentSampleProbeResult> Samples,
    IReadOnlyList<DevilmentActionProbeResult> Actions,
    IReadOnlyList<DevilmentCategoryProbeResult> Categories,
    IReadOnlyList<DevilmentActionCoverageResult> Coverage,
    IReadOnlyList<DevilmentWindowProbeResult> Windows,
    IReadOnlyList<DevilmentSensitivityProbeResult> Sensitivity,
    IReadOnlyList<DevilmentTopActionProbeResult> TopModeledErrorActions,
    IReadOnlyList<string> EvidenceBoundaries);
