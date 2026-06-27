using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime;

// Slice 21/22 — reactive spawn point for domain agents. Called from
// ArtifactCommandService.RecordAsync after an artifact is persisted; never blocks or throws back
// into that call path (each loop run is detached, and the run body is wrapped in try/catch). A
// single recorded artifact can wake more than one domain agent (e.g. a Decision mentioning both
// "OAuth" and "microservice migration" is relevant to both Security and Architecture).
public sealed class DomainAgentTriggerService(
    IAgentControlService agentControl,
    IServiceProvider serviceProvider,
    WorkspaceOptions options,
    ILogger<DomainAgentTriggerService> logger) : IDomainAgentTriggerService
{
    public Task NotifyArtifactRecordedAsync(ArtifactRef artifact, CancellationToken ct = default)
    {
        if (artifact.Type is not (ArtifactType.Research or ArtifactType.Decision or ArtifactType.Constraint))
            return Task.CompletedTask;

        // Cross-agent guard: never react to ANY domain agent's own prior output, not just the
        // matching definition's — domain agents only observe artifacts from structural agents and
        // humans. Conservative default while there are only two agents to observe; revisit if a
        // concrete case for cross-agent triggering shows up.
        if (artifact.Title is { } title && DomainAgentRegistry.All.Any(d => title.StartsWith(d.TitlePrefix, StringComparison.Ordinal)))
            return Task.CompletedTask;

        if (artifact.OwnedByWorkUnitId is not { } workUnitId)
            return Task.CompletedTask;

        // Per-work-unit override takes priority over the global default; both must explicitly
        // enable a given domain agent before any LLM call ever fires for it.
        var enabled = agentControl.GetEnabledDomainAgents(workUnitId) ?? options.EnabledDomainAgents;

        foreach (var definition in DomainAgentRegistry.All)
        {
            if (!enabled.Contains(definition.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            if (!DomainAgentTriggerHeuristic.IsRelevant(definition, artifact.Title, artifact.Body))
                continue;

            // Fire-and-forget — RecordAsync's caller must never wait on or fail because of a
            // domain agent's full LLM loop, only on the cheap checks above.
            _ = RunAsync(definition, workUnitId, artifact, ct);
        }

        return Task.CompletedTask;
    }

    private async Task RunAsync(DomainAgentDefinition definition, string workUnitId, ArtifactRef triggeringArtifact, CancellationToken ct)
    {
        try
        {
            var creds = agentControl.GetOrchestratorCredentials(workUnitId);

            // Child work units spawned by fan-out have credentials registered on the parent.
            if (creds is null)
            {
                var workUnits = serviceProvider.GetService<IWorkUnitService>();
                if (workUnits is not null)
                {
                    var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
                    if (wu?.ParentWorkUnitId is { } parentId)
                        creds = agentControl.GetOrchestratorCredentials(parentId);
                }
            }

            if (creds is null)
                return; // no LLM credentials resolvable — skip silently, same as InlineReviewerService

            var agentId = $"{definition.Name.ToLowerInvariant()}-{Guid.NewGuid():N}";
            var dispatcher = serviceProvider.GetRequiredService<McpToolDispatcher>();
            var llm = serviceProvider.GetRequiredService<LlmClient>();
            var conversationLog = serviceProvider.GetRequiredService<IConversationLogService>();

            var loop = new DomainAgentLoop(
                definition, agentId, workUnitId, triggeringArtifact.ArtifactId,
                creds.Provider, creds.Model, creds.BaseUrl, creds.ApiKey,
                dispatcher, llm, conversationLog: conversationLog);

            await loop.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{DomainAgent} run failed for work unit {WorkUnitId}", definition.Name, workUnitId);
        }
    }
}
