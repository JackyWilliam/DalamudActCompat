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
        var actorToken = root.GetValue("ActorID", StringComparison.OrdinalIgnoreCase);
        var actorName = root.GetValue("Name", StringComparison.OrdinalIgnoreCase)
            ?.Value<string>()
            ?.Trim();
        if (actorToken is null && string.IsNullOrWhiteSpace(actorName))
        {
            throw new InvalidDataException(
                "PostNamazu mark payload requires ActorID or Name.");
        }

        var markerToken = root.GetValue("MarkType", StringComparison.OrdinalIgnoreCase)
                          ?? throw new InvalidDataException(
                              "PostNamazu mark payload requires MarkType.");
        uint? actorId = actorToken is null ? null : ParseActorId(actorToken);
        var markerIndex = ParseMarkerIndex(markerToken);
        var localOnly = root.GetValue("LocalOnly", StringComparison.OrdinalIgnoreCase)
                            ?.Value<bool?>() ?? false;
        return new PostNamazuMarkAction(actorId, actorName, markerIndex, localOnly);
    }

    internal static PostNamazuWaymarkAction ParseWaymarks(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        var command = payload.Trim();
        if (string.Equals(command, "clear", StringComparison.OrdinalIgnoreCase))
        {
            return new PostNamazuWaymarkAction(
                PostNamazuWaymarkOperation.ClearLocal,
                true,
                []);
        }

        if (string.Equals(command, "save", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "backup", StringComparison.OrdinalIgnoreCase))
        {
            return new PostNamazuWaymarkAction(PostNamazuWaymarkOperation.Save, true, []);
        }

        if (string.Equals(command, "load", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "restore", StringComparison.OrdinalIgnoreCase))
        {
            return new PostNamazuWaymarkAction(PostNamazuWaymarkOperation.Load, true, []);
        }

        if (string.Equals(command, "reset", StringComparison.OrdinalIgnoreCase))
        {
            return new PostNamazuWaymarkAction(PostNamazuWaymarkOperation.Reset, true, []);
        }

        if (string.Equals(command, "public", StringComparison.OrdinalIgnoreCase))
        {
            return new PostNamazuWaymarkAction(PostNamazuWaymarkOperation.Publicize, false, []);
        }

        var root = ParseObject(payload);
        var localOnly = root.GetValue("LocalOnly", StringComparison.OrdinalIgnoreCase)
                            ?.Value<bool?>() ?? true;
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

        return new PostNamazuWaymarkAction(
            PostNamazuWaymarkOperation.Apply,
            localOnly,
            updates);
    }

    internal static PostNamazuPresetAction ParsePreset(string payload)
    {
        var root = ParseObject(payload);
        var slotName = root.GetValue("Name", StringComparison.OrdinalIgnoreCase)
            ?.Value<string>()
            ?.Trim();
        var slot = 1;
        if (!string.IsNullOrWhiteSpace(slotName) &&
            slotName.StartsWith("Slot", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(slotName[4..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed is >= 1 and <= 30)
        {
            slot = parsed;
        }

        var mapIdToken = root.GetValue("MapID", StringComparison.OrdinalIgnoreCase);
        var mapId = mapIdToken?.Value<ushort?>() ?? 0;
        var markers = new List<PostNamazuPresetMarker>(FieldMarkerNames.Length);
        foreach (var (name, index) in FieldMarkerNames)
        {
            var token = root.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token is not JObject marker)
            {
                markers.Add(new PostNamazuPresetMarker(index, false, Vector3.Zero));
                continue;
            }

            var active = marker.GetValue("Active", StringComparison.OrdinalIgnoreCase)
                             ?.Value<bool?>() ?? false;
            var position = active
                ? new Vector3(
                    ParseCoordinate(marker, "X"),
                    ParseCoordinate(marker, "Y"),
                    ParseCoordinate(marker, "Z"))
                : Vector3.Zero;
            markers.Add(new PostNamazuPresetMarker(index, active, position));
        }

        return new PostNamazuPresetAction(slot, mapId, markers);
    }

    internal static int ParseKeyCode(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        return int.TryParse(payload.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var keyCode)
            ? keyCode
            : throw new InvalidDataException("PostNamazu sendkey payload is not a valid key code.");
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

internal sealed record PostNamazuMarkAction(
    uint? ActorId,
    string? ActorName,
    int MarkerIndex,
    bool LocalOnly);

internal enum PostNamazuWaymarkOperation
{
    Apply,
    ClearLocal,
    Save,
    Load,
    Reset,
    Publicize,
}

internal sealed record PostNamazuWaymarkAction(
    PostNamazuWaymarkOperation Operation,
    bool LocalOnly,
    IReadOnlyList<PostNamazuWaymarkUpdate> Updates);

internal sealed record PostNamazuWaymarkUpdate(int Index, bool Active, Vector3 Position);

internal sealed record PostNamazuPresetAction(
    int Slot,
    ushort MapId,
    IReadOnlyList<PostNamazuPresetMarker> Markers);

internal sealed record PostNamazuPresetMarker(int Index, bool Active, Vector3 Position);
