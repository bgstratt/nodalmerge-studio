using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class WorkerAgentLoop(
    string agentId,
    string workUnitId,
    string taskId,
    string provider,
    string model,
    string baseUrl,
    string apiKey,
    McpToolDispatcher dispatcher,
    LlmClient llm)
{
    private const int MaxIterations = 20;

    private static readonly IReadOnlyList<LlmToolDef> Tools = BuildTools();

    private static readonly string SystemPrompt =
        """
        You are a WorkerAgent in NodalMerge Studio.
        Your job is to execute a single assigned task, then propose a merge of your work.

        Workflow:
        1. Call nm.v1.task.update to set your task status to InProgress.
        2. Call nm.v1.workunit.get to understand the broader goal context.
        3. Reason about the work required for this task and do it.
        4. When work is complete, call nm.v1.task.update to set the task status to Completed.
        5. Call nm.v1.merge.propose to create a merge proposal summarising your work.
        6. Call nm.v1.merge.validate to move the proposal to ReadyForReview.
        7. Stop — a human will review and approve the merge (AP-4).

        Rules:
        - You are responsible for one task only. Do not create new tasks or spawn other agents.
        - Do not approve or apply merges yourself.
        - Produce a clear, accurate summary of the work done in your merge proposal.
        - If you cannot complete the task, call nm.v1.task.update with status Blocked and stop.
        """;

    public async Task RunAsync(CancellationToken ct)
    {
        var messages = new List<NmMessage>
        {
            new("user", [new NmText($"Execute task {taskId} for work unit {workUnitId}. Your agent ID is {agentId}.")])
        };

        for (var i = 0; i < MaxIterations && !ct.IsCancellationRequested; i++)
        {
            var response = await llm.SendAsync(provider, model, baseUrl, apiKey, messages, Tools, SystemPrompt, ct)
                .ConfigureAwait(false);

            messages.Add(new NmMessage("assistant", response.Content));

            if (response.StopReason == "end_turn")
                break;

            if (response.StopReason != "tool_use")
                break;

            var toolResults = new List<NmContent>();
            foreach (var block in response.Content)
            {
                if (block is not NmToolUse toolUse) continue;

                var result = await dispatcher
                    .DispatchAsync(toolUse.Name, toolUse.Input, ct)
                    .ConfigureAwait(false);

                toolResults.Add(new NmToolResult(toolUse.Id, result));
            }

            if (toolResults.Count == 0)
                break;

            messages.Add(new NmMessage("user", toolResults));
        }
    }

    private static IReadOnlyList<LlmToolDef> BuildTools()
    {
        static object Str(string desc) => new { type = "string", description = desc };

        static object Schema(string[] required, Dictionary<string, object> props) => required.Length > 0
            ? new { type = "object", properties = props, required }
            : (object)new { type = "object", properties = props };

        return
        [
            new(McpToolNames.WorkUnitGet, "Get a work unit by ID.",
                Schema(["workUnitId"], new() { ["workUnitId"] = Str("Work unit ID") })),

            new(McpToolNames.TaskUpdate, "Update a task's status, title, or description.",
                Schema(["taskId"], new()
                {
                    ["taskId"]      = Str("Task ID"),
                    ["status"]      = Str("New status: Open, InProgress, Blocked, Completed, Cancelled"),
                    ["title"]       = Str("New title (optional)"),
                    ["description"] = Str("New description (optional)")
                })),

            new(McpToolNames.MergePropose, "Submit a merge proposal from a work branch.",
                Schema(["sourceBranch", "targetBranch", "summary"], new()
                {
                    ["sourceBranch"]      = Str("Source branch"),
                    ["targetBranch"]      = Str("Target branch (usually main)"),
                    ["summary"]           = Str("Summary of changes"),
                    ["goal"]              = Str("Goal that was accomplished (optional)"),
                    ["changeDescription"] = Str("Detailed change description (optional)")
                })),

            new(McpToolNames.MergeValidate, "Validate a draft merge proposal, moving it to ReadyForReview.",
                Schema(["proposalId"], new() { ["proposalId"] = Str("Merge proposal ID") })),

            new(McpToolNames.WorkspaceSummary, "Get a summary of the current workspace state.",
                Schema([], new() { ["branchId"] = Str("Branch ID filter (optional)") })),
        ];
    }
}
