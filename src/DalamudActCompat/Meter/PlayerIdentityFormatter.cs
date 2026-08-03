using DalamudActCompat.Core.Models;
using DalamudActCompat.UI;

namespace DalamudActCompat.Meter;

public static class PlayerIdentityFormatter
{
    public static string Format(
        Combatant combatant,
        IReadOnlyList<Combatant> party,
        MeterSettings settings,
        UiText text)
        => settings.PlayerIdentityMode switch
        {
            PlayerIdentityMode.Job => FormatJob(combatant.Job, text),
            PlayerIdentityMode.Anonymous => FormatAnonymous(combatant, party, settings, text),
            _ => combatant.Name,
        };

    public static string FormatActionOwner(
        string combatantId,
        IReadOnlyList<Combatant> party,
        MeterSettings settings,
        UiText text)
    {
        var combatant = party.FirstOrDefault(member =>
            string.Equals(member.Id, combatantId, StringComparison.OrdinalIgnoreCase));
        if (combatant is null)
        {
            return text.Get("未知职业", "Unknown job");
        }

        return !string.IsNullOrWhiteSpace(combatant.Job)
            ? FormatJob(combatant.Job, text)
            : Format(combatant, party, settings, text);
    }

    public static string FormatJob(string job, UiText text)
    {
        var normalized = job.Trim().ToUpperInvariant();
        if (!text.IsChinese)
        {
            return string.IsNullOrWhiteSpace(normalized) ? "Unknown" : normalized;
        }

        return normalized switch
        {
            "PLD" => "骑士",
            "WAR" => "战士",
            "DRK" => "暗黑骑士",
            "GNB" => "绝枪战士",
            "WHM" => "白魔法师",
            "SCH" => "学者",
            "AST" => "占星术士",
            "SGE" => "贤者",
            "MNK" => "武僧",
            "DRG" => "龙骑士",
            "NIN" => "忍者",
            "SAM" => "武士",
            "RPR" => "钐镰客",
            "VPR" => "蝰蛇剑士",
            "BRD" => "吟游诗人",
            "MCH" => "机工士",
            "DNC" => "舞者",
            "BLM" => "黑魔法师",
            "SMN" => "召唤师",
            "RDM" => "赤魔法师",
            "PCT" => "绘灵法师",
            "BLU" => "青魔法师",
            _ => string.IsNullOrWhiteSpace(normalized) ? "未知职业" : normalized,
        };
    }

    private static string FormatAnonymous(
        Combatant combatant,
        IReadOnlyList<Combatant> party,
        MeterSettings settings,
        UiText text)
    {
        if (combatant.IsLocalPlayer)
        {
            return string.IsNullOrWhiteSpace(settings.LocalPlayerAlias)
                ? text.Get("自己", "You")
                : settings.LocalPlayerAlias.Trim();
        }

        var index = party
            .Where(member => !member.IsLocalPlayer)
            .Select((member, position) => new { member.Id, Position = position + 1 })
            .FirstOrDefault(item => string.Equals(item.Id, combatant.Id, StringComparison.OrdinalIgnoreCase))
            ?.Position ?? 0;
        return index > 0
            ? $"{text.Get("玩家", "Player")} {index}"
            : text.Get("玩家", "Player");
    }
}
