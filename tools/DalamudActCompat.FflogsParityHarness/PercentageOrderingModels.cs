namespace DalamudActCompat.FflogsParityHarness;

internal static class PercentageOrderingCandidates
{
    // The normalized timeline intentionally remains separate from the measured
    // production counters until every event-state residual is eliminated.
    public const string CurrentProduction = "CurrentProduction.TimelineClone";
    public const string NominalSharedLog = "PacketNominal.SharedBaseLog";
    public const string ObservedPercentageFirst = "ObservedEventState.PercentageFirst";
    public const string ObservedRateFirst = "ObservedEventState.RateFirst";
    public const string ObservedSharedShapley = "ObservedEventState.SharedBaseShapley";
    public const string ObservedSharedLog = "ObservedEventState.SharedBaseLog";

    public static IReadOnlyList<PercentageOrderingCandidateDefinition> Definitions { get; } =
    [
        new(
            CurrentProduction,
            "N%=N; P=N-N/M; Nrate=N/M. Production-covered status set and causal two-second missing-remove fallback."),
        new(
            NominalSharedLog,
            "SharedBaseLog with packet ordering and production nominal/early-remove endpoints."),
        new(
            ObservedPercentageFirst,
            "N%=N; P=N-N/M; Nrate=N/M. Packet-ordered apply/remove state and authoritative fixed metadata."),
        new(
            ObservedRateFirst,
            "Rraw=M*R(N/M); N%=N-Rraw; P=N%-(N%/M). The percentage/rate interaction is credited to rate."),
        new(
            ObservedSharedShapley,
            "P=(P_percentage-first+P_rate-first)/2. This is the parameter-free two-order Shapley split of the interaction."),
        new(
            ObservedSharedLog,
            "B=N/M-R; Q=(N/M)/B; P=(N-B)*ln(M)/ln(MQ), then split providers by ln(mi)/ln(M)."),
    ];
}

internal sealed record PercentageOrderingCandidateDefinition(string Name, string Equation);

internal sealed record RateOverlapEventProbeRow(
    string Report,
    int FightId,
    string Encounter,
    double Timestamp,
    long AttributionSequence,
    int DamageActorId,
    string DamageActor,
    string DamageActorJob,
    int DamageSourceId,
    string DamageSource,
    int TargetActorId,
    long ActionId,
    string ActionName,
    long RawDamage,
    long EffectiveDamage,
    bool IsCritical,
    bool IsDirectHit,
    bool IsGuaranteedCritical,
    bool IsGuaranteedDirectHit,
    bool IsGuaranteedCriticalDirectHit,
    bool IsPeriodic,
    string ActivePercentageBuffs,
    string ActiveCriticalRateBuffs,
    string ActiveDirectRateBuffs,
    double CombinedPercentageMultiplier,
    double ProductionPercentageContribution,
    double ProductionCriticalContribution,
    double ProductionDirectHitContribution,
    double ProductionGuaranteedContribution,
    double OfflineProductionPercentageContribution,
    double OfflineProductionCriticalContribution,
    double OfflineProductionDirectHitContribution,
    double PercentageCalibrationResidual,
    double CriticalCalibrationResidual,
    double DirectHitCalibrationResidual,
    double DamageBasisUsedByPercentage,
    double DamageBasisUsedByRate,
    double ObservedRateContributionAfterPercentage,
    double ObservedRateContributionOnRaw,
    double ObservedPercentageFirstTotal,
    double ObservedRateFirstTotal,
    double ObservedSharedShapleyTotal,
    double ObservedSharedLogTotal,
    double CurrentConservationTotal,
    double RateFirstConservationTotal,
    double SharedShapleyConservationTotal,
    double SharedLogConservationTotal,
    string OverlapGroups,
    string StateBoundaryNote);

internal sealed record PercentageOrderingConstraintRow(
    string Report,
    int FightId,
    int EncounterId,
    string Encounter,
    string PartyComposition,
    string ProviderType,
    int ProviderActorId,
    string ProviderActor,
    string ProviderJob,
    int RecipientActorId,
    string RecipientActor,
    string RecipientJob,
    long BuffStatusId,
    string BuffName,
    double FflogsContribution,
    double CurrentProductionContribution,
    double NominalSharedLogContribution,
    double ObservedPercentageFirstContribution,
    double ObservedRateFirstContribution,
    double ObservedSharedShapleyContribution,
    double ObservedSharedLogContribution,
    double CurrentProductionDelta,
    double NominalSharedLogDelta,
    double ObservedPercentageFirstDelta,
    double ObservedRateFirstDelta,
    double ObservedSharedShapleyDelta,
    double ObservedSharedLogDelta,
    int EventCount,
    int DirectEventCount,
    int PeriodicEventCount,
    int RateOverlapEventCount,
    int StateDifferenceEventCount,
    int GuaranteedCriticalEventCount,
    int GuaranteedDirectHitEventCount,
    int GuaranteedCriticalDirectHitEventCount,
    long EligibleDamage,
    long RateOverlapDamage,
    string RateComposition,
    string OverlapGroups,
    string SourceCache);

