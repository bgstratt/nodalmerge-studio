using System.Text.Json;
using System.Text.Json.Serialization;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class OrchestratorAgentLoop(
    string agentId,
    string workUnitId,
    string provider,
    string model,
    string baseUrl,
    string apiKey,
    McpToolDispatcher dispatcher,
    LlmClient llm,
    AgentProfile? profile = null)
{
    private static readonly string DefaultSystemPrompt =
        """
        You are an OrchestratorAgent in NodalMerge Studio, a collaborative AI workspace.
        Your job is to manage a work unit from planning through to a validated merge proposal.

        Routing strategy — read artifact state before each decision:
        Call nm_v1_projection_get with projectionType="AgentWorkspace" and your workUnitId to see the current
        artifact chain. Then route based on what exists:
        - No Task artifacts → create tasks with nm_v1_task_create, then spawn a worker.
        - Task artifacts exist but no MergeProposal → spawn a worker for each open task.
        - MergeProposal artifact exists with status Active/ReadyForReview → wait for human review; stop.
        - MergeProposal artifact with status Approved → call nm_v1_merge_apply; done.
        - MergeProposal artifact with status Rejected → create a new task to address the rejection, spawn another worker.

        Workflow:
        1. Call nm_v1_workunit_get to understand the goal for your assigned work unit.
        2. Call nm_v1_projection_get (projectionType="AgentWorkspace", workUnitId=<id>) to see existing artifacts.
        3. Route based on artifact state (see above).
        4. When spawning a worker: agentType="worker", profileId="worker", workUnitId=<your workUnitId>, taskId=<task id>.
           LLM credentials are injected automatically — do not supply model, baseUrl, apiKey, or provider.
        5. Monitor progress with nm_v1_task_list and nm_v1_agent_status.
        6. After a worker completes, re-read the AgentWorkspace projection to see if a proposal now exists.
        7. Stop after a proposal is validated — a human will review and approve it.

        Rules:
        - Always re-read the AgentWorkspace projection before making a routing decision.
        - Do not approve or apply merges yourself — that requires human approval.
        - If you encounter an unrecoverable error, stop and report it clearly.
        - Be efficient: use each tool call purposefully, do not repeat calls unnecessarily.
        - Do not spawn a second worker for a task that already has an active worker.
        """;

    private readonly int _maxIterations = profile?.MaxIterations ?? 25;
    private readonly string _systemPrompt = !string.IsNullOrEmpty(profile?.SystemPrompt)
        ? profile.SystemPrompt
        : DefaultSystemPrompt;
    private readonly IReadOnlyList<LlmToolDef> _tools = FilterTools(profile?.AllowedTools);
    private readonly IReadOnlyList<string>? _allowedTools = profile?.AllowedTools is { Count: > 0 }
        ? profile.AllowedTools
        : null;

    public async Task RunAsync(CancellationToken ct)
    {
        var messages = new List<NmMessage>
        {
            new("user", [new NmText($"Begin orchestrating work unit {workUnitId}. Your agent ID is {agentId}.")])
        };

        for (var i = 0; i < _maxIterations && !ct.IsCancellationRequested; i++)
        {
            var response = await llm.SendAsync(provider, model, baseUrl, apiKey, messages, _tools, _systemPrompt, ct)
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

                var input = toolUse.Name == McpToolNames.AgentSpawn
                    ? InjectSpawnCredentials(toolUse.Input)
                    : toolUse.Input;

                var result = await dispatcher
                    .DispatchAsync(toolUse.Name, input, _allowedTools, ct)
                    .ConfigureAwait(false);

                toolResults.Add(new NmToolResult(toolUse.Id, result));
            }

            if (toolResults.Count == 0)
                break;

            messages.Add(new NmMessage("user", toolResults));
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private JsonElement InjectSpawnCredentials(JsonElement input)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input) ?? [];
        // Always overwrite credentials — the model must not hallucinate them.
        dict["model"]    = JsonSerializer.SerializeToElement(model);
        dict["baseUrl"]  = JsonSerializer.SerializeToElement(baseUrl);
        dict["apiKey"]   = JsonSerializer.SerializeToElement(apiKey);
        dict["provider"] = JsonSerializer.SerializeToElement(provider);
        // Default profileId to "worker" if the model didn't specify one.
        if (!dict.ContainsKey("profileId"))
            dict["profileId"] = JsonSerializer.SerializeToElement("worker");
        return JsonSerializer.SerializeToElement(dict);
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
        static object Int(string desc) => new { type = "integer", description = desc };

        static object Schema(string[] required, Dictionary<string, object> props) => required.Length > 0
            ? new { type = "object", properties = props, required }
            : (object)new { type = "object", properties = props };

        return
        [
            new(McpToolNames.WorkUnitGet, "Get a work unit by ID.",
                Schema(["workUnitId"], new() { ["workUnitId"] = Str("Work unit ID") })),

            new(McpToolNames.WorkUnitCreate, "Create a new work unit with a branch.",
                Schema(["goal", "branchId"], new()
                {
                    ["goal"]            = Str("Goal description"),
                    ["branchId"]        = Str("Branch ID to associate"),
                    ["owner"]           = Str("Owner name (optional)"),
                    ["successCriteria"] = Str("Success criteria (optional)")
                })),

            new(McpToolNames.WorkUnitUpdate, "Update work unit status or assignment.",
                Schema(["workUnitId"], new()
                {
                    ["workUnitId"]    = Str("Work unit ID"),
                    ["status"]        = Str("New status: Created, Active, Waiting, Completed, Failed"),
                    ["assignedAgent"] = Str("Agent ID to assign")
                })),

            new(McpToolNames.WorkUnitList, "List work units, optionally filtered by branch.",
                Schema([], new() { ["branchId"] = Str("Branch ID filter (optional)") })),

            new(McpToolNames.TaskCreate, "Create a task for a work unit.",
                Schema(["workUnitId", "title", "description"], new()
                {
                    ["workUnitId"]   = Str("Work unit ID"),
                    ["title"]        = Str("Task title"),
                    ["description"]  = Str("Task description"),
                    ["priority"]     = Int("Priority 0-9 (optional, default 0)")
                })),

            new(McpToolNames.TaskList, "List tasks, optionally filtered by work unit.",
                Schema([], new() { ["workUnitId"] = Str("Work unit ID filter (optional)") })),

            new(McpToolNames.TaskUpdate, "Update a task's status, title, description, or priority.",
                Schema(["taskId"], new()
                {
                    ["taskId"]      = Str("Task ID"),
                    ["status"]      = Str("New status: Open, InProgress, Blocked, Completed, Cancelled"),
                    ["title"]       = Str("New title (optional)"),
                    ["description"] = Str("New description (optional)"),
                    ["priority"]    = Int("New priority (optional)")
                })),

            new(McpToolNames.TaskAssign, "Assign a task to an agent.",
                Schema(["taskId", "agentId"], new()
                {
                    ["taskId"]   = Str("Task ID"),
                    ["agentId"]  = Str("Agent ID to assign")
                })),

            new(McpToolNames.BranchCreate, "Create a new branch.",
                Schema(["name"], new()
                {
                    ["name"]       = Str("Branch name"),
                    ["fromBranch"] = Str("Parent branch ID (optional)")
                })),

            new(McpToolNames.BranchList, "List all branches.",
                Schema([], new())),

            new(McpToolNames.BranchStatus, "Get branch status.",
                Schema(["branchId"], new() { ["branchId"] = Str("Branch ID") })),

            new(McpToolNames.AgentSpawn, "Spawn a worker agent to execute a specific task. LLM credentials are injected automatically.",
                Schema(["agentType", "workUnitId", "taskId"], new()
                {
                    ["agentType"]  = Str("Agent type: worker"),
                    ["workUnitId"] = Str("Work unit ID (use your own workUnitId)"),
                    ["taskId"]     = Str("Task ID to assign to the worker"),
                    ["profileId"]  = Str("Pipeline profile ID to load for the worker (optional, e.g. 'worker')"),
                })),

            new(McpToolNames.AgentStatus, "Get the status of an agent.",
                Schema(["agentId"], new() { ["agentId"] = Str("Agent ID") })),

            new(McpToolNames.AgentStop, "Stop a running agent.",
                Schema(["agentId"], new() { ["agentId"] = Str("Agent ID") })),

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

            new(McpToolNames.SnapshotGet, "Get an agent's execution snapshot.",
                Schema(["agentId", "workUnitId"], new()
                {
                    ["agentId"]    = Str("Agent ID"),
                    ["workUnitId"] = Str("Work unit ID")
                })),

            new(McpToolNames.ProjectionGet, "Get a projection of the current workspace state. Use projectionType='AgentWorkspace' with workUnitId to read the artifact chain for routing decisions.",
                Schema(["projectionType"], new()
                {
                    ["projectionType"] = Str("Projection type: AgentWorkspace, WorkUnit, Task, MergeProposal, ExecutionSnapshot, AuthoritativeState"),
                    ["projectionLevel"] = Str("Compression level: Full, Normal, Compact, Emergency (optional, default Normal)"),
                    ["workUnitId"]      = Str("Work unit ID (required for AgentWorkspace)"),
                    ["agentId"]         = Str("Agent ID (optional)"),
                })),

            new(McpToolNames.MergeApply, "Apply an approved merge proposal, writing changes back to disk.",
                Schema(["proposalId"], new() { ["proposalId"] = Str("Approved merge proposal ID") })),
        ];
    }
}
