using System.Globalization;

namespace DalamudActCompat.ActRuntime;

internal readonly record struct NetworkDamageFallbackEvent(
    uint SourceId,
    string SourceName,
    string TargetName,
    string ActionName,
    long Damage,
    bool IsCritical,
    bool IsDirectHit);

internal static class NetworkDamageFallbackParser
{
    public static bool TryParse(string rawLine, out NetworkDamageFallbackEvent damageEvent)
    {
        damageEvent = default;
        var fields = rawLine.Split('|');
        if (fields.Length >= 20 && fields[0] == "24" && fields[4] == "DoT")
        {
            return TryParsePeriodicDamage(fields, out damageEvent);
        }

        if (fields.Length < 10 || fields[0] is not ("21" or "22") ||
            !uint.TryParse(
                fields[2],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var sourceId))
        {
            return false;
        }

        var damage = 0L;
        var isCritical = false;
        var isDirectHit = false;
        // Network action lines reserve eight flag/value pairs at fields 8..23.
        // Restricting decoding to that block avoids treating target HP or checksums as effects.
        for (var index = 8; index + 1 < Math.Min(fields.Length, 24); index += 2)
        {
            if (!FfxivActionEffectDecoder.TryDecodeDamage(
                    fields[index],
                    fields[index + 1],
                    out var effectDamage,
                    out var effectCritical,
                    out var effectDirectHit))
            {
                continue;
            }

            damage += effectDamage;
            isCritical |= effectCritical;
            isDirectHit |= effectDirectHit;
        }

        if (damage <= 0)
        {
            return false;
        }

        damageEvent = new NetworkDamageFallbackEvent(
            sourceId,
            fields[3],
            fields[7],
            fields[5],
            damage,
            isCritical,
            isDirectHit);
        return true;
    }

    private static bool TryParsePeriodicDamage(
        IReadOnlyList<string> fields,
        out NetworkDamageFallbackEvent damageEvent)
    {
        damageEvent = default;
        if (!uint.TryParse(
                fields[17],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var sourceId) ||
            !long.TryParse(
                fields[6],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var damage) ||
            damage <= 0)
        {
            return false;
        }

        // Periodic-effect lines carry their original source at 17/18; fields 2/3 are
        // the target. This attribution is stable across client languages and regions.
        damageEvent = new NetworkDamageFallbackEvent(
            sourceId,
            fields[18],
            fields[3],
            "DoT",
            damage,
            false,
            false);
        return true;
    }
}
