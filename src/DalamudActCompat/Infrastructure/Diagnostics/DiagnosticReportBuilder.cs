using System.Text;
using System.Text.RegularExpressions;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;

namespace DalamudActCompat.Infrastructure.Diagnostics;

internal sealed record DiagnosticReportSnapshot(
    string PluginVersion,
    string DalamudVersion,
    ParserStatus ParserStatus,
    HostSupervisorSnapshot Host,
    bool ParsingEnabled,
    bool DebugMode,
    bool FflogsEnabled,
    MeterSortMode MeterSortMode,
    bool MeterCompactMode,
    IReadOnlyList<InstalledActPlugin> InstalledPlugins,
    string? PluginDiscoveryError = null);

internal static class DiagnosticReportBuilder
{
    internal const int MaximumReportCharacters = 96_000;
    private const int MaximumLogReadBytes = 384 * 1024;
    private const int MaximumLogLines = 500;
    private const int MaximumLineCharacters = 2_000;
    private const int MaximumCurrentLogCharacters = 36_000;
    private const int MaximumPreviousLogCharacters = 20_000;
    private const int MaximumHostLogCharacters = 20_000;

    private static readonly Regex UserDirectoryPattern = new(
        @"(?<prefix>[A-Za-z]:\\Users\\)[^\\\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex CredentialAssignmentPattern = new(
        "\\b(?<key>access[_ -]?token|token|api[_ -]?key|password|secret|authorization)\\b(?<separator>\\s*[:=]\\s*)(?<value>\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|Bearer\\s+[^\\s,;]+|[^\\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex CredentialQueryPattern = new(
        @"(?<prefix>[?&](?:access_token|token|api_key|apikey|key)=)[^&\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SessionPattern = new(
        @"(?<prefix>\bsession(?:id)?(?:\s*[:=]\s*|\s+))[0-9a-f-]{12,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    internal static string Build(
        PluginPaths paths,
        DiagnosticReportSnapshot snapshot)
    {
        var report = new StringBuilder();
        report.AppendLine("Dalamud ACT Compat diagnostic report");
        report.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Plugin: {snapshot.PluginVersion}");
        report.AppendLine($"Dalamud: {snapshot.DalamudVersion}");
        report.AppendLine($"Runtime: {Environment.Version}");
        report.AppendLine($"OS: {Environment.OSVersion.VersionString}");
        report.AppendLine(
            $"Parser: {snapshot.ParserStatus.State}; enabled={snapshot.ParsingEnabled}; " +
            $"updated={snapshot.ParserStatus.UpdatedAt:O}");
        report.AppendLine($"Parser message: {snapshot.ParserStatus.Message}");
        if (!string.IsNullOrWhiteSpace(snapshot.ParserStatus.Detail))
        {
            report.AppendLine($"Parser detail: {snapshot.ParserStatus.Detail}");
        }

        report.AppendLine(
            $"Meter: sort={MeterSortModeOptions.Normalize(snapshot.MeterSortMode)}; " +
            $"collapsed={snapshot.MeterCompactMode}; fflogs={snapshot.FflogsEnabled}; " +
            $"debug={snapshot.DebugMode}");
        AppendHostSnapshot(report, snapshot.Host);
        AppendInstalledPlugins(report, snapshot.InstalledPlugins, snapshot.PluginDiscoveryError);

        report.AppendLine();
        report.AppendLine("Privacy: combat/network logs and configuration files are excluded; user paths and common credentials are redacted.");

        var launcherDirectory = FindLauncherDirectory(paths.ConfigDirectory);
        AppendLogSection(
            report,
            "Current Dalamud plugin log",
            launcherDirectory is null ? null : Path.Combine(launcherDirectory, "dalamud.log"),
            relevantOnly: true,
            MaximumCurrentLogCharacters);
        AppendLogSection(
            report,
            "Previous Dalamud plugin log",
            launcherDirectory is null ? null : Path.Combine(launcherDirectory, "dalamud.old.log"),
            relevantOnly: true,
            MaximumPreviousLogCharacters);
        AppendLogSection(
            report,
            "External ACT Host log",
            Path.Combine(paths.LogDirectory, "external-host.log"),
            relevantOnly: false,
            MaximumHostLogCharacters);

        var sanitized = RedactSensitiveText(
            report.ToString(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (sanitized.Length <= MaximumReportCharacters)
        {
            return sanitized;
        }

        const string truncationNotice = "\n[Report truncated to the most useful bounded diagnostic data.]\n";
        return sanitized[..(MaximumReportCharacters - truncationNotice.Length)] + truncationNotice;
    }

    internal static string RedactSensitiveText(string value, string? userProfile)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var sanitized = value;
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            sanitized = sanitized.Replace(
                userProfile,
                "%USERPROFILE%",
                StringComparison.OrdinalIgnoreCase);
        }

        sanitized = UserDirectoryPattern.Replace(sanitized, "${prefix}<user>");
        sanitized = CredentialAssignmentPattern.Replace(
            sanitized,
            "${key}${separator}<redacted>");
        sanitized = CredentialQueryPattern.Replace(
            sanitized,
            "${prefix}<redacted>");
        return SessionPattern.Replace(sanitized, "${prefix}<redacted>");
    }

    internal static IReadOnlyList<string> SelectRelevantDalamudLines(
        IEnumerable<string> lines,
        int maximumLines = MaximumLogLines)
    {
        var selected = new Queue<string>();
        var includeContinuation = false;
        foreach (var line in lines)
        {
            var directlyRelevant = line.Contains(
                "DalamudActCompat",
                StringComparison.OrdinalIgnoreCase);
            var continuation = includeContinuation && IsExceptionContinuation(line);
            if (directlyRelevant || continuation)
            {
                EnqueueBounded(selected, line, maximumLines);
            }

            includeContinuation = directlyRelevant || continuation;
        }

        return selected.ToArray();
    }

