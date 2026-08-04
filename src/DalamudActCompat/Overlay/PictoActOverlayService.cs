using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace DalamudActCompat.Overlay;

internal sealed partial class PictoActOverlayService(IGameGui gameGui)
{
    private const int CircleSegments = 72;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, PictoActCircle> circles =
        new(StringComparer.OrdinalIgnoreCase);

    internal void Apply(string payload)
    {
        foreach (var command in Parse(payload))
        {
            lock (syncRoot)
            {
                if (command.Remove)
                {
                    circles.Remove(command.Tag);
                }
                else
                {
                    circles[command.Tag] = command.Circle!;
                }
            }
        }
    }

    internal void Draw()
    {
        PictoActCircle[] snapshot;
        var now = DateTimeOffset.UtcNow;
        lock (syncRoot)
        {
            foreach (var expired in circles
                         .Where(pair => pair.Value.ExpiresAt <= now)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                circles.Remove(expired);
            }

            snapshot = circles.Values.ToArray();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        foreach (var circle in snapshot)
        {
            Vector2? first = null;
            Vector2? previous = null;
            var fullyProjected = true;
            var color = ImGui.ColorConvertFloat4ToU32(circle.Color);
            for (var index = 0; index <= CircleSegments; index++)
            {
                var angle = MathF.Tau * index / CircleSegments;
                var world = new Vector3(
                    circle.Position.X + MathF.Cos(angle) * circle.Radius,
                    circle.Position.Y,
                    circle.Position.Z + MathF.Sin(angle) * circle.Radius);
                if (!gameGui.WorldToScreen(world, out var screen))
                {
                    fullyProjected = false;
                    previous = null;
                    continue;
                }

                first ??= screen;
                if (previous is { } from)
                {
                    drawList.AddLine(from, screen, color, 3f);
                }

                previous = screen;
            }

            if (fullyProjected && previous is { } last && first is { } start)
            {
                drawList.AddLine(last, start, color, 3f);
            }
        }
    }

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
            var tag = Required(values, "Tag");
            if (tag.Length > 128)
            {
                throw new InvalidDataException("PictoACT Tag exceeds 128 characters.");
            }

            var action = values.GetValueOrDefault("Action", "Create").Trim();
            if (string.Equals(action, "Remove", StringComparison.OrdinalIgnoreCase))
            {
                commands.Add(new PictoActOverlayCommand(tag, true, null));
                continue;
            }

            if (!string.Equals(action, "Create", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"PictoACT action '{action}' is outside the game-side drawing subset.");
            }

            var omen = Required(values, "Omen");
            if (!string.Equals(omen, "Circle", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"PictoACT omen '{omen}' is outside the game-side drawing subset.");
            }

            var position = ParseVector3(Required(values, "Pos"), "Pos");
            var scale = ParseNumbers(Required(values, "Scale"), 1, 3, "Scale");
            var radius = MathF.Abs(scale[0]);
            if (!float.IsFinite(radius) || radius is <= 0 or > 1000)
            {
                throw new InvalidDataException("PictoACT circle radius is outside 0-1000.");
            }

            var colorValues = ParseNumbers(Required(values, "Color"), 4, 4, "Color");
            var color = new Vector4(
                Math.Clamp(colorValues[0], 0, 1),
                Math.Clamp(colorValues[1], 0, 1),
                Math.Clamp(colorValues[2], 0, 1),
                Math.Clamp(colorValues[3], 0, 1));
            var duration = ParseSingle(values.GetValueOrDefault("t", "5"), "t");
            if (!float.IsFinite(duration) || duration is <= 0 or > 300)
            {
                throw new InvalidDataException("PictoACT duration is outside 0-300 seconds.");
            }

            commands.Add(new PictoActOverlayCommand(
                tag,
                false,
                new PictoActCircle(
                    position,
                    radius,
                    color,
                    DateTimeOffset.UtcNow.AddSeconds(duration))));
        }

        return commands.Count > 0
            ? commands
            : throw new InvalidDataException("PictoACT payload contains no commands.");
    }

    private static Dictionary<string, string> ParseFields(string segment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in segment.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException($"PictoACT line '{line}' has no key separator.");
            }

            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"PictoACT requires '{name}'.");

    private static Vector3 ParseVector3(string value, string name)
    {
        var numbers = ParseNumbers(value.Trim().Trim('<', '>', '(', ')', '[', ']'), 3, 3, name);
        // PictoACT follows ACT/FFXIV log coordinates: X/Y are the ground plane and Z is height.
        // Dalamud's world vector follows the client layout: X/Z are the ground plane and Y is height.
        var result = new Vector3(numbers[0], numbers[2], numbers[1]);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) ||
            !float.IsFinite(result.Z) || result.LengthSquared() > 30_000_000_000f)
        {
            throw new InvalidDataException($"PictoACT {name} is outside the safe coordinate range.");
        }

        return result;
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
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
           float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result)
            ? result
            : throw new InvalidDataException($"PictoACT {name} contains an invalid number.");

    [GeneratedRegex(@"(?:\r\n|\n|\r)\s*---\s*(?:\r\n|\n|\r)", RegexOptions.CultureInvariant)]
    private static partial Regex CommandSeparator();
}

internal sealed record PictoActOverlayCommand(
    string Tag,
    bool Remove,
    PictoActCircle? Circle);

internal sealed record PictoActCircle(
    Vector3 Position,
    float Radius,
    Vector4 Color,
    DateTimeOffset ExpiresAt);
