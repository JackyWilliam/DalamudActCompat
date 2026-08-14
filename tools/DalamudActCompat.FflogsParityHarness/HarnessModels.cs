namespace DalamudActCompat.FflogsParityHarness;

// Ranking metrics are retained for discovery provenance only. FFLogs DamageDone
// table totals, parsed later, are the parity reference for the actual fight.
internal sealed record RankingSeed(
    string ReportCode,
    int FightId,
    int EncounterId,
    string EncounterName,
    string ActorName,
    double DurationMilliseconds,
    double FflogsDps,
    double FflogsRdps,
    double? FflogsAdps,
    double? FflogsNdps,
    int SourcePage,
    int SourceRank);

internal sealed record FflogsActor(
    int Id,
    long GameId,
    string Name,
    string Type,
    string Job,
    int? PetOwnerId);

internal sealed record FflogsFight(
    int Id,
    int EncounterId,
    string Name,
    double StartTime,
    double EndTime,
    double CombatTime,
    bool Kill,
    int Difficulty);

internal sealed record NormalizedFflogsEvent(
    long Sequence,
    // DACT attributes direct damage at the action packet timestamp, not at the later HP result.
    double Timestamp,
    double DamageTimestamp,
    string Type,
    int SourceId,
    int TargetId,
    long ApiAbilityId,
    long AbilityId,
    string AbilityName,
    long Amount,
    double DurationMilliseconds,
    long ExtraAbilityId,
    int Stack,
    long Overkill,
    long Absorbed,
    bool Critical,
    bool DirectHit,
    bool IsPeriodic,
    int? SourceInstance,
    int? TargetInstance,
    long? PacketId,
    bool MatchedCalculatedDamage);

internal sealed record FflogsContribution(long AbilityId, string AbilityName, double Amount);

internal sealed record FflogsDamageTableMetrics(
    double DurationMilliseconds,
    double RawDamage,
    double RdpsTotal,
    double ExternalBuffContributionReceived,
    double OwnBuffContributionGiven,
    double AdpsTotal,
    double NdpsTotal,
    IReadOnlyList<FflogsContribution> Given,
    IReadOnlyList<FflogsContribution> Taken);

/// <summary>
/// This is the explicit API-to-DACT boundary. Reference metrics travel beside the
/// normalized events but are never injected into the production estimator.
/// </summary>
internal sealed record NormalizedFight(
    RankingSeed Seed,
    long ReportStartTime,
    FflogsFight Fight,
    FflogsActor Dancer,
    IReadOnlyList<FflogsActor> Party,
    IReadOnlyDictionary<int, FflogsActor> Actors,
    IReadOnlyList<NormalizedFflogsEvent> Events,
    long DamageTableTotal,
    long DancerDamageTableTotal,
    FflogsDamageTableMetrics FflogsMetrics,
    string PartyComposition,
    string DancePartnerJob,
    bool TechnicalFinishPresent,
    bool StandardFinishPresent,
    bool DevilmentPresent,
    bool MultiRaidBuffOverlap,
    int MaximumRaidBuffOverlap,
    int DeathCount,
    int ResurrectionCount,
    bool HasPetJob,
    string PetJobs,
    bool HasDotJob,
    string DotJobs,
    double FflogsMetricDurationSeconds,
    double WallDurationSeconds,
    double CombatDurationSeconds,
    double DowntimeSeconds,
    int TechnicalFinishRankResolvedCount,
    IReadOnlyList<string> NormalizationWarnings);

internal sealed record ParitySampleResult
{
    public required string Report { get; init; }

    public required int FightId { get; init; }

    public required int ActorId { get; init; }

    public required string Actor { get; init; }

    public required string Encounter { get; init; }

    public required int EncounterId { get; init; }

    public required double Duration { get; init; }

    public required string DurationSource { get; init; }

    public required string PartyComposition { get; init; }

    public required double FflogsDps { get; init; }

    public required double FflogsRdps { get; init; }

    public double? FflogsAdps { get; init; }

    public double? FflogsNdps { get; init; }

    public required double RankingPdps { get; init; }

    public required double RankingRdps { get; init; }

    public double? RankingAdps { get; init; }

    public double? RankingNdps { get; init; }

    public required double DactDps { get; init; }

    public required double DactRdps { get; init; }

    public required double DeltaRdps { get; init; }

    public required double DisplayDeltaRdps { get; init; }

    public required double DeltaPercent { get; init; }

    public required long RawDamage { get; init; }

    public required long FflogsDamageTableAmount { get; init; }

    public required long DamageNormalizationDelta { get; init; }

    public required double ExternalBuffContributionReceived { get; init; }

