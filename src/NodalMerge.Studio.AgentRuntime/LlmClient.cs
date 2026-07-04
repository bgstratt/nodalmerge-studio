using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NodalMerge.Studio.AgentRuntime;

// Normalized message types — provider-agnostic, used by all agent loops.
internal abstract record NmContent(string Type);
internal sealed record NmText(string Text) : NmContent("text");
internal sealed record NmToolUse(string Id, string Name, JsonElement Input) : NmContent("tool_use");
internal sealed record NmToolResult(string ToolUseId, string Result) : NmContent("tool_result");
internal sealed record NmMessage(string Role, IReadOnlyList<NmContent> Content);

// Thrown when a provider's response (or a tool call's argument JSON inside it) fails to parse —
// e.g. DeepSeek occasionally emits broken JSON or stray characters in tool-call arguments. Caught
// by SendAsync's retry loop, which carries RawContent into the reask message so the model can see
// what it actually sent.
internal sealed class MalformedLlmResponseException(string rawContent, string reason) : Exception(reason)
{
    public string RawContent { get; } = rawContent;
    public string Reason { get; } = reason;
}

// Thrown on any non-success HTTP status from a provider call. Distinct from
// MalformedLlmResponseException (that's a 2xx response whose body doesn't parse; this is the
// request itself being rejected). Carries RetryAfter when the provider sent one, so a transient
// retry can honor it instead of guessing a backoff delay.
internal sealed class LlmHttpException(string message, System.Net.HttpStatusCode? statusCode, TimeSpan? retryAfter)
    : HttpRequestException(message, null, statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

// Reported to callers (via SendAsync's onTransientRetry callback) once per transient-error retry
// attempt, so a caller with domain context (workUnitId/agentId/sessionId) can record it somewhere
// more durable than a log line — see ExecutionEventKind.ProviderRetryAttempted.
internal sealed record TransientRetryAttempt(
    System.Net.HttpStatusCode? StatusCode,
    int AttemptNumber,
    int MaxAttempts,
    TimeSpan Delay,
    string Reason);

internal sealed class LlmClient(HttpClient http, ILogger<LlmClient>? logger = null)
{
    // Appended to every outgoing system prompt — DeepSeek (OpenAI-compatible path) is the main
    // offender for mixing stray/non-JSON content into tool-call arguments; Anthropic rarely needs
    // this but it's harmless there too.
    private const string OutputFormatGuardrail =
        "\n\nOutput formatting: respond with plain text or one or more tool calls, never both " +
        "unless asked. Tool call arguments must be syntactically valid JSON with no markdown " +
        "fences, commentary, or stray/non-JSON characters mixed in.";

    private const int MaxRetries = 2;

    // Transient provider errors (overload/rate-limit/gateway) benefit from more attempts with
    // real spacing between them than a malformed-response retry, which is nearly free — that one
    // just re-asks on the same connection with no reason to expect the *next* attempt to differ
    // unless the model corrects itself.
    private const int MaxTransientRetries = 3;

    private static readonly System.Net.HttpStatusCode[] TransientStatusCodes =
    [
        System.Net.HttpStatusCode.TooManyRequests,     // 429
        System.Net.HttpStatusCode.InternalServerError, // 500
        System.Net.HttpStatusCode.BadGateway,          // 502
        System.Net.HttpStatusCode.ServiceUnavailable,  // 503
        (System.Net.HttpStatusCode)529,                // Anthropic "Overloaded" — no BCL member
    ];

    private static bool IsTransient(System.Net.HttpStatusCode? statusCode) =>
        statusCode is { } code && TransientStatusCodes.Contains(code);

    private static TimeSpan ComputeBackoffDelay(int attempt) =>
        // 1s, 2s, 4s... plus up to 250ms jitter so concurrent callers hitting the same overloaded
        // provider don't all retry in lockstep.
        TimeSpan.FromMilliseconds((1000 << attempt) + Random.Shared.Next(250));

    private static readonly JsonSerializerOptions SerOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions DeserOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<LlmResponse> SendAsync(
        string provider,
        string model,
        string baseUrl,
        string apiKey,
        IReadOnlyList<NmMessage> messages,
        IReadOnlyList<LlmToolDef> tools,
        string systemPrompt,
        CancellationToken ct = default,
        Func<TransientRetryAttempt, Task>? onTransientRetry = null)
    {
        var attemptMessages = messages;
        var transientAttempt = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
                    ? await SendOpenAiAsync(model, baseUrl, apiKey, attemptMessages, tools, systemPrompt, ct).ConfigureAwait(false)
                    : await SendAnthropicAsync(model, baseUrl, apiKey, attemptMessages, tools, systemPrompt, ct).ConfigureAwait(false);
            }
            catch (MalformedLlmResponseException ex) when (attempt < MaxRetries)
            {
                logger?.LogWarning(
                    "LLM response malformed (attempt {Attempt}/{Max}): {Reason}",
                    attempt + 1, MaxRetries + 1, ex.Reason);
                attemptMessages = [.. messages, BuildReaskMessage(ex.Reason, ex.RawContent)];
            }
            catch (LlmHttpException ex) when (transientAttempt < MaxTransientRetries && IsTransient(ex.StatusCode))
            {
                transientAttempt++;
                var delay = ex.RetryAfter ?? ComputeBackoffDelay(transientAttempt - 1);
                logger?.LogWarning(
                    "Transient provider error (attempt {Attempt}/{Max}): {Message}. Retrying in {DelayMs}ms.",
                    transientAttempt, MaxTransientRetries, ex.Message, delay.TotalMilliseconds);
                var retryInfo = new TransientRetryAttempt(
                    ex.StatusCode, transientAttempt, MaxTransientRetries, delay, ex.Message);
                if (onTransientRetry is not null)
                    await onTransientRetry(retryInfo).ConfigureAwait(false);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static NmMessage BuildReaskMessage(string reason, string rawContent)
    {
        var snippet = rawContent.Length > 300 ? rawContent[..300] + "…" : rawContent;
        return new NmMessage("user", [new NmText(
            $"Your previous response could not be processed: {reason}\n\n" +
            $"What you sent (truncated): {snippet}\n\n" +
            "Resend your last turn now. Output must be either plain text, or one or more " +
            "well-formed tool calls with syntactically valid JSON arguments — no markdown code " +
            "fences, commentary, or stray/non-JSON characters mixed into the tool arguments.")]);
    }

    // ── Anthropic ─────────────────────────────────────────────────────────────

    private async Task<LlmResponse> SendAnthropicAsync(
        string model, string baseUrl, string apiKey,
        IReadOnlyList<NmMessage> messages,
        IReadOnlyList<LlmToolDef> tools,
        string systemPrompt,
        CancellationToken ct)
    {
        var requestBody = new Dictionary<string, object>
        {
            ["model"]     = model,
            ["max_tokens"] = 8192,
            // Both system and tools are identical on every cycle of a work unit's loop (the
            // growing part is `messages`, not these) — marking a cache_control breakpoint on each
            // means every cycle after the first is a cheap cache read for this portion instead of
            // full-price input tokens. This was the "fast follow" flagged after the eval run
            // showed real cost concentrated in the same system+tools payload being resent
            // unchanged, cycle after cycle. Message-history caching (letting the ever-growing
            // conversation itself benefit, which is the bigger piece of that cost) is deferred —
            // it needs the last message forced out of the string-shorthand form into block form,
            // more invasive than this, and worth its own pass with live verification.
            ["system"]    = new object[]
            {
                new Dictionary<string, object>
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt + OutputFormatGuardrail,
                    ["cache_control"] = new { type = "ephemeral" }
                }
            },
            ["tools"]     = BuildCacheableAnthropicTools(tools),
            ["messages"]  = messages.Select(SerializeAnthropicMessage).ToList()
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/v1/messages");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(
            JsonSerializer.Serialize(requestBody, SerOpts), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new LlmHttpException(
                $"Anthropic {(int)resp.StatusCode}: {errorBody}", resp.StatusCode, resp.Headers.RetryAfter?.Delta);
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        AnthropicResponse raw;
        try
        {
            raw = JsonSerializer.Deserialize<AnthropicResponse>(json, DeserOpts)
                  ?? throw new MalformedLlmResponseException(json, "Anthropic returned a null response body.");
        }
        catch (JsonException ex)
        {
            throw new MalformedLlmResponseException(json, $"Response was not valid JSON: {ex.Message}");
        }

        if (raw.Usage is { CacheReadInputTokens: > 0 } or { CacheCreationInputTokens: > 0 })
        {
            logger?.LogInformation(
                "Prompt cache: {CacheRead} tokens read, {CacheWrite} tokens written this call.",
                raw.Usage.CacheReadInputTokens ?? 0, raw.Usage.CacheCreationInputTokens ?? 0);
        }

        return new LlmResponse(
            raw.Content.Select(ParseAnthropicBlock).OfType<NmContent>().ToList(),
            raw.StopReason,
            raw.Usage?.InputTokens,
            raw.Usage?.OutputTokens);
    }

    // Anthropic caches everything up to and including a marked block, so putting the breakpoint
    // on the LAST tool definition covers the whole (otherwise-identical-every-cycle) tools array.
    private static List<object> BuildCacheableAnthropicTools(IReadOnlyList<LlmToolDef> tools)
    {
        var list = new List<object>(tools.Count);
        for (var i = 0; i < tools.Count; i++)
        {
            var dict = new Dictionary<string, object>
            {
                ["name"] = tools[i].Name,
                ["description"] = tools[i].Description,
                ["input_schema"] = tools[i].InputSchema
            };
            if (i == tools.Count - 1)
                dict["cache_control"] = new { type = "ephemeral" };
            list.Add(dict);
        }
        return list;
    }

    // Single-text messages serialized as string shorthand — keeps ScriptedLlmHandler working
    // and is valid per the Anthropic API spec.
    private static object SerializeAnthropicMessage(NmMessage msg)
    {
        if (msg.Content is [NmText t])
            return new { role = msg.Role, content = (object)t.Text };

        return new { role = msg.Role, content = msg.Content.Select(SerializeAnthropicContent).ToList() };
    }

    private static object SerializeAnthropicContent(NmContent c) => c switch
    {
        NmText t       => (object)new { type = "text", text = t.Text },
        NmToolUse u    => new { type = "tool_use", id = u.Id, name = u.Name, input = u.Input },
        NmToolResult r => new { type = "tool_result", tool_use_id = r.ToolUseId, content = r.Result },
        _              => throw new InvalidOperationException($"Unknown content type: {c.Type}")
    };

    private static NmContent? ParseAnthropicBlock(AnthropicContentBlock b) => b.Type switch
    {
        "text"     => new NmText(b.Text ?? ""),
        "tool_use" => b.Id is not null && b.Name is not null && b.Input.HasValue
                      ? new NmToolUse(b.Id, b.Name, b.Input.Value)
                      : null,
        _          => null
    };

    // ── OpenAI-compatible ─────────────────────────────────────────────────────

    private async Task<LlmResponse> SendOpenAiAsync(
        string model, string baseUrl, string apiKey,
        IReadOnlyList<NmMessage> messages,
        IReadOnlyList<LlmToolDef> tools,
        string systemPrompt,
        CancellationToken ct)
    {
        var allMessages = new List<object> { new { role = "system", content = systemPrompt + OutputFormatGuardrail } };
        foreach (var msg in messages)
            allMessages.AddRange(ToOpenAiMessages(msg));

        var requestBody = new Dictionary<string, object>
        {
            ["model"]     = model,
            ["max_tokens"] = 8192,
            ["tools"]     = tools.Select(t => (object)new
            {
                type     = "function",
                function = new
                {
                    name        = t.Name,
                    description = t.Description,
                    parameters  = t.InputSchema
                }
            }).ToList(),
            ["messages"]  = allMessages
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/v1/chat/completions");
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
        req.Content = new StringContent(
            JsonSerializer.Serialize(requestBody, SerOpts), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new LlmHttpException(
                $"OpenAI-compat {(int)resp.StatusCode}: {errorBody}", resp.StatusCode, resp.Headers.RetryAfter?.Delta);
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        OpenAiResponse raw;
        try
        {
            raw = JsonSerializer.Deserialize<OpenAiResponse>(json, DeserOpts)
                  ?? throw new MalformedLlmResponseException(json, "OpenAI-compat returned a null response body.");
        }
        catch (JsonException ex)
        {
            throw new MalformedLlmResponseException(json, $"Response was not valid JSON: {ex.Message}");
        }

        var choice = raw.Choices?.FirstOrDefault();
        if (choice is null) return new LlmResponse([], "end_turn");

        var contents = new List<NmContent>();
        if (!string.IsNullOrEmpty(choice.Message.Content))
            contents.Add(new NmText(choice.Message.Content));

        if (choice.Message.ToolCalls is { Count: > 0 } toolCalls)
        {
            foreach (var tc in toolCalls)
            {
                if (tc.Function is null) continue;
                var argsJson = tc.Function.Arguments ?? "{}";
                JsonElement input;
                try
                {
                    input = JsonSerializer.Deserialize<JsonElement>(argsJson, DeserOpts);
                }
                catch (JsonException ex)
                {
                    throw new MalformedLlmResponseException(
                        argsJson, $"Tool call '{tc.Function.Name}' arguments were not valid JSON: {ex.Message}");
                }
                contents.Add(new NmToolUse(
                    tc.Id ?? Guid.NewGuid().ToString("N"),
                    tc.Function.Name ?? "",
                    input));
            }
        }

        var stopReason = choice.FinishReason switch
        {
            "tool_calls" => "tool_use",
            "stop"       => "end_turn",
            _            => "end_turn"
        };

        // Unlike Anthropic, OpenAI and DeepSeek both cache automatically — no request-side
        // cache_control needed, but the two report a hit differently: OpenAI nests it under
        // prompt_tokens_details.cached_tokens, DeepSeek is a flat prompt_cache_hit_tokens. Whoever
        // this endpoint actually is, at most one of the two will ever be populated.
        var cacheReadTokens = raw.Usage?.PromptCacheHitTokens ?? raw.Usage?.PromptTokensDetails?.CachedTokens ?? 0;
        if (cacheReadTokens > 0)
            logger?.LogInformation("Prompt cache: {CacheRead} tokens read this call (OpenAI-compatible endpoint).", cacheReadTokens);

        return new LlmResponse(
            contents, stopReason, raw.Usage?.PromptTokens, raw.Usage?.CompletionTokens,
            raw.Usage?.Estimated ?? false);
    }

    // OpenAI requires one message per tool result; a NmMessage with tool results expands
    // into N separate "tool" role messages.
    private static IEnumerable<object> ToOpenAiMessages(NmMessage msg)
    {
        var toolResults = msg.Content.OfType<NmToolResult>().ToList();
        if (toolResults.Count > 0)
        {
            foreach (var r in toolResults)
                yield return new { role = "tool", tool_call_id = r.ToolUseId, content = r.Result };
            yield break;
        }

        var toolUses = msg.Content.OfType<NmToolUse>().ToList();
        var texts    = msg.Content.OfType<NmText>().ToList();
        if (toolUses.Count > 0)
        {
            yield return new
            {
                role       = "assistant",
                content    = texts.Count > 0 ? (object?)texts[0].Text : null,
                tool_calls = toolUses.Select(u => new
                {
                    id       = u.Id,
                    type     = "function",
                    function = new { name = u.Name, arguments = JsonSerializer.Serialize(u.Input, SerOpts) }
                }).ToList()
            };
            yield break;
        }

        yield return new { role = msg.Role, content = texts.Count == 1 ? (object)texts[0].Text : texts.Select(t => t.Text).ToList() };
    }

    // ── Anthropic response types ──────────────────────────────────────────────

    private sealed record AnthropicResponse(
        [property: JsonPropertyName("content")]     IReadOnlyList<AnthropicContentBlock> Content,
        [property: JsonPropertyName("stop_reason")] string StopReason,
        [property: JsonPropertyName("usage")]       AnthropicUsage? Usage = null);

    private sealed record AnthropicUsage(
        [property: JsonPropertyName("input_tokens")]  int? InputTokens,
        [property: JsonPropertyName("output_tokens")] int? OutputTokens,
        // Present once cache_control breakpoints are actually taking effect — logged (not yet
        // threaded further into ConversationLogEntry/UI) so caching can be verified against a
        // real call rather than trusted blindly. CacheReadInputTokens > 0 confirms a cache hit;
        // CacheCreationInputTokens > 0 confirms this call wrote the cache the next one should hit.
        [property: JsonPropertyName("cache_read_input_tokens")]     int? CacheReadInputTokens = null,
        [property: JsonPropertyName("cache_creation_input_tokens")] int? CacheCreationInputTokens = null);

    private sealed record AnthropicContentBlock(
        [property: JsonPropertyName("type")]  string Type,
        [property: JsonPropertyName("text")]  string? Text,
        [property: JsonPropertyName("id")]    string? Id,
        [property: JsonPropertyName("name")]  string? Name,
        [property: JsonPropertyName("input")] JsonElement? Input);

    // ── OpenAI response types ─────────────────────────────────────────────────

    private sealed record OpenAiResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChoice>? Choices,
        [property: JsonPropertyName("usage")]    OpenAiUsage? Usage = null);

    private sealed record OpenAiUsage(
        [property: JsonPropertyName("prompt_tokens")]     int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
        // Set by LmApiProxy.ts when these counts come from VS Code's countTokens() rather than a
        // real provider-reported usage block (vscode-lm/Copilot never reports real usage).
        [property: JsonPropertyName("estimated")]         bool? Estimated = null,
        // DeepSeek's automatic disk-based caching — flat fields, no request-side markers needed.
        [property: JsonPropertyName("prompt_cache_hit_tokens")]  int? PromptCacheHitTokens = null,
        [property: JsonPropertyName("prompt_cache_miss_tokens")] int? PromptCacheMissTokens = null,
        // OpenAI's automatic caching (gpt-4o/gpt-4.1+, prompts >= 1024 tokens) — nested, not flat.
        [property: JsonPropertyName("prompt_tokens_details")]    OpenAiPromptTokensDetails? PromptTokensDetails = null);

    private sealed record OpenAiPromptTokensDetails(
        [property: JsonPropertyName("cached_tokens")] int? CachedTokens);

    private sealed record OpenAiChoice(
        [property: JsonPropertyName("finish_reason")] string? FinishReason,
        [property: JsonPropertyName("message")]       OpenAiMessage Message);

    private sealed record OpenAiMessage(
        [property: JsonPropertyName("content")]    string? Content,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<OpenAiToolCall>? ToolCalls);

    private sealed record OpenAiToolCall(
        [property: JsonPropertyName("id")]       string? Id,
        [property: JsonPropertyName("function")] OpenAiFunction? Function);

    private sealed record OpenAiFunction(
        [property: JsonPropertyName("name")]      string? Name,
        [property: JsonPropertyName("arguments")] string? Arguments);
}

internal sealed record LlmResponse(
    IReadOnlyList<NmContent> Content,
    string StopReason,
    int? InputTokens = null,
    int? OutputTokens = null,
    bool TokensEstimated = false);

internal sealed record LlmToolDef(
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("description")]  string Description,
    [property: JsonPropertyName("input_schema")] object InputSchema);