internal sealed record PercentageOrderingStatisticsRow(
    string Scope,
    string Value,
    string Candidate,
    MatrixResidualStatistics Statistics);

internal sealed record PercentageProviderRateComparisonRow(
    string Provider,
    string Buff,
    int NoRateCount,
    MatrixResidualStatistics NoRate,
    int RateOverlapCount,
    MatrixResidualStatistics RateOverlap);

internal sealed record PercentageMatchedControlRow(
    string Actor,
    string Job,
    string Report,
    int FightId,
    string Encounter,
    long ActionId,
    string ActionName,
    string PercentageComposition,
    int PercentageOnlyEvents,
    int CriticalOnlyEvents,
    int DirectOnlyEvents,
    int CriticalAndDirectEvents,
    double MeanPercentageOnlyPerDamage,
    double MeanCriticalOnlyPerDamage,
    double MeanDirectOnlyPerDamage,
    double MeanCriticalAndDirectPerDamage,
    string Quality,
    string ReferenceAvailability);

internal sealed record StatusExpiryAuditRow(
    string Report,
    int FightId,
    long StatusId,
    string Buff,
    string Provider,
    string Recipient,
    double Start,
    double NominalEnd,
    double ObservedEnd,
    long ObservedEndSequence,
    bool ExplicitRemove,
    bool RefreshOrOverwrite,
    bool ClearedByDeath,
    int DamageBetweenNominalAndObserved,
    long RawDamageBetweenNominalAndObserved,
    int SameTimestampApplyBeforeStatus,
    int SameTimestampApplyAfterStatus,
    int SameTimestampRemoveBeforeStatus,
    int SameTimestampRemoveAfterStatus,
    string Evidence);

internal sealed record TechnicalEligibilityAuditRow(
    string Report,
    int FightId,
    int ProviderActorId,
    int RecipientActorId,
    double ApplyTimestamp,
    double NominalEnd,
    double ObservedEnd,
    int EventsCurrentOnly,
    long DamageCurrentOnly,
    int EventsObservedOnly,
    long DamageObservedOnly,
    int SameTimestampApplyBeforeStatus,
    int SameTimestampApplyAfterStatus,
    int SameTimestampRemoveBeforeStatus,
    int SameTimestampRemoveAfterStatus,
    string AggregateReferenceAvailability);

internal sealed record PercentageRateConservationRow(
    string Report,
    int FightId,
    string Encounter,
    int RecipientActorId,
    string RecipientActor,
    string RecipientJob,
    double FflogsPercentageTaken,
    double FflogsRateTaken,
    double FflogsCombinedTaken,
    double ProductionPercentageReceived,
    double ProductionCriticalReceived,
    double ProductionDirectHitReceived,
    double ProductionCombinedReceived,
    double ProductionCombinedDelta,
    double ObservedPercentageFirst,
    double ObservedRateFirst,
    double ObservedSharedShapley,
    double ObservedSharedLog,
    double ObservedRateCurrentEquation,
    double ObservedPercentageFirstCombinedDelta,
    double ObservedRateFirstCombinedDelta,
    double ObservedSharedShapleyCombinedDelta,
    double ObservedSharedLogCombinedDelta);

internal sealed record PercentageOrderingReport(
    DateTimeOffset GeneratedAt,
    int FightCount,
    int ConstraintCount,
    int RateOverlapConstraintCount,
    int RateOverlapEventCount,
    IReadOnlyList<PercentageOrderingCandidateDefinition> Candidates,
    IReadOnlyList<string> ProductionPipeline,
    MatrixResidualStatistics ProductionPercentageCalibration,
    MatrixResidualStatistics ProductionCriticalCalibration,
    MatrixResidualStatistics ProductionDirectHitCalibration,
    IReadOnlyList<PercentageOrderingConstraintRow> Constraints,
    IReadOnlyList<RateOverlapEventProbeRow> RateOverlapEvents,
    IReadOnlyList<PercentageOrderingStatisticsRow> Statistics,
    IReadOnlyList<PercentageProviderRateComparisonRow> ProviderRateComparison,
    IReadOnlyList<PercentageMatchedControlRow> MatchedControls,
    IReadOnlyList<StatusExpiryAuditRow> ExpiryAudit,
    IReadOnlyList<TechnicalEligibilityAuditRow> TechnicalAudit,
    IReadOnlyList<PercentageRateConservationRow> Conservation,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> EvidenceBoundaries);

internal sealed record PercentageOrderingReportPaths(
    string JsonPath,
    string MarkdownPath,
    string ConstraintsCsvPath,
    string EventsCsvPath,
    string StatisticsCsvPath,
    string MatchedControlsCsvPath,
    string ExpiryCsvPath,
    string TechnicalCsvPath,
    string ConservationCsvPath);
