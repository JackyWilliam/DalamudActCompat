using System.Globalization;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace DalamudActCompat.ActRuntime;

internal static class PostNamazuSemanticActions
{
    private const uint InvalidActorId = 0xE0000000;
    private static readonly IReadOnlyDictionary<string, int> MarkerIndexes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["attack1"] = 0,
            ["attack2"] = 1,
            ["attack3"] = 2,
            ["attack4"] = 3,
            ["attack5"] = 4,
            ["bind1"] = 5,
            ["bind2"] = 6,
            ["bind3"] = 7,
            ["stop1"] = 8,
            ["stop2"] = 9,
            ["square"] = 10,
            ["circle"] = 11,
            ["cross"] = 12,
            ["triangle"] = 13,
            ["attack6"] = 14,
            ["attack7"] = 15,
            ["attack8"] = 16,
        };
    private static readonly (string Name, int Index)[] FieldMarkerNames =
    [
        ("A", 0),
        ("B", 1),
        ("C", 2),
        ("D", 3),
        ("One", 4),
        ("Two", 5),
        ("Three", 6),
        ("Four", 7),
    ];

    internal static PostNamazuMarkAction ParseMark(string payload)
    {
        var root = ParseObject(payload);
        var actorToken = root.GetValue("ActorID", StringComparison.OrdinalIgnoreCase)
                         ?? throw new InvalidDataException(
                             "PostNamazu mark payload requires ActorID; name-only marking is not brokered.");
        var markerToken = root.GetValue("MarkType", StringComparison.OrdinalIgnoreCase)
                          ?? throw new InvalidDataException(
                              "PostNamazu mark payload requires MarkType.");
        var actorId = ParseActorId(actorToken);
        var markerIndex = ParseMarkerIndex(markerToken);
        var localOnly = root.GetValue("LocalOnly", StringComparison.OrdinalIgnoreCase)
                            ?.Value<bool?>() ?? false;
        return new PostNamazuMarkAction(actorId, markerIndex, localOnly);
    }

    internal static PostNamazuWaymarkAction ParseWaymarks(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (string.Equals(payload.Trim(), "clear", StringComparison.OrdinalIgnoreCase))
        {
            return new PostNamazuWaymarkAction(false, true, []);
        }

        var root = ParseObject(payload);
        var localOnly = root.GetValue("LocalOnly", StringComparison.OrdinalIgnoreCase)
                            ?.Value<bool?>() ?? false;
        var updates = new List<PostNamazuWaymarkUpdate>();
        foreach (var (name, index) in FieldMarkerNames)
        {
            var token = root.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token is null || token.Type == JTokenType.Null)
            {
                continue;
            }

            if (token is not JObject marker)
            {
                throw new InvalidDataException(
                    $"PostNamazu waymark '{name}' must be a JSON object.");
            }

            var active = marker.GetValue("Active", StringComparison.OrdinalIgnoreCase)
                             ?.Value<bool?>() ?? false;
            var position = active
                ? new Vector3(
                    ParseCoordinate(marker, "X"),
                    ParseCoordinate(marker, "Y"),
                    ParseCoordinate(marker, "Z"))
                : Vector3.Zero;
            updates.Add(new PostNamazuWaymarkUpdate(index, active, position));
        }

        if (updates.Count == 0)
        {
            throw new InvalidDataException(
                "PostNamazu waymark payload contains no A-D or One-Four marker updates.");
        }

        return new PostNamazuWaymarkAction(localOnly, false, updates);
    }

    private static JObject ParseObject(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (payload.Length > 32_768)
        {
            throw new InvalidDataException(
                "PostNamazu semantic payload exceeds 32768 characters.");
        }

        try
        {
            return JObject.Parse(payload);
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or FormatException)
        {
            throw new InvalidDataException("PostNamazu semantic payload is not valid JSON.", ex);
        }
    }

    private static uint ParseActorId(JToken token)
    {
        if (token.Type == JTokenType.Integer)
        {
            var value = token.Value<ulong>();
            return value <= uint.MaxValue
                ? (uint)value
                : throw new InvalidDataException("PostNamazu ActorID exceeds UInt32.");
        }

        var text = token.Value<string>()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidDataException("PostNamazu ActorID is empty.");
        }

        var style = NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            style = NumberStyles.AllowHexSpecifier;
        }

        return uint.TryParse(text, style, CultureInfo.InvariantCulture, out var actorId)
            ? actorId
            : throw new InvalidDataException("PostNamazu ActorID is invalid.");
    }

    private static int ParseMarkerIndex(JToken token)
    {
        if (token.Type == JTokenType.Integer)
        {
            var value = token.Value<int>();
            if (value is >= 1 and <= 17)
            {
                return value - 1;
            }
        }

        var name = token.Value<string>()?.Trim();
        if (!string.IsNullOrEmpty(name) && MarkerIndexes.TryGetValue(name, out var markerIndex))
        {
            return markerIndex;
        }

        throw new InvalidDataException("PostNamazu MarkType is outside the supported 17 markers.");
    }

    private static float ParseCoordinate(JObject marker, string name)
    {
        var token = marker.GetValue(name, StringComparison.OrdinalIgnoreCase)
                    ?? throw new InvalidDataException(
                        $"Active PostNamazu waymark requires coordinate {name}.");
        var value = token.Value<float>();
        if (!float.IsFinite(value) || Math.Abs(value) > 100_000)
        {
            throw new InvalidDataException(
                $"PostNamazu waymark coordinate {name} is outside the safe range.");
        }

        return value;
    }

    internal const uint ClearActorId = InvalidActorId;
}

internal sealed record PostNamazuMarkAction(uint ActorId, int MarkerIndex, bool LocalOnly);

internal sealed record PostNamazuWaymarkAction(
    bool LocalOnly,
    bool ClearAll,
    IReadOnlyList<PostNamazuWaymarkUpdate> Updates);

internal sealed record PostNamazuWaymarkUpdate(int Index, bool Active, Vector3 Position);
