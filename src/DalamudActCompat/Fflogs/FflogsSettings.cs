namespace DalamudActCompat.Fflogs;

public sealed class FflogsSettings
{
    public bool Enabled { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string UploadEmail { get; set; } = string.Empty;

    public bool UploadAutoLogin { get; set; }
    public bool UploadAutoStartLive { get; set; }
    public bool UploadPreviousFights { get; set; }
    public int UploadVisibility { get; set; } = 1;
    public int UploadRegion { get; set; } = 1;
    public string UploadGuildId { get; set; } = string.Empty;
    public string UploadDescription { get; set; } = string.Empty;

    public int CacheHours { get; set; } = 24;

    // Retained for backward-compatible deserialization of older configurations.
    // Current encounters are resolved from stable territory IDs instead.
    public Dictionary<string, int> EncounterMappings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public FflogsSettings Snapshot()
        => new()
        {
            Enabled = Enabled,
            ClientId = ClientId,
            ClientSecret = ClientSecret,
            UploadEmail = UploadEmail,
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
