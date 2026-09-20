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
    // Auto is resolved by DACT before reaching ACT; the four persisted native
    // values above must not move when adding a DACT-only mode.
    Auto = 4,
}

internal static class ParserScopePolicy
{
    public static ParserScope Normalize(ParserScope scope)
        => Enum.IsDefined(scope) ? scope : ParserScope.All;

    public static ParserScope ResolveEffectiveScope(ParserScope scope,
        IReadOnlyList<ActPlayerIdentity> identities)
    {
        scope = Normalize(scope);
        if (scope != ParserScope.Auto) return scope;
        // Login/zone transitions can temporarily lack the local identity. Keep
        // collecting until the roster is known instead of dropping those events.
        if (!identities.Any(static identity => identity.IsLocalPlayer)) return ParserScope.All;
        // A/B/C membership identifies an alliance even while fewer than nine
        // members are loaded. The meter's 8/24 layout is only a display choice.
        if (identities.Any(static identity => identity.PartyGroup is >= 1 and <= 3)) return ParserScope.Alliance;
        return identities.Count > 1 ? ParserScope.Party : ParserScope.Self;
    }

    public static bool Includes(ParserScope scope, ActPlayerIdentity? identity,
        IReadOnlyList<ActPlayerIdentity> identities)
    {
        scope = ResolveEffectiveScope(scope, identities);
        if (scope == ParserScope.All) return true;
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
    {
        // ACT includes an interaction when either participant belongs to the scope,
        // including incoming damage/healing. Callers resolve pets to their owners.
        scope = ResolveEffectiveScope(scope, identities);
        return scope == ParserScope.All ||
               Includes(scope, Resolve(source, identities), identities) ||
               Includes(scope, Resolve(target, identities), identities);
    }

    private static ActPlayerIdentity? Resolve(string actor, IReadOnlyList<ActPlayerIdentity> identities)
        => actor.Length == 8 && uint.TryParse(actor, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) && id != 0
            ? identities.FirstOrDefault(identity => identity.EntityId == id)
            : ActPlayerIdentityResolver.Resolve(identities, actor);
}
