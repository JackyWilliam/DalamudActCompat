using System.Globalization;
using System.Text;
using DalamudActCompat.Ai;

// AI 助手 without a network: the provider catalog and model picks, the reply-button allow-list,
// every job's notes, and the review numbers computed from a synthetic network log.
internal static class AiAssistantSmokeTests
{
    public static void Run(string testRoot)
    {
        Providers();
        Actions();
        JobNotes();
        ReviewFromLog(testRoot);
        PromptCarriesJobNotes();
    }

    private static void Providers()
    {
        var ids = AiProviders.All.Select(provider => provider.Id).ToArray();
        Assert(ids.Distinct().Count() == ids.Length && ids.All(id => id.All(c => c is >= 'a' and <= 'z' or '-')),
            "Provider ids must be unique key-file-safe names.");
        Assert(AiProviders.Normalize("no-such-provider") == AiProviders.Anthropic && AiProviders.Normalize("openai") == AiProviders.OpenAi,
            "Unknown providers must fall back to Claude as older builds did.");
        foreach (var id in new[] { "openai", "anthropic", "gemini", "xai", "deepseek", "moonshot", "qwen", "doubao", "zhipu", "minimax", "mistral", "openrouter", "local", "compatible" })
            Assert(ids.Contains(id), $"Provider template missing: {id}");
        Assert(AiProviders.All.Where(provider => provider.Id != AiProviders.Compatible).All(provider => provider.Endpoint.StartsWith("http", StringComparison.Ordinal)),
            "Every provider template needs its official address.");

        var listed = AiProviders.ChatModels(["models/gemini-2.5-pro", "models/gemini-3-pro", "models/text-embedding-004", "models/gemini-2.5-flash-image", "models/imagen-4.0"]);
        Assert(listed.SequenceEqual(["gemini-2.5-pro", "gemini-3-pro"]), "Model list must drop the models/ prefix and non-chat models: " + string.Join(",", listed));
        Assert(AiProviders.Recommend(AiProviders.Get("gemini"), listed) == "gemini-3-pro", "Gemini should recommend the newest Pro.");
        var openAi = AiProviders.ChatModels(["gpt-5.6", "gpt-5.6-mini", "gpt-6-astra", "gpt-4o-2024-08-06", "text-embedding-3-large", "tts-1", "o3"]);
        Assert(AiProviders.Recommend(AiProviders.Get("openai"), openAi) == "gpt-6-astra", "OpenAI should recommend the newest general GPT, not mini or snapshots.");
        Assert(AiProviders.Recommend(AiProviders.Get("deepseek"), ["deepseek-v4-flash", "deepseek-v4-pro"]) == "deepseek-v4-pro", "DeepSeek should prefer Pro.");
        Assert(AiProviders.Recommend(AiProviders.Get("moonshot"), ["kimi-k2.6", "kimi-k3", "moonshot-v1-8k"]) == "kimi-k3", "Kimi should prefer the newest K model.");
        Assert(AiProviders.Recommend(AiProviders.Get("qwen"), ["qwen-plus", "qwen3-max", "qwen3.8-max", "qwen3.8-flash"]) == "qwen3.8-max", "Qwen should prefer the newest Max.");
        Assert(AiProviders.Recommend(AiProviders.Get("doubao"), []) == "doubao-seed-1-8-251228", "Without a list Doubao falls back to its template model.");
        Assert(AiProviders.Recommend(AiProviders.Get("zhipu"), ["glm-4.6", "glm-5", "glm-4.5-air"]) == "glm-5", "GLM should prefer the newest version.");
        Assert(AiProviders.Recommend(AiProviders.Get("anthropic"), ["claude-opus-4-8", AiClient.RecommendedClaudeModel], AiClient.RecommendedClaudeModel) == AiClient.RecommendedClaudeModel,
            "Claude keeps its pinned recommendation when listed.");
        var settings = new AiSettings { Provider = "local" };
        Assert(settings.ResolvedEndpoint == "http://localhost:11434/v1" && AiProviders.Get("local").KeyOptional, "Local models need no key.");
        settings.Endpoint = "http://localhost:1234/v1/";
        Assert(settings.ResolvedEndpoint == "http://localhost:1234/v1", "A typed address replaces the template's.");

        var store = new AiKeyStore(Path.Combine(Path.GetTempPath(), "dact-ai-keys-" + Guid.NewGuid().ToString("N")), Path.Combine(Path.GetTempPath(), "dact-ai-legacy-" + Guid.NewGuid().ToString("N") + ".dat"));
        try
        {
            store.Save("deepseek", "sk-one");
            store.Save("openai", "sk-two");
            Assert(store.Load("deepseek") == "sk-one" && store.Load("openai") == "sk-two" && !store.Has("qwen"), "Keys must be stored per provider.");
            store.Clear("deepseek");
            Assert(!store.Has("deepseek") && store.Has("openai"), "Clearing one provider's key must keep the others.");
        }
        finally
        {
            foreach (var id in ids) store.Clear(id);
        }
    }

