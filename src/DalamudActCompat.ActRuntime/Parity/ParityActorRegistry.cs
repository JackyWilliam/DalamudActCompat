namespace DalamudActCompat.ActRuntime.Parity;

internal sealed class ParityActorRegistry
{
    private readonly Dictionary<string, ActorState> actors;
    private readonly IReadOnlySet<string> partyActorIds;

    private ParityActorRegistry(
        Dictionary<string, ActorState> actors,
        IReadOnlySet<string> partyActorIds)
    {
        this.actors = actors;
        this.partyActorIds = partyActorIds;
    }

    public static ParityActorRegistry Create(
        IEnumerable<ParityReplayEvent> events,
        IReadOnlySet<string> partyActorIds)
    {
        var result = new Dictionary<string, ActorState>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events.OrderBy(static item => item.Sequence))
        {
            RememberActor(item.SourceId, item.SourceName, item.Job, item.OwnerId, item.IsPartyMember);
            RememberActor(item.TargetId, item.TargetName, string.Empty, string.Empty, false);
        }
        return new ParityActorRegistry(result, partyActorIds);

        void RememberActor(string id, string name, string job, string ownerId, bool isPartyMember)
        {
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var key = ActorKey(id, name);
            if (!result.TryGetValue(key, out var actor))
            {
                actor = new ActorState(id, name, job, ownerId, isPartyMember);
            }
            else
            {
                actor = actor with
                {
                    Id = string.IsNullOrWhiteSpace(id) ? actor.Id : id,
                    Name = string.IsNullOrWhiteSpace(name) ? actor.Name : name,
                    Job = string.IsNullOrWhiteSpace(job) ? actor.Job : job,
                    OwnerId = string.IsNullOrWhiteSpace(ownerId) ? actor.OwnerId : ownerId,
                    IsPartyMember = actor.IsPartyMember || isPartyMember,
                };
            }

            actor = actor with
            {
                IsPartyMember = actor.IsPartyMember ||
                                (!string.IsNullOrWhiteSpace(actor.Id) && partyActorIds.Contains(actor.Id)),
            };
            result[key] = actor;
        }
    }

    public ActorState? Resolve(string id, string name)
        => actors.GetValueOrDefault(ActorKey(id, name));

    public bool IsPartyActor(string id, string name)
        => IsPartyActor(Resolve(id, name));

    public bool IsPartyActor(ActorState? actor)
        => actor is not null &&
           (actor.IsPartyMember ||
            (!string.IsNullOrWhiteSpace(actor.Id) && partyActorIds.Contains(actor.Id)));

    public static string ActorKey(string id, string name)
        => !string.IsNullOrWhiteSpace(id)
            ? $"id:{id.Trim().ToUpperInvariant()}"
            : $"name:{name.Trim().ToUpperInvariant()}";

    internal sealed record ActorState(
        string Id,
        string Name,
        string Job,
        string OwnerId,
        bool IsPartyMember);
}
