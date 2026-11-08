using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace DalamudActCompat.Ai;

internal sealed record AiTurn(bool FromUser, string Text);

// One request: the system prompt, the conversation so far and how hard the model should think.
internal sealed record AiCall(string System, IReadOnlyList<AiTurn> Turns, string Effort);

// A failure the page shows as is, already worded for the player.
internal sealed class AiServiceException(string message) : Exception(message);

// Sends AI 助手 requests straight from the player's PC to the service they chose, with their
// own key; DACT servers never see the request or the key. Claude goes through the official
// Anthropic SDK; OpenAI and xAI through their Responses API; every other service through the
// chat-completions protocol they share.
internal static class AiClient
{
    // The model used when the player leaves 模型 on 推荐（自动选择） with Claude.
    public const string RecommendedClaudeModel = "claude-opus-5-5";

    // Long enough for a full review in JSON, including reasoning tokens on thinking models.
    private const int MaxOutputTokens = 16000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static string RecommendedModel(AiSettings settings)
    {
        var provider = AiProviders.Get(settings.Provider);
        return AiProviders.Recommend(provider, settings.KnownModels,
            provider.Protocol == AiProtocol.Claude ? RecommendedClaudeModel : null);
    }

    public static string ResolveModel(AiSettings settings)
        => string.IsNullOrWhiteSpace(settings.Model) ? RecommendedModel(settings) : settings.Model.Trim();

    public static async Task<string> CompleteAsync(AiSettings settings, string key, AiCall call, CancellationToken cancellationToken)
    {
        var model = ResolveModel(settings);
        if (string.IsNullOrWhiteSpace(model))
            throw new AiServiceException("还没有选择模型：先在 AI 设置里测试连接，再选一个模型。");
        return Protocol(settings) switch
        {
            AiProtocol.Claude => await CompleteClaudeAsync(settings, key, model, call, cancellationToken).ConfigureAwait(false),
            AiProtocol.Responses => await CompleteResponsesAsync(settings, key, model, call, cancellationToken).ConfigureAwait(false),
            _ => await CompleteChatAsync(settings, key, model, call, cancellationToken).ConfigureAwait(false),
        };
    }

