using System.Globalization;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal sealed record HarnessOptions
{
    public int SampleCount { get; init; } = 200;

    public string CacheDirectory { get; init; } = Path.Combine("artifacts", "fflogs-parity-harness", "cache");

    public string OutputDirectory { get; init; } = Path.Combine("artifacts", "fflogs-parity-harness", "reports");

    public string ConfigurationPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncherCN",
        "pluginConfigs",
        "DalamudActCompat.json");

    public bool RefreshCache { get; init; }

    public bool CollectOnly { get; init; }

    public bool ReplayOnly { get; init; }

    public bool SelfTest { get; init; }

    public bool DevilmentProbe { get; init; }

    public bool GuaranteedHitExperiment { get; init; }

    public bool AttributionMatrix { get; init; }

    public bool TargetedMatrixMining { get; init; }

    public bool PercentageAudit { get; init; }

    public bool PercentageOrderingOnly { get; init; }

    public bool MatchedOwnershipMining { get; init; }

    public bool MatchedOwnershipReplay { get; init; }

    public bool MatchedOwnershipDhAudit { get; init; }

    public bool ProductionCandidateEvaluation { get; init; }

    public string? QualitySnapshotPath { get; init; }

    public bool ShowHelp { get; init; }

    public static HarnessOptions Parse(IReadOnlyList<string> args)
    {
        var options = new HarnessOptions();
        for (var index = 0; index < args.Count; index++)
        {
            var value = args[index];
            options = value switch
            {
                "--samples" => options with
                {
                    SampleCount = ParseSampleCount(RequireValue(args, ref index, value)),
                },
                "--cache-dir" => options with
                {
                    CacheDirectory = Path.GetFullPath(RequireValue(args, ref index, value)),
                },
                "--output-dir" => options with
                {
                    OutputDirectory = Path.GetFullPath(RequireValue(args, ref index, value)),
                },
                "--config" => options with
                {
                    ConfigurationPath = Path.GetFullPath(RequireValue(args, ref index, value)),
                },
                "--refresh" => options with { RefreshCache = true },
                "--collect-only" => options with { CollectOnly = true },
                "--replay-only" => options with { ReplayOnly = true },
                "--self-test" => options with { SelfTest = true },
                "--devilment-probe" => options with { DevilmentProbe = true, ReplayOnly = true },
                "--guaranteed-hit-experiment" => options with
                {
                    GuaranteedHitExperiment = true,
                    ReplayOnly = true,
                },
                "--attribution-matrix" => options with
                {
                    AttributionMatrix = true,
                    ReplayOnly = true,
                },
                "--percentage-audit" => options with
                {
                    PercentageAudit = true,
                    ReplayOnly = true,
                },
                "--percentage-ordering-only" => options with
                {
                    PercentageOrderingOnly = true,
                    ReplayOnly = true,
                },
                "--mine-targeted-matrix" => options with
                {
                    AttributionMatrix = true,
                    TargetedMatrixMining = true,
                    ReplayOnly = false,
                },
                "--matched-ownership-mining" => options with
                {
                    MatchedOwnershipMining = true,
                    ReplayOnly = false,
                },
                "--matched-ownership-replay" => options with
                {
                    MatchedOwnershipReplay = true,
                    ReplayOnly = true,
                },
                "--matched-ownership-dh-audit" => options with
                {
                    MatchedOwnershipDhAudit = true,
                    ReplayOnly = true,
                },
                "--production-candidate-evaluation" => options with
                {
                    ProductionCandidateEvaluation = true,
                    ReplayOnly = true,
                },
                "--quality-snapshot" => options with
                {
                    QualitySnapshotPath = Path.GetFullPath(RequireValue(args, ref index, value)),
                },
                "--help" or "-h" => options with { ShowHelp = true },
                _ => throw new ArgumentException($"Unknown argument '{value}'. Use --help for usage."),
            };
        }

        if (options.CollectOnly && options.ReplayOnly)
        {
            throw new ArgumentException("--collect-only and --replay-only are mutually exclusive.");
        }
        if (options.TargetedMatrixMining && options.ReplayOnly)
        {
            throw new ArgumentException("--mine-targeted-matrix cannot be combined with --replay-only.");
        }
        var matchedOwnershipModeCount =
            Convert.ToInt32(options.MatchedOwnershipMining) +
            Convert.ToInt32(options.MatchedOwnershipReplay) +
            Convert.ToInt32(options.MatchedOwnershipDhAudit);
        if (matchedOwnershipModeCount > 1)
        {
            throw new ArgumentException(
                "Matched ownership modes are mutually exclusive.");
        }

        return options with
        {
            CacheDirectory = Path.GetFullPath(options.CacheDirectory),
            OutputDirectory = Path.GetFullPath(options.OutputDirectory),
            ConfigurationPath = Path.GetFullPath(options.ConfigurationPath),
        };
    }

    public FflogsCredentials LoadCredentials()
    {
        var environmentClientId = Environment.GetEnvironmentVariable("FFLOGS_CLIENT_ID");
        var environmentClientSecret = Environment.GetEnvironmentVariable("FFLOGS_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(environmentClientId) &&
            !string.IsNullOrWhiteSpace(environmentClientSecret))
        {
            return new FflogsCredentials(environmentClientId.Trim(), environmentClientSecret.Trim());
        }

        using var configuration = JsonDocument.Parse(File.ReadAllText(ConfigurationPath));
        if (!configuration.RootElement.TryGetProperty("Fflogs", out var fflogs))
        {
            throw new InvalidDataException($"Configuration '{ConfigurationPath}' has no Fflogs section.");
        }

        var clientId = fflogs.GetProperty("ClientId").GetString();
        var clientSecret = fflogs.GetProperty("ClientSecret").GetString();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidDataException(
                "FFLogs credentials are missing. Set FFLOGS_CLIENT_ID/FFLOGS_CLIENT_SECRET or configure DACT.");
        }

        return new FflogsCredentials(clientId.Trim(), clientSecret.Trim());
    }

    public static string Usage => """
        FFLogs rDPS Parity Harness

          --samples N       Valid DNC samples to collect (100-300, default 200)
          --cache-dir PATH  Raw GraphQL response cache
          --output-dir PATH CSV/JSON/Markdown output
          --config PATH     DACT configuration used only to read API credentials
          --refresh         Ignore existing response cache
          --collect-only    Fetch/cache samples without replay
          --replay-only     Replay the cached manifest without network requests
          --self-test       Run deterministic statistics checks without network requests
          --devilment-probe Run the cached SAM/DRG per-action Devilment audit without network requests
          --guaranteed-hit-experiment Run cache-only aggregate guaranteed-hit candidate elimination
          --attribution-matrix Run the cache-only cross-provider Crit/DH attribution matrix
          --percentage-audit Run the cache-only fixed percentage-damage attribution audit
          --percentage-ordering-only Run only the cache-only percentage/rate ordering probe
          --matched-ownership-mining Mine same-actor longitudinal ownership controls only
          --matched-ownership-replay Replay cached longitudinal controls without network requests
          --matched-ownership-dh-audit Exhaust cached metadata for DH-only matched controls
          --production-candidate-evaluation Compare PercentageFirst and SharedBaseLog from cache
          --quality-snapshot PATH Write the generated production quality snapshot to PATH
          --mine-targeted-matrix Mine only targeted BRD→G-CDH public fights, cache them, then run the matrix
          --help            Show this help
        """;

    private static int ParseSampleCount(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            count is < 100 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Sample count must be between 100 and 300.");
        }

        return count;
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string argument)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{argument} requires a value.");
        }

        return args[index];
    }
}