    public required double OwnBuffContributionGiven { get; init; }

    public required double FflogsExternalBuffContributionReceived { get; init; }

    public required double FflogsOwnBuffContributionGiven { get; init; }

    public required IReadOnlyList<FflogsContribution> FflogsGivenBreakdown { get; init; }

    public required IReadOnlyList<FflogsContribution> FflogsTakenBreakdown { get; init; }

    public required double ExternalBuffContributionReceivedDelta { get; init; }

    public required double OwnBuffContributionGivenDelta { get; init; }

    public double? TechnicalFinishContribution { get; init; }

    public double? StandardFinishContribution { get; init; }

    public required double TechnicalAndStandardContribution { get; init; }

    public required double DevilmentContribution { get; init; }

    public required double FflogsTechnicalFinishContribution { get; init; }

    public required double FflogsStandardFinishContribution { get; init; }

    public required double FflogsTechnicalAndStandardContribution { get; init; }

    public required double FflogsDevilmentContribution { get; init; }

    public required double TechnicalAndStandardContributionDelta { get; init; }

    public required double DevilmentContributionDelta { get; init; }

    public required double CritContributionReceived { get; init; }

    public required double DirectHitContributionReceived { get; init; }

    public required double CritDirectHitContributionReceived { get; init; }

    public required double CritContributionGiven { get; init; }

    public required double DirectHitContributionGiven { get; init; }

    public required double CritDirectHitContributionGiven { get; init; }

    public required double PercentageContributionReceived { get; init; }

    public required double PercentageContributionGiven { get; init; }

    public required bool TechnicalFinishPresent { get; init; }

    public required bool StandardFinishPresent { get; init; }

    public required bool DevilmentPresent { get; init; }

    public required bool MultiRaidBuffOverlap { get; init; }

    public required int MaximumRaidBuffOverlap { get; init; }

    public required string DancePartnerJob { get; init; }

    public required double WallDuration { get; init; }

    public required double Downtime { get; init; }

    public required int DeathCount { get; init; }

    public required int ResurrectionCount { get; init; }

    public required bool HasPetJob { get; init; }

    public required string PetJobs { get; init; }

    public required bool HasDotJob { get; init; }

    public required string DotJobs { get; init; }

    public required int DamageEventCount { get; init; }

    public required int StatusEventCount { get; init; }

    public required int MatchedCalculatedDamageCount { get; init; }

    public required int UnmatchedCalculatedDamageCount { get; init; }

    public required int UnmatchedDirectDamageCount { get; init; }

    public required int PeriodicDamageEventCount { get; init; }

    public required int TechnicalFinishRankResolvedCount { get; init; }

    public required IReadOnlyList<string> NormalizationWarnings { get; init; }
}

internal sealed record ParityStatistics(
    int SampleCount,
    int ExactMatchCount,
    int Within1,
    int Within10,
    int Within50,
    int Within100,
    int Within500,
    double MeanDelta,
    double MedianDelta,
    double MeanAbsoluteDelta,
    double MaxAbsoluteDelta,
    double MinimumDelta,
    double MaximumDelta);

internal sealed record ParityGroupStatistics(
    string Dimension,
    string Value,
    ParityStatistics Statistics);

internal sealed record ParityDeltaDistribution(
    int NegativeCount,
    int ZeroCount,
    int PositiveCount,
    double P10,
    double P25,
    double P50,
    double P75,
    double P90);

internal sealed record ParityContributionDeltaStatistics(
    string Component,
    double MeanDeltaDamage,
    double MedianDeltaDamage,
    double MeanAbsoluteDeltaDamage,
    double MaximumAbsoluteDeltaDamage,
    double MinimumDeltaDamage,
    double MaximumDeltaDamage);

internal sealed record ParityReport(
    DateTimeOffset GeneratedAt,
    string Scope,
    string DisplayPrecision,
    string DurationBoundary,
    ParityStatistics Overall,
    ParityDeltaDistribution DeltaDistribution,
    IReadOnlyList<ParityContributionDeltaStatistics> ContributionDeltas,
    IReadOnlyList<ParityGroupStatistics> Groups,
    IReadOnlyList<ParitySampleResult> TopLargestErrors,
    IReadOnlyList<ParitySampleResult> TopClosest,
    IReadOnlyList<ParitySampleResult> Samples,
    IReadOnlyList<string> KnownBoundaries);

internal sealed record CacheManifest(
    DateTimeOffset GeneratedAt,
    int RequestedSamples,
    IReadOnlyList<RankingSeed> Seeds,
    IReadOnlyList<string> Failures);

internal sealed record FflogsCredentials(string ClientId, string ClientSecret);
