namespace DalamudActCompat.Fflogs;

public sealed class FflogsSettings
{
    public bool Enabled { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public int CacheHours { get; set; } = 24;

    public Dictionary<string, int> EncounterMappings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
