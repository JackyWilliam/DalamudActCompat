using System.Globalization;
using System.Text.RegularExpressions;

namespace DalamudActCompat.Ai;

public enum AiProtocol
{
    // The official Anthropic SDK.
    Claude,
    // OpenAI's Responses API. OpenAI and xAI mark chat completions as legacy on their own
    // endpoints, and their newest models may be served by Responses only.
    Responses,
    // The chat-completions protocol nearly every other service and gateway speaks.
    ChatCompletions,
}

// One entry of 服务商: where requests go, where the player gets a key, and how 推荐（自动选择）
// picks a model from the list the service returns. Model ids are never hard-coded as the
// recommendation (services rename them every few months); Prefer only ranks families.
public sealed record AiProvider(
    string Id,
    string NameZh,
    string NameEn,
    AiProtocol Protocol,
    string Endpoint,
    string ConsoleUrl,
    string KeyHint,
    string[] Prefer,
    string[]? Fallback = null,
    bool KeyOptional = false,
    string? NoteZh = null,
    string? NoteEn = null);

public static class AiProviders
{
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    // Any other service that speaks chat completions; the player enters its address.
    public const string Compatible = "compatible";

    public static readonly IReadOnlyList<AiProvider> All =
    [
        new(Anthropic, "Anthropic Claude", "Anthropic Claude", AiProtocol.Claude, "https://api.anthropic.com",
            "https://platform.claude.com/settings/keys", "sk-ant-…", ["^claude-opus-", "^claude-sonnet-", "^claude-"]),
        new(OpenAi, "OpenAI（ChatGPT）", "OpenAI (ChatGPT)", AiProtocol.Responses, "https://api.openai.com/v1",
            "https://platform.openai.com/api-keys", "sk-…", [@"^gpt-\d+(\.\d+)?(-[a-z]+)?$", @"^gpt-\d", @"^o\d"]),
        new("gemini", "Google Gemini", "Google Gemini", AiProtocol.ChatCompletions, "https://generativelanguage.googleapis.com/v1beta/openai",
            "https://aistudio.google.com/apikey", "AIza…", [@"^gemini-\d+(\.\d+)?-pro$", @"^gemini-\d+(\.\d+)?-pro", @"^gemini-\d+(\.\d+)?-flash$", "^gemini-"],
            NoteZh: "在 Google AI Studio 创建 Key；国内网络一般需要代理。", NoteEn: "Create the key in Google AI Studio."),
        new("xai", "xAI Grok", "xAI Grok", AiProtocol.Responses, "https://api.x.ai/v1",
            "https://console.x.ai", "xai-…", [@"^grok-\d+(\.\d+)?$", @"^grok-\d+(\.\d+)?-", "^grok-"]),
        new("deepseek", "DeepSeek（深度求索）", "DeepSeek", AiProtocol.ChatCompletions, "https://api.deepseek.com",
            "https://platform.deepseek.com/api_keys", "sk-…", [@"^deepseek-v[\d.]+-pro", @"^deepseek-v[\d.]+$", "^deepseek-v", "^deepseek-"]),
        new("moonshot", "Kimi（月之暗面 · 国内站）", "Kimi (Moonshot, China)", AiProtocol.ChatCompletions, "https://api.moonshot.cn/v1",
            "https://platform.moonshot.cn/console/api-keys", "sk-…", [@"^kimi-k[\d.]+$", @"^kimi-k[\d.]+", "^kimi-", "^moonshot-"],
            NoteZh: "国内站和国际站的 Key 不通用，请选对应的站点。", NoteEn: "China and international keys are not interchangeable."),
        new("moonshot-intl", "Kimi（Moonshot · 国际站）", "Kimi (Moonshot, international)", AiProtocol.ChatCompletions, "https://api.moonshot.ai/v1",
            "https://platform.moonshot.ai/console/api-keys", "sk-…", [@"^kimi-k[\d.]+$", @"^kimi-k[\d.]+", "^kimi-", "^moonshot-"],
            NoteZh: "国内站和国际站的 Key 不通用，请选对应的站点。", NoteEn: "China and international keys are not interchangeable."),
        new("qwen", "通义千问 Qwen（阿里云百炼 · 国内）", "Qwen (Alibaba Cloud Bailian, China)", AiProtocol.ChatCompletions, "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "https://bailian.console.aliyun.com/?tab=model#/api-key", "sk-…", [@"^qwen[\d.]*-max$", @"^qwen[\d.]*-max", @"^qwen[\d.]*-plus$", @"^qwen[\d.]*-plus", "^qwen"],
            NoteZh: "百炼的 Key 按地域发放，国内和国际地域的 Key 与地址不通用。", NoteEn: "Model Studio keys are per region; use the matching address."),
        new("qwen-intl", "通义千问 Qwen（阿里云 · 国际）", "Qwen (Alibaba Cloud Model Studio, international)", AiProtocol.ChatCompletions, "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
            "https://modelstudio.console.alibabacloud.com/?tab=model#/api-key", "sk-…", [@"^qwen[\d.]*-max$", @"^qwen[\d.]*-max", @"^qwen[\d.]*-plus$", @"^qwen[\d.]*-plus", "^qwen"],
            NoteZh: "百炼的 Key 按地域发放，国内和国际地域的 Key 与地址不通用。", NoteEn: "Model Studio keys are per region; use the matching address."),
        new("doubao", "豆包 Seed（火山方舟 · 国内）", "Doubao Seed (Volcengine Ark, China)", AiProtocol.ChatCompletions, "https://ark.cn-beijing.volces.com/api/v3",
            "https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey", "", [@"^doubao-seed-\d+-\d+-\d{6}$", "^doubao-seed", "^doubao"],
            Fallback: ["doubao-seed-1-8-251228", "doubao-seed-1-6-251015"],
            NoteZh: "先在方舟控制台「开通管理」里开通要用的模型；拉不到模型列表时，在「模型」里填写控制台显示的模型 ID。",
            NoteEn: "Activate the model in the Ark console first; if no model list loads, type the model ID shown there."),
        new("byteplus", "Seed（BytePlus ModelArk · 国际）", "Seed (BytePlus ModelArk, international)", AiProtocol.ChatCompletions, "https://ark.ap-southeast.bytepluses.com/api/v3",
            "https://console.byteplus.com/ark/region:ark+ap-southeast-1/apiKey", "", ["^seed-", "^doubao-seed", "seed"],
            NoteZh: "先在 ModelArk 控制台开通要用的模型；拉不到模型列表时，在「模型」里填写控制台显示的模型 ID。",
            NoteEn: "Activate the model in the ModelArk console first; if no model list loads, type the model ID shown there."),
        new("zhipu", "智谱 GLM（国内）", "Zhipu GLM (China)", AiProtocol.ChatCompletions, "https://open.bigmodel.cn/api/paas/v4",
            "https://open.bigmodel.cn/usercenter/apikeys", "", [@"^glm-[\d.]+$", @"^glm-[\d.]+", "^glm-"], Fallback: ["glm-4.6"]),
        new("zai", "Z.ai GLM（国际）", "Z.ai GLM (international)", AiProtocol.ChatCompletions, "https://api.z.ai/api/paas/v4",
            "https://z.ai/manage-apikey/apikey-list", "", [@"^glm-[\d.]+$", @"^glm-[\d.]+", "^glm-"], Fallback: ["glm-4.6"]),
        new("minimax", "MiniMax（国内）", "MiniMax (China)", AiProtocol.ChatCompletions, "https://api.minimaxi.com/v1",
            "https://platform.minimaxi.com/user-center/basic-information/interface-key", "", [@"^MiniMax-M[\d.]+$", "^MiniMax-M", "^MiniMax"], Fallback: ["MiniMax-M2"]),
        new("minimax-intl", "MiniMax（国际）", "MiniMax (international)", AiProtocol.ChatCompletions, "https://api.minimax.io/v1",
            "https://www.minimax.io/platform/user-center/basic-information/interface-key", "", [@"^MiniMax-M[\d.]+$", "^MiniMax-M", "^MiniMax"], Fallback: ["MiniMax-M2"]),
        new("mistral", "Mistral AI", "Mistral AI", AiProtocol.ChatCompletions, "https://api.mistral.ai/v1",
            "https://console.mistral.ai/api-keys", "", ["^mistral-large-latest$", "^mistral-medium-latest$", "^mistral-large", "^mistral-"]),
        new("openrouter", "OpenRouter（一个 Key 用多家模型）", "OpenRouter (many providers, one key)", AiProtocol.ChatCompletions, "https://openrouter.ai/api/v1",
            "https://openrouter.ai/settings/keys", "sk-or-…", ["^anthropic/claude-opus", "^anthropic/claude-sonnet", "^openai/gpt-", "^google/gemini-.*-pro"],
            NoteZh: "按 OpenRouter 的价格统一计费；模型名带服务商前缀，例如 anthropic/…。", NoteEn: "Billed by OpenRouter; model names carry the provider prefix."),
        new("siliconflow", "硅基流动 SiliconFlow", "SiliconFlow", AiProtocol.ChatCompletions, "https://api.siliconflow.cn/v1",
            "https://cloud.siliconflow.cn/account/ak", "sk-…", ["^deepseek-ai/DeepSeek-V", @"^Qwen/Qwen\d", "^moonshotai/Kimi", "^zai-org/GLM"]),
        new("qianfan", "百度千帆（文心 ERNIE）", "Baidu Qianfan (ERNIE)", AiProtocol.ChatCompletions, "https://qianfan.baidubce.com/v2",
            "https://console.bce.baidu.com/qianfan/ais/console/apiKey", "bce-v3/…", [@"^ernie-\d+(\.\d+)?$", @"^ernie-\d", "^ernie-"], Fallback: ["ernie-4.5-turbo-128k"]),
        new("hunyuan", "腾讯混元", "Tencent Hunyuan", AiProtocol.ChatCompletions, "https://api.hunyuan.cloud.tencent.com/v1",
            "https://console.cloud.tencent.com/hunyuan/api-key", "sk-…", [@"^hunyuan-t\d", "^hunyuan-turbos", "^hunyuan-"], Fallback: ["hunyuan-turbos-latest"]),
        new("local", "本地模型（Ollama / LM Studio）", "Local model (Ollama / LM Studio)", AiProtocol.ChatCompletions, "http://localhost:11434/v1",
            "https://ollama.com/download", "", [], KeyOptional: true,
            NoteZh: "默认是 Ollama 的地址；LM Studio 改成 http://localhost:1234/v1。不需要 Key，复盘效果取决于本地模型的能力。",
            NoteEn: "Ollama's address by default; for LM Studio use http://localhost:1234/v1. No key needed."),
        new(Compatible, "其他 OpenAI 兼容接口", "Other OpenAI-compatible service", AiProtocol.ChatCompletions, string.Empty,
            string.Empty, string.Empty, []),
    ];

