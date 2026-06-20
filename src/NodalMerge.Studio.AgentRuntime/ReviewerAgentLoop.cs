using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class ReviewerAgentLoop(
    string agentId,
    string workUnitId,
    string proposalId,
    string provider,
    string model,
    string baseUrl,
    string apiKey,
    McpToolDispatcher dispatcher,
    LlmClient llm,
    AgentProfile? profile = null,
    string? sessionId = null,
    Action<string?>? onActivity = null)
{
    private static readonly string DefaultSystemPrompt =
        """
        You are a ReviewerAgent in NodalMerge Studio.
        Your job is to evaluate a merge proposal before it reaches human review.

        Workflow:
        1. Call nm_v1_projection_get with projectionType="AgentWorkspace" and the work unit ID to see artifacts.
        2. Call nm_v1_merge_validate if the proposal is still Draft (usually already ReadyForReview).
        3. Read changed files with nm_v1_workspace_read from the proposal's source branch.
        4. Compare filesTouched against the original goal and plan fileScope.
        5. Call nm_v1_merge_review with automated=true, decision Approved or Rejected, and verificationResults
           explaining your findings. Approved means the proposal may proceed to human review; Rejected blocks it.

        Rules:
        - Always set automated=true on merge.review — you are the pre-gate, not the human approver.
        - verificationResults must be a concise note (what you checked and why you approved/rejected).
        - Reject if required files are missing, changes are obviously wrong, or scope does not match the goal.
        """;

    private readonly int _maxIterations = profile?.MaxIterations ?? 10;
    private readonly string _systemPrompt = !string.IsNullOrEmpty(profile?.SystemPrompt)
        ? profile.SystemPrompt
        : DefaultSystemPrompt;
    private readonly IReadOnlyList<LlmToolDef> _tools = FilterTools(profile?.AllowedTools);
    private readonly IReadOnlyList<string>? _allowedTools = profile?.AllowedTools is { Count: > 0 }
        ? profile.AllowedTools
        : null;

    public async Task<AgentLoopCompletion> RunAsync(CancellationToken ct)
    {
        var messages = new List<NmMessage>
        {
            new("user", [new NmText(
                $"Review merge proposal {proposalId} for work unit {workUnitId}. " +
                $"Your agent ID is {agentId}. Submit automated review when done.")])
        };

        var completedNaturally = false;
        for (var i = 0; i < _maxIterations && !ct.IsCancellationRequested; i++)
        {
            onActivity?.Invoke("Thinking...");
            var response = await llm.SendAsync(provider, model, baseUrl, apiKey, messages, _tools, _systemPrompt, ct)
                .ConfigureAwait(false);

            messages.Add(new NmMessage("assistant", response.Content));

            if (response.StopReason == "end_turn")
            {
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
                var result = await dispatcher
                    .DispatchAsync(toolUse.Name, toolUse.Input, _allowedTools, ct, sessionId)
                    .ConfigureAwait(false);

                toolResults.Add(new NmToolResult(toolUse.Id, result));
            }

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

    private static IReadOnlyList<LlmToolDef> FilterTools(IReadOnlyList<string>? allowedTools)
    {
        var all = BuildAllTools();
        return allowedTools is { Count: > 0 }
            ? all.Where(t => allowedTools.Contains(t.Name)).ToList()
            : all;
    }

    private static IReadOnlyList<LlmToolDef> BuildAllTools()
    {
        static object Str(string desc) => new { type = "string", description = desc };

        static object Schema(string[] required, Dictionary<string, object> props) => required.Length > 0
            ? new { type = "object", properties = props, required }
            : (object)new { type = "object", properties = props };

        return
        [
            new(McpToolNames.WorkUnitGet, "Get a work unit by ID.",
                Schema(["workUnitId"], new() { ["workUnitId"] = Str("Work unit ID") })),

            new(McpToolNames.MergeValidate, "Validate a draft proposal, moving it to ReadyForReview.",
                Schema(["proposalId"], new() { ["proposalId"] = Str("Merge proposal ID") })),

            new(McpToolNames.MergeReview, "Submit automated pre-gate review (set automated=true).",
                Schema(["proposalId", "decision", "verificationResults"], new()
                {
                    ["proposalId"]          = Str("Merge proposal ID"),
                    ["decision"]            = Str("Approved or Rejected"),
                    ["verificationResults"] = Str("Concise review notes"),
                    ["automated"]           = Str("Must be true for automated pre-gate review"),
                })),

            new(McpToolNames.ProjectionGet, "Get a projection of workspace state.",
                Schema(["projectionType"], new()
                {
                    ["projectionType"] = Str("Projection type: AgentWorkspace, MergeProposal"),
                    ["workUnitId"]     = Str("Work unit ID (for AgentWorkspace)"),
                    ["proposalId"]     = Str("Proposal ID (for MergeProposal)"),
                })),

            new(McpToolNames.WorkspaceRead, "Read a file from the branch working directory.",
                Schema(["branchId", "path"], new()
                {
                    ["branchId"] = Str("Branch ID"),
                    ["path"]     = Str("Relative file path"),
                })),
        ];
    }
}
