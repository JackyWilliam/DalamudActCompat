using System.IO.Pipes;
using System.Text.Json;

namespace DalamudActCompat.Host;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = HostOptions.Parse(args);
        if (string.IsNullOrWhiteSpace(options.PipeName))
        {
            Console.Error.WriteLine("Missing required --pipe <name> argument.");
            return 2;
        }

        Console.WriteLine("DalamudActCompat compatibility host starting.");
        Console.WriteLine($"Pipe: {options.PipeName}");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            await RunPipeServerAsync(options, shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Compatibility host stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunPipeServerAsync(HostOptions options, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(
            options.PipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Console.WriteLine("Waiting for Dalamud plugin IPC client.");
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Dalamud plugin IPC client connected.");

        await using var writer = new StreamWriter(pipe)
        {
            AutoFlush = true,
        };

        var start = DateTimeOffset.UtcNow;
        var tick = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (options.SampleMode)
            {
                var message = HostIpcMessage.CreateSample(start, tick++);
                var json = JsonSerializer.Serialize(message);
                await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed record HostOptions(string PipeName, bool SampleMode)
{
    public static HostOptions Parse(string[] args)
    {
        var pipeName = string.Empty;
        var sample = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pipe" when index + 1 < args.Length:
                    pipeName = args[++index];
                    break;
                case "--sample":
                    sample = true;
                    break;
            }
        }

        return new HostOptions(pipeName, sample);
    }
}

internal sealed record HostIpcMessage(
    string Type,
    HostEncounterDto? Current,
    IReadOnlyList<HostEncounterDto> Recent,
    string? Message)
{
    public static HostIpcMessage CreateSample(DateTimeOffset start, int tick)
    {
        var scale = tick + 1;
        var encounter = new HostEncounterDto(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            start,
            null,
            "Host Sample Zone",
            "IPC Training Dummy",
            new[]
            {
                new HostCombatantDto("local", "You", "SAM", true, 1_245_000 + (38_500 * scale), 18_000, 0),
                new HostCombatantDto("p2", "Party Member A", "WHM", false, 420_000 + (9_500 * scale), 965_000 + (16_000 * scale), 0),
                new HostCombatantDto("p3", "Party Member B", "DRG", false, 1_030_000 + (31_000 * scale), 12_000, 1),
                new HostCombatantDto("p4", "Party Member C", "BRD", false, 880_000 + (26_000 * scale), 24_000, 0),
            });

        return new HostIpcMessage("snapshot", encounter, Array.Empty<HostEncounterDto>(), null);
    }
}

internal sealed record HostEncounterDto(
    Guid Id,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    string ZoneName,
    string EnemyName,
    IReadOnlyList<HostCombatantDto> Combatants);

internal sealed record HostCombatantDto(
    string Id,
    string Name,
    string Job,
    bool IsLocalPlayer,
    long TotalDamage,
    long TotalHealing,
    int Deaths);
