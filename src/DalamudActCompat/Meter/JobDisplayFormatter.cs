using DalamudActCompat.UI;

namespace DalamudActCompat.Meter;

public static class JobDisplayFormatter
{
    public static IReadOnlyList<string> SupportedJobCodes { get; } =
    [
        "ACN", "ARC", "AST", "BRD", "BST", "BLM", "BLU", "CNJ",
        "DNC", "DRK", "DRG", "GLA", "GNB", "LNC", "MCH", "MRD",
        "MNK", "NIN", "PLD", "PCT", "PGL", "RPR", "RDM", "ROG",
        "SGE", "SAM", "SCH", "SMN", "THM", "VPR", "WAR", "WHM",
    ];

    public static bool UsesIcon(JobDisplayStyle style)
        => style is JobDisplayStyle.MinimalIcon or
            JobDisplayStyle.ClassicIcon or
            JobDisplayStyle.FlatIcon;

    public static string FormatText(string job, JobDisplayStyle style)
    {
        var normalized = NormalizeJobCode(job);
        if (style != JobDisplayStyle.ChineseAbbreviation)
        {
            return normalized;
        }

        return normalized switch
        {
            "ACN" => "巴术",
            "ARC" => "弓箭",
            "AST" => "占星",
            "BRD" => "诗人",
            "BST" => "魔兽",
            "BLM" => "黑魔",
            "BLU" => "青魔",
            "CNJ" => "幻术",
            "DNC" => "舞者",
            "DRK" => "暗骑",
            "DRG" => "龙骑",
            "GLA" => "剑术",
            "GNB" => "绝枪",
            "LNC" => "枪术",
            "MCH" => "机工",
            "MRD" => "斧术",
            "MNK" => "武僧",
            "NIN" => "忍者",
            "PLD" => "骑士",
            "PCT" => "绘灵",
            "PGL" => "格斗",
            "RPR" => "钐镰",
            "RDM" => "赤魔",
            "ROG" => "双剑",
            "SGE" => "贤者",
            "SAM" => "武士",
            "SCH" => "学者",
            "SMN" => "召唤",
            "THM" => "咒术",
            "VPR" => "蝰蛇",
            "WAR" => "战士",
            "WHM" => "白魔",
            _ => normalized,
        };
    }

    public static string Label(JobDisplayStyle style, UiText text) => style switch
    {
        JobDisplayStyle.ChineseAbbreviation => text.Get("中文简称", "Chinese abbreviation"),
        JobDisplayStyle.MinimalIcon => text.Get("简约", "Minimal"),
        JobDisplayStyle.ClassicIcon => text.Get("传统", "Classic"),
        JobDisplayStyle.FlatIcon => text.Get("平面", "Flat"),
        _ => text.Get("简称", "Abbreviation"),
    };

    internal static string NormalizeJobCode(string job)
    {
        var normalized = job.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "?" : normalized;
    }

    internal static bool IsSupportedJobCode(string job)
        => SupportedJobCodes.Contains(job, StringComparer.Ordinal);

    internal static string? IconFolder(JobDisplayStyle style) => style switch
    {
        JobDisplayStyle.MinimalIcon => "Minimal",
        JobDisplayStyle.ClassicIcon => "Classic",
        JobDisplayStyle.FlatIcon => "Flat",
        _ => null,
    };
}
