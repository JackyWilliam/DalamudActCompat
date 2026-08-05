using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace DalamudActCompat.Overlay;

internal sealed partial class PictoActOverlayService(IGameGui gameGui)
{
    private const int CurveSegments = 72;
    private static readonly Vector4 DefaultColor = Vector4.One;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, StoredPictoActShape> shapes =
        new(StringComparer.OrdinalIgnoreCase);
    private long automaticTagSequence;

    internal int ShapeCount
    {
        get
        {
            lock (syncRoot)
            {
                return shapes.Count;
            }
        }
    }

    internal void Apply(string payload)
    {
        foreach (var command in Parse(payload))
        {
            lock (syncRoot)
            {
                if (command.Remove)
                {
                    RemoveMatching(command.Tag, command.Regex);
                    continue;
                }

                var semanticTag = string.IsNullOrWhiteSpace(command.Tag)
                    ? "Auto"
                    : command.Tag;
                var storageKey = string.IsNullOrWhiteSpace(command.Tag)
                    ? $"Auto:{Interlocked.Increment(ref automaticTagSequence)}"
                    : $"Tag:{command.Tag}";
                shapes[storageKey] = new StoredPictoActShape(semanticTag, command.Shape!);
            }
        }
    }

    private void RemoveMatching(string? tag, Regex? regex)
    {
        if (string.IsNullOrWhiteSpace(tag) && regex is null)
        {
            shapes.Clear();
            return;
        }

        foreach (var key in shapes
                     .Where(pair =>
                         !string.IsNullOrWhiteSpace(tag)
                            ? string.Equals(
                                pair.Value.SemanticTag,
                                tag,
                                StringComparison.OrdinalIgnoreCase)
                            : regex!.IsMatch(pair.Value.SemanticTag))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            shapes.Remove(key);
        }
    }

    internal void Draw()
    {
        PictoActShape[] snapshot;
        var now = DateTimeOffset.UtcNow;
        lock (syncRoot)
        {
            foreach (var expired in shapes
                         .Where(pair => pair.Value.Shape.ExpiresAt <= now)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                shapes.Remove(expired);
            }

            snapshot = shapes.Values
                .Select(value => value.Shape)
                .Where(shape => shape.StartsAt <= now)
                .ToArray();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        foreach (var shape in snapshot)
        {
            DrawWorldPath(drawList, BuildWorldPath(shape), shape.Color);
        }
    }

    private void DrawWorldPath(
        ImDrawListPtr drawList,
        IReadOnlyList<Vector3> worldPath,
        Vector4 colorValue)
    {
        Vector2? previous = null;
        var color = ImGui.ColorConvertFloat4ToU32(colorValue);
        foreach (var world in worldPath)
        {
            if (!gameGui.WorldToScreen(world, out var screen))
            {
                previous = null;
                continue;
            }

            if (previous is { } from)
            {
                drawList.AddLine(from, screen, color, 3f);
            }

            previous = screen;
        }
    }

    private static IReadOnlyList<Vector3> BuildWorldPath(PictoActShape shape)
        => shape.Kind switch
        {
            PictoActShapeKind.Circle => BuildCirclePath(shape),
            PictoActShapeKind.Rectangle => BuildRectanglePath(shape, bidirectional: false),
            PictoActShapeKind.BidirectionalRectangle =>
                BuildRectanglePath(shape, bidirectional: true),
            PictoActShapeKind.Fan => BuildFanPath(shape),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

    private static IReadOnlyList<Vector3> BuildCirclePath(PictoActShape shape)
    {
        var result = new Vector3[CurveSegments + 1];
        for (var index = 0; index <= CurveSegments; index++)
        {
            var angle = MathF.Tau * index / CurveSegments;
            result[index] = shape.Position + GroundOffset(angle, shape.PrimaryScale);
        }

        return result;
    }

    private static IReadOnlyList<Vector3> BuildRectanglePath(
        PictoActShape shape,
        bool bidirectional)
    {
        var forward = GroundOffset(shape.Angle, 1);
        var right = GroundOffset(shape.Angle + MathF.PI / 2, 1);
        var start = bidirectional
            ? shape.Position - forward * shape.SecondaryScale
            : shape.Position;
        var end = shape.Position + forward * shape.SecondaryScale;
        var halfWidth = shape.PrimaryScale;
        return
        [
            start - right * halfWidth,
            start + right * halfWidth,
            end + right * halfWidth,
            end - right * halfWidth,
            start - right * halfWidth,
        ];
    }

    private static IReadOnlyList<Vector3> BuildFanPath(PictoActShape shape)
    {
        var result = new List<Vector3>(CurveSegments + 3) { shape.Position };
        var segmentCount = Math.Max(
            8,
            (int)MathF.Ceiling(CurveSegments * shape.FanRadians / MathF.Tau));
        var start = shape.Angle - shape.FanRadians / 2;
        for (var index = 0; index <= segmentCount; index++)
        {
            var angle = start + shape.FanRadians * index / segmentCount;
            result.Add(shape.Position + GroundOffset(angle, shape.PrimaryScale));
        }

        result.Add(shape.Position);
        return result;
    }

    private static Vector3 GroundOffset(float angle, float distance)
        => new(
            MathF.Sin(angle) * distance,
            0,
            MathF.Cos(angle) * distance);

    internal static IReadOnlyList<PictoActOverlayCommand> Parse(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (payload.Length > 32_768)
        {
            throw new InvalidDataException("PictoACT payload exceeds 32768 characters.");
        }

        var commands = new List<PictoActOverlayCommand>();
        foreach (var segment in CommandSeparator().Split(payload))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var values = ParseFields(segment);
            var tag = Optional(values, "Tag");
            if (tag?.Length > 128)
            {
                throw new InvalidDataException("PictoACT Tag exceeds 128 characters.");
            }

            var regexText = Optional(values, "Regex");
            if (regexText?.Length > 512)
            {
                throw new InvalidDataException("PictoACT Regex exceeds 512 characters.");
            }

            var action = values.GetValueOrDefault("Action", "Create").Trim();
            if (string.Equals(action, "Remove", StringComparison.OrdinalIgnoreCase))
            {
                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexText))
                {
                    regex = new Regex(
                        regexText,
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                        TimeSpan.FromMilliseconds(100));
                }

                commands.Add(new PictoActOverlayCommand(tag, regex, true, null));
                continue;
            }

            if (!string.Equals(action, "Create", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"PictoACT action '{action}' is outside the game-side drawing subset.");
            }

            var omen = Required(values, "Omen");
            var (kind, fanRadians) = ParseShape(omen);
            var position = ParseVector3(Required(values, "Pos"), "Pos");
            var scale = ParseNumbers(Required(values, "Scale"), 1, 3, "Scale");
            var primaryScale = MathF.Abs(scale[0]);
            var secondaryScale = MathF.Abs(scale.Length > 1 ? scale[1] : scale[0]);
            if (!float.IsFinite(primaryScale) || primaryScale is <= 0 or > 1000 ||
                !float.IsFinite(secondaryScale) || secondaryScale is <= 0 or > 1000)
            {
                throw new InvalidDataException("PictoACT Scale is outside 0-1000.");
            }

            var color = ParseColor(Optional(values, "Color"));
            var duration = ParseSingle(values.GetValueOrDefault("t", "5"), "t");
            if (!float.IsFinite(duration) || duration is <= 0 or > 300)
            {
                throw new InvalidDataException("PictoACT duration is outside 0-300 seconds.");
            }

            var delay = ParseSingle(values.GetValueOrDefault("Delay", "0"), "Delay");
            if (!float.IsFinite(delay) || delay > 300)
            {
                throw new InvalidDataException("PictoACT delay is outside 0-300 seconds.");
            }

            delay = Math.Max(0, delay);
            var angle = ParseSingle(values.GetValueOrDefault("Angle", "0"), "Angle");
            if (!float.IsFinite(angle))
            {
                throw new InvalidDataException("PictoACT Angle is not finite.");
            }

            var startsAt = DateTimeOffset.UtcNow.AddSeconds(delay);
            commands.Add(new PictoActOverlayCommand(
                tag,
                null,
                false,
                new PictoActShape(
                    kind,
                    position,
                    primaryScale,
                    secondaryScale,
                    angle,
                    fanRadians,
                    color,
                    startsAt,
                    startsAt.AddSeconds(duration))));
        }

