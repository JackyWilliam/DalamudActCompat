using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Fflogs.Upload;

public sealed class FflogsUploadService : IDisposable
{
    private readonly Func<string> defaultDirectory;
    private readonly FflogsUploadCredentialStore credentialStore;
    private FflogsUploadClient? client;

    public FflogsUploadService(PluginLogger logger, Func<string> defaultDirectory, string credentialPath)
    {
        this.defaultDirectory = defaultDirectory;
        credentialStore = new FflogsUploadCredentialStore(credentialPath);
        UploadLog.Logger = logger;
    }

    public bool IsLoggedIn => client?.IsLoggedIn == true;

    public string Username => client?.Username ?? string.Empty;
    public IReadOnlyList<GuildInfo> Guilds => client?.Guilds ?? [];

    public bool HasSavedPassword => credentialStore.HasSavedPassword;
    public string? LoadSavedPassword() => credentialStore.Load();
    public void SavePassword(string password) => credentialStore.Save(password);
    public void ClearSavedPassword() => credentialStore.Clear();

    public async Task<bool> LoginAsync(string email, string password, bool chineseRegion)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            return false;
        client?.Dispose();
        client = new FflogsUploadClient(chineseRegion ? "https://cn.fflogs.com" : "https://www.fflogs.com");
        var success = await client.LoginAsync(email.Trim(), password).ConfigureAwait(false);
        return success;
    }

    public async Task<string> UploadLatestAsync(string? directory, bool chineseRegion, string description = "")
    {
        directory = string.IsNullOrWhiteSpace(directory) ? defaultDirectory() : directory;
        var path = LogFileHelper.GetLatestLogFileFromPath(directory);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("No Network log file was found.", path);
        if (!IsLoggedIn)
            throw new InvalidOperationException("Sign in to FFLogs before uploading.");
        var code = chineseRegion ? "CN" : "NA";
        return await client!.UploadLogAsync(path, 1, code, 1, string.Empty, description).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ParserEngine.FightUpload>> PrepareAsync(string? directory, bool chineseRegion)
    {
        directory = string.IsNullOrWhiteSpace(directory) ? defaultDirectory() : directory;
        var path = LogFileHelper.GetLatestLogFileFromPath(directory);
        if (!File.Exists(path)) throw new FileNotFoundException("No Network log file was found.", path);
        if (!IsLoggedIn) throw new InvalidOperationException("Sign in to FFLogs before parsing.");
        return await client!.PrepareUploadsAsync(path, chineseRegion ? "CN" : "NA").ConfigureAwait(false);
    }

    public async Task<string> UploadPreparedAsync(
        string? directory,
        bool chineseRegion,
        IList<ParserEngine.FightUpload> fights,
        int visibility,
        int region,
        string guildId,
        string description)
    {
        directory = string.IsNullOrWhiteSpace(directory) ? defaultDirectory() : directory;
        var path = LogFileHelper.GetLatestLogFileFromPath(directory);
        if (!File.Exists(path)) throw new FileNotFoundException("No Network log file was found.", path);
        if (!IsLoggedIn) throw new InvalidOperationException("Sign in to FFLogs before uploading.");
        return await client!.UploadPreparedAsync(Path.GetFileName(path), fights, region, visibility, guildId, description).ConfigureAwait(false);
    }

    public async Task<string> UploadEncounterAsync(
        string? directory,
        bool chineseRegion,
        long targetStartTime,
        int visibility,
        int region,
        string guildId,
        string description)
    {
        directory = string.IsNullOrWhiteSpace(directory) ? defaultDirectory() : directory;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (!IsLoggedIn) throw new InvalidOperationException("Sign in to FFLogs before uploading.");

        var files = Directory.GetFiles(directory, "*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        var regionCode = chineseRegion ? "CN" : "NA";
        foreach (var file in files)
        {
            var fights = await client!.PrepareUploadsAsync(file, regionCode).ConfigureAwait(false);
            if (fights.Count == 0) continue;
            var match = fights
                .OrderBy(fight => Math.Abs(fight.FightStartTime - targetStartTime))
                .First();
            if (Math.Abs(match.FightStartTime - targetStartTime) > 600_000) continue;
            return await client.UploadPreparedAsync(
                Path.GetFileName(file), [match], region, visibility, guildId, description).ConfigureAwait(false);
        }

        throw new InvalidOperationException("No matching fight was found in the saved Network logs.");
    }

    public void StartLive(string? directory, bool chineseRegion, int visibility, int region, string guildId, string description, bool previousFights)
    {
        directory = string.IsNullOrWhiteSpace(directory) ? defaultDirectory() : directory;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (!IsLoggedIn) throw new InvalidOperationException("Sign in to FFLogs before starting live upload.");
        client!.StartLiveLog(directory, region, chineseRegion ? "CN" : "NA", visibility, guildId, description, previousFights);
    }

    public void StopLive() => client?.StopLiveLog();
    public bool IsLiveLogging => client?.IsLiveLogging == true;
    public void CancelCurrentOperation()
    {
        client?.StopLiveLog();
        client?.Dispose();
        client = null;
    }

    public void Dispose()
    {
        client?.Dispose();
        client = null;
    }
}
