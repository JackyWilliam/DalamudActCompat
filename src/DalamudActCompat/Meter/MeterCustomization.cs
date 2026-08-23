using System.Numerics;

namespace DalamudActCompat.Meter;

public enum MeterPreset
{
    CurrentDefault,
    HorizontalTransparent,
    RoleSplit,
    Custom,
}

public enum MeterTemplate
{
    CurrentDefault,
    HorizontalTransparent,
    RoleSplit,
}

public enum MeterSlotMetric
{
    Rank,
    Job,
    PlayerName,
    Dps,
    Hps,
    DamagePercent,
    TotalDamage,
    HighestDamageAction,
    HighestDamage,
    Deaths,
    CriticalHitPercent,
    DirectHitPercent,
    CriticalDirectHitPercent,
}

public enum MeterSlotAlignment
{
    Left,
    Center,
    Right,
}

public sealed class MeterCustomStyle
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "自定义横版";

    public MeterTemplate BaseTemplate { get; set; } = MeterTemplate.HorizontalTransparent;

    public float BackgroundOpacity { get; set; }

    public float CardOpacity { get; set; } = 0.36f;

    public float FontScale { get; set; } = 1;

    public float CardSpacing { get; set; } = 6;

    public float CardRounding { get; set; } = 3;

    public Vector4 TextColor { get; set; } = Vector4.One;

    public List<MeterSlotDefinition> Slots { get; set; } = CreateHorizontalSlots();

    public MeterCustomStyle Clone(string name)
    {
        var clone = new MeterCustomStyle
        {
            Name = name,
            BaseTemplate = BaseTemplate,
            BackgroundOpacity = BackgroundOpacity,
            CardOpacity = CardOpacity,
            FontScale = FontScale,
            CardSpacing = CardSpacing,
            CardRounding = CardRounding,
            TextColor = TextColor,
            Slots = (Slots ?? []).Select(static slot => slot.Clone()).ToList(),
        };
        clone.Normalize();
        return clone;
    }

    internal bool Normalize()
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = Guid.NewGuid().ToString("N");
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "自定义横版";
            changed = true;
        }

        BackgroundOpacity = ClampFinite(BackgroundOpacity, 0, 1, 0);
        CardOpacity = ClampFinite(CardOpacity, 0, 1, 0.36f);
        FontScale = ClampFinite(FontScale, 0.65f, 2, 1);
        CardSpacing = ClampFinite(CardSpacing, 0, 24, 6);
        CardRounding = ClampFinite(CardRounding, 0, 18, 3);
        Slots ??= [];
        if (BaseTemplate == MeterTemplate.HorizontalTransparent && Slots.Count == 0)
        {
            Slots = CreateHorizontalSlots();
            changed = true;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in Slots)
        {
            changed |= slot.Normalize(ids);
        }

        return changed;
    }

    public static List<MeterSlotDefinition> CreateHorizontalSlots()
        =>
        [
            new(MeterSlotMetric.Rank, 0, 0, 2, 2, MeterSlotAlignment.Left),
            new(MeterSlotMetric.Job, 2, 0, 3, 4, MeterSlotAlignment.Center),
            new(MeterSlotMetric.PlayerName, 5, 0, 10, 2, MeterSlotAlignment.Left),
            new(MeterSlotMetric.Dps, 15, 0, 9, 2, MeterSlotAlignment.Right),
            new(MeterSlotMetric.DamagePercent, 5, 2, 5, 2, MeterSlotAlignment.Left),
            new(MeterSlotMetric.TotalDamage, 10, 2, 7, 2, MeterSlotAlignment.Right),
            new(MeterSlotMetric.HighestDamage, 17, 2, 7, 2, MeterSlotAlignment.Right),
            new(MeterSlotMetric.HighestDamageAction, 5, 4, 19, 2, MeterSlotAlignment.Left),
        ];

    private static float ClampFinite(float value, float minimum, float maximum, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public sealed class MeterSlotDefinition
{
    public MeterSlotDefinition()
    {
    }

    public MeterSlotDefinition(
        MeterSlotMetric metric,
        int column,
        int row,
        int columnSpan,
        int rowSpan,
        MeterSlotAlignment alignment)
    {
        Metric = metric;
        Column = column;
        Row = row;
        ColumnSpan = columnSpan;
        RowSpan = rowSpan;
        Alignment = alignment;
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public MeterSlotMetric Metric { get; set; }

    public int Column { get; set; }

    public int Row { get; set; }

    public int ColumnSpan { get; set; } = 4;

    public int RowSpan { get; set; } = 2;

    public MeterSlotAlignment Alignment { get; set; }

    public bool Visible { get; set; } = true;

    public MeterSlotDefinition Clone()
        => new(Metric, Column, Row, ColumnSpan, RowSpan, Alignment)
        {
            Visible = Visible,
        };

    internal bool Normalize(HashSet<string> usedIds)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(Id) || !usedIds.Add(Id))
        {
            Id = Guid.NewGuid().ToString("N");
            usedIds.Add(Id);
            changed = true;
        }

        var normalizedColumn = Math.Clamp(Column, 0, 23);
        var normalizedRow = Math.Clamp(Row, 0, 5);
        var normalizedColumnSpan = Math.Clamp(ColumnSpan, 1, 24 - normalizedColumn);
        var normalizedRowSpan = Math.Clamp(RowSpan, 1, 6 - normalizedRow);
        changed |= normalizedColumn != Column ||
                   normalizedRow != Row ||
                   normalizedColumnSpan != ColumnSpan ||
                   normalizedRowSpan != RowSpan;
        Column = normalizedColumn;
        Row = normalizedRow;
        ColumnSpan = normalizedColumnSpan;
        RowSpan = normalizedRowSpan;
        return changed;
    }
}

internal static class JobRoleClassifier
{
    private static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase)
    {
        "CNJ", "WHM", "SCH", "AST", "SGE",
    };

    public static bool IsHealer(string job)
        => Healers.Contains(JobDisplayFormatter.NormalizeJobCode(job));
}