    private static void Actions()
    {
        bool Allowed(AiMode mode, string id) => AiPrompts.IsAllowedAction(mode, id, 10, topic => topic == "CombatMeter");
        Assert(Allowed(AiMode.Diagnose, "restart_parser") && Allowed(AiMode.Diagnose, "restart_host:matcha") && Allowed(AiMode.Review, "open:history"),
            "Offered actions must stay allowed.");
        Assert(!Allowed(AiMode.Diagnose, "send_to_developer") && !Allowed(AiMode.Review, "restart_parser") && !Allowed(AiMode.Diagnose, "restart_host:other") &&
               !Allowed(AiMode.Ask, "setting:11") && !Allowed(AiMode.Ask, "setting:0") && !Allowed(AiMode.Ask, "help:Unknown") && !Allowed(AiMode.Ask, "open:somewhere") &&
               Allowed(AiMode.Ask, "setting:10") && Allowed(AiMode.Ask, "help:CombatMeter"),
            "Reply buttons must be limited to what the mode's prompt offered.");
    }

    private static void JobNotes()
    {
        string[] jobs = ["PLD", "WAR", "DRK", "GNB", "WHM", "SCH", "AST", "SGE", "MNK", "DRG", "NIN", "SAM", "RPR", "VPR", "BRD", "MCH", "DNC", "BLM", "SMN", "RDM", "PCT"];
        foreach (var job in jobs)
        {
            var notes = AiJobKnowledge.For(job, id => "技能" + id.ToString(CultureInfo.InvariantCulture), id => "状态" + id.ToString(CultureInfo.InvariantCulture));
            Assert(notes is { Length: > 800 } && notes.Contains("Checks xivanalysis applies", StringComparison.Ordinal) && !notes.Contains("[a:", StringComparison.Ordinal) &&
                   !notes.Contains("[s:", StringComparison.Ordinal) && notes.Contains("「技能", StringComparison.Ordinal), $"Job notes for {job} did not load or resolve.");
        }
        Assert(AiJobKnowledge.For("Lnc", _ => null, _ => null)?.Contains("Battle Litany", StringComparison.Ordinal) == true, "Base classes must use their job's notes.");
        Assert(AiJobKnowledge.For("BLU", _ => null, _ => null) is null && AiJobKnowledge.For("", _ => null, _ => null) is null, "Jobs without notes must return none.");
        var english = AiJobKnowledge.For("DRG", id => id == 3557 ? "Battle Litany" : null, _ => null)!;
        Assert(english.Contains("Battle Litany", StringComparison.Ordinal) && !english.Contains("「Battle Litany」", StringComparison.Ordinal),
            "An English client must not repeat the same name.");
    }

    // A two-minute DRG fight: GCDs every 2.5 s with a 10 s gap, Lance Charge twice, Battle Litany
    // once, a teammate's Brotherhood, a DoT on the boss, a potion and a death.
    private static void ReviewFromLog(string testRoot)
    {
        var directory = Path.Combine(testRoot, "ai-review-log");
        Directory.CreateDirectory(directory);
        var start = new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.FromHours(8));
        var lines = new List<string>();
        void Line(double at, string body) => lines.Add($"{body[..2]}|{start.AddSeconds(at):yyyy-MM-ddTHH:mm:ss.fffffffzzz}|{body[3..]}|0000000000000000");
        string Cast(string type, string source, string name, uint action, string actionName, string target, int sequence)
        {
            var fields = new string[47];
            Array.Fill(fields, "0");
            fields[0] = type; fields[1] = "TIME"; fields[2] = source; fields[3] = name; fields[4] = action.ToString("X", CultureInfo.InvariantCulture);
            fields[5] = actionName; fields[6] = target; fields[7] = "Boss"; fields[44] = sequence.ToString("X8", CultureInfo.InvariantCulture);
            return string.Join('|', fields.Where((_, index) => index != 1));
        }
        Line(-60, "00|0039||old line before the fight");
        var sequence = 1;
        for (var at = 0.0; at < 120; at += 2.5)
        {
            if (at is > 40 and < 50) continue; // a 10 s gap
            Line(at, Cast("21", "10000001", "Player", 75, "精准刺", "40000001", sequence++));
        }
        Line(1, Cast("21", "10000001", "Player", 85, "猛枪", "10000001", sequence++));
        Line(62, Cast("21", "10000001", "Player", 85, "猛枪", "10000001", sequence++));
        Line(1.2, Cast("21", "10000001", "Player", 3557, "战斗连祷", "10000001", sequence++));
        Line(0.8, Cast("21", "10000002", "Teammate", 7396, "义结金兰", "10000002", sequence++));
        // An AoE hitting two targets is one cast.
        Line(30, Cast("22", "10000001", "Player", 7397, "音速刺", "40000001", sequence));
        Line(30, Cast("22", "10000001", "Player", 7397, "音速刺", "40000002", sequence++));
        Line(3, "26|A9F|樱花缭乱|24.00|10000001|Player|40000001|Boss|00|1000|1000");
        Line(27, "26|A9F|樱花缭乱|24.00|10000001|Player|40000001|Boss|00|1000|1000");
        Line(80, "26|A9F|樱花缭乱|24.00|10000001|Player|40000001|Boss|00|1000|1000");
        Line(5, "26|31|强化药|30.00|10000001|Player|10000001|Player|00|1000|1000");
        Line(100, "25|10000001|Player|40000001|Boss");
        Line(125, "21|10000001|Player|4B|after the fight|40000001|Boss");
        var file = Path.Combine(directory, "Network_30304_20261107.log");
        File.WriteAllLines(file, lines.OrderBy(line => line[3..30], StringComparer.Ordinal), new UTF8Encoding(false));
        // The reader only opens logs written during the fight, judged by the file times.
        File.SetCreationTimeUtc(file, start.UtcDateTime.AddHours(-1));
        File.SetLastWriteTimeUtc(file, start.UtcDateTime.AddMinutes(5));

