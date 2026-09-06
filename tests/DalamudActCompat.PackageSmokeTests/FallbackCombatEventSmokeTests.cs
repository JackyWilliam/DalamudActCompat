using System.Reflection;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;

internal static class FallbackCombatEventSmokeTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 12, 0, 0, TimeSpan.FromHours(8));

    public static void Run()
    {
        foreach (var mode in Enum.GetValues<EncounterMode>())
        {
            ValidateDelayedCombatFlag(mode);
            ValidateUnconfirmedCombatTimeout(mode);
            ValidatePartyContinues(mode, confirmed: false);
            ValidatePartyContinues(mode, confirmed: true);
            ValidateAlreadyInCombatAndReset(mode);
        }
        Console.WriteLine("Fallback combat event smoke tests passed (15 deterministic runtime scenarios).");
    }

    private static void ValidateDelayedCombatFlag(EncounterMode mode)
    {
        using var fixture = new RuntimeFixture(mode);
        fixture.Damage(Start);
        fixture.Tick(Start);
        fixture.Tick(Start.AddMilliseconds(249));
        Check(fixture.Events.Count == 0,
            $"{mode}: combat ended before the initial fallback snapshot was ready to publish.");
        fixture.Tick(Start.AddMilliseconds(250));
        Check(fixture.Events.Count == 1 && !fixture.Events[0].Finished,
            $"{mode}: fresh damage emitted Start and End before the game combat flag caught up.");
        var encounterId = fixture.Events[0].Snapshot.Id;
        fixture.Tick(Start.AddSeconds(1));
        fixture.InCombat = true;
        fixture.Tick(Start.AddMilliseconds(1100));
        fixture.Damage(Start.AddSeconds(2));
        fixture.Tick(Start.AddMilliseconds(2250));
        Check(fixture.Events.All(item => !item.Finished && item.Snapshot.Id == encounterId),
            $"{mode}: delayed combat confirmation split a live fallback encounter.");

        fixture.InCombat = false;
        fixture.Tick(Start.AddMilliseconds(2300));
        Check(fixture.Events[^1].Finished && fixture.Events[^1].Snapshot.Id == encounterId &&
              fixture.Events[^1].Snapshot.Combatants.Single().TotalDamage == 2000 &&
              fixture.Events[^1].Snapshot.EndTime == Start.AddSeconds(2),
            $"{mode}: a confirmed combat exit was delayed or lost its damage/end timestamp.");
        var eventCount = fixture.Events.Count;
        fixture.Tick(Start.AddSeconds(3));
        Check(fixture.Events.Count == eventCount, $"{mode}: combat completion was emitted twice.");

        fixture.Damage(Start.AddSeconds(4));
        fixture.Tick(Start.AddMilliseconds(4250));
        Check(!fixture.Events[^1].Finished && fixture.Events[^1].Snapshot.Id != encounterId &&
              fixture.Events[^1].Snapshot.Combatants.Single().TotalDamage == 1000,
            $"{mode}: the next pull inherited prior combat confirmation or damage.");
    }

    private static void ValidateUnconfirmedCombatTimeout(EncounterMode mode)
    {
        using var fixture = new RuntimeFixture(mode);
        fixture.Damage(Start);
        fixture.Tick(Start.AddMilliseconds(250));
        fixture.Tick(Start.AddMilliseconds(4999));
        Check(fixture.Events.Count == 1 && !fixture.Events[0].Finished,
            $"{mode}: an unconfirmed combat ended before the inactivity grace.");

        // Local InCombat can remain false while a party member attacks. New damage
        // must renew the grace, but a permanently false flag must not leave combat stuck.
        var lastDamage = Start.AddMilliseconds(4999);
        fixture.Damage(lastDamage);
        fixture.Tick(Start.AddSeconds(5));
        fixture.Tick(lastDamage.AddMilliseconds(4999));
        Check(fixture.Events.All(item => !item.Finished),
            $"{mode}: continued party damage did not renew the unconfirmed combat grace.");
        fixture.Tick(lastDamage + OpenWorldEncounterEndPolicy.InactivityGrace);
        Check(fixture.Events.Count(item => item.Finished) == 1 &&
              fixture.Events[^1].Snapshot.EndTime == lastDamage,
            $"{mode}: inactive fallback combat was stuck or included the grace in its final clock.");
    }

    private static void ValidatePartyContinues(EncounterMode mode, bool confirmed)
    {
        using var fixture = new RuntimeFixture(mode) { InCombat = confirmed };
        fixture.Damage(Start);
        fixture.Tick(Start.AddMilliseconds(250));
        fixture.InCombat = false;
        fixture.PartyContinuesAfterLocalDeath = true;
        fixture.Tick(Start.AddMinutes(1));
        Check(fixture.Events.All(item => !item.Finished),
            $"{mode}: local death ended combat while the party was still fighting.");
        fixture.PartyContinuesAfterLocalDeath = false;
        fixture.Tick(Start.AddMinutes(1).AddMilliseconds(1));
        Check(fixture.Events.Count(item => item.Finished) == 1,
            $"{mode}: combat did not finish when the surviving party stopped fighting.");
    }

    private static void ValidateAlreadyInCombatAndReset(EncounterMode mode)
    {
        using var fixture = new RuntimeFixture(mode) { InCombat = true };
        fixture.Damage(Start);
        // Capture an already-true state at the damage callback, even when the
        // next framework update observes the exit of a very short real combat.
        fixture.InCombat = false;
        fixture.Tick(Start.AddMilliseconds(250));
        Check(fixture.Events.Count(item => item.Finished) == 1,
            $"{mode}: an already-confirmed short combat was unnecessarily delayed.");

        fixture.InCombat = true;
        fixture.Damage(Start.AddSeconds(1));
        fixture.Tick(Start.AddMilliseconds(1250));
        fixture.Runtime.StopParser();
        fixture.Events.Clear();
        fixture.InCombat = false;
        fixture.Damage(Start.AddSeconds(2));
        fixture.Tick(Start.AddMilliseconds(2250));
        Check(fixture.Events.Count == 1 && !fixture.Events[0].Finished,
            $"{mode}: parser reset leaked combat confirmation into the next fallback encounter.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private static readonly MethodInfo RecordDamage = typeof(SelfHostedActRuntime).GetMethod(
            "RecordFallbackDamageUnsafe", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly ActPlayerIdentity identity = new("Fallback Player", "", "PLD", true, false)
        {
            EntityId = 0x10000001, CurrentHp = 100, MaxHp = 100,
        };

        public RuntimeFixture(EncounterMode mode)
        {
            // This is the real managed runtime with inert Dalamud services. Never
            // start the parser, Host, native hooks, or any user configuration here.
            Runtime = new SelfHostedActRuntime(
                null!, DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>(), null!,
                () => true, () => identity.Name, () => [identity], () => null,
                null!, DispatchProxy.Create<IFramework, NoOpPluginLogProxy>(), null!,
                () => new(134, 0, mode, InCombat, false, false),
                null!, null!, null!, _ => null, () => PartyContinuesAfterLocalDeath,
                _ => new(), () => new Dictionary<string, HtmlOverlayWindowSettings>(),
                () => { }, () => false, () => false, (_, _) => false);
            Runtime.EncounterChanged += (snapshot, finished) => Events.Add((snapshot, finished));
        }

        public SelfHostedActRuntime Runtime { get; }
        public bool InCombat { get; set; }
        public bool PartyContinuesAfterLocalDeath { get; set; }
        public List<(ActEncounterSnapshot Snapshot, bool Finished)> Events { get; } = [];

        public void Damage(DateTimeOffset timestamp)
            => RecordDamage.Invoke(Runtime,
                [timestamp, identity.Name, "Training Dummy", 1000L, "Hit", false, false, "Diagnostic Zone"]);

        // An explicit frame time exercises exact grace boundaries without sleeps
        // or machine-speed assumptions; snapshots still come from production code.
        public void Tick(DateTimeOffset timestamp) => Runtime.UpdateFrameworkState(timestamp);

        public void Dispose() => Runtime.Dispose();
    }
}
