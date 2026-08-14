using System.Globalization;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class FflogsEventNormalizer
{
    private static readonly HashSet<long> RaidBuffStatusIds =
    [
        0x756, 0x4A1, 0xA8F, 0xA27, 0x511, 0xE65, 0x839, 0x71E,
        0xF09, 0xF2F, 0xF31, 0x312, 0x4C5, 0x721, 0x08D,
    ];

    private static readonly HashSet<string> StatusApplyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "applybuff", "refreshbuff", "applydebuff", "refreshdebuff",
        "applybuffstack", "applydebuffstack",
    };

    private static readonly HashSet<string> StatusRemoveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "removebuff", "removedebuff", "removebuffstack", "removedebuffstack",
    };

    public static NormalizedFight Normalize(CachedFightSample sample)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(sample.MetadataPath));
        var report = metadata.RootElement
            .GetProperty("data")
            .GetProperty("reportData")
            .GetProperty("report");
        if (report.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException("Cached report metadata is null.");
        }

        var reportStartTime = GetLong(report, "startTime");
        var fightElement = report.GetProperty("fights").EnumerateArray()
            .Single(fight => GetInt(fight, "id") == sample.Seed.FightId);
        var fight = new FflogsFight(
            GetInt(fightElement, "id"),
            GetInt(fightElement, "encounterID"),
            GetString(fightElement, "name"),
            GetDouble(fightElement, "startTime"),
            GetDouble(fightElement, "endTime"),
            GetDouble(fightElement, "combatTime"),
            GetBoolean(fightElement, "kill"),
            GetInt(fightElement, "difficulty"));
        if (!fight.Kill)
        {
            throw new InvalidDataException("Ranking seed resolved to a non-kill fight.");
        }

        var masterData = report.GetProperty("masterData");
        var actors = ParseActors(masterData.GetProperty("actors"));
        var abilities = masterData.GetProperty("abilities").EnumerateArray()
            .Where(static ability => ability.TryGetProperty("gameID", out _))
            .GroupBy(static ability => GetLong(ability, "gameID"))
            .ToDictionary(
                static group => group.Key,
                static group => GetString(group.First(), "name"));
        var table = report.GetProperty("damageTable").GetProperty("data");
        var tableEntries = table.TryGetProperty("entries", out var entries)
            ? entries.EnumerateArray().ToArray()
            : [];
        var tableActorIds = tableEntries
            .Select(static entry => GetInt(entry, "id"))
            .Where(static id => id != 0)
            .ToHashSet();
        var party = actors.Values
            .Where(actor =>
                tableActorIds.Contains(actor.Id) &&
                string.Equals(actor.Type, "Player", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(actor.Job) &&
                !string.Equals(actor.Job, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(actor.Job, "LimitBreak", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(static actor => actor.Id)
            .OrderBy(static actor => actor.Job, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static actor => actor.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dancer = party.FirstOrDefault(actor =>
                         string.Equals(actor.Job, "Dancer", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(actor.Name, sample.Seed.ActorName, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException(
                         $"DamageDone table has no Dancer actor matching ranking seed '{sample.Seed.ActorName}'.");

        var normalizedEvents = ParseEvents(sample.EventPaths, abilities);
        if (normalizedEvents.Count == 0)
        {
            throw new InvalidDataException("Filtered FFLogs event cache is empty.");
        }

        var warnings = new List<string>();
        if (fight.EncounterId != sample.Seed.EncounterId)
        {
            warnings.Add($"Seed encounter {sample.Seed.EncounterId} resolved to fight encounter {fight.EncounterId}.");
        }
        if (Math.Abs(fight.CombatTime - sample.Seed.DurationMilliseconds) > 1)
        {
            warnings.Add(
                $"Ranking duration {sample.Seed.DurationMilliseconds:F3}ms differs from combatTime {fight.CombatTime:F3}ms.");
        }

        var partyIds = party.Select(static actor => actor.Id).ToHashSet();
        var damageTableTotal = tableEntries.Sum(static entry => GetLong(entry, "total"));
        var dancerTableEntry = tableEntries.FirstOrDefault(entry => GetInt(entry, "id") == dancer.Id);
        var dancerTableTotal = dancerTableEntry.ValueKind == JsonValueKind.Undefined
            ? 0
            : GetLong(dancerTableEntry, "total");
        if (dancerTableTotal <= 0)
        {
            warnings.Add("Dancer DamageDone table amount is missing or non-positive.");
        }

        var metricDurationMilliseconds = GetDouble(table, "totalTime");
        if (metricDurationMilliseconds <= 0)
        {
            metricDurationMilliseconds = GetDouble(table, "combatTime");
        }
        if (metricDurationMilliseconds <= 0)
        {
            metricDurationMilliseconds = fight.EndTime - fight.StartTime;
            warnings.Add("DamageDone table duration was missing; wall duration is used as the metric denominator.");
        }

        var fflogsMetrics = ParseDamageTableMetrics(dancerTableEntry, metricDurationMilliseconds);
        var damageEvents = normalizedEvents.Where(IsDamageEvent).ToArray();
        var unmatchedDirectDamage = damageEvents.Count(static item =>
            !item.IsPeriodic && !item.MatchedCalculatedDamage);
        if (unmatchedDirectDamage > 0)
        {
            warnings.Add(
                $"{unmatchedDirectDamage}/{damageEvents.Length} direct damage events lacked a packet-matched calculateddamage timestamp.");
        }

        var technicalPresent = HasDancerStatus(normalizedEvents, dancer.Id, 0x71E);
        var standardPresent = HasDancerStatus(normalizedEvents, dancer.Id, 0x839);
        var devilmentPresent = HasDancerStatus(normalizedEvents, dancer.Id, 0x721);
        var technicalRankResolved = CountResolvedTechnicalApplications(normalizedEvents, dancer.Id);
        if (technicalPresent && technicalRankResolved == 0)
        {
            warnings.Add("Technical Finish status was observed without a nearby three/four-step damage action.");
        }

        var partnerJob = ResolveDancePartnerJob(normalizedEvents, actors, dancer.Id);
        var (maximumOverlap, hasOverlap) = ResolveMaximumRaidBuffOverlap(
            normalizedEvents,
            actors,
            partyIds);
        var deaths = normalizedEvents.Count(item =>
            string.Equals(item.Type, "death", StringComparison.OrdinalIgnoreCase) &&
            partyIds.Contains(item.TargetId));
        var resurrections = normalizedEvents.Count(item =>
            string.Equals(item.Type, "resurrect", StringComparison.OrdinalIgnoreCase) &&
            partyIds.Contains(item.TargetId));
        // Resolve pet participation directly from source actors; owner jobs are what matter for grouping.
        var participatingPetJobs = normalizedEvents
            .Where(IsDamageEvent)
            .Select(item => actors.GetValueOrDefault(item.SourceId))
            .Where(static source => source is not null &&
                                    string.Equals(source.Type, "Pet", StringComparison.OrdinalIgnoreCase) &&
                                    source.PetOwnerId is not null)
            .Select(source => actors.GetValueOrDefault(source!.PetOwnerId!.Value))
            .Where(owner => owner is not null && partyIds.Contains(owner.Id))
            .Select(static owner => owner!.Job)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static job => job, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dotJobs = normalizedEvents
            .Where(static item => IsDamageEvent(item) && item.IsPeriodic && item.Amount > 0)
            .Select(item => ResolveOwnerActor(item.SourceId, actors))
            .Where(owner => owner is not null && partyIds.Contains(owner.Id))
            .Select(static owner => owner!.Job)
            .Where(static job => !string.IsNullOrWhiteSpace(job))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static job => job, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var wallDuration = Math.Max(0, (fight.EndTime - fight.StartTime) / 1000d);
        var combatDuration = Math.Max(0, fight.CombatTime / 1000d);
        var damageDowntime = table.TryGetProperty("damageDowntime", out var downtimeValue) &&
                             downtimeValue.ValueKind == JsonValueKind.Number
            ? Math.Max(0, downtimeValue.GetDouble() / 1000d)
            : Math.Max(0, wallDuration - combatDuration);

        return new NormalizedFight(
            sample.Seed,
            reportStartTime,
            fight,
            dancer,
            party,
            actors,
            normalizedEvents,
            damageTableTotal,
            dancerTableTotal,
            fflogsMetrics,
            string.Join("/", party.Select(static actor => ToJobAbbreviation(actor.Job))),
            ToJobAbbreviation(partnerJob),
            technicalPresent,
            standardPresent,
            devilmentPresent,
            hasOverlap,
            maximumOverlap,
            deaths,
            resurrections,
            participatingPetJobs.Length > 0,
            string.Join("/", participatingPetJobs.Select(ToJobAbbreviation)),
            dotJobs.Length > 0,
            string.Join("/", dotJobs.Select(ToJobAbbreviation)),
            metricDurationMilliseconds / 1000d,
            wallDuration,
            combatDuration,
            damageDowntime,
            technicalRankResolved,
            warnings);
    }

    public static NormalizedAttributionFight NormalizeAttribution(CachedFightSample sample)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(sample.MetadataPath));
        var report = metadata.RootElement
            .GetProperty("data")
            .GetProperty("reportData")
            .GetProperty("report");
        if (report.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException("Cached report metadata is null.");
        }

        var fightElement = report.GetProperty("fights").EnumerateArray()
            .Single(fight => GetInt(fight, "id") == sample.Seed.FightId);
        var fight = new FflogsFight(
            GetInt(fightElement, "id"),
            GetInt(fightElement, "encounterID"),
            GetString(fightElement, "name"),
            GetDouble(fightElement, "startTime"),
            GetDouble(fightElement, "endTime"),
            GetDouble(fightElement, "combatTime"),
            GetBoolean(fightElement, "kill"),
            GetInt(fightElement, "difficulty"));
        if (!fight.Kill)
        {
            throw new InvalidDataException("Attribution matrix accepts kill fights only.");
        }

        var masterData = report.GetProperty("masterData");
        var actors = ParseActors(masterData.GetProperty("actors"));
        var abilities = masterData.GetProperty("abilities").EnumerateArray()
            .Where(static ability => ability.TryGetProperty("gameID", out _))
            .GroupBy(static ability => GetLong(ability, "gameID"))
            .ToDictionary(
                static group => group.Key,
                static group => GetString(group.First(), "name"));
        var table = report.GetProperty("damageTable").GetProperty("data");
        var tableEntries = table.TryGetProperty("entries", out var entries)
            ? entries.EnumerateArray().ToArray()
            : [];
        var damageActors = tableEntries
            .Select(entry => ParseDamageTableActor(entry, actors))
            .Where(static actor => actor.ActorId != 0)
            .ToDictionary(static actor => actor.ActorId);
        var party = actors.Values
            .Where(actor =>
                damageActors.ContainsKey(actor.Id) &&
                string.Equals(actor.Type, "Player", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(actor.Job) &&
                !string.Equals(actor.Job, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(actor.Job, "LimitBreak", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(static actor => actor.Id)
            .OrderBy(static actor => actor.Job, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static actor => actor.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedEvents = ParseEvents(sample.EventPaths, abilities);
        if (normalizedEvents.Count == 0)
        {
            throw new InvalidDataException("Filtered FFLogs event cache is empty.");
        }

        var warnings = new List<string>();
        var metricDurationMilliseconds = GetDouble(table, "totalTime");
        if (metricDurationMilliseconds <= 0)
        {
            metricDurationMilliseconds = GetDouble(table, "combatTime");
        }
        if (metricDurationMilliseconds <= 0)
        {
            metricDurationMilliseconds = fight.EndTime - fight.StartTime;
            warnings.Add("DamageDone duration was missing; wall duration is used for matrix provenance.");
        }
        var damageEvents = normalizedEvents.Where(IsDamageEvent).ToArray();
        var unmatched = damageEvents.Count(static item => !item.IsPeriodic && !item.MatchedCalculatedDamage);
        if (unmatched > 0)
        {
            warnings.Add($"{unmatched}/{damageEvents.Length} direct events lack calculateddamage packet correlation.");
        }

        return new NormalizedAttributionFight(
            sample.Seed,
            GetLong(report, "startTime"),
            fight,
            party,
            actors,
            normalizedEvents,
            damageActors,
            metricDurationMilliseconds / 1000d,
            string.Join("/", party.Select(static actor => ToJobAbbreviation(actor.Job))),
            "9",
            warnings);
    }

    private static FflogsDamageTableActor ParseDamageTableActor(
        JsonElement entry,
        IReadOnlyDictionary<int, FflogsActor> actors)
    {
        var actorId = GetInt(entry, "id");
        var actor = actors.GetValueOrDefault(actorId);
        return new FflogsDamageTableActor(
            actorId,
            GetString(entry, "name"),
            actor?.Job ?? GetString(entry, "icon"),
            GetLong(entry, "total"),
            GetDouble(entry, "totalRDPS"),
            GetDouble(entry, "totalRDPSTaken"),
            GetDouble(entry, "totalRDPSGiven"),
            ParseContributions(entry, "given"),
            ParseContributions(entry, "taken"));
    }

    private static Dictionary<int, FflogsActor> ParseActors(JsonElement actorArray)
        => actorArray.EnumerateArray()
            .Select(actor => new FflogsActor(
                GetInt(actor, "id"),
                GetLong(actor, "gameID"),
                GetString(actor, "name"),
                GetString(actor, "type"),
                GetString(actor, "subType"),
                actor.TryGetProperty("petOwner", out var owner) && owner.ValueKind == JsonValueKind.Number
                    ? owner.GetInt32()
                    : null))
            .Where(static actor => actor.Id != 0)
            .GroupBy(static actor => actor.Id)
            .ToDictionary(static group => group.Key, static group => group.First());

    private static IReadOnlyList<NormalizedFflogsEvent> ParseEvents(
        IReadOnlyList<string> eventPaths,
        IReadOnlyDictionary<long, string> abilities)
    {
        var parsed = new List<NormalizedFflogsEvent>();
        long sequence = 0;
        foreach (var path in eventPaths)
        {
            using var page = JsonDocument.Parse(File.ReadAllText(path));
            var eventArray = page.RootElement
                .GetProperty("data")
                .GetProperty("reportData")
                .GetProperty("report")
                .GetProperty("events")
                .GetProperty("data");
            foreach (var item in eventArray.EnumerateArray())
            {
                var type = GetString(item, "type");
                var apiAbilityId = GetLong(item, "abilityGameID");
                var abilityId = NormalizeAbilityId(type, apiAbilityId);
                var timestamp = GetDouble(item, "timestamp");
                parsed.Add(new NormalizedFflogsEvent(
                    sequence++,
                    timestamp,
                    timestamp,
                    type,
                    GetInt(item, "sourceID"),
                    GetInt(item, "targetID"),
                    apiAbilityId,
                    abilityId,
                    abilities.GetValueOrDefault(apiAbilityId, apiAbilityId.ToString(CultureInfo.InvariantCulture)),
                    GetLong(item, "amount"),
                    GetDouble(item, "duration"),
                    GetLong(item, "extraAbilityGameID"),
                    GetInt(item, "stack"),
                    GetLong(item, "overkill"),
                    GetLong(item, "absorbed"),
                    GetInt(item, "hitType") == 2,
                    GetBoolean(item, "directHit"),
                    GetBoolean(item, "tick"),
                    GetNullableInt(item, "sourceInstance"),
                    GetNullableInt(item, "targetInstance"),
                    GetNullableLong(item, "packetID"),
                    MatchedCalculatedDamage: false));
            }
        }

        return CorrelateCalculatedDamage(parsed)
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => item.Sequence)
            .ToArray();
    }

    internal static IReadOnlyList<NormalizedFflogsEvent> CorrelateCalculatedDamage(
        IReadOnlyList<NormalizedFflogsEvent> parsed)
    {
        var result = new List<NormalizedFflogsEvent>(parsed.Count);
        var calculatedByPacket = new Dictionary<DamagePacketKey, Queue<NormalizedFflogsEvent>>();
        foreach (var item in parsed.OrderBy(static item => item.Timestamp).ThenBy(static item => item.Sequence))
        {
            if (string.Equals(item.Type, "calculateddamage", StringComparison.OrdinalIgnoreCase))
            {
                if (item.PacketId is { } packetId)
                {
                    var key = new DamagePacketKey(packetId, item.SourceId, item.TargetId, item.ApiAbilityId);
                    if (!calculatedByPacket.TryGetValue(key, out var queue))
                    {
                        queue = new Queue<NormalizedFflogsEvent>();
                        calculatedByPacket.Add(key, queue);
                    }
                    queue.Enqueue(item);
                }
                continue;
            }

            if (!string.Equals(item.Type, "damage", StringComparison.OrdinalIgnoreCase) ||
                item.PacketId is not { } damagePacketId)
            {
                result.Add(item);
                continue;
            }

            var damageKey = new DamagePacketKey(
                damagePacketId,
                item.SourceId,
                item.TargetId,
                item.ApiAbilityId);
            if (!calculatedByPacket.TryGetValue(damageKey, out var candidates))
            {
                result.Add(item);
                continue;
            }

            // Packet IDs are finite and can be reused in long reports. A ten-second
            // window keeps correlation mechanical while rejecting stale same-ID events.
            while (candidates.Count > 0 && item.Timestamp - candidates.Peek().Timestamp > 10_000)
            {
                candidates.Dequeue();
            }
            if (candidates.Count == 0)
            {
                calculatedByPacket.Remove(damageKey);
                result.Add(item);
                continue;
            }

            var calculated = candidates.Dequeue();
            if (candidates.Count == 0)
            {
                calculatedByPacket.Remove(damageKey);
            }
            result.Add(item with
            {
                Timestamp = calculated.Timestamp,
                Critical = calculated.Critical,
                DirectHit = calculated.DirectHit,
                IsPeriodic = item.IsPeriodic || calculated.IsPeriodic,
                MatchedCalculatedDamage = true,
            });
        }

        return result;
    }

    private static FflogsDamageTableMetrics ParseDamageTableMetrics(
        JsonElement dancerTableEntry,
        double durationMilliseconds)
    {
        if (dancerTableEntry.ValueKind == JsonValueKind.Undefined)
        {
            return new FflogsDamageTableMetrics(durationMilliseconds, 0, 0, 0, 0, 0, 0, [], []);
        }

        return new FflogsDamageTableMetrics(
            durationMilliseconds,
            GetDouble(dancerTableEntry, "total"),
            GetDouble(dancerTableEntry, "totalRDPS"),
            GetDouble(dancerTableEntry, "totalRDPSTaken"),
            GetDouble(dancerTableEntry, "totalRDPSGiven"),
            GetDouble(dancerTableEntry, "totalADPS"),
            GetDouble(dancerTableEntry, "totalNDPS"),
            ParseContributions(dancerTableEntry, "given"),
            ParseContributions(dancerTableEntry, "taken"));
    }

    private static IReadOnlyList<FflogsContribution> ParseContributions(
        JsonElement entry,
        string propertyName)
    {
        if (!entry.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Select(value => new FflogsContribution(
                NormalizeAbilityId("applybuff", GetLong(value, "guid")),
                GetString(value, "name"),
                GetDouble(value, "total")))
            .ToArray();
    }

    private static bool HasDancerStatus(
        IEnumerable<NormalizedFflogsEvent> events,
        int dancerId,
        long statusId)
        => events.Any(item =>
            item.SourceId == dancerId &&
            item.AbilityId == statusId &&
            StatusApplyTypes.Contains(item.Type));

    private static int CountResolvedTechnicalApplications(
        IReadOnlyList<NormalizedFflogsEvent> events,
        int dancerId)
        => events.Count(item =>
            item.SourceId == dancerId &&
            item.AbilityId == 0x71E &&
            StatusApplyTypes.Contains(item.Type) &&
            events.Any(action =>
                action.SourceId == dancerId &&
                action.AbilityId is 0x81C1 or 0x81C2 &&
                Math.Abs(action.Timestamp - item.Timestamp) <= 2000));

    private static string ResolveDancePartnerJob(
        IReadOnlyList<NormalizedFflogsEvent> events,
        IReadOnlyDictionary<int, FflogsActor> actors,
        int dancerId)
        => events
            .Where(item =>
                item.SourceId == dancerId &&
                item.TargetId != dancerId &&
                item.AbilityId == 0x839 &&
                StatusApplyTypes.Contains(item.Type))
            .Select(item => actors.GetValueOrDefault(item.TargetId)?.Job ?? string.Empty)
            .Where(static job => !string.IsNullOrWhiteSpace(job))
            .GroupBy(static job => job, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .Select(static group => group.Key)
            .FirstOrDefault() ?? "Unknown";

    internal static FflogsActor? ResolveDancePartnerActor(
        IReadOnlyList<NormalizedFflogsEvent> events,
        IReadOnlyDictionary<int, FflogsActor> actors,
        int dancerId)
        => events
            .Where(item =>
                item.SourceId == dancerId &&
                item.TargetId != dancerId &&
                item.AbilityId == 0x839 &&
                StatusApplyTypes.Contains(item.Type))
            .Select(item => actors.GetValueOrDefault(item.TargetId))
            .Where(static actor => actor is not null)
            .GroupBy(static actor => actor!.Id)
            .OrderByDescending(static group => group.Count())
            .Select(static group => group.First())
            .FirstOrDefault();

    private static (int Maximum, bool HasOverlap) ResolveMaximumRaidBuffOverlap(
        IReadOnlyList<NormalizedFflogsEvent> events,
        IReadOnlyDictionary<int, FflogsActor> actors,
        IReadOnlySet<int> partyIds)
    {
        var active = new HashSet<(long AbilityId, int SourceId, int TargetId)>();
        var maximum = 0;
        foreach (var item in events)
        {
            var key = (item.AbilityId, item.SourceId, item.TargetId);
            if (RaidBuffStatusIds.Contains(item.AbilityId))
            {
                if (StatusRemoveTypes.Contains(item.Type))
                {
                    active.Remove(key);
                }
                else if (StatusApplyTypes.Contains(item.Type))
                {
                    active.Add(key);
                }
            }

            if (!IsDamageEvent(item) || item.Amount <= 0)
            {
                continue;
            }

            var owner = ResolveOwnerActor(item.SourceId, actors);
            if (owner is null || !partyIds.Contains(owner.Id))
            {
                continue;
            }

            var count = active.Count(status =>
                status.SourceId != owner.Id &&
                (status.TargetId == owner.Id || status.TargetId == item.SourceId || status.TargetId == item.TargetId));
            maximum = Math.Max(maximum, count);
        }

        return (maximum, maximum >= 2);
    }

    internal static FflogsActor? ResolveOwnerActor(
        int sourceId,
        IReadOnlyDictionary<int, FflogsActor> actors)
    {
        var source = actors.GetValueOrDefault(sourceId);
        if (source?.PetOwnerId is { } ownerId)
        {
            return actors.GetValueOrDefault(ownerId);
        }

        return source;
    }

    internal static bool IsDamageEvent(NormalizedFflogsEvent item)
        => string.Equals(item.Type, "damage", StringComparison.OrdinalIgnoreCase);

    internal static bool IsStatusApply(string type) => StatusApplyTypes.Contains(type);

    internal static bool IsStatusRemove(string type) => StatusRemoveTypes.Contains(type);

    internal static long NormalizeAbilityId(string type, long apiAbilityId)
    {
        if ((StatusApplyTypes.Contains(type) || StatusRemoveTypes.Contains(type)) &&
            apiAbilityId is >= 1_000_000 and < 2_000_000)
        {
            // FFLogs namespaces FFXIV statuses by adding one million; DACT consumes
            // the raw status ID carried by 26/30 network lines.
            return apiAbilityId - 1_000_000;
        }

        return apiAbilityId switch
        {
            // FFLogs canonicalizes Technical Finish damage to legacy action IDs,
            // while current FFXIV packet lines carry the step-specific replacements.
            16_195 => 0x81C1,
            16_196 => 0x81C2,
            _ => apiAbilityId,
        };
    }

    private static string ToJobAbbreviation(string job) => job switch
    {
        "Paladin" => "PLD",
        "Warrior" => "WAR",
        "DarkKnight" => "DRK",
        "Gunbreaker" => "GNB",
        "WhiteMage" => "WHM",
        "Scholar" => "SCH",
        "Astrologian" => "AST",
        "Sage" => "SGE",
        "Monk" => "MNK",
        "Dragoon" => "DRG",
        "Ninja" => "NIN",
        "Samurai" => "SAM",
        "Reaper" => "RPR",
        "Viper" => "VPR",
        "Bard" => "BRD",
        "Machinist" => "MCH",
        "Dancer" => "DNC",
        "BlackMage" => "BLM",
        "Summoner" => "SMN",
        "RedMage" => "RDM",
        "Pictomancer" => "PCT",
        "BlueMage" => "BLU",
        "Unknown" or "" => "Unknown",
        _ => job,
    };

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt32(out var result) ? result : (int)Math.Round(value.GetDouble())
            : 0;

    private static int? GetNullableInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? GetInt(element, propertyName)
            : null;

    private static long? GetNullableLong(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? GetLong(element, propertyName)
            : null;

    private static long GetLong(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt64(out var result) ? result : (long)Math.Round(value.GetDouble())
            : 0;

    private static double GetDouble(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static bool GetBoolean(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private readonly record struct DamagePacketKey(
        long PacketId,
        int SourceId,
        int TargetId,
        long AbilityId);
}
