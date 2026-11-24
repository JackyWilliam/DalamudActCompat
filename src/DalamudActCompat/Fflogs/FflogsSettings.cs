namespace DalamudActCompat.Fflogs;

public sealed class FflogsSettings
{
    public bool Enabled { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    // The FFLogs website account used for log upload. The password is not part of the
    // configuration: it is kept DPAPI-protected in its own file (FflogsUploadCredentialStore).
    public string UploadEmail { get; set; } = string.Empty;

    // Whether a password the site accepted is kept (DPAPI file). Switching it off deletes the file.
    public bool UploadRememberPassword { get; set; }

    public bool UploadAutoLogin { get; set; }

    public bool UploadAutoStartLive { get; set; }

    public bool UploadPreviousFights { get; set; }

    // FF Logs' own numbering: 0 public, 1 private, 2 unlisted. Private is the default so a
    // first upload never publishes a report by surprise.
    public int UploadVisibility { get; set; } = 1;

    // FF Logs' server region of the report: 1 NA, 2 EU, 3 JP, 6 OC on the global site.
    public int UploadRegion { get; set; } = 1;

    public string UploadGuildId { get; set; } = string.Empty;

    public string UploadDescription { get; set; } = string.Empty;

    public int CacheHours { get; set; } = 24;

    // Retained for backward-compatible deserialization of older configurations.
    // Current encounters are resolved from stable territory IDs instead.
    public Dictionary<string, int> EncounterMappings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // The Chinese site has a single region; a region chosen for the global site must not be
    // sent to it after the player switches game regions.
    public DalamudActCompat.Protocol.FflogsUploadOptions CreateUploadOptions(bool chineseSite)
        => new(
            Math.Clamp(UploadVisibility, 0, 2),
            chineseSite ? 1 : UploadRegion,
            UploadGuildId ?? string.Empty,
            UploadDescription ?? string.Empty,
            UploadPreviousFights);

    public FflogsSettings Snapshot()
        => new()
        {
            Enabled = Enabled,
            ClientId = ClientId,
            ClientSecret = ClientSecret,
            UploadEmail = UploadEmail,
            UploadRememberPassword = UploadRememberPassword,
            UploadAutoLogin = UploadAutoLogin,
            UploadAutoStartLive = UploadAutoStartLive,
            UploadPreviousFights = UploadPreviousFights,
            UploadVisibility = UploadVisibility,
            UploadRegion = UploadRegion,
            UploadGuildId = UploadGuildId,
            UploadDescription = UploadDescription,
            CacheHours = CacheHours,
            EncounterMappings = new Dictionary<string, int>(
                EncounterMappings ?? [],
                StringComparer.OrdinalIgnoreCase),
        };
}
