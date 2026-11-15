using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Advanced_Combat_Tracker;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;

// Reported after 1.0.0.8: alliance raids and crowded field operations fell to about 2 fps and
// recovered after restarting DACT, while the same content with few players nearby was fine.
// The parser thread rebuilt the whole encounter snapshot for every single combat action, and
// the game's frame thread waits on the same locks every frame.
internal static class CrowdedEncounterSmokeTests
{
    private const int NearbyPlayers = 120;
    private const int PartyPlayers = 8;
    private const int Enemies = 12;
    private const int Swings = 3000;
    // A thousand actions per second, fed in real time: 120 players with area attacks and heals.
    private const double SwingSpacingMs = 1;
    private static readonly DateTimeOffset At = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    internal static void Run()
    {
        VerifyIdentityResolution();
        VerifyPublication();
    }

    // The scan was rewritten to compare in place and an index was added for whole snapshots.
    // Both must keep the original rules, spelled out here as the strings they used to build.
    private static void VerifyIdentityResolution()
    {
        ActPlayerIdentity[] identities =
        [
            new("Local Player", "Alpha", "PLD", true, false),
            new("Twin Name", "Alpha", "WHM", false, false),
            new("Twin Name", "Beta", "BLM", false, false),
            new("Twin Name", "", "NIN", false, false),
            new("No World", "", "SAM", false, false),
            new("Spaced World", " ", "DRG", false, false),
            new("Unique", "Gamma", "SCH", false, false),
            new("unique", "Delta", "AST", false, false),
            new("At@Sign", "Gamma", "MCH", false, false),
        ];
        string[] names =
        [
            "YOU", "you", "Local Player", "local player@ALPHA", "Local Player (Alpha)", "Local Player@Beta",
            "Twin Name", "Twin Name@Alpha", "twin name (beta)", "Twin Name@Gamma", "Twin Name ()",
            "No World", " No World ", "No World@Anywhere", "No World ()", "Spaced World", "Spaced World ( )",
            "Spaced World@ ", "Unique", "UNIQUE@Gamma", "unique@Delta", "Unique (Delta)", "At@Sign", "At@Sign@Gamma",
            "At", "Nobody", "", "@", "Local Player@", " Local Player@Alpha",
        ];
        var lookup = new ActPlayerIdentityResolver.Lookup(identities);
        foreach (var name in names)
        {
            var expected = ResolveByFormatting(identities, name);
            Check(ReferenceEquals(ActPlayerIdentityResolver.Resolve(identities, name), expected),
                $"In-place identity resolution changed the result for '{name}'.");
            Check(ReferenceEquals(lookup.Resolve(name), expected),
                $"Indexed identity resolution changed the result for '{name}'.");
        }
    }