        return commands.Count > 0
            ? commands
            : throw new InvalidDataException("PictoACT payload contains no commands.");
    }

    private static (PictoActShapeKind Kind, float FanRadians) ParseShape(string omen)
    {
        if (string.Equals(omen, "Circle", StringComparison.OrdinalIgnoreCase))
        {
            return (PictoActShapeKind.Circle, 0);
        }

        if (string.Equals(omen, "Rect", StringComparison.OrdinalIgnoreCase))
        {
            return (PictoActShapeKind.Rectangle, 0);
        }

        if (string.Equals(omen, "Rect2", StringComparison.OrdinalIgnoreCase))
        {
            return (PictoActShapeKind.BidirectionalRectangle, 0);
        }

        var fanMatch = FanOmen().Match(omen);
        if (fanMatch.Success &&
            int.TryParse(
                fanMatch.Groups["degrees"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var fanDegrees) &&
            fanDegrees is > 0 and <= 360)
        {
            return (PictoActShapeKind.Fan, fanDegrees * MathF.PI / 180);
        }

        throw new NotSupportedException(
            $"PictoACT omen '{omen}' is outside the game-side drawing subset.");
    }

    private static Dictionary<string, string> ParseFields(string segment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in segment.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException($"PictoACT line '{line}' has no key separator.");
            }

            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return result;
    }

    private static string? Optional(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static string Required(IReadOnlyDictionary<string, string> values, string name)
        => Optional(values, name)
           ?? throw new InvalidDataException($"PictoACT requires '{name}'.");

    private static Vector3 ParseVector3(string value, string name)
    {
        var numbers = ParseNumbers(value.Trim().Trim('<', '>', '(', ')', '[', ']'), 2, 3, name);
        // PictoACT follows ACT/FFXIV log coordinates: X/Y are the ground plane and Z is height.
        // Dalamud's world vector follows the client layout: X/Z are the ground plane and Y is height.
        var result = new Vector3(numbers[0], numbers.Length > 2 ? numbers[2] : 0, numbers[1]);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) ||
            !float.IsFinite(result.Z) || result.LengthSquared() > 30_000_000_000f)
        {
            throw new InvalidDataException($"PictoACT {name} is outside the safe coordinate range.");
        }

        return result;
    }

    private static Vector4 ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultColor;
        }

        var colorValues = ParseNumbers(value, 3, 4, "Color");
        return new Vector4(
            Math.Clamp(colorValues[0], 0, 1),
            Math.Clamp(colorValues[1], 0, 1),
            Math.Clamp(colorValues[2], 0, 1),
            Math.Clamp(colorValues.Length > 3 ? colorValues[3] : DefaultColor.W, 0, 1));
    }

    private static float[] ParseNumbers(
        string value,
        int minimum,
        int maximum,
        string name)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < minimum || parts.Length > maximum)
        {
            throw new InvalidDataException(
                $"PictoACT {name} requires {minimum}-{maximum} comma-separated numbers.");
        }

        return parts.Select(part => ParseSingle(part, name)).ToArray();
    }

    private static float ParseSingle(string value, string name)
    {
        try
        {
            return checked((float)new NumericExpressionParser(value).Parse());
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new InvalidDataException(
                $"PictoACT {name} contains an invalid number expression.",
                ex);
        }
    }

    private sealed class NumericExpressionParser(string text)
    {
        private int position;

        internal double Parse()
        {
            var result = ParseExpression();
            SkipWhiteSpace();
            if (position != text.Length)
            {
                throw new FormatException($"Unexpected token at position {position}.");
            }

            return result;
        }

        private double ParseExpression()
        {
            var result = ParseTerm();
            while (true)
            {
                SkipWhiteSpace();
                if (Take('+'))
                {
                    result += ParseTerm();
                }
                else if (Take('-'))
                {
                    result -= ParseTerm();
                }
                else
                {
                    return result;
                }
            }
        }

        private double ParseTerm()
        {
            var result = ParseUnary();
            while (true)
            {
                SkipWhiteSpace();
                if (Take('*'))
                {
                    result *= ParseUnary();
                }
                else if (Take('/'))
                {
                    result /= ParseUnary();
                }
                else
                {
                    return result;
                }
            }
        }

        private double ParseUnary()
        {
            SkipWhiteSpace();
            if (Take('+'))
            {
                return ParseUnary();
            }

            if (Take('-'))
            {
                return -ParseUnary();
            }

            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhiteSpace();
            double result;
            if (Take('('))
            {
                result = ParseExpression();
                SkipWhiteSpace();
                if (!Take(')'))
                {
                    throw new FormatException("Missing closing parenthesis.");
                }
            }
            else if (Take('π'))
            {
                result = Math.PI;
            }
            else if (TryTakeWord("pi"))
            {
                result = Math.PI;
            }
            else
            {
                result = ParseNumber();
            }

            SkipWhiteSpace();
            if (Take('°'))
            {
                result *= Math.PI / 180;
            }

            return result;
        }

        private double ParseNumber()
        {
            SkipWhiteSpace();
            var start = position;
            var hasExponent = false;
            while (position < text.Length)
            {
                var character = text[position];
                if (char.IsDigit(character) || character == '.')
                {
                    position++;
                    continue;
                }

                if (!hasExponent && (character == 'e' || character == 'E'))
                {
                    hasExponent = true;
                    position++;
                    if (position < text.Length && text[position] is '+' or '-')
                    {
                        position++;
                    }
                    continue;
                }

                break;
            }

            if (start == position ||
                !double.TryParse(
                    text.AsSpan(start, position - start),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var result))
            {
                throw new FormatException($"Expected number at position {start}.");
            }

            return result;
        }

        private bool TryTakeWord(string value)
        {
            if (position + value.Length > text.Length ||
                !text.AsSpan(position, value.Length).Equals(
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            position += value.Length;
            return true;
        }

        private bool Take(char expected)
        {
            if (position >= text.Length || text[position] != expected)
            {
                return false;
            }

            position++;
            return true;
        }

        private void SkipWhiteSpace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
        }
    }

    [GeneratedRegex(@"(?:\r\n|\n|\r)\s*---\s*(?:\r\n|\n|\r)", RegexOptions.CultureInvariant)]
    private static partial Regex CommandSeparator();

    [GeneratedRegex(@"^Fan(?<degrees>\d{1,3})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FanOmen();
}

internal sealed record PictoActOverlayCommand(
    string? Tag,
    Regex? Regex,
    bool Remove,
    PictoActShape? Shape);

internal sealed record PictoActShape(
    PictoActShapeKind Kind,
    Vector3 Position,
    float PrimaryScale,
    float SecondaryScale,
    float Angle,
    float FanRadians,
    Vector4 Color,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt);

internal enum PictoActShapeKind
{
    Circle,
    Rectangle,
    BidirectionalRectangle,
    Fan,
}

internal sealed record StoredPictoActShape(string SemanticTag, PictoActShape Shape);
