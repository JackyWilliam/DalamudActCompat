using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DalamudActCompat.Infrastructure.Storage;

internal sealed class PortableConfigurationBackupService
{
    private const string ProductDirectoryName = "DalamudActCompat";
    private const string MainConfigurationFileName = "DalamudActCompat.json";
    private static readonly IReadOnlyList<string> PortableScopes = Array.AsReadOnly(
        new[]
        {
            MainConfigurationFileName,
            "DalamudActCompat/RainbowMage.OverlayPlugin.config.json",
            "DalamudActCompat/Config/Triggernometry.config.xml",
            "DalamudActCompat/Config/Cafe.Matcha.config",
            "DalamudActCompat/Config/PostNamazu.config.xml",
            "DalamudActCompat/Config/ACT.FoxTTS.config.xml",
            "DalamudActCompat/SilverDasher/config.json",
            "DalamudActCompat/cactbot_user",
        });

    private readonly PortableConfigurationArchiveService archiveService;
    private readonly PortableConfigurationEncryptionService encryptionService;

    public PortableConfigurationBackupService()
        : this(
            new PortableConfigurationArchiveService(),
            new PortableConfigurationEncryptionService())
    {
    }

    internal PortableConfigurationBackupService(
        PortableConfigurationArchiveService archiveService,
        PortableConfigurationEncryptionService encryptionService)
    {
        this.archiveService = archiveService;
        this.encryptionService = encryptionService;
    }

    public IReadOnlyList<string> IncludedPaths => PortableScopes;

    public string GenerateRecoveryKey()
        => encryptionService.GenerateRecoveryKey();

    public async Task<PortableConfigurationBackupExportResult> ExportEncryptedAsync(
        string pluginConfigurationDirectory,
        string encryptedArchivePath,
        string recoveryKey,
        CancellationToken cancellationToken)
    {
        var layout = ResolveLayout(pluginConfigurationDirectory);
        if (!File.Exists(layout.MainConfigurationFile))
        {
            throw new FileNotFoundException(
                "The DACT main configuration file was not found.",
                layout.MainConfigurationFile);
        }

        var destination = EnsureArchiveOutsideConfigurationRoot(
            layout.ConfigurationRoot,
            encryptedArchivePath);
        var operationRoot = CreateOperationRoot();
        var plaintextArchive = Path.Combine(operationRoot, "configuration.dactbackup");
        try
        {
            var exported = await archiveService.ExportAsync(
                    layout.ConfigurationRoot,
                    plaintextArchive,
                    PortableScopes,
                    cancellationToken)
                .ConfigureAwait(false);
            await encryptionService.EncryptFileAsync(
                    plaintextArchive,
                    destination,
                    recoveryKey,
                    cancellationToken)
                .ConfigureAwait(false);
            return new PortableConfigurationBackupExportResult(
                destination,
                exported.ScopeCount,
                exported.FileCount,
                exported.UncompressedBytes,
                new FileInfo(destination).Length);
        }
        finally
        {
            TryDeleteDirectory(operationRoot);
        }
    }

