using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DalamudActCompat.Infrastructure.Storage;

internal static class CactbotUserDirectoryStorage
{
    internal const string UserScope = "DalamudActCompat/cactbot_user";
    internal const string OverlayScope = "DalamudActCompat/RainbowMage.OverlayPlugin.config.json";
    private static readonly string[] OptionPath = ["EventSourceConfigs", "CactbotESConfig", "OverlayData", "options", "general"];

    internal static string Read(string configurationRoot)
    {
        var file = Path.Combine(configurationRoot, OverlayScope);
        if (!File.Exists(file)) return string.Empty;
        JToken? token = JObject.Parse(File.ReadAllText(file));
        foreach (var part in OptionPath) token = (token as JObject)?[part];
        return (token as JObject)?["CactbotUserDirectory"]?.Value<string>() ?? string.Empty;
    }

    internal static string? LocalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) return null; // Existing remote cactbot URLs remain remote, not filesystem scopes.
            value = uri.LocalPath;
        }
        if (!Path.IsPathFullyQualified(value)) throw new InvalidDataException("Cactbot user directory must be an absolute path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    internal static IReadOnlyDictionary<string, string> ScopePaths(string root, bool restoring = false, string? extractedRoot = null)
    {
        if (restoring && !File.Exists(Path.Combine(extractedRoot!, OverlayScope))) return new Dictionary<string, string>();
        var path = LocalPath(Read(root));
        if (path is null || path.Equals(Path.GetFullPath(Path.Combine(root, UserScope)), StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, string>();
        if (!Directory.Exists(path))
        {
            if (restoring) return new Dictionary<string, string>();
            throw new DirectoryNotFoundException($"Cactbot user directory is unavailable: {path}");
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [UserScope] = path };
    }

    internal static async Task PreserveLocalPathAsync(string extractedRoot, string currentRoot,
        IReadOnlyDictionary<string, string> scopePaths, CancellationToken cancellationToken)
    {
        var file = Path.Combine(extractedRoot, OverlayScope);
        if (!File.Exists(file)) return;
        var document = JObject.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
        var localValue = Read(currentRoot);
        // Backups carry scripts under one portable name. Only this computer decides
        // their destination; an absolute path from another machine never authorizes writes.
        var value = scopePaths.TryGetValue(UserScope, out var path) ? path
            : LocalPath(localValue) is null ? localValue : string.Empty;
        if (string.IsNullOrEmpty(value) && !document.SelectTokens("EventSourceConfigs.CactbotESConfig.OverlayData.options.general.CactbotUserDirectory").Any()) return;
        var current = document;
        foreach (var part in OptionPath)
        {
            if (current[part] is not JObject child) current[part] = child = new JObject();
            current = child;
        }
        current["CactbotUserDirectory"] = value;
        await File.WriteAllTextAsync(file, document.ToString(Formatting.Indented), cancellationToken).ConfigureAwait(false);
    }
}
