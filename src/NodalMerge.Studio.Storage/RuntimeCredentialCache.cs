using System.Collections.Concurrent;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Deliberately the only place a live ApiKey is allowed to sit for longer than a single request —
// and even here, only in memory. Never wired to IStudioNodeStore; a Host restart wipes it exactly
// like the orchestrator registry it complements, and that's the point (see ServiceContracts.cs's
// IRuntimeCredentialCache doc comment).
public sealed class RuntimeCredentialCache : IRuntimeCredentialCache
{
    private readonly ConcurrentDictionary<string, LlmConnectionInfo> _entries = new();

    public void Capture(string? credentialRef, string? provider, string? model, string? baseUrl, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(credentialRef))
            return;
        // Provider/model can legitimately be empty (vscode-lm), but a capture with neither an
        // apiKey nor a baseUrl carries nothing worth caching.
        if (string.IsNullOrEmpty(apiKey) && string.IsNullOrWhiteSpace(baseUrl))
            return;

        _entries[credentialRef] = new LlmConnectionInfo(provider ?? "", model ?? "", baseUrl ?? "", apiKey ?? "");
    }

    public LlmConnectionInfo? TryGet(string? credentialRef)
    {
        if (string.IsNullOrWhiteSpace(credentialRef))
            return null;
        return _entries.TryGetValue(credentialRef, out var info) ? info : null;
    }

    public void Evict(string? credentialRef)
    {
        if (string.IsNullOrWhiteSpace(credentialRef))
            return;
        _entries.TryRemove(credentialRef, out _);
    }
}
