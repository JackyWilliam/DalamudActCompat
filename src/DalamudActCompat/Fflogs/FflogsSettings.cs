namespace DalamudActCompat.Fflogs;

public sealed class FflogsSettings
{
    public bool Enabled { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

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
            CacheHours = CacheHours,
            EncounterMappings = new Dictionary<string, int>(
                EncounterMappings ?? [],
                StringComparer.OrdinalIgnoreCase),
        };
}
