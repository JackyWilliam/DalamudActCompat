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
