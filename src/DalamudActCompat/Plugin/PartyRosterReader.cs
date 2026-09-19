using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Group;

namespace DalamudActCompat.Plugin;

internal static class PartyRosterReader
{
    internal static unsafe IReadOnlyList<(IPartyMember Member, int PartyGroup)> Read(IPartyList partyList)
    {
        var result = new List<(IPartyMember, int)>();
        var manager = (GroupManager*)partyList.GroupManagerAddress;
        // Four-player alliance groups have a different layout. Keep their existing
        // local-party behavior instead of interpreting twenty slots as two eight-player teams.
        var alliance = partyList.IsAlliance && manager != null && !manager->MainGroup.IsSmallGroupAlliance;
        var groups = new[] { 1, 2, 3 };
        if (alliance)
        {
            var indices = manager->MainGroup.AllianceGroupIndices;
            if (indices[0] < 3 && indices[1] < 3 && indices[2] < 3 &&
                indices[0] != indices[1] && indices[0] != indices[2] && indices[1] != indices[2])
            {
                for (var slot = 0; slot < 3; slot++) groups[slot] = indices[slot] + 1;
            }
        }

        // The indexer stops at Length (the local party count), even in a 24-player
        // raid. Its separate alliance array contains only the other two parties.
        for (var index = 0; index < Math.Clamp(partyList.Length, 0, 8); index++)
            Add(partyList.CreatePartyMemberReference(partyList.GetPartyMemberAddress(index)), alliance ? groups[0] : 0);

        if (alliance)
        {
            for (var index = 0; index < 16; index++)
            {
                var address = partyList.GetAllianceMemberAddress(index);
                // Vacant alliance slots may retain a name from the previous occupant.
                if (address != 0 && (((PartyMember*)address)->Flags & 1) != 0)
                    Add(partyList.CreateAllianceMemberReference(address), groups[1 + index / 8]);
            }
        }
        return result;

        void Add(IPartyMember? member, int group)
        {
            if (member is null || string.IsNullOrWhiteSpace(member.Name.TextValue) ||
                (member.ContentId == 0 && member.EntityId is 0 or 0xE0000000)) return;
            if (result.Any(existing =>
                    (member.ContentId != 0 && member.ContentId == existing.Item1.ContentId) ||
                    (member.EntityId is not (0 or 0xE0000000) && member.EntityId == existing.Item1.EntityId))) return;
            result.Add((member, group));
        }
    }
}