    private static void AppendHostSnapshot(StringBuilder report, HostSupervisorSnapshot host)
    {
        report.AppendLine();
        report.AppendLine("[Host]");
        report.AppendLine(
            $"state={host.State}; process={host.ProcessRunning}; ipc={host.IpcStatus}; " +
            $"health={host.HealthState}; queues={host.ControlQueueLength}/{host.DataQueueLength}; " +
            $"dropped={host.DroppedMessages}; progress={host.HostAcknowledgedSequence}/{host.LastWrittenSequence}");
        report.AppendLine(
            $"resources={host.HostWorkingSetBytes / (1024d * 1024d):0.0} MiB; threads={host.HostThreadCount}");
        if (!string.IsNullOrWhiteSpace(host.HealthDetail))
        {
            report.AppendLine($"detail={host.HealthDetail}");
        }

        foreach (var plugin in host.PluginHealth ?? [])
        {
            report.AppendLine(
                $"plugin={plugin.PluginId}; state={plugin.State}; events={plugin.CompletedEvents}; " +
                $"exceptions={plugin.Exceptions}; slow={plugin.SlowCalls}; last={plugin.LastDurationMilliseconds}ms; " +
                $"active={plugin.ActiveCallback ?? "none"}/{plugin.ActiveMilliseconds}ms; circuitOpen={plugin.CircuitOpen}");
        }

        foreach (var stage in host.PluginStages ?? [])
        {
            report.AppendLine(
                $"stage={stage.PluginId}/{stage.Stage}; state={stage.State}; " +
                $"updated={stage.UpdatedAt:O}; detail={stage.Detail}");
        }

        foreach (var diagnostic in (host.Diagnostics ?? []).TakeLast(20))
        {
            report.AppendLine(
                $"diagnostic={diagnostic.PluginId}/{diagnostic.Phase}; " +
                $"{diagnostic.ExceptionType}: {diagnostic.Message}; " +
                $"source={diagnostic.SourceAssembly}/{diagnostic.SourceType}.{diagnostic.SourceMethod}; " +
                $"thread={diagnostic.ThreadId}; repeat={diagnostic.RepeatCount}");
            if (!string.IsNullOrWhiteSpace(diagnostic.StackTrace))
            {
                report.AppendLine(TrimLine(diagnostic.StackTrace));
            }
        }
    }

    private static void AppendInstalledPlugins(
        StringBuilder report,
        IReadOnlyList<InstalledActPlugin> installedPlugins,
        string? discoveryError)
    {
        report.AppendLine();
        report.AppendLine("[ACT extensions]");
        if (!string.IsNullOrWhiteSpace(discoveryError))
        {
            report.AppendLine($"discovery-error={discoveryError}");
        }

        if (installedPlugins.Count == 0)
        {
            report.AppendLine("none");
            return;
        }

        foreach (var plugin in installedPlugins.OrderBy(
                     plugin => plugin.Manifest.Id,
                     StringComparer.OrdinalIgnoreCase))
        {
            report.AppendLine(
                $"{plugin.Manifest.Id}; version={plugin.Manifest.Version}; " +
                $"enabled={plugin.Enabled}; hostApi={plugin.Manifest.HostApiVersion}");
        }
    }

    private static void AppendLogSection(
        StringBuilder report,
        string title,
        string? path,
        bool relevantOnly,
        int maximumCharacters)
    {
        report.AppendLine();
        report.AppendLine($"[{title}]");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            report.AppendLine("(not found)");
            return;
        }

        try
        {
            var lines = ReadTailLines(path, MaximumLogLines);
            if (relevantOnly)
            {
                lines = SelectRelevantDalamudLines(lines, MaximumLogLines);
            }

            if (lines.Count == 0)
            {
                report.AppendLine(relevantOnly ? "(no matching plugin lines)" : "(empty)");
                return;
            }

            var section = string.Join(
                Environment.NewLine,
                lines.Select(TrimLine));
            if (section.Length > maximumCharacters)
            {
                section = "[Earlier lines omitted.]" + Environment.NewLine +
                          section[^maximumCharacters..];
            }

            report.AppendLine(section);
        }
        catch (Exception ex)
        {
            report.AppendLine($"(unavailable: {ex.GetType().Name})");
        }
    }

    private static IReadOnlyList<string> ReadTailLines(string path, int maximumLines)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - MaximumLogReadBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        if (start > 0)
        {
            _ = reader.ReadLine();
        }

        var lines = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            EnqueueBounded(lines, line, maximumLines);
        }

        return lines.ToArray();
    }

    private static string? FindLauncherDirectory(string configDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(configDirectory));
        for (var depth = 0; depth < 4 && current is not null; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "dalamud.log")) ||
                File.Exists(Path.Combine(current.FullName, "dalamud.old.log")))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private static bool IsExceptionContinuation(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("at ", StringComparison.Ordinal) ||
               trimmed.StartsWith("---", StringComparison.Ordinal) ||
               trimmed.StartsWith("--->", StringComparison.Ordinal) ||
               trimmed.StartsWith("Caused by", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Inner exception", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimLine(string value)
        => value.Length <= MaximumLineCharacters
            ? value
            : value[..MaximumLineCharacters] + "…";

    private static void EnqueueBounded(Queue<string> lines, string line, int maximumLines)
    {
        if (maximumLines <= 0)
        {
            return;
        }

        lines.Enqueue(line);
        while (lines.Count > maximumLines)
        {
            _ = lines.Dequeue();
        }
    }
}
