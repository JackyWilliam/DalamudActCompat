using System.Text.Json;
using System.Text.Json.Serialization;

namespace DalamudActCompat.ActRuntime.Parity;

internal static class FflogsParityReplay
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ParityRawReplayFixture ReadRawFixture(string path)
        => JsonSerializer.Deserialize<ParityRawReplayFixture>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidDataException($"Raw parity fixture '{path}' is empty.");

    public static ParityReplayFixture ReadNormalizedFixture(string path)
        => JsonSerializer.Deserialize<ParityReplayFixture>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidDataException($"Normalized parity fixture '{path}' is empty.");

    public static ParityReconciliationReplayFixture ReadReconciliationFixture(string path)
        => JsonSerializer.Deserialize<ParityReconciliationReplayFixture>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidDataException($"Reconciliation parity fixture '{path}' is empty.");

    public static FflogsParityDiagnostic ReplayRaw(ParityRawReplayFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var normalizer = new FflogsParityRawNormalizer(fixture.PartyActorIds);
        var events = normalizer.Normalize(fixture.RawLines);
        return AnalyzeFixture(
            fixture.Name,
            fixture.Zone,
            fixture.EncounterName,
            fixture.PartyActorIds,
            events,
            events);
    }

    public static FflogsParityDiagnostic ReplayNormalized(ParityReplayFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return AnalyzeFixture(
            fixture.Name,
            fixture.Zone,
            fixture.EncounterName,
            fixture.PartyActorIds,
            fixture.Events,
            []);
    }

    public static FflogsParityDiagnostic ReplayReconciliation(
        ParityReconciliationReplayFixture fixture,
        string fixtureDirectory)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureDirectory);
        var normalizer = new FflogsParityRawNormalizer(fixture.PartyActorIds);
        var rawEvents = normalizer.Normalize(fixture.RawLines);
        var reference = string.IsNullOrWhiteSpace(fixture.ReferencePath)
            ? null
            : ParityReferenceFightImporter.Read(Path.Combine(fixtureDirectory, fixture.ReferencePath));
        return AnalyzeFixture(
            fixture.Name,
            fixture.Zone,
            fixture.EncounterName,
            fixture.PartyActorIds,
            fixture.NormalizedEvents,
            rawEvents,
            reference);
    }

    private static FflogsParityDiagnostic AnalyzeFixture(
        string fixtureName,
        string zone,
        string encounterName,
        IReadOnlyList<string> partyActorIds,
        IReadOnlyList<ParityReplayEvent> normalizedEvents,
        IReadOnlyList<ParityReplayEvent> rawEvents,
        ParityReferenceFight? reference = null)
    {
        var boundaries = normalizedEvents
            .Where(static item => item.Kind == ParityReplayEventKind.EncounterBoundary)
            .OrderBy(static item => item.Timestamp)
            .ToArray();
        var damageEvents = normalizedEvents
            .Where(static item => item.Kind == ParityReplayEventKind.Damage && item.IsDamageSwing)
            .OrderBy(static item => item.Timestamp)
            .ToArray();
        var start = boundaries.FirstOrDefault(item =>
                        string.Equals(item.Evidence, "start", StringComparison.OrdinalIgnoreCase))?.Timestamp ??
                    damageEvents.FirstOrDefault()?.Timestamp;
        var end = boundaries.LastOrDefault(item =>
                      string.Equals(item.Evidence, "end", StringComparison.OrdinalIgnoreCase))?.Timestamp ??
                  damageEvents.LastOrDefault()?.Timestamp;
        var party = partyActorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capture = new ParityCaptureHealth(
            0,
            0,
            false,
            false,
            rawEvents.Count(static item => item.Kind == ParityReplayEventKind.Damage),
            normalizedEvents.Count(static item => item.Kind == ParityReplayEventKind.Damage),
            "fixture",
            "fixture");
        return FflogsParityAnalyzer.Analyze(
            CreateStableFixtureId(fixtureName),
            zone,
            encounterName,
            start,
            end,
            normalizedEvents,
            rawEvents,
            party,
            capture,
            referenceFight: reference);
    }

    private static Guid CreateStableFixtureId(string fixtureName)
    {
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(fixtureName));
        return new Guid(hash);
    }
}