    public static AiProvider Get(string? id) => All.FirstOrDefault(provider => provider.Id == id) ?? All[0];

    // Unknown ids (a newer config read by an older build) fall back to Claude, as before.
    public static string Normalize(string? id) => Get(id).Id;

    public static string DefaultEndpoint(string provider) => Get(provider).Endpoint;

    // Not chat models: listing them in 模型 would only invite a failing request.
    private static readonly Regex NotChat = new(
        @"(?<![a-z])(embed\w*|text-embedding\w*|tts|whisper|transcribe|speech|audio|realtime|dall-e|image\w*|imagen|sora|veo|lyria|moderation|rerank|ocr|asr|aqa|video|babbage|davinci)(?![a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Usable for chat but not the default pick for reviewing fights (small, specialised or a dated snapshot).
    private static readonly Regex NotRecommended = new(
        @"(?<![a-z])(mini|nano|lite|tiny|small|vl|vision|search|code[rx]?|instruct|distill)(?![a-z])|-\d{4}-\d{2}-\d{2}$|-\d{8}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ChatModels(IEnumerable<string> ids)
        => ids.Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id)
            .Where(id => id.Length > 0 && !NotChat.IsMatch(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // The first preferred family that the service offers, newest version first.
    public static string Recommend(AiProvider provider, IReadOnlyList<string> models, string? pinned = null)
    {
        if (pinned is not null && (models.Count == 0 || models.Contains(pinned))) return pinned;
        foreach (var pattern in provider.Prefer)
        {
            var family = models.Where(id => Regex.IsMatch(id, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && !NotRecommended.IsMatch(id))
                .ToArray();
            if (family.Length > 0)
                return family.OrderByDescending(id => id, VersionComparer.Instance).ThenBy(id => id.Length).First();
        }
        return models.FirstOrDefault(id => !NotRecommended.IsMatch(id)) ?? models.FirstOrDefault() ?? provider.Fallback?.FirstOrDefault() ?? string.Empty;
    }

    // Orders ids by the numbers in them, so gpt-5.10 sorts above gpt-5.9 and glm-5 above glm-4.6.
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            var a = Numbers(left);
            var b = Numbers(right);
            for (var index = 0; index < Math.Min(a.Length, b.Length); index++)
                if (a[index] != b[index]) return a[index].CompareTo(b[index]);
            return a.Length.CompareTo(b.Length);
        }

        private static long[] Numbers(string? value)
            => Regex.Matches(value ?? string.Empty, @"\d+")
                .Select(match => long.TryParse(match.Value.Length > 12 ? match.Value[..12] : match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
                .ToArray();
    }
}

// AI 助手 settings (features.js aiSettings). Everything except the key, which AiKeyStore keeps
// DPAPI-protected in its own files so it never reaches the configuration file or cloud sync.
public sealed class AiSettings
{
    public bool Enabled { get; set; }

    public string Provider { get; set; } = AiProviders.Anthropic;

    // Empty means 推荐（自动选择）.
    public string Model { get; set; } = string.Empty;

    // Empty means the provider's official address.
    public string Endpoint { get; set; } = string.Empty;

    public bool StripNames { get; set; } = true;

    public bool AttachDiagnosticLog { get; set; } = true;

    public bool ConfirmBeforeSend { get; set; }

    // The last 测试连接 passed for this provider, address and key; the in-page AI entries
    // (让 AI 诊断, AI 复盘) only appear once it has.
    public bool Verified { get; set; }

    // Models the provider listed during the last successful test, offered in the model picker.
    public List<string> KnownModels { get; set; } = [];

    public string ResolvedEndpoint
        => string.IsNullOrWhiteSpace(Endpoint) ? AiProviders.DefaultEndpoint(Provider) : Endpoint.Trim().TrimEnd('/');

    public bool Ready => Enabled && Verified;
}
