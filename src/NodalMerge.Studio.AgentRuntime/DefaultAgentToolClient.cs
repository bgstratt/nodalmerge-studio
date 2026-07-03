using System.Text.Json;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class DefaultAgentToolClient(
    string provider,
    string model,
    string baseUrl,
    string apiKey,
    LlmClient llm,
    McpToolDispatcher dispatcher) : IAgentToolClient
{
    public string Provider => provider;
    public string Model => model;
    public string BaseUrl => baseUrl;
    public string ApiKey => apiKey;

    public Task<LlmResponse> SendAsync(
        IReadOnlyList<NmMessage> messages,
        IReadOnlyList<LlmToolDef> tools,
        string systemPrompt,
        CancellationToken ct = default,
        Func<TransientRetryAttempt, Task>? onTransientRetry = null)
        => llm.SendAsync(provider, model, baseUrl, apiKey, messages, tools, systemPrompt, ct, onTransientRetry);

    public Task<string> DispatchAsync(
        string toolName,
        JsonElement input,
        IReadOnlyList<string>? allowedTools,
        CancellationToken ct,
        string? sessionId = null)
        => dispatcher.DispatchAsync(toolName, input, allowedTools, ct, sessionId);
}
