using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// Slice 21/22 — domain/intelligence-plane agent, generalized over DomainAgentDefinition. Unlike
// ReviewerAgentLoop (which it's modeled on), this loop owns no lifecycle, is never assigned a task
// by the Planner, and has no AgentProfile/PipelineStage slot — its tool list is fixed in code
// because it doesn't participate in stage-based scheduling at all. It only observes a triggering
// artifact and its projection, and optionally proposes a Constraint/Research artifact back into
// the same lineage. Every domain agent shares this exact loop; only definition.SystemPrompt/Name
// differ per agent.
internal sealed class DomainAgentLoop(
    DomainAgentDefinition definition,
    string agentId,
    string workUnitId,
    string triggeringArtifactId,
    IAgentToolClient client,
    string? sessionId = null,
    Action<string?>? onActivity = null,
    IConversationLogService? conversationLog = null,
    IExecutionEventStream? events = null)
{
    private static readonly IReadOnlyList<LlmToolDef> Tools = BuildTools();
    private static readonly string[] AllowedToolNames =
    [
        McpToolNames.ProjectionGet,
        McpToolNames.ArtifactQuery,
        McpToolNames.WorkspaceSearch,
        McpToolNames.ArtifactRecord,
    ];

    // See OrchestratorAgentLoop.OnTransientRetryAsync for rationale — same pattern.
    private async Task OnTransientRetryAsync(TransientRetryAttempt attempt, CancellationToken ct)
    {
        if (events is null || sessionId is null) return;
        await events.AppendAsync(
            sessionId, workUnitId, ExecutionEventKind.ProviderRetryAttempted,
            new ProviderRetryAttemptedPayload(
                agentId, client.Provider, (int?)attempt.StatusCode,
                attempt.AttemptNumber, attempt.MaxAttempts,
                (int)attempt.Delay.TotalMilliseconds, attempt.Reason),
            ct: ct).ConfigureAwait(false);
    }

    public async Task<AgentLoopCompletion> RunAsync(CancellationToken ct)
    {
        var messages = new List<NmMessage>
        {
            new("user", [new NmText(
                $"Work unit {workUnitId} just had artifact {triggeringArtifactId} recorded, which " +
                $"looks {definition.Name.ToLowerInvariant()}-related. Your agent ID is {agentId}. " +
                "Investigate and, if warranted, record a Constraint describing the specific gap.")])
        };

        var completedNaturally = false;
        for (var i = 0; i < definition.MaxIterations && !ct.IsCancellationRequested; i++)
        {
            onActivity?.Invoke("Thinking...");
            var response = await client.SendAsync(
                    messages, Tools, definition.SystemPrompt, ct,
                    attempt => OnTransientRetryAsync(attempt, ct))
                .ConfigureAwait(false);

            messages.Add(new NmMessage("assistant", response.Content));

            if (response.StopReason == "end_turn")
            {
                await ConversationLogRecorder.RecordTurnAsync(
                    conversationLog, workUnitId, agentId, definition.Name, null, i, response, [], sessionId, ct,
                    client.Provider, client.Model).ConfigureAwait(false);
                completedNaturally = true;
                break;
            }

            if (response.StopReason != "tool_use")
                break;

            var toolResults = new List<NmContent>();
            foreach (var block in response.Content)
            {
                if (block is not NmToolUse toolUse) continue;

                onActivity?.Invoke(ActivityLabeler.Describe(toolUse.Name, toolUse.Input));
                var result = await client
                    .DispatchAsync(toolUse.Name, toolUse.Input, AllowedToolNames, ct, sessionId)
                    .ConfigureAwait(false);

                toolResults.Add(new NmToolResult(toolUse.Id, result));
            }

            await ConversationLogRecorder.RecordTurnAsync(
                conversationLog, workUnitId, agentId, definition.Name, null, i, response, toolResults, sessionId, ct,
                client.Provider, client.Model).ConfigureAwait(false);

            if (toolResults.Count == 0)
                break;

            messages.Add(new NmMessage("user", toolResults));
        }

        onActivity?.Invoke(null);

        if (ct.IsCancellationRequested)
            return AgentLoopCompletion.Cancelled;

        return completedNaturally
            ? AgentLoopCompletion.Succeeded
            : AgentLoopCompletion.MaxIterationsExceeded;
    }

    private static IReadOnlyList<LlmToolDef> BuildTools()
    {
        static object Str(string desc) => new { type = "string", description = desc };

        static object Schema(string[] required, Dictionary<string, object> props) => required.Length > 0
            ? new { type = "object", properties = props, required }
            : (object)new { type = "object", properties = props };

        return
        [
            new(McpToolNames.ProjectionGet, "Get a projection of workspace state.",
                Schema(["projectionType"], new()
                {
                    ["projectionType"] = Str("Projection type: AgentWorkspace, MergeProposal"),
                    ["workUnitId"]     = Str("Work unit ID (for AgentWorkspace)"),
                })),

            new(McpToolNames.ArtifactQuery, "Search knowledge artifacts (Research, Decision, Constraint) for this work unit and its ancestors. Check before recording a finding — an equivalent Constraint may already exist.",
                Schema(["workUnitId"], new()
                {
                    ["workUnitId"] = Str("Work unit ID to search from"),
                    ["type"]       = Str("Filter by type: Research | Decision | Constraint (optional)"),
                    ["keywords"]   = Str("Space-separated keywords to match against title and body (optional)"),
                })),

            new(McpToolNames.WorkspaceSearch, "Search file CONTENTS across the branch (content grep) to corroborate the relevance signal before judging it.",
                Schema(["branchId", "query"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("The work unit being observed — the server resolves the real branch from it"),
                    ["query"]      = Str("Text to search for (literal substring by default; set regex=true to treat it as a .NET regex)"),
                })),

            new(McpToolNames.ArtifactRecord, "Record a Research or Constraint artifact. Title MUST start with this domain agent's literal prefix (see your system prompt).",
                Schema(["workUnitId", "type", "title", "body"], new()
                {
                    ["workUnitId"] = Str("Work unit ID this finding applies to"),
                    ["type"]       = Str("Research or Constraint"),
                    ["title"]      = Str("Must start with your domain agent's exact literal title prefix"),
                    ["body"]       = Str("Concise description of the specific gap"),
                })),
        ];
    }
}
