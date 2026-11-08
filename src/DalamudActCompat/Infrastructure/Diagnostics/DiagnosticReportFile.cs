using System.Text;

namespace DalamudActCompat.Infrastructure.Diagnostics;

public sealed record DiagnosticReportExport(string Report, string FilePath);

// "复制诊断日志" hands users a .txt file rather than the raw text: pasting tens of thousands of
// characters into QQ / WeChat / Discord gets truncated, split or refused, while a pasted file
// arrives as one attachment.
internal static class DiagnosticReportFile
{
    internal const string ExportDirectoryName = "diagnostic-exports";

    // An export only matters until it has been sent; a few are kept in case one is asked for again.
    internal const int RetainedExports = 10;

    public static string Write(string logDirectory, string report, DateTimeOffset now)
    {
        var directory = Path.Combine(logDirectory, ExportDirectoryName);
        Directory.CreateDirectory(directory);
        // ASCII-only name: some chat clients mangle or reject non-ASCII attachment names.
        var path = Path.Combine(directory, $"DACT-diagnostic-{now:yyyyMMdd-HHmmss}.txt");
        // The BOM lets older Notepad builds and phone previewers detect UTF-8 Chinese text.
        File.WriteAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Prune(directory, path);
        return path;
    }

    private static void Prune(string directory, string keep)
    {
        // Timestamped names sort chronologically, so the newest exports come first.
        var stale = Directory.EnumerateFiles(directory, "DACT-diagnostic-*.txt")
            .Where(file => !string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal)
            .Skip(RetainedExports - 1);
        foreach (var file in stale)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Still open in an editor or chat upload; it is removed on a later export.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
