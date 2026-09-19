using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.InteropServices;
using Advanced_Combat_Tracker;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Core.State;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using NativeMember = FFXIVClientStructs.FFXIV.Client.Game.Group.PartyMember;

internal static class AllianceRosterSmokeTests
{
    internal static unsafe void Run()
    {
        ActGlobals.Init();
        var memory = (GroupManager*)NativeMemory.AllocZeroed((nuint)sizeof(GroupManager));
        try
        {
            var party = DispatchProxy.Create<IPartyList, AlliancePartyProxy>();
            var proxy = (AlliancePartyProxy)(object)party;
            proxy.Manager = (nint)memory;
            for (var slot = 0; slot < 28; slot++)
            {
                var address = slot < 8
                    ? (nint)Unsafe.AsPointer(ref memory->MainGroup.PartyMembers[slot])
                    : (nint)Unsafe.AsPointer(ref memory->MainGroup.AllianceMembers[slot - 8]);
                var native = (NativeMember*)address;
                native->Flags = 1;
                proxy.Members[address] = Member(slot + 1);
            }
            foreach (var count in new[] { 0, 1, 4, 8 })
            {
                memory->MainGroup.MemberCount = (byte)count;
                memory->MainGroup.AllianceFlags = 0;
                var rows = PartyRosterReader.Read(party);
                Check(rows.Count == count && rows.All(row => row.PartyGroup == 0), $"Normal {count}-player party changed.");
            }
            memory->MainGroup.MemberCount = 8;
            memory->MainGroup.AllianceFlags = 1;
            foreach (var mapping in new[] { new byte[] { 0, 1, 2 }, new byte[] { 1, 0, 2 }, new byte[] { 2, 0, 1 } })
            {
                mapping.CopyTo(memory->MainGroup.AllianceGroupIndices);
                // This reproduces the actual API contract that the former 24-index
                // loop missed: Length/indexer expose eight, separate addresses expose sixteen.
                Check(Enumerable.Range(0, 24).Count(i => party[i] is not null) == 8, "Fixture no longer reproduces the reported eight-player limit.");
                var roster = PartyRosterReader.Read(party);
                Check(roster.Count == 24 && roster.Select(row => row.Member.ContentId).Distinct().Count() == 24,
                    "Alliance roster lost players or admitted the unused final four slots.");
                Check(roster.Take(8).All(row => row.PartyGroup == mapping[0] + 1) &&
                      roster.Skip(8).Take(8).All(row => row.PartyGroup == mapping[1] + 1) &&
                      roster.Skip(16).All(row => row.PartyGroup == mapping[2] + 1), "A/B/C grouping changed when the local party moved.");

                var identities = (IReadOnlyList<ActPlayerIdentity>)typeof(Plugin)
                    .GetMethod("BuildPlayerIdentities", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [EntityServiceProxy.Create<IPlayerState>(new() { ["EntityId"] = 0x10000001u }),
                        party, EntityServiceProxy.Create<IObjectTable>(new()), false])!;
                var encounter = new EncounterData("Player 1", "Alliance fixture", false, null!);
                encounter.SetAllies(identities.Select(id => new CombatantData(id.Name, encounter))
                    .Append(new CombatantData("Unrelated player or NPC", encounter))
                    .Append(new CombatantData("Limit Break", encounter)).ToList());
                var resolved = SelfHostedActRuntime.ResolveEncounterCombatants(encounter, identities, [], identities.Count);
                Check(resolved.Count == 25 && resolved.Count(row => row.Identity is not null) == 24,
                    "ACT whitelist lost alliance players or admitted unrelated entities.");
                var at = DateTimeOffset.UtcNow;
                var mapped = ActEncounterMapper.Map(new ActEncounterSnapshot(Guid.NewGuid(), at.AddMinutes(-1), at,
                    "Alliance fixture", "Boss", identities.Select(id => new ActCombatantSnapshot(id.DisplayName,
                        id.DisplayName, "PLD", id.IsLocalPlayer, 1000, 0, 0, PartyGroup: id.PartyGroup)).ToArray())
                    { PartyCapacity = 24 });
                var settings = new MeterSettings();
                var rows = new MeterService(new EncounterStateStore(), settings).GetRows(mapped);
                var select = typeof(MeterWindow).GetMethod("SelectClassicRows", BindingFlags.Static | BindingFlags.NonPublic)!;
                Check(((IReadOnlyList<CombatantRow>)select.Invoke(null, [rows, settings])!).Count == 8,
                    "Default local-party mode no longer shows eight.");
                settings.ClassicAllianceView = true;
                Check(((IReadOnlyList<CombatantRow>)select.Invoke(null, [rows, settings])!).Count == 24,
                    "24-player mode still truncates a complete roster.");
            }

            memory->MainGroup.AllianceMembers[0].Flags = 0;
            Check(PartyRosterReader.Read(party).Count == 23, "A stale name in a vacant alliance slot leaked into the roster.");
            memory->MainGroup.AllianceMembers[0].Flags = 1;
            proxy.Members[(nint)Unsafe.AsPointer(ref memory->MainGroup.AllianceMembers[0])] = Member(1);
            Check(PartyRosterReader.Read(party).Count == 23, "Duplicate local/alliance member was counted twice.");
            memory->MainGroup.AllianceFlags = 3;
            memory->MainGroup.MemberCount = 4;
            Check(PartyRosterReader.Read(party).Count == 4, "A small-group alliance was read as two eight-player groups.");
            Console.WriteLine("Alliance roster: real eight-entry indexer contract, 24-player pipeline, A/B/C, 0/1/4/8 parties, vacancies, duplicates and small-group isolation passed.");
        }
        finally { NativeMemory.Free(memory); }
    }

    private static IPartyMember Member(int id) => EntityServiceProxy.Create<IPartyMember>(new()
    {
        ["ContentId"] = (ulong)id,
        ["EntityId"] = 0x10000000u + (uint)id,
        ["Name"] = new SeString(new TextPayload($"Player {id}")),
    });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public class AlliancePartyProxy : DispatchProxy
{
    public nint Manager;
    public Dictionary<nint, IPartyMember> Members = [];

    protected override unsafe object? Invoke(MethodInfo? method, object?[]? args)
    {
        var group = &((GroupManager*)Manager)->MainGroup;
        return method!.Name switch
        {
            "get_GroupManagerAddress" => Manager,
            "get_IsAlliance" => group->AllianceFlags > 0,
            "get_Length" => (int)group->MemberCount,
            "get_Item" => (int)args![0]! < group->MemberCount
                ? Members[(nint)Unsafe.AsPointer(ref group->PartyMembers[(int)args[0]!])] : null,
            "GetPartyMemberAddress" => (nint)Unsafe.AsPointer(ref group->PartyMembers[(int)args![0]!]),
            "GetAllianceMemberAddress" => (nint)Unsafe.AsPointer(ref group->AllianceMembers[(int)args![0]!]),
            "CreatePartyMemberReference" or "CreateAllianceMemberReference" => Members.GetValueOrDefault((nint)args![0]!),
            _ => throw new NotSupportedException(method.Name),
        };
    }
}
