namespace DalamudActCompat.ActRuntime;

public readonly record struct ActPosition(float X, float Y, float Z);

public static class ActCoordinateMapper
{
    public static ActPosition FromDalamud(float x, float y, float z)
    {
        // ACT defines X/Y as the horizontal plane and Z as height, while Dalamud uses
        // X/Z horizontally and Y vertically. Keep this swap at the game-state boundary.
        return new ActPosition(x, z, y);
    }
}

public sealed record ActPlayerPose(
    uint EntityId,
    float PositionX,
    float PositionY,
    float PositionZ,
    float Rotation)
{
    public static ActPlayerPose FromDalamud(
        uint entityId,
        float x,
        float y,
        float z,
        float rotation)
    {
        var position = ActCoordinateMapper.FromDalamud(x, y, z);
        return new ActPlayerPose(entityId, position.X, position.Y, position.Z, rotation);
    }
}

public sealed record ActPlayerIdentity(
    string Name,
    string World,
    string Job,
    bool IsLocalPlayer,
    bool IsDead)
{
    public uint EntityId { get; init; }

    public ulong ContentId { get; init; }

    public uint WorldId { get; init; }

    public byte JobId { get; init; }

    public byte Level { get; init; }

    public uint CurrentHp { get; init; }

    public uint MaxHp { get; init; }

    public ushort CurrentMp { get; init; }

    public ushort MaxMp { get; init; }

    public ushort TerritoryId { get; init; }

    public float PositionX { get; init; }

    public float PositionY { get; init; }

    public float PositionZ { get; init; }

    public float Rotation { get; init; }

    public int PartyGroup { get; init; }

    public string DisplayName
        => string.IsNullOrWhiteSpace(World) ? Name : $"{Name}@{World}";
}

public static class ActPlayerIdentityResolver
{
    public static ActPlayerIdentity? Resolve(
        IReadOnlyList<ActPlayerIdentity> identities,
        string combatantName)
    {
        if (string.Equals(combatantName, "YOU", StringComparison.OrdinalIgnoreCase))
        {
            return identities.FirstOrDefault(static identity => identity.IsLocalPlayer);
        }

        // This runs for every combatant of every published snapshot and for every combat
        // action. Formatting "Name@World" and "Name (World)" per identity to compare them cost
        // megabytes per action with a hundred players nearby, so the parts are compared in
        // place instead; the matching rules are unchanged.
        var count = identities.Count;
        for (var index = 0; index < count; index++)
        {
            var identity = identities[index];
            if (MatchesFullName(combatantName, identity))
            {
                return identity;
            }
        }

        var separator = combatantName.IndexOf('@');
        var normalized = (separator < 0 ? combatantName.AsSpan() : combatantName.AsSpan(0, separator)).Trim();
        ActPlayerIdentity? match = null;
        for (var index = 0; index < count; index++)
        {
            var identity = identities[index];
            if (!normalized.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (match is not null)
            {
                // Two players share the bare name; without a world it cannot be told apart.
                return null;
            }
            match = identity;
        }
        return match;
    }

    /// <summary>
    /// Resolves many names against one identity list. A snapshot resolves every combatant of
    /// the encounter, so with a hundred combatants and a hundred nearby players the per-name
    /// scan above is ten thousand comparisons; this indexes the list once instead. The result
    /// for any name is the same as <see cref="Resolve"/>.
    /// </summary>
    public sealed class Lookup
    {
        private readonly ActPlayerIdentity? localPlayer;
        private readonly Dictionary<string, ActPlayerIdentity> fullNames = new(StringComparer.OrdinalIgnoreCase);
        // A null value marks a bare name shared by several players.
        private readonly Dictionary<string, ActPlayerIdentity?> bareNames = new(StringComparer.OrdinalIgnoreCase);

        public Lookup(IReadOnlyList<ActPlayerIdentity> identities)
        {
            foreach (var identity in identities)
            {
                if (identity.IsLocalPlayer)
                {
                    localPlayer ??= identity;
                }

                var name = identity.Name ?? string.Empty;
                var world = identity.World ?? string.Empty;
                // TryAdd keeps the first identity in list order, as the scan returns it.
                fullNames.TryAdd(string.IsNullOrWhiteSpace(world) ? name : $"{name}@{world}", identity);
                fullNames.TryAdd($"{name} ({world})", identity);
                if (!bareNames.TryAdd(name, identity))
                {
                    bareNames[name] = null;
                }
            }
        }

        public ActPlayerIdentity? Resolve(string combatantName)
        {
            if (string.Equals(combatantName, "YOU", StringComparison.OrdinalIgnoreCase))
            {
                return localPlayer;
            }
            if (fullNames.TryGetValue(combatantName, out var exact))
            {
                return exact;
            }

            var separator = combatantName.IndexOf('@');
            var normalized = (separator < 0 ? combatantName.AsSpan() : combatantName.AsSpan(0, separator)).Trim();
            return bareNames.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(normalized, out var match)
                ? match
                : null;
        }
    }

    private static bool MatchesFullName(string combatantName, ActPlayerIdentity identity)
    {
        var value = combatantName.AsSpan();
        var name = identity.Name ?? string.Empty;
        var world = identity.World ?? string.Empty;
        // DisplayName is the bare name for a player without a known world.
        var displayNameMatches = string.IsNullOrWhiteSpace(world)
            ? value.Equals(name, StringComparison.OrdinalIgnoreCase)
            : EqualsJoined(value, name, "@", world, string.Empty);
        return displayNameMatches || EqualsJoined(value, name, " (", world, ")");
    }

    private static bool EqualsJoined(
        ReadOnlySpan<char> value,
        string name,
        string separator,
        string world,
        string suffix)
        => value.Length == name.Length + separator.Length + world.Length + suffix.Length &&
           value[..name.Length].Equals(name, StringComparison.OrdinalIgnoreCase) &&
           value.Slice(name.Length, separator.Length).SequenceEqual(separator) &&
           value.Slice(name.Length + separator.Length, world.Length)
               .Equals(world, StringComparison.OrdinalIgnoreCase) &&
           value[^suffix.Length..].SequenceEqual(suffix);
}
