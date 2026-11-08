using System.Globalization;
using System.Text;

namespace DalamudActCompat.Ai;

// What the game client says about an action. Recasts and charges come from the player's own
// client, so they match the player's region and patch even when the job notes are newer.
internal sealed record AiActionInfo(string Name, bool IsGcd, float RecastSeconds, int MaxCharges);

internal sealed record AiLogCast(double At, string SourceId, string SourceName, uint ActionId, string ActionName);

internal sealed record AiLogStatus(double At, bool Gained, uint StatusId, string StatusName, string SourceId, string TargetId, double Duration);

internal sealed record AiLogDeath(double At, string TargetId, string SourceName);

// One fight's slice of the network log FFXIV_ACT_Plugin writes (DACT always keeps it on). Times
// are seconds from the pull. Only the line types a review needs are kept.
internal sealed class AiCombatLogWindow
{
    public List<AiLogCast> Casts { get; } = [];

    // Cast bars the player started (line 20), keyed by time, so hard casts count from their start.
    public List<AiLogCast> CastStarts { get; } = [];

    public List<double> Interrupts { get; } = [];

    public List<AiLogStatus> Statuses { get; } = [];

    public List<AiLogDeath> Deaths { get; } = [];

    public string? PlayerId { get; set; }
}

internal static class AiCombatLog
{
    // Reading stops here: a fight is minutes long, and a broken log must not stall the request.
    private const int MaxLines = 400_000;

    public static AiCombatLogWindow? Read(string directory, DateTimeOffset start, DateTimeOffset end, string playerName)
    {
        playerName = LogName(playerName);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || string.IsNullOrWhiteSpace(playerName)) return null;
        // The daily log and the archived sessions; only files written during the fight matter.
        var files = new DirectoryInfo(directory).EnumerateFiles("Network_*.log")
            .Where(file => file.LastWriteTimeUtc >= start.UtcDateTime.AddMinutes(-1) && file.CreationTimeUtc <= end.UtcDateTime.AddMinutes(1))
            .OrderBy(file => file.CreationTimeUtc)
            .ToArray();
        if (files.Length == 0) return null;
        var window = new AiCombatLogWindow();
        var sequences = new HashSet<string>(StringComparer.Ordinal);
        var lines = 0;
        foreach (var file in files)
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = Seek(stream, start.AddSeconds(-30));
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16);
            if (stream.Position > 0) reader.ReadLine();
            while (reader.ReadLine() is { } line && ++lines < MaxLines)
            {
                if (line.Length < 40 || line[2] != '|' || !TryTime(line, out var time)) continue;
                if (time > end.AddSeconds(5)) break;
                if (time < start.AddSeconds(-2)) continue;
                Add(window, line, (time - start).TotalSeconds, playerName, sequences);
            }
        }
        return window.PlayerId is null ? null : window;
    }

    private static void Add(AiCombatLogWindow window, string line, double at, string playerName, HashSet<string> sequences)
    {
        var type = line[..2];
        if (type is not ("20" or "21" or "22" or "23" or "25" or "26" or "30")) return;
        var fields = line.Split('|');
        switch (type)
        {
            case "21" or "22" when fields.Length > 8:
                if (window.PlayerId is null && fields[3] == playerName) window.PlayerId = fields[2];
                // An AoE writes one line per target; the action's sequence number ties them together.
                if (fields.Length > 44 && fields[44].Length > 0 && !sequences.Add(fields[2] + "|" + fields[44])) return;
                if (TryHex(fields[4], out var action))
                    window.Casts.Add(new AiLogCast(at, fields[2], fields[3], action, fields[5]));
                break;
            case "20" when fields.Length > 8 && fields[3] == playerName && TryHex(fields[4], out var started):
                window.CastStarts.Add(new AiLogCast(at, fields[2], fields[3], started, fields[5]));
                break;
            case "23" when fields.Length > 5 && fields[3] == playerName:
                window.Interrupts.Add(at);
                break;
            case "25" when fields.Length > 5:
                window.Deaths.Add(new AiLogDeath(at, fields[2], fields[5]));
                break;
            case "26" when fields.Length > 9 && TryHex(fields[2], out var gained):
                window.Statuses.Add(new AiLogStatus(at, true, gained, fields[3], fields[5], fields[7],
                    double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ? duration : 0));
                break;
            case "30" when fields.Length > 8 && TryHex(fields[2], out var lost):
                window.Statuses.Add(new AiLogStatus(at, false, lost, fields[3], fields[5], fields[7], 0));
                break;
        }
    }

    // DACT's history stores combatants as "Name@World"; the network log writes the bare name.
    public static string LogName(string name)
    {
        var at = name.IndexOf('@');
        return (at > 0 ? name[..at] : name).Trim();
    }

    // Binary search for the last 64 KiB block before the target time; the log is written in order.
    private static long Seek(FileStream stream, DateTimeOffset target)
    {
        long low = 0, high = stream.Length;
        while (high - low > 1 << 16)
        {
            var middle = low + (high - low) / 2;
            var time = FirstTimeAfter(stream, middle);
            if (time is null || time >= target) high = middle;
            else low = middle;
        }
        return low;
    }

    private static DateTimeOffset? FirstTimeAfter(FileStream stream, long position)
    {
        stream.Position = position;
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        if (position > 0) reader.ReadLine();
        for (var index = 0; index < 50; index++)
        {
            if (reader.ReadLine() is not { } line) return null;
            if (line.Length > 3 && line[2] == '|' && TryTime(line, out var time)) return time;
        }
        return null;
    }

    private static bool TryTime(string line, out DateTimeOffset time)
    {
        var end = line.IndexOf('|', 3);
        return DateTimeOffset.TryParse(line.AsSpan(3, (end < 0 ? line.Length : end) - 3), CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    private static bool TryHex(string value, out uint number)
        => uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number);
}
