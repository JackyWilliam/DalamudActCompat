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
        actor = TryExtractActor(message, out var explicitActor)
            ? explicitActor
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

    public static bool TryExtractActor(string message, out string actor)
    {
        var actorMatch = ActorRegex.Match(message);
        actor = actorMatch.Success
            ? actorMatch.Groups["actor"].Value.Trim()
            : string.Empty;
        return !string.IsNullOrWhiteSpace(actor);
    }
}

public sealed class ChineseCombatChatContext
{
    private static readonly TimeSpan ActorContextLifetime = TimeSpan.FromSeconds(2);
    private string pendingActor = string.Empty;
    private DateTimeOffset pendingActorObservedAt;

    public bool TryParse(
        string message,
        DateTimeOffset observedAt,
        out string actor,
        out string target,
        out long damage)
    {
        var hasActorAnnouncement = ChineseCombatChatParser.TryExtractActor(
            message,
            out var announcedActor);
        if (hasActorAnnouncement)
        {
            pendingActor = announcedActor;
            pendingActorObservedAt = observedAt;
        }

        var elapsed = observedAt - pendingActorObservedAt;
        var inheritedActor = elapsed >= TimeSpan.Zero && elapsed <= ActorContextLifetime
            ? pendingActor
            : string.Empty;
        if (ChineseCombatChatParser.TryParse(
                message,
                inheritedActor,
                out actor,
                out target,
                out damage))
        {
            return true;
        }

        if (!hasActorAnnouncement)
        {
            Clear();
        }
        return false;
    }

    public void Clear()
    {
        pendingActor = string.Empty;
        pendingActorObservedAt = default;
    }
}
