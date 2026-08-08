using System.Text.RegularExpressions;

namespace DalamudActCompat.ActRuntime;

public static class ChineseCombatChatParser
{
    private static readonly Regex ActionAnnouncementRegex = new(
        @"(?<actor>.+?)发动(?:攻击|了)(?:“(?<action>[^”]+)”)?",
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
        => TryParse(
            message,
            previousActor,
            out actor,
            out target,
            out damage,
            out _,
            out _);

    public static bool TryParse(
        string message,
        string previousActor,
        out string actor,
        out string target,
        out long damage,
        out bool isCritical,
        out bool isDirectHit)
    {
        actor = TryExtractActor(message, out var explicitActor)
            ? explicitActor
            : previousActor;
        isCritical = message.Contains("\u66B4\u51FB", StringComparison.Ordinal);
        isDirectHit = message.Contains("\u76F4\u51FB", StringComparison.Ordinal);

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
        var announcementMatch = ActionAnnouncementRegex.Match(message);
        actor = announcementMatch.Success
            ? announcementMatch.Groups["actor"].Value.Trim()
            : string.Empty;
        return !string.IsNullOrWhiteSpace(actor);
    }

    public static bool TryExtractActionAnnouncement(
        string message,
        out string actor,
        out string action)
    {
        var announcementMatch = ActionAnnouncementRegex.Match(message);
        actor = announcementMatch.Success
            ? announcementMatch.Groups["actor"].Value.Trim()
            : string.Empty;
        action = announcementMatch.Success
            ? announcementMatch.Groups["action"].Value.Trim()
            : string.Empty;
        return !string.IsNullOrWhiteSpace(actor);
    }
}

public sealed class ChineseCombatChatContext
{
    public const string LimitBreakActorName = "Limit Break";
    private static readonly TimeSpan ActorContextLifetime = TimeSpan.FromSeconds(2);
    private readonly IReadOnlySet<string> limitBreakActionNames;
    private string pendingActor = string.Empty;
    private DateTimeOffset pendingActorObservedAt;

    public ChineseCombatChatContext(IReadOnlySet<string>? limitBreakActionNames = null)
    {
        this.limitBreakActionNames = limitBreakActionNames ?? new HashSet<string>();
    }

    public bool TryParse(
        string message,
        DateTimeOffset observedAt,
        out string actor,
        out string target,
        out long damage)
        => TryParse(
            message,
            observedAt,
            out actor,
            out target,
            out damage,
            out _,
            out _);

    public bool TryParse(
        string message,
        DateTimeOffset observedAt,
        out string actor,
        out string target,
        out long damage,
        out bool isCritical,
        out bool isDirectHit)
    {
        var hasActorAnnouncement = ChineseCombatChatParser.TryExtractActionAnnouncement(
            message,
            out var announcedActor,
            out var announcedAction);
        if (hasActorAnnouncement)
        {
            pendingActor = !string.IsNullOrWhiteSpace(announcedAction) &&
                           limitBreakActionNames.Contains(announcedAction)
                ? LimitBreakActorName
                : announcedActor;
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
                out damage,
                out isCritical,
                out isDirectHit))
        {
            return true;
        }

        isCritical = false;
        isDirectHit = false;

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
