using System.Text.Json;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Quality;

public sealed record CombatQualitySnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset LastRun,
    string ModelIdentifier,
    string StatusEngine,
    int Samples,
    double MeanDelta,
    double MedianDelta,
    double Mae,
    double P90AbsoluteDelta,
    double P95AbsoluteDelta,
    double MaxAbsoluteDelta,
    int RawParityExactCount,
    int RawParitySampleCount,
    int DirectPacketMatched,
    int DirectPacketExpected,
    int NormalizationWarnings,
    string LatestReport)
{
    public static CombatQualitySnapshot? Load(string path, PluginLogger logger)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<CombatQualitySnapshot>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                : null;
        }
        catch (Exception ex)
        {
            // Quality metadata must never prevent the meter from loading; the panel
            // reports unavailable data while the generated artifact can be repaired.
            logger.Warning($"Combat quality snapshot could not be loaded: {ex.Message}");
            return null;
        }
    }
}
