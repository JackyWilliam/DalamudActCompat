using System.Globalization;

namespace DalamudActCompat.ActRuntime;

// Keep persisted values aligned with ACT's None/Self/Party/Alliance modes.
// Zero preserves the unrestricted parser used by configurations predating this setting.
public enum ParserScope
{
    All = 0,
    Self = 1,
    Party = 2,
    Alliance = 3,
}

internal static class ParserScopePolicy
{
    public static ParserScope Normalize(ParserScope scope)
        => Enum.IsDefined(scope) ? scope : ParserScope.All;

    public static bool Includes(ParserScope scope, ActPlayerIdentity? identity,
        IReadOnlyList<ActPlayerIdentity> identities)
    {
        if (Normalize(scope) == ParserScope.All) return true;
        if (identity is null) return false;
        if (identity.IsLocalPlayer) return true;
        return scope switch
        {
            ParserScope.Alliance => true,
            // In an alliance the local party can be A, B, or C. Group zero is
            // the ordinary party; never assume that alliance group A is ours.
            ParserScope.Party => identities.FirstOrDefault(static member => member.IsLocalPlayer)
                is { } local && identity.PartyGroup == local.PartyGroup,
            _ => false,
        };
    }

    public static bool IncludesEvent(ParserScope scope, string source, string target,
        IReadOnlyList<ActPlayerIdentity> identities)
        // ACT includes an interaction when either participant belongs to the scope,
        // including incoming damage/healing. Callers resolve pets to their owners.
        => Normalize(scope) == ParserScope.All ||
           Includes(scope, Resolve(source, identities), identities) ||
           Includes(scope, Resolve(target, identities), identities);

    private static ActPlayerIdentity? Resolve(string actor, IReadOnlyList<ActPlayerIdentity> identities)
        => actor.Length == 8 && uint.TryParse(actor, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) && id != 0
            ? identities.FirstOrDefault(identity => identity.EntityId == id)
            : ActPlayerIdentityResolver.Resolve(identities, actor);
}
