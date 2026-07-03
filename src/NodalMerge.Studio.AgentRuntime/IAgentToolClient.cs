using System.Text.Json;

namespace NodalMerge.Studio.AgentRuntime;

// Separates agent definition (system prompt, tool set) from execution mode (which LLM + dispatcher).
// This lets domain agents be configured with different providers/tools without touching loop logic,
// and makes it straightforward to swap in a headless-peer or mock client in tests.
internal interface IAgentToolClient
{
    string Provider { get; }
    string Model { get; }
    string BaseUrl { get; }
    string ApiKey { get; }

    Task<LlmResponse> SendAsync(
        IReadOnlyList<NmMessage> messages,
        IReadOnlyList<LlmToolDef> tools,
        string systemPrompt,
        CancellationToken ct = default,
        Func<TransientRetryAttempt, Task>? onTransientRetry = null);

    Task<string> DispatchAsync(
        string toolName,
        JsonElement input,
        IReadOnlyList<string>? allowedTools,
        CancellationToken ct,
        string? sessionId = null);
}