    // 测试连接: listing models proves the address and key work and fills the model picker.
    public static async Task<IReadOnlyList<string>> ListModelsAsync(AiSettings settings, string key, CancellationToken cancellationToken)
    {
        var provider = AiProviders.Get(settings.Provider);
        if (Protocol(settings) == AiProtocol.Claude)
        {
            try
            {
                var page = await CreateClaudeClient(settings, key).Models.List(cancellationToken: cancellationToken).ConfigureAwait(false);
                return page.Items.Select(model => model.ID).ToArray();
            }
            catch (Exception exception) when (exception is AnthropicException or HttpRequestException or TaskCanceledException)
            {
                throw new AiServiceException(DescribeClaudeFailure(exception));
            }
        }

        var endpoint = settings.ResolvedEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) throw new AiServiceException("请先填写接口地址。");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/models");
        Authorize(request, key);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            // Some services have no model list; a one-word request with the typed (or the
            // template's) model proves the connection instead.
            var model = !string.IsNullOrWhiteSpace(settings.Model) ? settings.Model.Trim() : provider.Fallback?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(model))
                throw new AiServiceException("这个服务没有提供模型列表：请先在「模型」里填写模型名称，再测试连接。");
            var ping = new AiCall("Reply with OK.", [new AiTurn(true, "ping")], "low");
            if (Protocol(settings) == AiProtocol.Responses) await CompleteResponsesAsync(settings, key, model, ping, cancellationToken, structured: false).ConfigureAwait(false);
            else await CompleteChatAsync(settings, key, model, ping, cancellationToken, structured: false).ConfigureAwait(false);
            return [];
        }
        EnsureSuccess(response, body);
        try
        {
            var ids = JsonNode.Parse(body)?["data"]?.AsArray()
                .Select(item => item?["id"]?.GetValue<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!) ?? [];
            return AiProviders.ChatModels(ids);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new AiServiceException("接口返回的模型列表无法识别，请检查接口地址。");
        }
    }

    // A custom address on OpenAI or xAI is usually a proxy, which rarely implements Responses.
    private static AiProtocol Protocol(AiSettings settings)
    {
        var provider = AiProviders.Get(settings.Provider);
        if (provider.Protocol != AiProtocol.Responses) return provider.Protocol;
        return Uri.TryCreate(settings.ResolvedEndpoint, UriKind.Absolute, out var uri) &&
               Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var official) &&
               uri.Host.Equals(official.Host, StringComparison.OrdinalIgnoreCase)
            ? AiProtocol.Responses
            : AiProtocol.ChatCompletions;
    }

    // ---- Claude

    private static AnthropicClient CreateClaudeClient(AiSettings settings, string key) => new()
    {
        ApiKey = key,
        BaseUrl = settings.ResolvedEndpoint,
        Timeout = TimeSpan.FromMinutes(3),
        MaxRetries = 2,
    };

    private static bool IsOfficialClaudeEndpoint(AiSettings settings)
        => Uri.TryCreate(settings.ResolvedEndpoint, UriKind.Absolute, out var uri) &&
           uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase);

    // Effort and structured outputs exist on current Claude models only; an older model the
    // player picked from the list still works, just without them.
    private static bool SupportsEffort(string model)
        => Regex.IsMatch(model, "^claude-(opus|sonnet|haiku|fable|mythos)-(5|4-[5-9])", RegexOptions.IgnoreCase) &&
           !model.StartsWith("claude-sonnet-4-5", StringComparison.OrdinalIgnoreCase) &&
           !model.StartsWith("claude-haiku-4-5", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> CompleteClaudeAsync(AiSettings settings, string key, string model, AiCall call, CancellationToken cancellationToken)
    {
        var client = CreateClaudeClient(settings, key);
        var official = IsOfficialClaudeEndpoint(settings);
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxOutputTokens,
            System = call.System,
            Messages = call.Turns.Select(turn => turn.FromUser
                ? (BetaMessageParam)new BetaUserMessageParam { Content = turn.Text }
                : new BetaAssistantMessageParam { Content = turn.Text }).ToList(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = SupportsEffort(model) ? call.Effort switch { "low" => Effort.Low, "high" => Effort.High, _ => Effort.Medium } : null,
                Format = new BetaJsonOutputFormat { Schema = AiReplyFormat.Schema },
            },
            // A policy decline is re-served by Anthropic's default fallback model inside the same
            // call. Proxies and gateways may not know the beta, so only the official API gets it.
            Betas = official ? ["server-side-fallback-2026-07-01"] : null,
            Fallbacks = official ? (BetaFallbacksParam)new Default() : null,
        };
        BetaMessage message;
        try
        {
            message = await client.Beta.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AnthropicException or HttpRequestException or TaskCanceledException)
        {
            throw new AiServiceException(DescribeClaudeFailure(exception));
        }

        if (message.StopReason == "refusal")
            throw new AiServiceException("AI 服务拒绝回答这次请求。换个说法再试一次。");
        if (message.StopReason == "max_tokens")
            throw new AiServiceException("回答太长被截断了：请换一个输出上限更高的模型，或把问题拆小。");
        var text = new StringBuilder();
        foreach (var block in message.Content)
            if (block.TryPickText(out var part)) text.Append(part.Text);
        return text.ToString();
    }

    private static string DescribeClaudeFailure(Exception exception) => exception switch
    {
        AnthropicUnauthorizedException => "API Key 无效：请确认复制完整，没有多余空格。",
        AnthropicForbiddenException => "这个 Key 没有权限使用该模型或接口。",
        AnthropicNotFoundException => "找不到这个模型或接口，请检查「模型」和「接口地址」。",
        AnthropicRateLimitException => "请求太频繁或额度已用完，请稍后再试，或检查账户余额。",
        AnthropicBadRequestException bad => $"请求被拒绝：{Shorten(bad.Message)}",
        Anthropic5xxException => "AI 服务暂时不可用，请稍后再试。",
        AnthropicIOException or HttpRequestException => "连不上 AI 服务：请检查网络、代理或接口地址。",
        TaskCanceledException => "AI 服务响应超时，请稍后再试。",
        AnthropicApiException api => $"AI 服务返回错误：{Shorten(api.Message)}",
        _ => $"请求失败：{Shorten(exception.Message)}",
    };

    // ---- OpenAI / xAI Responses API

    private static async Task<string> CompleteResponsesAsync(AiSettings settings, string key, string model, AiCall call,
        CancellationToken cancellationToken, bool structured = true)
    {
        var input = new JsonArray();
        foreach (var turn in call.Turns)
            input.Add(new JsonObject { ["role"] = turn.FromUser ? "user" : "assistant", ["content"] = turn.Text });
        JsonObject Body(bool extras)
        {
            var body = new JsonObject { ["model"] = model, ["instructions"] = call.System, ["input"] = input.DeepClone(), ["store"] = false };
            if (!extras) return body;
            body["max_output_tokens"] = MaxOutputTokens;
            body["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_object" } };
            body["reasoning"] = new JsonObject { ["effort"] = call.Effort };
            return body;
        }

        var text = await PostWithFallbackAsync(settings.ResolvedEndpoint + "/responses", key, Body(structured), structured ? Body(false) : null, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var root = JsonNode.Parse(text);
            if (root?["status"]?.GetValue<string>() == "incomplete")
                throw new AiServiceException(root["incomplete_details"]?["reason"]?.GetValue<string>() == "content_filter"
                    ? "AI 服务拒绝回答这次请求。换个说法再试一次。"
                    : "回答太长被截断了：请换一个输出上限更高的模型，或把问题拆小。");
            var reply = new StringBuilder();
            foreach (var item in root?["output"]?.AsArray() ?? [])
            {
                if (item?["type"]?.GetValue<string>() != "message") continue;
                foreach (var part in item["content"]?.AsArray() ?? [])
                {
                    var type = part?["type"]?.GetValue<string>();
                    if (type == "refusal") throw new AiServiceException("AI 服务拒绝回答这次请求。换个说法再试一次。");
                    if (type == "output_text") reply.Append(part!["text"]?.GetValue<string>());
                }
            }
            return reply.ToString();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new AiServiceException("接口返回的内容无法识别，请检查接口地址。");
        }
    }

    // ---- Chat completions (every other service)

    private static async Task<string> CompleteChatAsync(AiSettings settings, string key, string model, AiCall call,
        CancellationToken cancellationToken, bool structured = true)
    {
        var endpoint = settings.ResolvedEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) throw new AiServiceException("请先填写接口地址。");
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = call.System } };
        foreach (var turn in call.Turns)
            messages.Add(new JsonObject { ["role"] = turn.FromUser ? "user" : "assistant", ["content"] = turn.Text });
        JsonObject Body(bool extras)
        {
            var body = new JsonObject { ["model"] = model, ["messages"] = messages.DeepClone() };
            if (!extras) return body;
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
            body["max_tokens"] = MaxOutputTokens;
            return body;
        }

        var text = await PostWithFallbackAsync(endpoint + "/chat/completions", key, Body(structured), structured ? Body(false) : null, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var choice = JsonNode.Parse(text)?["choices"]?[0];
            switch (choice?["finish_reason"]?.GetValue<string>())
            {
                case "content_filter":
                    throw new AiServiceException("AI 服务拒绝回答这次请求。换个说法再试一次。");
                case "length":
                    throw new AiServiceException("回答太长被截断了：请换一个输出上限更高的模型，或把问题拆小。");
            }
            return choice?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new AiServiceException("接口返回的内容无法识别，请检查接口地址。");
        }
    }

    // JSON mode and an output limit help, but services differ in which of them they accept
    // (and in the limit's maximum); a request they reject as malformed is sent once more as the
    // plain request every service understands, and the reply is then parsed leniently.
    private static async Task<string> PostWithFallbackAsync(string url, string key, JsonObject body, JsonObject? plain, CancellationToken cancellationToken)
    {
        var (status, text) = await PostAsync(url, key, body, cancellationToken).ConfigureAwait(false);
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity && plain is not null)
            (status, text) = await PostAsync(url, key, plain, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(status, text);
        return text;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(string url, string key, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(request, key);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    // Local servers (Ollama, LM Studio) take no key.
    private static void Authorize(HttpRequestMessage request, string key)
    {
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiServiceException("AI 服务响应超时，请稍后再试。");
        }
        catch (HttpRequestException)
        {
            throw new AiServiceException("连不上 AI 服务：请检查网络、代理或接口地址。");
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body) => EnsureSuccess(response.StatusCode, body);

    private static void EnsureSuccess(HttpStatusCode status, string body)
    {
        if ((int)status is >= 200 and < 300) return;
        string? detail = null;
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            detail = error is JsonValue ? error.GetValue<string>() : error?["message"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }
        throw new AiServiceException((int)status switch
        {
            401 => "API Key 无效：请确认复制完整，没有多余空格。",
            403 => "这个 Key 没有权限使用该模型或接口。",
            404 => "找不到这个模型或接口，请检查「模型」和「接口地址」。",
            429 => "请求太频繁或额度已用完，请稍后再试，或检查账户余额。",
            >= 500 => "AI 服务暂时不可用，请稍后再试。",
            _ => $"AI 服务返回错误（{(int)status}）：{Shorten(detail ?? body)}",
        });
    }

    private static string Shorten(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 160 ? value : value[..160] + "…";
    }
}
