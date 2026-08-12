using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.ActRuntime.Parity;

/// <summary>
/// Developer-only import boundary for exact reference events. Normal runtime
/// calculations never call FFLogs or load this model.
/// </summary>
internal static class ParityReferenceFightImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ParityReferenceFight Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => ReadJson(path),
            ".csv" => ReadCsv(path),
            _ => throw new NotSupportedException(
                $"Reference fight '{path}' must use the Phase 1A JSON or CSV schema."),
        };
    }

    public static ParityReferenceFight ReadJson(string path)
        => JsonSerializer.Deserialize<ParityReferenceFight>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidDataException($"Reference fight '{path}' is empty.");

    public static ParityReferenceFight ReadCsv(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            throw new InvalidDataException($"Reference CSV '{path}' has no header.");
        }

        var headers = ParseCsvRow(headerLine);
        var events = new List<ParityReferenceDamageEvent>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var values = ParseCsvRow(line);
            var row = headers
                .Select((header, index) => new { header, value = index < values.Count ? values[index] : string.Empty })
                .ToDictionary(static item => item.header, static item => item.value, StringComparer.OrdinalIgnoreCase);
            events.Add(ParseDamageEvent(row, events.Count + 1, path));
        }

        var ordered = events.OrderBy(static item => item.Timestamp).ThenBy(static item => item.Sequence).ToArray();
        return new ParityReferenceFight(
            "1.0",
            "developer-reference-csv",
            new ParityReferenceFightInfo(
                string.Empty,
                Path.GetFileNameWithoutExtension(path),
                string.Empty,
                string.Empty,
                ordered.FirstOrDefault()?.Timestamp,
                ordered.LastOrDefault()?.Timestamp),
            BuildEntities(ordered, source: true),
            BuildEntities(ordered, source: false),
            ordered);
    }

    private static ParityReferenceDamageEvent ParseDamageEvent(
        IReadOnlyDictionary<string, string> row,
        long fallbackSequence,
        string path)
    {
        if (!DateTimeOffset.TryParse(
                Get(row, "timestamp"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp) ||
            !long.TryParse(Get(row, "amount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
        {
            throw new InvalidDataException(
                $"Reference CSV '{path}' requires an ISO-8601 timestamp and integer amount on every row.");
        }

        return new ParityReferenceDamageEvent(
            ParseLong(row, "sequence") ?? fallbackSequence,
            timestamp,
            Get(row, "sourceId"),
            ParseInt(row, "sourceInstanceId"),
            Get(row, "sourceName"),
            Get(row, "targetId"),
            ParseInt(row, "targetInstanceId"),
            Get(row, "targetName"),
            Get(row, "abilityId"),
            Get(row, "abilityName"),
            amount,
            ParseLong(row, "overkill"),
            ParseLong(row, "absorbed"),
            ParseBool(row, "critical"),
            ParseBool(row, "directHit"),
            Get(row, "packetId"));
    }

    private static IReadOnlyList<ParityReferenceEntity> BuildEntities(
        IEnumerable<ParityReferenceDamageEvent> events,
        bool source)
        => events
            .Select(item => source
                ? new ParityReferenceEntity(
                    item.SourceId,
                    item.SourceInstanceId,
                    item.SourceName,
                    "actor",
                    string.Empty)
                : new ParityReferenceEntity(
                    item.TargetId,
                    item.TargetInstanceId,
                    item.TargetName,
                    "target",
                    string.Empty))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id) || !string.IsNullOrWhiteSpace(item.Name))
            .DistinctBy(static item => (item.Id.ToUpperInvariant(), item.InstanceId, item.Name.ToUpperInvariant()))
            .ToArray();

    private static IReadOnlyList<string> ParseCsvRow(string line)
    {
        var values = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var value = line[index];
            if (value == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (value == ',' && !quoted)
            {
                values.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(value);
            }
        }
        if (quoted)
        {
            throw new InvalidDataException("Reference CSV contains an unterminated quoted field.");
        }
        values.Add(field.ToString());
        return values;
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static long? ParseLong(IReadOnlyDictionary<string, string> row, string key)
        => long.TryParse(Get(row, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int? ParseInt(IReadOnlyDictionary<string, string> row, string key)
        => int.TryParse(Get(row, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool ParseBool(IReadOnlyDictionary<string, string> row, string key)
    {
        var value = Get(row, key);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               bool.TryParse(value, out var parsed) && parsed;
    }
}
