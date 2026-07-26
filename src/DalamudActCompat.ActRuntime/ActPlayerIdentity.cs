namespace DalamudActCompat.ActRuntime;

public sealed record ActPlayerIdentity(
    string Name,
    string World,
    string Job,
    bool IsLocalPlayer,
    bool IsDead)
{
    public string DisplayName
        => string.IsNullOrWhiteSpace(World) ? Name : $"{Name}@{World}";
}

public static class ActPlayerIdentityResolver
{
    public static ActPlayerIdentity? Resolve(
        IReadOnlyList<ActPlayerIdentity> identities,
        string combatantName)
    {
        var exact = identities.FirstOrDefault(identity =>
            string.Equals(combatantName, identity.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                combatantName,
                $"{identity.Name} ({identity.World})",
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var separator = combatantName.IndexOf('@');
        var normalized = (separator < 0 ? combatantName : combatantName[..separator]).Trim();
        var matches = identities
            .Where(identity => string.Equals(identity.Name, normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
