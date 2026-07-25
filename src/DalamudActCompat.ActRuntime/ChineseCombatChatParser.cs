using System.Text.RegularExpressions;

namespace DalamudActCompat.ActRuntime;

public static class ChineseCombatChatParser
{
    private static readonly Regex ActorRegex = new(
        @"(?<actor>.+?)发动(?:攻击|了)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DamageRegex = new(
        @"(?<target>\S+?)受到了(?<damage>\d+)(?:\([^)]+\))?点伤害",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(
        string message,
        string previousActor,
        out string actor,
        out string target,
        out long damage)
    {
        var actorMatch = ActorRegex.Match(message);
        actor = actorMatch.Success
            ? actorMatch.Groups["actor"].Value.Trim()
            : previousActor;

        var damageMatch = DamageRegex.Match(message);
        damage = 0;
        if (!damageMatch.Success || string.IsNullOrWhiteSpace(actor) ||
            !long.TryParse(damageMatch.Groups["damage"].Value, out damage))
        {
            target = string.Empty;
            return false;
        }

        target = damageMatch.Groups["target"].Value;
        return true;
    }
}
