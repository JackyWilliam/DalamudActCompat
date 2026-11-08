using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DalamudActCompat.Encounters;

// Samples a duty pull for 战斗记录 · 开荒进度. ACT encounters only carry totals, so the game is
// read directly while the party fights inside a duty:
// - phase: the main boss (the targetable enemy with the largest max HP) changing name;
// - how far: that boss's HP at the last sample before the pull ended;
// - wipe cause: the boss's most recent cast when the first party member fell;
// - clear or wipe: Dalamud's duty events, which ACT does not report.
internal sealed class ProgressionTracker : IDisposable
{
    private const double SampleSeconds = .25;
    // A new boss name must hold this long, and a phase must last this long, before it counts;
    // otherwise a big add or a boss going briefly untargetable would look like a phase change.
    private const double BossSettleSeconds = 2;
    private const double MinimumPhaseSeconds = 30;
    // A cast older than this when someone dies is unlikely to be what killed them.
    private const double CastRelevanceSeconds = 12;

    private readonly IFramework framework;
    private readonly IDutyState dutyState;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IDataManager data;
    private readonly ProgressionStore store;
    private readonly Func<uint?, string, string> localizeZone;
    private readonly IPluginLog log;

    private bool inPull;
    private DateTimeOffset pullStart;
    private uint territory;
    private string dutyName = string.Empty;
    private readonly List<string> bosses = [];
    private DateTimeOffset lastPhaseAt;
    private string? candidate;
    private DateTimeOffset candidateSince;
    private double bossHp = 1;
    private string? lastCast;
    private DateTimeOffset lastCastAt;
    private double? firstDeath;
    private string? wipeCast;
    private DateTimeOffset nextSample;
    private DateTimeOffset outOfCombatSince;

    public ProgressionTracker(
        IFramework framework,
        IDutyState dutyState,
        ICondition condition,
        IClientState clientState,
        IObjectTable objectTable,
        IPartyList partyList,
        IDataManager data,
        ProgressionStore store,
        Func<uint?, string, string> localizeZone,
        IPluginLog log)
    {
        this.framework = framework;
        this.dutyState = dutyState;
        this.condition = condition;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.data = data;
        this.store = store;
        this.localizeZone = localizeZone;
        this.log = log;
        framework.Update += OnUpdate;
        dutyState.DutyWiped += OnWiped;
        dutyState.DutyCompleted += OnCompleted;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        dutyState.DutyWiped -= OnWiped;
        dutyState.DutyCompleted -= OnCompleted;
    }

    private void OnUpdate(IFramework _)
    {
        try
        {
            if (!condition[ConditionFlag.BoundByDuty])
            {
                inPull = false;
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var inCombat = condition[ConditionFlag.InCombat];
            if (!inPull)
            {
                if (inCombat) StartPull(now);
                return;
            }

            // Duty events end a pull. Losing combat for a while without one (leaving the duty,
            // a reset outside the duty director) drops the pull instead of guessing its result.
            if (!inCombat)
            {
                if (outOfCombatSince == default) outOfCombatSince = now;
                else if ((now - outOfCombatSince).TotalSeconds > 30) inPull = false;
                return;
            }
            outOfCombatSince = default;

            if (now < nextSample) return;
            nextSample = now.AddSeconds(SampleSeconds);
            Sample(now);
        }
        catch (Exception error)
        {
            // Progression is a side feature; a read failure must never disturb the game frame.
            inPull = false;
            log.Debug(error, "Progression sampling stopped for this pull.");
        }
    }

    private unsafe void StartPull(DateTimeOffset now)
    {
        inPull = true;
        pullStart = now;
        territory = clientState.TerritoryType;
        bosses.Clear();
        lastPhaseAt = now;
        candidate = null;
        bossHp = 1;
        lastCast = null;
        firstDeath = null;
        wipeCast = null;
        nextSample = now;
        outOfCombatSince = default;
        var game = GameMain.Instance();
        var conditionId = game == null ? 0u : game->CurrentContentFinderConditionId;
        dutyName = conditionId > 0 && data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(conditionId) is { } row &&
                   row.Name.ToString() is { Length: > 0 } name
            ? name
            : localizeZone(territory, string.Empty);
    }

    private void Sample(DateTimeOffset now)
    {
        IBattleNpc? boss = null;
        foreach (var gameObject in objectTable)
        {
            // Combatant covers every battle NPC; the hostile flag keeps escorted or allied NPCs out.
            if (gameObject is not IBattleNpc npc || npc.BattleNpcKind != BattleNpcSubKind.Combatant ||
                (npc.StatusFlags & StatusFlags.Hostile) == 0 ||
                !npc.IsTargetable || npc.MaxHp == 0 || npc.CurrentHp == 0) continue;
            if (boss is null || npc.MaxHp > boss.MaxHp) boss = npc;
        }

        if (boss is not null)
        {
            var name = boss.Name.TextValue;
            if (bosses.Count == 0)
            {
                bosses.Add(name);
                lastPhaseAt = now;
            }
            else if (name != bosses[^1])
            {
                if (candidate != name)
                {
                    candidate = name;
                    candidateSince = now;
                }
                else if ((now - candidateSince).TotalSeconds >= BossSettleSeconds &&
                         (now - lastPhaseAt).TotalSeconds >= MinimumPhaseSeconds)
                {
                    bosses.Add(name);
                    lastPhaseAt = now;
                    candidate = null;
                }
            }
            else candidate = null;

            if (name == bosses[^1]) bossHp = (double)boss.CurrentHp / boss.MaxHp;
            if (boss.IsCasting && boss.CastActionId != 0 &&
                data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(boss.CastActionId) is { } action &&
                action.Name.ToString() is { Length: > 0 } castName)
            {
                lastCast = castName;
                lastCastAt = now;
            }
        }

        if (firstDeath is null && AnyPartyMemberDown())
        {
            firstDeath = (now - pullStart).TotalSeconds;
            wipeCast = lastCast is not null && (now - lastCastAt).TotalSeconds <= CastRelevanceSeconds ? lastCast : null;
        }
    }

    private bool AnyPartyMemberDown()
    {
        if (partyList.Length == 0)
            return objectTable.LocalPlayer is { } self && self.CurrentHp == 0;
        foreach (var member in partyList)
        {
            // Members in another zone report 0 HP as well; only those in this territory count.
            if (member.Territory.RowId == territory && member.CurrentHP == 0) return true;
        }
        return false;
    }

    private void OnWiped(IDutyStateEventArgs args) => Finish(cleared: false);

    private void OnCompleted(IDutyStateEventArgs args) => Finish(cleared: true);

    private void Finish(bool cleared)
    {
        if (!inPull) return;
        inPull = false;
        // Trash pulls and duties without a boss would only add noise to the chart.
        if (bosses.Count == 0) return;
        store.Add(new ProgressionPull(
            territory,
            dutyName,
            pullStart,
            DateTimeOffset.UtcNow,
            cleared,
            bosses.ToArray(),
            cleared ? 0 : bossHp,
            cleared ? null : wipeCast,
            firstDeath));
    }
}
