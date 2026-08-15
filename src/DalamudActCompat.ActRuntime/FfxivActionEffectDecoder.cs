using System.Globalization;

namespace DalamudActCompat.ActRuntime;

internal static class FfxivActionEffectDecoder
{
    private static readonly HashSet<byte> DamageEffectTypes = [1, 3, 5, 6];
    private const byte HealingEffectType = 4;

    public static bool TryDecodeDamage(
        string flagsText,
        string valueText,
        out long amount,
        out bool critical,
        out bool directHit)
    {
        amount = 0;
        critical = false;
        directHit = false;
        if (!TryDecodeEffect(
                flagsText,
                valueText,
                out var type,
                out _,
                out var param0,
                out amount) ||
            !DamageEffectTypes.Contains(type))
        {
            amount = 0;
            return false;
        }

        critical = (param0 & 0x20) != 0;
        directHit = (param0 & 0x40) != 0;
        return true;
    }

    public static bool TryDecodeHealing(
        string flagsText,
        string valueText,
        out long amount,
        out bool critical)
    {
        amount = 0;
        critical = false;
        if (!TryDecodeEffect(
                flagsText,
                valueText,
                out var type,
                out var param1,
                out _,
                out amount) ||
            type != HealingEffectType)
        {
            amount = 0;
            return false;
        }

        critical = (param1 & 0x20) != 0;
        return true;
    }

    private static bool TryDecodeEffect(
        string flagsText,
        string valueText,
        out byte type,
        out byte param1,
        out byte param0,
        out long amount)
    {
        type = 0;
        param1 = 0;
        param0 = 0;
        amount = 0;
        var flags = flagsText.PadLeft(8, '0');
        var value = valueText.PadLeft(8, '0');
        if (flags.Length != 8 || value.Length != 8 ||
            !byte.TryParse(flags.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out type) ||
            !byte.TryParse(flags.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out param1) ||
            !byte.TryParse(flags.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out param0) ||
            !ushort.TryParse(value.AsSpan(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var baseAmount) ||
            !byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags2) ||
            !byte.TryParse(value.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags1))
        {
            return false;
        }

        // Large values use a 16-bit base plus Flags1 * 65536 when Flags2 bit 0x40 is set.
        amount = baseAmount + ((flags2 & 0x40) != 0 ? flags1 * 65_536L : 0);
        return true;
    }
}
