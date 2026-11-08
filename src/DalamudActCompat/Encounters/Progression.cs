using DalamudActCompat.Infrastructure.Storage;

namespace DalamudActCompat.Encounters;

// One pull of a duty for 战斗记录 · 开荒进度. ACT encounters carry neither phases nor clear/wipe,
// so ProgressionTracker samples them from the game while the pull runs (see there for how).
public sealed record ProgressionPull(
    uint TerritoryId,
    string DutyName,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    bool Cleared,
    IReadOnlyList<string> Bosses,
    double BossHpFraction,
    string? WipeCast,
    double? FirstDeathSeconds,
    // FFLogs' phase for the fights FflogsPhases knows; 0 elsewhere and in files from before it.
    int FflogsPhase = 0)
{
    // Elsewhere each main boss met in order is a phase; a pull that saw none still counts as phase 1.
    public int Phase => FflogsPhase > 0 ? FflogsPhase : Math.Max(1, Bosses.Count);

    public string? LastBoss => Bosses.Count > 0 ? Bosses[^1] : null;

    // How far into the current phase the party got (the boss HP already taken off).
    public double PhaseFraction => Cleared ? 1 : Math.Clamp(1 - BossHpFraction, 0, 1);

    public double Progress => Phase - 1 + PhaseFraction;
}

// A wipe cause shown under 最常见的团灭点: the boss cast right before the first death, or
// (when nothing was cast) the boss and roughly how much HP it had left.
internal sealed record WipePoint(int Phase, string? Cast, string? Boss, int HpBucket, int Count);

internal sealed record DutyProgress(
    uint TerritoryId,
    string Name,
    IReadOnlyList<ProgressionPull> Pulls,
    int MaxPhase,
    bool Cleared,
    int BestIndex,
    IReadOnlyList<WipePoint> WipePoints)
{
    public ProgressionPull Best => Pulls[BestIndex];

    public DateTimeOffset LastPull => Pulls[^1].EndTime;

    internal static IReadOnlyList<DutyProgress> Summarize(IReadOnlyList<ProgressionPull> pulls)
        => pulls
            .GroupBy(pull => pull.TerritoryId)
            .Select(group => Create(group.OrderBy(pull => pull.StartTime).ToArray()))
            .OrderByDescending(duty => duty.LastPull)
            .ToArray();

    private static DutyProgress Create(ProgressionPull[] pulls)
    {
        var best = 0;
        for (var index = 1; index < pulls.Length; index++)
            if (pulls[index].Progress >= pulls[best].Progress) best = index;
        static (int Phase, string? Cast, string? Boss, int Hp) Cause(ProgressionPull pull)
            => pull.WipeCast is { Length: > 0 } cast
                ? (pull.Phase, cast, null, 0)
                : (pull.Phase, null, pull.LastBoss, HpBucket(pull.BossHpFraction));
        var wipes = pulls
            .Where(pull => !pull.Cleared)
            .GroupBy(Cause)
            .Select(group => new WipePoint(group.Key.Phase, group.Key.Cast, group.Key.Boss, group.Key.Hp, group.Count()))
            .OrderByDescending(point => point.Count)
            .ThenByDescending(point => point.Phase)
            .Take(3)
            .ToArray();
        return new DutyProgress(
            pulls[0].TerritoryId,
            pulls[^1].DutyName,
            pulls,
            pulls.Max(pull => pull.Phase),
            pulls.Any(pull => pull.Cleared),
            best,
            wipes);
    }

    // Nearest 10 % of boss HP left, so wipes a few percent apart land in the same row.
    internal static int HpBucket(double fraction) => (int)Math.Round(Math.Clamp(fraction, 0, 1) * 10) * 10;

    internal double AverageProgress(IEnumerable<ProgressionPull> pulls)
        => pulls.Select(pull => pull.Cleared ? MaxPhase : pull.Progress).DefaultIfEmpty(0).Average();
}

public sealed class ProgressionStore
{
    // Weeks of nightly progression stay well under this; the oldest pulls go first.
    internal const int MaximumPulls = 3000;

    private readonly JsonFileStore jsonStore;
    private readonly string path;
    private readonly object gate = new();
    private ProgressionPull[] pulls = [];
    private Task pendingSave = Task.CompletedTask;

    // Brings pulls saved by an older version up to date as they load (see FflogsPhases.Restore).
    private readonly Func<ProgressionPull, ProgressionPull> upgrade;

    public ProgressionStore(JsonFileStore jsonStore, string path, Func<ProgressionPull, ProgressionPull>? upgrade = null)
    {
        this.jsonStore = jsonStore;
        this.path = path;
        this.upgrade = upgrade ?? (pull => pull);
    }

    // The UI reads this every frame, so it is an immutable snapshot swapped on change.
    public IReadOnlyList<ProgressionPull> Pulls => Volatile.Read(ref pulls);

    public int Version { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var loaded = await jsonStore.ReadAsync<List<ProgressionPull>>(path, cancellationToken).ConfigureAwait(false);
        if (loaded is null) return;
        lock (gate)
        {
            Volatile.Write(ref pulls, loaded.OrderBy(pull => pull.StartTime).TakeLast(MaximumPulls).Select(upgrade).ToArray());
            Version++;
        }
    }

    public void Add(ProgressionPull pull)
    {
        ProgressionPull[] next;
        lock (gate)
        {
            next = pulls.Append(pull).TakeLast(MaximumPulls).ToArray();
            Volatile.Write(ref pulls, next);
            Version++;
            // Saves run one after another so an older list never overwrites a newer one.
            var previous = pendingSave;
            pendingSave = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { }
                await jsonStore.WriteAsync(path, next, CancellationToken.None).ConfigureAwait(false);
            });
        }
    }

    public Task FlushAsync()
    {
        lock (gate) return pendingSave;
    }
}
