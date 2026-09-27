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
        if (!text.IsChinese && text.Language != "ja")
        {
            return string.IsNullOrWhiteSpace(normalized) ? text.Get("未知职业", "Unknown job") : normalized;
        }

        return normalized switch
        {
            "PLD" => text.Get("骑士", "Job: PLD"),
            "WAR" => text.Get("战士", "Job: WAR"),
            "DRK" => text.Get("暗黑骑士", "Job: DRK"),
            "GNB" => text.Get("绝枪战士", "Job: GNB"),
            "WHM" => text.Get("白魔法师", "Job: WHM"),
            "SCH" => text.Get("学者", "Job: SCH"),
            "AST" => text.Get("占星术士", "Job: AST"),
            "SGE" => text.Get("贤者", "Job: SGE"),
            "MNK" => text.Get("武僧", "Job: MNK"),
            "DRG" => text.Get("龙骑士", "Job: DRG"),
            "NIN" => text.Get("忍者", "Job: NIN"),
            "SAM" => text.Get("武士", "Job: SAM"),
            "RPR" => text.Get("钐镰客", "Job: RPR"),
            "VPR" => text.Get("蝰蛇剑士", "Job: VPR"),
            "BRD" => text.Get("吟游诗人", "Job: BRD"),
            "MCH" => text.Get("机工士", "Job: MCH"),
            "DNC" => text.Get("舞者", "Job: DNC"),
            "BLM" => text.Get("黑魔法师", "Job: BLM"),
            "SMN" => text.Get("召唤师", "Job: SMN"),
            "RDM" => text.Get("赤魔法师", "Job: RDM"),
            "PCT" => text.Get("绘灵法师", "Job: PCT"),
            "BLU" => text.Get("青魔法师", "Job: BLU"),
            _ => string.IsNullOrWhiteSpace(normalized) ? text.Get("未知职业", "Unknown job") : normalized,
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