    public async Task<PortableConfigurationBackupPreview> PreviewRestoreAsync(
        string encryptedArchivePath,
        string pluginConfigurationDirectory,
        string recoveryKey,
        CancellationToken cancellationToken)
    {
        var layout = ResolveLayout(pluginConfigurationDirectory);
        var operationRoot = CreateOperationRoot();
        try
        {
            var extracted = await DecryptAndExtractAsync(
                    encryptedArchivePath,
                    recoveryKey,
                    layout,
                    operationRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            await PreserveLocalMachinePathsAsync(
                    extracted.Directory,
                    layout,
                    cancellationToken)
                .ConfigureAwait(false);
            return await BuildPreviewAsync(
                    extracted.Directory,
                    layout.ConfigurationRoot,
                    extracted.Inspection,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(operationRoot);
        }
    }

    public async Task<PortableConfigurationBackupRestoreResult> RestoreEncryptedAsync(
        string encryptedArchivePath,
        string pluginConfigurationDirectory,
        string encryptedRollbackArchivePath,
        string recoveryKey,
        CancellationToken cancellationToken)
    {
        var layout = ResolveLayout(pluginConfigurationDirectory);
        var rollbackDestination = EnsureArchiveOutsideConfigurationRoot(
            layout.ConfigurationRoot,
            encryptedRollbackArchivePath);
        if (File.Exists(rollbackDestination) || Directory.Exists(rollbackDestination))
        {
            throw new IOException(
                $"Encrypted rollback archive already exists: {rollbackDestination}");
        }

        var operationRoot = CreateOperationRoot();
        var materializedArchive = Path.Combine(operationRoot, "materialized.dactbackup");
        var plaintextRollback = Path.Combine(operationRoot, "rollback.dactbackup");
        try
        {
            var extracted = await DecryptAndExtractAsync(
                    encryptedArchivePath,
                    recoveryKey,
                    layout,
                    operationRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            await PreserveLocalMachinePathsAsync(
                    extracted.Directory,
                    layout,
                    cancellationToken)
                .ConfigureAwait(false);

            await archiveService.ExportAsync(
                    extracted.Directory,
                    materializedArchive,
                    PortableScopes,
                    cancellationToken)
                .ConfigureAwait(false);
            var restored = await archiveService.RestoreAsync(
                    materializedArchive,
                    layout.ConfigurationRoot,
                    plaintextRollback,
                    cancellationToken,
                    (rollbackPath, token) => encryptionService.EncryptFileAsync(
                        rollbackPath,
                        rollbackDestination,
                        recoveryKey,
                        token))
                .ConfigureAwait(false);
            return new PortableConfigurationBackupRestoreResult(
                Path.GetFullPath(encryptedArchivePath),
                rollbackDestination,
                restored.ScopeCount,
                restored.FileCount);
        }
        finally
        {
            TryDeleteDirectory(operationRoot);
        }
    }

    private async Task<ExtractedArchive> DecryptAndExtractAsync(
        string encryptedArchivePath,
        string recoveryKey,
        ConfigurationLayout layout,
        string operationRoot,
        CancellationToken cancellationToken)
    {
        var plaintextArchive = Path.Combine(operationRoot, "decrypted.dactbackup");
        var extractedDirectory = Path.Combine(operationRoot, "extracted");
        await encryptionService.DecryptFileAsync(
                encryptedArchivePath,
                plaintextArchive,
                recoveryKey,
                cancellationToken)
            .ConfigureAwait(false);
        var inspection = await archiveService.ExtractVerifiedAsync(
                plaintextArchive,
                layout.ConfigurationRoot,
                extractedDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateProductScopes(inspection);
        return new ExtractedArchive(extractedDirectory, inspection);
    }

    private static void ValidateProductScopes(
        PortableConfigurationArchiveInspection inspection)
    {
        var actualScopes = inspection.Scopes
            .Select(static scope => scope.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (inspection.Scopes.Count != PortableScopes.Count ||
            !actualScopes.SetEquals(PortableScopes))
        {
            throw new InvalidDataException(
                "Encrypted configuration archive does not match the DACT backup whitelist.");
        }

        var mainConfiguration = inspection.Scopes.Single(scope =>
            scope.RelativePath.Equals(
                MainConfigurationFileName,
                StringComparison.OrdinalIgnoreCase));
        if (mainConfiguration.Kind != ArchiveScopeKind.File)
        {
            throw new InvalidDataException(
                "Encrypted configuration archive has no DACT main configuration.");
        }
    }

    private static async Task PreserveLocalMachinePathsAsync(
        string extractedRoot,
        ConfigurationLayout layout,
        CancellationToken cancellationToken)
    {
        var restoredConfigurationPath = Path.Combine(
            extractedRoot,
            MainConfigurationFileName);
        var restoredConfiguration = JObject.Parse(
            await File.ReadAllTextAsync(restoredConfigurationPath, cancellationToken)
                .ConfigureAwait(false));
        JObject? currentConfiguration = null;
        if (File.Exists(layout.MainConfigurationFile))
        {
            currentConfiguration = JObject.Parse(
                await File.ReadAllTextAsync(layout.MainConfigurationFile, cancellationToken)
                    .ConfigureAwait(false));
        }

        // Absolute paths belong to the current computer. Everything else remains the
        // exact authenticated cloud snapshot, including third-party credentials.
        SetStringProperty(
            restoredConfiguration,
            "LogDirectory",
            ReadStringProperty(currentConfiguration, "LogDirectory")
            ?? Path.Combine(layout.PluginConfigurationDirectory, "logs", "ffxiv"));
        SetStringProperty(
            restoredConfiguration,
            "ActPluginDirectory",
            ReadStringProperty(currentConfiguration, "ActPluginDirectory") ?? string.Empty);

        await File.WriteAllTextAsync(
                restoredConfigurationPath,
                restoredConfiguration.ToString(Formatting.Indented),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? ReadStringProperty(JObject? document, string name)
    {
        if (document is null ||
            !document.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var token) ||
            token.Type != JTokenType.String)
        {
            return null;
        }
        return token.Value<string>();
    }

    private static void SetStringProperty(JObject document, string name, string value)
    {
        var property = document.Properties().FirstOrDefault(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property is null)
        {
            document.Add(name, value);
        }
        else
        {
            property.Value = value;
        }
    }

    private static async Task<PortableConfigurationBackupPreview> BuildPreviewAsync(
        string extractedRoot,
        string configurationRoot,
        PortableConfigurationArchiveInspection inspection,
        CancellationToken cancellationToken)
    {
        var scopes = new List<PortableConfigurationScopePreview>();
        foreach (var scope in inspection.Scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = GetPath(extractedRoot, scope.RelativePath);
            var target = GetPath(configurationRoot, scope.RelativePath);
            scopes.Add(await CompareScopeAsync(
                    scope.RelativePath,
                    scope.Kind,
                    source,
                    target,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return new PortableConfigurationBackupPreview(
            inspection.CreatedAtUtc,
            inspection.Files.Count,
            inspection.Files.Sum(static file => file.Length),
            scopes);
    }

    private static async Task<PortableConfigurationScopePreview> CompareScopeAsync(
        string relativePath,
        ArchiveScopeKind sourceKind,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        if (sourceKind == ArchiveScopeKind.Missing)
        {
            var removed = CountEntries(target);
            return new PortableConfigurationScopePreview(
                relativePath,
                sourceKind,
                AddedFiles: 0,
                ChangedFiles: 0,
                RemovedFiles: removed,
                UnchangedFiles: removed == 0 ? 1 : 0);
        }

        if (sourceKind == ArchiveScopeKind.File)
        {
            if (!File.Exists(target))
            {
                return new PortableConfigurationScopePreview(
                    relativePath,
                    sourceKind,
                    AddedFiles: 1,
                    ChangedFiles: 0,
                    RemovedFiles: Directory.Exists(target) ? CountEntries(target) : 0,
                    UnchangedFiles: 0);
            }

            var unchanged = await FilesEqualAsync(source, target, cancellationToken)
                .ConfigureAwait(false);
            return new PortableConfigurationScopePreview(
                relativePath,
                sourceKind,
                AddedFiles: 0,
                ChangedFiles: unchanged ? 0 : 1,
                RemovedFiles: 0,
                UnchangedFiles: unchanged ? 1 : 0);
        }

        var sourceFiles = EnumerateFilesSafely(source)
            .ToDictionary(
                file => NormalizeRelativePath(Path.GetRelativePath(source, file)),
                StringComparer.OrdinalIgnoreCase);
        var targetFiles = Directory.Exists(target)
            ? EnumerateFilesSafely(target).ToDictionary(
                file => NormalizeRelativePath(Path.GetRelativePath(target, file)),
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var changed = File.Exists(target) ? 1 : 0;
        var unchangedFiles = 0;
        foreach (var (path, sourceFile) in sourceFiles)
        {
            if (!targetFiles.TryGetValue(path, out var targetFile))
            {
                added++;
                continue;
            }

            if (await FilesEqualAsync(sourceFile, targetFile, cancellationToken)
                    .ConfigureAwait(false))
            {
                unchangedFiles++;
            }
            else
            {
                changed++;
            }
        }

        var removedFiles = targetFiles.Keys.Count(path => !sourceFiles.ContainsKey(path));
        if (sourceFiles.Count == 0 && !Directory.Exists(target))
        {
            added = 1;
        }
        else if (sourceFiles.Count == 0 && Directory.Exists(target) && targetFiles.Count == 0)
        {
            unchangedFiles = 1;
        }

        return new PortableConfigurationScopePreview(
            relativePath,
            sourceKind,
            added,
            changed,
            removedFiles,
            unchangedFiles);
    }

    private static IEnumerable<string> EnumerateFilesSafely(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RejectReparsePoint(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                RejectReparsePoint(entry);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                else if (File.Exists(entry))
                {
                    files.Add(entry);
                }
            }
        }
        return files;
    }

    private static int CountEntries(string path)
    {
        if (File.Exists(path))
        {
            return 1;
        }
        if (!Directory.Exists(path))
        {
            return 0;
        }
        var files = EnumerateFilesSafely(path).Count();
        return Math.Max(files, 1);
    }

    private static async Task<bool> FilesEqualAsync(
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length)
        {
            return false;
        }

        var leftHash = await ComputeSha256Async(left, cancellationToken).ConfigureAwait(false);
        var rightHash = await ComputeSha256Async(right, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static async Task<byte[]> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static ConfigurationLayout ResolveLayout(string pluginConfigurationDirectory)
    {
        var configurationDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(pluginConfigurationDirectory));
        if (!Path.GetFileName(configurationDirectory).Equals(
                ProductDirectoryName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"DACT configuration directory must be named '{ProductDirectoryName}'.");
        }

        var root = Path.GetDirectoryName(configurationDirectory)
                   ?? throw new InvalidOperationException(
                       "DACT configuration directory has no parent directory.");
        return new ConfigurationLayout(
            root,
            configurationDirectory,
            Path.Combine(root, MainConfigurationFileName));
    }

    private static string EnsureArchiveOutsideConfigurationRoot(
        string configurationRoot,
        string archivePath)
    {
        var destination = Path.GetFullPath(archivePath);
        var rootPrefix = Path.TrimEndingDirectorySeparator(configurationRoot) +
                         Path.DirectorySeparatorChar;
        if (destination.Equals(configurationRoot, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Encrypted configuration archives must be stored outside the plugin configuration root.");
        }
        return destination;
    }

    private static string CreateOperationRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"DalamudActCompat-cloud-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string GetPath(string root, string relativePath)
        => Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string NormalizeRelativePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"Configuration preview cannot follow links or reparse points: {path}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary plaintext must normally be removed, but cleanup failure must
            // not replace the restore result. The caller can report the leftover path.
        }
    }

    private sealed record ConfigurationLayout(
        string ConfigurationRoot,
        string PluginConfigurationDirectory,
        string MainConfigurationFile);

    private sealed record ExtractedArchive(
        string Directory,
        PortableConfigurationArchiveInspection Inspection);
}

internal sealed record PortableConfigurationBackupExportResult(
    string ArchivePath,
    int ScopeCount,
    int FileCount,
    long UncompressedBytes,
    long EncryptedBytes);

internal sealed record PortableConfigurationBackupRestoreResult(
    string ArchivePath,
    string RollbackArchivePath,
    int ScopeCount,
    int FileCount);

internal sealed record PortableConfigurationBackupPreview(
    DateTimeOffset CreatedAtUtc,
    int FileCount,
    long UncompressedBytes,
    IReadOnlyList<PortableConfigurationScopePreview> Scopes)
{
    public int AddedFiles => Scopes.Sum(static scope => scope.AddedFiles);

    public int ChangedFiles => Scopes.Sum(static scope => scope.ChangedFiles);

    public int RemovedFiles => Scopes.Sum(static scope => scope.RemovedFiles);

    public int UnchangedFiles => Scopes.Sum(static scope => scope.UnchangedFiles);
}

internal sealed record PortableConfigurationScopePreview(
    string RelativePath,
    ArchiveScopeKind SourceKind,
    int AddedFiles,
    int ChangedFiles,
    int RemovedFiles,
    int UnchangedFiles);
