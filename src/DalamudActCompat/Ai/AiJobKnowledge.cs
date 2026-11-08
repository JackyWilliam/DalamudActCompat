using System.Globalization;
using System.Text.RegularExpressions;

namespace DalamudActCompat.Ai;

// What the reviewer is told about the player's job. Each job's notes live in JobKnowledge/<JOB>.md
// (embedded): a DACT-written summary of the job's burst and priorities, then the checks
// xivanalysis (MIT, https://github.com/xivanalysis/xivanalysis) applies to it. Actions and statuses
// are written as [a:id:English name] / [s:id:English name]; the ids resolve through the player's
// game client, so the notes use the same names as the player's combat log in any client language.
internal static class AiJobKnowledge
{
    private static readonly Regex Token = new(@"\[(a|s):(\d+):([^\]]+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Base classes share their job's notes.
    private static readonly Dictionary<string, string> BaseClasses = new(StringComparer.Ordinal)
    {
        ["GLA"] = "PLD", ["PGL"] = "MNK", ["MRD"] = "WAR", ["LNC"] = "DRG", ["ARC"] = "BRD",
        ["CNJ"] = "WHM", ["THM"] = "BLM", ["ACN"] = "SMN", ["ROG"] = "NIN",
    };

    public static string? For(string job, Func<uint, string?> actionName, Func<uint, string?> statusName)
    {
        var code = job.Trim().ToUpperInvariant();
        code = BaseClasses.GetValueOrDefault(code, code);
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper)) return null;
        using var stream = typeof(AiJobKnowledge).Assembly.GetManifestResourceStream($"DACT.JobKnowledge.{code}.md");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return Localize(reader.ReadToEnd(), actionName, statusName);
    }

    // "Battle Litany" becomes "Battle Litany「战斗连祷」" on a Chinese client: the English name keeps
    // the notes readable for the model, the local name matches the cast list.
    internal static string Localize(string notes, Func<uint, string?> actionName, Func<uint, string?> statusName)
        => Token.Replace(notes, match =>
        {
            var english = match.Groups[3].Value;
            if (!uint.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return english;
            string? local;
            try
            {
                local = match.Groups[1].Value == "a" ? actionName(id) : statusName(id);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                local = null;
            }
            return string.IsNullOrWhiteSpace(local) || string.Equals(local, english, StringComparison.OrdinalIgnoreCase)
                ? english
                : $"{english}「{local}」";
        });
}
