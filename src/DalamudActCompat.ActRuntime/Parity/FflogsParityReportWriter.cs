using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DalamudActCompat.ActRuntime.Parity;

internal static class FflogsParityReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static (string JsonPath, string MarkdownPath) Write(
        string directory,
        FflogsParityDiagnostic diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(diagnostic);
        Directory.CreateDirectory(directory);
        var timestamp = diagnostic.Durations.FightStart ?? DateTimeOffset.Now;
        var stem = $"{timestamp:yyyyMMdd-HHmmss}-{diagnostic.EncounterId:N}";
        var jsonPath = Path.Combine(directory, $"{stem}.parity.json");
        var markdownPath = Path.Combine(directory, $"{stem}.parity.md");
        WriteAtomically(jsonPath, JsonSerializer.Serialize(diagnostic, JsonOptions));
        WriteAtomically(markdownPath, BuildMarkdown(diagnostic));
        return (jsonPath, markdownPath);
    }

    internal static string BuildMarkdown(FflogsParityDiagnostic diagnostic)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# FFLogs Parity Diagnostic");
        builder.AppendLine();
        builder.AppendLine($"- Encounter: {Escape(diagnostic.EncounterName)}");
        builder.AppendLine($"- Zone: {Escape(diagnostic.Zone)}");
        builder.AppendLine($"- Encounter ID: `{diagnostic.EncounterId}`");
        builder.AppendLine($"- Fight: `{FormatTimestamp(diagnostic.Durations.FightStart)}` → `{FormatTimestamp(diagnostic.Durations.FightEnd)}`");
        builder.AppendLine($"- FightDuration: `{diagnostic.Durations.FightDurationSeconds:F3}s`");
        builder.AppendLine($"- DamageMetric: `{FormatTimestamp(diagnostic.Durations.DamageMetricStart)}` -> `{FormatTimestamp(diagnostic.Durations.DamageMetricEnd)}`");
        builder.AppendLine($"- DamageMetric wall: `{diagnostic.Durations.DamageMetricWallSeconds:F3}s`");
        builder.AppendLine($"- Current union downtime: `{diagnostic.Durations.CurrentUnionDowntimeSeconds:F3}s`");
        builder.AppendLine($"- Current DamageMetricDuration: `{diagnostic.Durations.CurrentDamageMetricDurationSeconds:F3}s`");
        builder.AppendLine($"- All-targets-unavailable downtime: `{diagnostic.Durations.AllTargetsUnavailableSeconds:F3}s`");
        builder.AppendLine($"- Candidate DamageMetricDuration: `{diagnostic.Durations.CandidateDamageMetricDurationSeconds:F3}s`");
        builder.AppendLine($"- Included damage: `{diagnostic.IncludedDamage:N0}`");
        builder.AppendLine($"- Excluded damage: `{diagnostic.ExcludedDamage:N0}`");
        builder.AppendLine($"- Raw network damage: `{diagnostic.RawNetworkDamage:N0}`");
        builder.AppendLine($"- Parser normalization delta: `{diagnostic.ParserNormalizationDelta:+#,0;-#,0;0}`");
        builder.AppendLine();
        builder.AppendLine("## Per Actor");
        builder.AppendLine();
        builder.AppendLine("| Actor ID | Name | Job | Owner | Damage | Active (observed span) | Hits | Crit | Direct | Crit+Direct |");
        builder.AppendLine("|---|---|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var actor in diagnostic.Actors)
        {
            builder.AppendLine(
                $"| `{actor.ActorId}` | {Escape(actor.Name)} | {Escape(actor.Job)} | `{actor.OwnerId}` | " +
                $"{actor.IncludedDamage:N0} | {actor.ActorActiveTimeSeconds:F3}s | {actor.DamageHits} | " +
                $"{actor.CriticalHits} ({actor.CriticalRate:P2}) | " +
                $"{actor.DirectHits} ({actor.DirectHitRate:P2}) | " +
                $"{actor.CriticalDirectHits} ({actor.CriticalDirectRate:P2}) |");
        }

        builder.AppendLine();
        builder.AppendLine("## Attribution");
        builder.AppendLine();
        foreach (var attribution in diagnostic.Attribution)
        {
            builder.AppendLine($"- `{attribution.ActorId}`: {Escape(attribution.Status)}");
        }

        builder.AppendLine();
        builder.AppendLine("## Downtime");
        builder.AppendLine();
        builder.AppendLine("| Measurement | Start | End | Seconds | Target | Phase | Evidence |");
        builder.AppendLine("|---|---|---|---:|---|---|---|");
        foreach (var interval in diagnostic.DowntimeIntervals)
        {
            builder.AppendLine(
                $"| {Escape(interval.Measurement)} | `{interval.Start:O}` | `{interval.End:O}` | " +
                $"{interval.DurationSeconds:F3} | {Escape(interval.TargetName)} (`{interval.TargetId}`) | " +
                $"{Escape(interval.Phase)} | {Escape(interval.Evidence)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Damage Ledger");
        builder.AppendLine();
        builder.AppendLine("| Seq | Timestamp | Decision | Exclusion | Source / Owner | Target | Ability | Kind | Amount | Crit | Direct | Raw / Packet | Evidence |");
        builder.AppendLine("|---:|---|---|---|---|---|---|---|---:|---|---|---|---|");
        foreach (var entry in diagnostic.DamageLedger)
        {
            var owner = string.IsNullOrWhiteSpace(entry.OwnerId) && string.IsNullOrWhiteSpace(entry.OwnerName)
                ? "none"
                : $"{entry.OwnerName} (`{entry.OwnerId}`)";
            builder.AppendLine(
                $"| {entry.Sequence} | `{entry.Timestamp:O}` | {entry.Decision} | {entry.ExclusionReason} | " +
                $"{Escape(entry.SourceName)} (`{entry.SourceId}`) / {Escape(owner)} | " +
                $"{Escape(entry.TargetName)} (`{entry.TargetId}`) | {Escape(entry.AbilityName)} (`{entry.AbilityId}`) | " +
                $"{entry.DamageKind} | {entry.Amount:N0} | {entry.Critical} | {entry.DirectHit} | " +
                $"{Escape(entry.RawLineType)} / `{Escape(entry.PacketId)}` | {Escape(entry.Evidence)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Damage Delta Breakdown");
        builder.AppendLine();
        builder.AppendLine("| Category | Local | Reference | Delta / Bounds | Precision | Evidence |");
        builder.AppendLine("|---|---:|---:|---:|---|---|");
        foreach (var item in diagnostic.DeltaBreakdown)
        {
            var delta = item.Delta is not null
                ? item.MinimumDelta != item.MaximumDelta
                    ? $"{item.MinimumDelta:+#,0;-#,0;0} … {item.MaximumDelta:+#,0;-#,0;0}"
                    : $"{item.Delta:+#,0;-#,0;0}"
                : "unknown";
            builder.AppendLine(
                $"| {Escape(item.Category)} | {item.LocalAmount:N0} | " +
                $"{(item.ReferenceAmount is null ? "unknown" : item.ReferenceAmount.Value.ToString("N0", CultureInfo.InvariantCulture))} | " +
                $"{delta} | {item.Precision} | {Escape(item.Evidence)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Capture Health");
        builder.AppendLine();
        builder.AppendLine($"- Raw queue drops: `{diagnostic.CaptureHealth.DroppedRawLogLines}`");
        builder.AppendLine($"- Normalized action queue drops: `{diagnostic.CaptureHealth.DroppedNormalizedActions}`");
        builder.AppendLine($"- Ledger truncated: `{diagnostic.CaptureHealth.LedgerTruncated}`");
        builder.AppendLine($"- Partial capture: `{diagnostic.CaptureHealth.PartialCapture}`");
        builder.AppendLine($"- Parser: `{diagnostic.CaptureHealth.ParserAssemblyVersion}` / `{diagnostic.CaptureHealth.ParserAssemblySha256}`");
        builder.AppendLine();
        builder.AppendLine("## Unknown / Inferred");
        builder.AppendLine();
        foreach (var unknown in diagnostic.Unknowns)
        {
            builder.AppendLine($"- {Escape(unknown)}");
        }
        return builder.ToString();
    }

    private static void WriteAtomically(string path, string content)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp)
        => timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown";

    private static string Escape(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");
}
