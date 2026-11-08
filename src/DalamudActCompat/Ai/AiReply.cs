using System.Text.Json;

namespace DalamudActCompat.Ai;

internal sealed record AiAction(string Id, string Label);

internal sealed record AiFinding(string Level, string Title, string Detail, AiAction? Action);

internal sealed record AiAdvice(string Title, string Detail);

// What the assistant answered, already split into the pieces the page draws: plain text,
// diagnose findings with a fix button, numbered review advice, jump buttons and sources.
internal sealed record AiReply(
    string Text,
    IReadOnlyList<AiFinding> Findings,
    IReadOnlyList<AiAdvice> Advice,
    IReadOnlyList<AiAction> Actions,
    IReadOnlyList<string> Sources,
    bool Escalate)
{
    public static AiReply PlainText(string text) => new(text, [], [], [], [], false);
}

internal static class AiReplyFormat
{
    // Claude gets this as a structured-output schema (the reply always parses); OpenAI-style
    // services get the same shape described in the prompt and are parsed leniently.
    public static readonly Dictionary<string, JsonElement> Schema = BuildSchema();

    private static Dictionary<string, JsonElement> BuildSchema()
    {
        static object Str() => new { type = "string" };
        var finding = new
        {
            type = "object",
            properties = new
            {
                level = new { type = "string", @enum = new[] { "error", "warn", "info" } },
                title = Str(),
                detail = Str(),
                action = Str(),
                action_label = Str(),
            },
            required = new[] { "level", "title", "detail", "action", "action_label" },
            additionalProperties = false,
        };
        var advice = new
        {
            type = "object",
            properties = new { title = Str(), detail = Str() },
            required = new[] { "title", "detail" },
            additionalProperties = false,
        };
        var action = new
        {
            type = "object",
            properties = new { id = Str(), label = Str() },
            required = new[] { "id", "label" },
            additionalProperties = false,
        };
        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                text = Str(),
                findings = new { type = "array", items = finding },
                advice = new { type = "array", items = advice },
                actions = new { type = "array", items = action },
                sources = new { type = "array", items = Str() },
                escalate = new { type = "boolean" },
            }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "text", "findings", "advice", "actions", "sources", "escalate" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };
    }

    // Accepts the bare JSON object, the object inside a ```json fence, or anything with a JSON
    // object somewhere in it; a reply that is not JSON at all is shown as plain text.
    public static AiReply Parse(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return AiReply.PlainText(raw.Trim());
        try
        {
            using var document = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return AiReply.PlainText(raw.Trim());
            string Str(JsonElement element, string name)
                => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;
            IEnumerable<JsonElement> Items(string name)
                => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

            var findings = Items("findings")
                .Where(item => item.ValueKind == JsonValueKind.Object && Str(item, "title").Length > 0)
                .Select(item =>
                {
                    var actionId = Str(item, "action");
                    var label = Str(item, "action_label");
                    return new AiFinding(Str(item, "level") is "error" or "warn" ? Str(item, "level") : "info", Str(item, "title"), Str(item, "detail"),
                        actionId.Length > 0 && label.Length > 0 ? new AiAction(actionId, label) : null);
                }).ToArray();
            var advice = Items("advice")
                .Where(item => item.ValueKind == JsonValueKind.Object && Str(item, "title").Length > 0)
                .Select(item => new AiAdvice(Str(item, "title"), Str(item, "detail"))).ToArray();
            var actions = Items("actions")
                .Where(item => item.ValueKind == JsonValueKind.Object && Str(item, "id").Length > 0 && Str(item, "label").Length > 0)
                .Select(item => new AiAction(Str(item, "id"), Str(item, "label"))).ToArray();
            var sources = Items("sources")
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim()).Where(source => source.Length > 0).ToArray();
            var escalate = root.TryGetProperty("escalate", out var flag) && flag.ValueKind == JsonValueKind.True;
            return new AiReply(Str(root, "text"), findings, advice, actions, sources, escalate);
        }
        catch (JsonException)
        {
            return AiReply.PlainText(raw.Trim());
        }
    }
}