        var log = AiCombatLog.Read(directory, start, start.AddSeconds(120), "Player")
                  ?? throw new Exception("The review did not find the player's casts in the log.");
        var info = new Dictionary<uint, AiActionInfo>
        {
            [75] = new("精准刺", true, 2.5f, 0), [7397] = new("音速刺", true, 2.5f, 0),
            [85] = new("猛枪", false, 60, 0), [3557] = new("战斗连祷", false, 120, 0),
        };
        var text = AiReviewData.Describe(log, 120, new Dictionary<string, string> { ["Teammate"] = "队友1·MNK" }, id => info.GetValueOrDefault(id));
        Assert(!text.Contains("Player", StringComparison.Ordinal) && !text.Contains("Teammate", StringComparison.Ordinal), "Review data must not carry character names.");
        Assert(text.Contains("GCD: 46 casts", StringComparison.Ordinal) && text.Contains("typical interval 2.50 s", StringComparison.Ordinal),
            "GCD count must dedupe AoE lines and skip casts outside the fight:\n" + text);
        Assert(text.Contains("0:40 10.0 s", StringComparison.Ordinal),
            "The 10 s GCD gap must be reported:\n" + text);
        Assert(text.Contains("猛枪 #85 (60 s): 2 of at most 2 possible", StringComparison.Ordinal) && text.Contains("held 1 s in total", StringComparison.Ordinal),
            "Cooldown uses and holding time must be computed:\n" + text);
        Assert(text.Contains("战斗连祷 #3557 (120 s): 1 of at most 1 possible", StringComparison.Ordinal), "A 120 s cooldown fits once in two minutes:\n" + text);
        Assert(text.Contains("Potions: 0:05", StringComparison.Ordinal) && text.Contains("Your deaths: 1:40", StringComparison.Ordinal),
            "Potions and deaths must be found:\n" + text);
        Assert(text.Contains("义结金兰 (队友1·MNK): 0:00", StringComparison.Ordinal) && text.Contains("战斗连祷 (你)", StringComparison.Ordinal),
            "Raid buffs must be labeled without names:\n" + text);
        Assert(text.Contains("樱花缭乱", StringComparison.Ordinal) && text.Contains("Uptime of your DoTs/debuffs on enemies", StringComparison.Ordinal),
            "DoT uptime must be reported:\n" + text);
        Assert(text.Contains("- 0:00: ", StringComparison.Ordinal) && text.Contains("Opener (first 25 s): 0:00 精准刺", StringComparison.Ordinal),
            "Burst window and opener must list the player's casts:\n" + text);
        Assert(AiCombatLog.Read(directory, start, start.AddSeconds(120), "Player@World")?.PlayerId == "10000001",
            "History names carry @World; the log writes the bare name.");
        Assert(AiCombatLog.Read(directory, start.AddDays(-2), start.AddDays(-2).AddSeconds(60), "Player") is null,
            "A fight the log does not cover must report no cast data.");
    }

    private static void PromptCarriesJobNotes()
    {
        var review = AiPrompts.System(AiMode.Review, "zh-CN", "NOTES-MARKER");
        var diagnose = AiPrompts.System(AiMode.Diagnose, "zh-CN", "NOTES-MARKER");
        Assert(review.Contains("NOTES-MARKER", StringComparison.Ordinal) && review.Contains("FFXIV combat knowledge", StringComparison.Ordinal) &&
               review.Contains("xivanalysis", StringComparison.Ordinal) && !diagnose.Contains("NOTES-MARKER", StringComparison.Ordinal),
            "Reviews must always carry the combat knowledge and the job notes; other modes must not.");
        Assert(!review.Contains("DACT does not record individual casts", StringComparison.Ordinal), "The review prompt must not deny the cast data it now receives.");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
