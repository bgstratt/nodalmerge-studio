using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodalMerge.Studio.AgentRuntime;

// Normalized message types — provider-agnostic, used by all agent loops.
internal abstract record NmContent(string Type);
internal sealed record NmText(string Text) : NmContent("text");
internal sealed record NmToolUse(string Id, string Name, JsonElement Input) : NmContent("tool_use");
internal sealed record NmToolResult(string ToolUseId, string Result) : NmContent("tool_result");
internal sealed record NmMessage(string Role, IReadOnlyList<NmContent> Content);

internal sealed class LlmClient(HttpClient http)
{
    private static readonly JsonSerializerOptions SerOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions DeserOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<LlmResponse> SendAsync(
        string provider,
        string model,
        string baseUrl,
        string apiKey,
        IReadOnlyList<NmMessage> messages,
        IReadOnlyList<LlmToolDef> tools,
        string systemPrompt,
        CancellationToken ct = default)
    {
        return provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
            ? SendOpenAiAsync(model, baseUrl, apiKey, messages, tools, systemPrompt, ct)
            : SendAnthropicAsync(model, baseUrl, apiKey, messages, tools, systemPrompt, ct);
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
            ["system"]    = systemPrompt,
            ["tools"]     = tools.Select(t => (object)new
            {
                name         = t.Name,
                description  = t.Description,
                input_schema = t.InputSchema
            }).ToList(),
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
            throw new HttpRequestException(
                $"Anthropic {(int)resp.StatusCode}: {errorBody}", null, resp.StatusCode);
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var raw = JsonSerializer.Deserialize<AnthropicResponse>(json, DeserOpts)
                  ?? throw new InvalidOperationException("LLM returned null response.");

        return new LlmResponse(
            raw.Content.Select(ParseAnthropicBlock).OfType<NmContent>().ToList(),
            raw.StopReason);
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
        var allMessages = new List<object> { new { role = "system", content = systemPrompt } };
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
            throw new HttpRequestException(
                $"OpenAI-compat {(int)resp.StatusCode}: {errorBody}", null, resp.StatusCode);
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var raw = JsonSerializer.Deserialize<OpenAiResponse>(json, DeserOpts)
                  ?? throw new InvalidOperationException("LLM returned null response.");

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
                var input = JsonSerializer.Deserialize<JsonElement>(
                    tc.Function.Arguments ?? "{}", DeserOpts);
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

        return new LlmResponse(contents, stopReason);
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
        [property: JsonPropertyName("stop_reason")] string StopReason);

    private sealed record AnthropicContentBlock(
        [property: JsonPropertyName("type")]  string Type,
        [property: JsonPropertyName("text")]  string? Text,
        [property: JsonPropertyName("id")]    string? Id,
        [property: JsonPropertyName("name")]  string? Name,
        [property: JsonPropertyName("input")] JsonElement? Input);

    // ── OpenAI response types ─────────────────────────────────────────────────

    private sealed record OpenAiResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChoice>? Choices);

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
    string StopReason);

internal sealed record LlmToolDef(
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("description")]  string Description,
    [property: JsonPropertyName("input_schema")] object InputSchema);
