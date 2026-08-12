namespace DalamudActCompat.ActRuntime.Parity;

internal enum ParityReplayEventKind
{
    Actor,
    Damage,
    Targetability,
    Death,
    EncounterBoundary,
}

internal enum ParityDamageKind
{
    Direct,
    AutoAttack,
    DamageOverTime,
    LimitBreak,
    DamageShield,
    Environment,
    Unknown,
}

internal enum ParityLedgerDecision
{
    Included,
    Excluded,
}

internal enum ParityExclusionReason
{
    None,
    NotDamageSwing,
    NonPositiveDamage,
    NonPartySource,
    MissingEncounter,
    UnresolvedActor,
}

internal enum ParityReferencePrecision
{
    Exact,
    Rounded,
    Inferred,
    Unknown,
}

/// <summary>
/// Canonical replay event shared by raw-log and normalized-event fixtures.
/// Optional fields are deliberate: keeping one stable wire shape makes fixtures
/// reviewable without coupling them to ACT or FFXIV_ACT_Plugin runtime types.
/// </summary>
internal sealed record ParityReplayEvent
{
    public required ParityReplayEventKind Kind { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public long Sequence { get; init; }

    public string SourceId { get; init; } = string.Empty;

    public string SourceName { get; init; } = string.Empty;

    public string Job { get; init; } = string.Empty;

    public string OwnerId { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public string TargetName { get; init; } = string.Empty;

    public string AbilityId { get; init; } = string.Empty;

    public string AbilityName { get; init; } = string.Empty;

    public long Amount { get; init; }

    public long? TargetCurrentHp { get; init; }

    public bool Critical { get; init; }

    public bool DirectHit { get; init; }

    public bool Targetable { get; init; }

    public bool IsPartyMember { get; init; }

    public bool IsDamageSwing { get; init; }

    public bool HasEncounter { get; init; } = true;

    public ParityDamageKind DamageKind { get; init; } = ParityDamageKind.Unknown;

    public string RawLineType { get; init; } = string.Empty;

    public string PacketId { get; init; } = string.Empty;

    public string Evidence { get; init; } = string.Empty;
}

internal sealed record ParityActorDiagnostic(
    string ActorId,
    string Name,
    string Job,
    string OwnerId,
    bool IsPartyMember,
    long IncludedDamage,
    long ExcludedDamage,
    int DamageHits,
    int CriticalHits,
    int DirectHits,
    int CriticalDirectHits,
    double CriticalRate,
    double DirectHitRate,
    double CriticalDirectRate,
    double ActorActiveTimeSeconds,
    string ActorActiveTimeMethod);

internal sealed record ParityAttributionDiagnostic(
    string ActorId,
    IReadOnlyList<string> OutgoingBuffs,
    IReadOnlyList<string> IncomingBuffs,
    IReadOnlyList<string> BuffWindows,
    double? AttributedGain,
    double? AttributedLoss,
    double? CalculatedRdps,
    double? CalculatedAdps,
    double? CalculatedNdps,
    string Status);

internal sealed record ParityDamageLedgerEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string SourceId,
    string SourceName,
    string OwnerId,
    string OwnerName,
    string TargetId,
    string TargetName,
    string AbilityId,
    string AbilityName,
    long Amount,
    long? EstimatedOverkill,
    ParityDamageKind DamageKind,
    bool Critical,
    bool DirectHit,
    ParityLedgerDecision Decision,
    ParityExclusionReason ExclusionReason,
    string RawLineType,
    string PacketId,
    string Evidence);

internal sealed record ParityDowntimeInterval(
    DateTimeOffset Start,
    DateTimeOffset End,
    double DurationSeconds,
    string TargetId,
    string TargetName,
    string Phase,
    string Measurement,
    string Evidence);

internal sealed record ParityDurationDiagnostic(
    DateTimeOffset? FightStart,
    DateTimeOffset? FightEnd,
    double FightDurationSeconds,
    DateTimeOffset? DamageMetricStart,
    DateTimeOffset? DamageMetricEnd,
    double DamageMetricWallSeconds,
    double CurrentUnionDowntimeSeconds,
    double AllTargetsUnavailableSeconds,
    double CurrentDamageMetricDurationSeconds,
    double CandidateDamageMetricDurationSeconds,
    string FightDurationMethod,
    string DamageMetricDurationMethod);

internal sealed record ParityCaptureHealth(
    long DroppedRawLogLines,
    long DroppedNormalizedActions,
    bool LedgerTruncated,
    bool PartialCapture,
    int CapturedRawDamageEvents,
    int CapturedNormalizedActions,
    string ParserAssemblyVersion,
    string ParserAssemblySha256);

internal sealed record ParityReferenceActor(
    string Name,
    long? ExactDamage,
    double? RoundedDamageMillions,
    double? DisplayedDps,
    double? ActivePercent);

internal sealed record ParityReferenceSnapshot(
    string Source,
    string ReportCode,
    string FightId,
    double? FightDurationSeconds,
    long? ExactTotalDamage,
    double? RoundedTotalDamageMillions,
    double? DisplayedTotalDps,
    ParityReferencePrecision Precision,
    IReadOnlyList<ParityReferenceActor> Actors);

internal sealed record ParityDeltaBreakdown(
    string Category,
    long LocalAmount,
    long? ReferenceAmount,
    long? Delta,
    long? MinimumDelta,
    long? MaximumDelta,
    ParityReferencePrecision Precision,
    string Evidence);

internal sealed record FflogsParityDiagnostic(
    string SchemaVersion,
    Guid EncounterId,
    string Zone,
    string EncounterName,
    ParityDurationDiagnostic Durations,
    long IncludedDamage,
    long ExcludedDamage,
    long RawNetworkDamage,
    long ParserNormalizationDelta,
    IReadOnlyList<ParityActorDiagnostic> Actors,
    IReadOnlyList<ParityAttributionDiagnostic> Attribution,
    IReadOnlyList<ParityDamageLedgerEntry> DamageLedger,
    IReadOnlyList<ParityDowntimeInterval> DowntimeIntervals,
    IReadOnlyList<ParityDeltaBreakdown> DeltaBreakdown,
    ParityCaptureHealth CaptureHealth,
    ParityReferenceSnapshot? Reference,
    IReadOnlyList<string> Unknowns);

internal sealed record ParityReplayFixture(
    string SchemaVersion,
    string Name,
    string Layer,
    string Zone,
    string EncounterName,
    IReadOnlyList<string> PartyActorIds,
    IReadOnlyList<ParityReplayEvent> Events,
    long ExpectedIncludedDamage,
    double ExpectedCurrentDamageMetricDurationSeconds,
    string Notes);

internal sealed record ParityRawReplayFixture(
    string SchemaVersion,
    string Name,
    string Layer,
    string Zone,
    string EncounterName,
    IReadOnlyList<string> PartyActorIds,
    IReadOnlyList<string> RawLines,
    long ExpectedIncludedDamage,
    double ExpectedCurrentDamageMetricDurationSeconds,
    string Notes);