    private static ActPlayerIdentity? ResolveByFormatting(ActPlayerIdentity[] identities, string combatantName)
    {
        if (string.Equals(combatantName, "YOU", StringComparison.OrdinalIgnoreCase))
            return identities.FirstOrDefault(identity => identity.IsLocalPlayer);
        var exact = identities.FirstOrDefault(identity =>
            string.Equals(combatantName, identity.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(combatantName, $"{identity.Name} ({identity.World})", StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var separator = combatantName.IndexOf('@');
        var normalized = (separator < 0 ? combatantName : combatantName[..separator]).Trim();
        var matches = identities
            .Where(identity => string.Equals(identity.Name, normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static void VerifyPublication()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        ActGlobals.Init();
        var previous = ActGlobals.oFormActMain;
        var form = (FormActMain)RuntimeHelpers.GetUninitializedObject(typeof(FormActMain));
        GC.SuppressFinalize(form);
        ActGlobals.oFormActMain = form;
        Advanced_Combat_Tracker.Resources.NotActMainFormatter.SetupEnvironment();
        // The game reports a home world for every player while ACT names combatants without
        // one, which is the slow path of identity resolution: no exact match for anybody.
        var observed = Enumerable.Range(0, NearbyPlayers)
            .Select(index => new ActPlayerIdentity($"Player{index:000}", "World", "WAR", index == 0, false)
            {
                EntityId = 0x10000000u + (uint)index,
            })
            .ToArray();
        var roster = observed[..PartyPlayers];
        try
        {
            using var runtime = new SelfHostedActRuntime(null!, DispatchProxy.Create<IPluginLog, MeterRuntimeLogProxy>(),
                null!, () => true, () => observed[0].Name, () => roster, () => null, null!,
                DispatchProxy.Create<IFramework, NoOpPluginLogProxy>(), null!,
                () => new(134, 0, EncounterMode.OpenWorld, true, false, false), null!, null!, null!,
                _ => null, () => false, _ => new(), () => new Dictionary<string, HtmlOverlayWindowSettings>(),
                () => { }, () => false, () => false, (_, _) => false, () => ParserScope.All, () => observed);
            var publications = 0;
            ActEncounterSnapshot? latest = null;
            var finished = false;
            runtime.EncounterChanged += (data, done) =>
            {
                publications++;
                latest = data;
                finished = done;
            };
            runtime.UpdateFrameworkState(At);
            var afterAction = typeof(SelfHostedActRuntime).GetMethod("OnAfterCombatAction", flags)!
                .CreateDelegate<Action<bool, CombatActionEventArgs>>(runtime);
            var encounter = new EncounterData(observed[0].Name, "Offline", null!);

            // Stands in for the game's frame thread, which enters the runtime once per frame.
            var frames = 0;
            var slowestFrame = TimeSpan.Zero;
            var frameTime = TimeSpan.Zero;
            using var stop = new CancellationTokenSource();
            var frameThread = new Thread(() =>
            {
                // The first snapshots compile the publishing code while holding the locks.
                // That happens once per session, so the first half second is not counted.
                var warmUp = Stopwatch.StartNew();
                while (!stop.IsCancellationRequested)
                {
                    var started = Stopwatch.GetTimestamp();
                    runtime.UpdateFrameworkState(null);
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    if (warmUp.ElapsedMilliseconds >= 500)
                    {
                        frames++;
                        frameTime += elapsed;
                        if (elapsed > slowestFrame) slowestFrame = elapsed;
                    }
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            frameThread.Start();

            var enemies = Enumerable.Range(0, Enemies).Select(index => $"Enemy{index:00}").ToArray();
            var allocated = 0L;
            var collectionsBefore = GC.CollectionCount(0);
            var watch = new Stopwatch();
            var pace = Stopwatch.StartNew();
            for (var index = 0; index < Swings; index++)
            {
                // Arriving at the game's pace leaves the locks free between actions, as in
                // play; a tight loop would starve the frame thread whatever the handler costs.
                while (pace.Elapsed.TotalMilliseconds < index * SwingSpacingMs) Thread.SpinWait(50);
                var time = At.AddMilliseconds(index * SwingSpacingMs);
                // Each player's fourth hit is the largest, so the running maximum has to move
                // off the first hit and then stay put through the equal hits that follow.
                var big = index / NearbyPlayers == 3;
                var swing = new MasterSwing(2, false, big ? 150 : 100, time.LocalDateTime, index, big ? "Big Hit" : "Hit",
                    observed[index % NearbyPlayers].Name, "damage", enemies[index % Enemies]);
                // ACT's own bookkeeping is upstream cost; only DACT's handler is measured.
                encounter.AddCombatAction(swing);
                var action = new CombatActionEventArgs(swing);
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                watch.Start();
                afterAction(false, action);
                watch.Stop();
                allocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            }
            var collections = GC.CollectionCount(0) - collectionsBefore;
            stop.Cancel();
            frameThread.Join();

            var livePublications = publications;
            Console.WriteLine(
                $"Crowded encounter: {Swings} actions in {pace.Elapsed.TotalSeconds:0.0} s, {NearbyPlayers} nearby players, " +
                $"handler {watch.Elapsed.TotalMilliseconds:0} ms total, {watch.Elapsed.TotalMicroseconds / Swings:0.0} us/action, " +
                $"{allocated / 1024d / Swings:0.0} KiB/action, {collections} gen0 collections, " +
                $"{livePublications} live publications, frame thread {frames} frames, " +
                $"slowest {slowestFrame.TotalMilliseconds:0.00} ms, average {frameTime.TotalMilliseconds / Math.Max(1, frames):0.000} ms.");

            // Timings vary with the machine and are only printed. These two do not: every
            // action used to publish a snapshot and allocate megabytes resolving names.
            Check(livePublications <= Swings / 10,
                $"Live snapshots were not coalesced: {livePublications} for {Swings} actions.");
            Check(allocated / Swings <= 64 * 1024,
                $"A combat action allocated {allocated / 1024d / Swings:0.0} KiB on average.");

            // Coalescing must not lose or delay anything for long. With no further action to
            // carry them, the next frame delivers what was still waiting...
            var perPlayer = Swings / NearbyPlayers * 100L + 50;
            runtime.UpdateFrameworkState(At.AddMilliseconds(Swings * SwingSpacingMs).AddSeconds(1));
            Check(!finished && latest?.ParsedPlayers is { Count: NearbyPlayers } live &&
                  live.All(player => player.TotalDamage == perPlayer),
                "The last actions of a burst never reached the live snapshot.");
            // ...and the finished encounter carries every action.
            typeof(SelfHostedActRuntime).GetMethod("OnAfterCombatEnd", flags)!.Invoke(runtime, [encounter]);
            Check(finished && latest?.ParsedPlayers is { Count: NearbyPlayers } players &&
                  players.All(player => player.TotalDamage == perPlayer),
                "The finished crowded encounter lost a nearby player or part of its damage.");
            // The largest hit is now tracked as swings arrive instead of rescanned per snapshot.
            foreach (var identity in observed)
            {
                var rescanned = SelfHostedActRuntime.GetHighestDamageHit(encounter.GetCombatant(identity.Name)!);
                var row = latest!.ParsedPlayers!.Single(player => player.Name == identity.DisplayName);
                Check(rescanned is { Action: "Big Hit", Amount: 150 } &&
                      row.HighestDamageAction == rescanned.Action && row.HighestDamage == rescanned.Amount,
                    $"Incremental highest damage for {identity.Name} is {row.HighestDamageAction}/{row.HighestDamage}.");
            }
            // Nobody heals in this scenario, so ACT's ally graph holds the local player alone.
            Check(latest!.Combatants is { Count: 1 } party && party[0].IsLocalPlayer && party[0].TotalDamage == perPlayer,
                "The finished crowded encounter lost the local player's row or part of its damage.");
        }
        finally { ActGlobals.oFormActMain = previous; }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
