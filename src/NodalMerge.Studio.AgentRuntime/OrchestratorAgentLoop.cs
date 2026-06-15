using System.Text.Json;
using System.Text.Json.Serialization;
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
    LlmClient llm)
{
    private const int MaxIterations = 25;

    private static readonly IReadOnlyList<LlmToolDef> Tools = BuildTools();

    private static readonly string SystemPrompt =
        """
        You are an OrchestratorAgent in NodalMerge Studio, a collaborative AI workspace.
        Your job is to manage a work unit from planning through to a validated merge proposal.

        Workflow:
        1. Call nm.v1.workunit.get to understand the goal for your assigned work unit.
        2. Break the goal into tasks using nm.v1.task.create (one task at a time).
        3. For each task, spawn a worker with nm.v1.agent.spawn (agentType="worker", taskId=<the task id>).
           Always pass your own model, baseUrl, apiKey, and provider when spawning workers.
        4. Monitor progress with nm.v1.task.list and nm.v1.agent.status.
        5. When all tasks are Completed, call nm.v1.merge.propose then nm.v1.merge.validate.
        6. Stop after the merge proposal is validated — a human will review and approve it.

        Rules:
        - Always check the workspace state before making decisions.
        - Do not approve or apply merges yourself — that requires human approval (AP-4).
        - If you encounter an unrecoverable error, stop and report it clearly.
        - Be efficient: use each tool call purposefully, do not repeat calls unnecessarily.
        """;

    public async Task RunAsync(CancellationToken ct)
    {
        var messages = new List<NmMessage>
        {
            new("user", [new NmText($"Begin orchestrating work unit {workUnitId}. Your agent ID is {agentId}.")])
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

                var input = toolUse.Name == McpToolNames.AgentSpawn
                    ? InjectSpawnCredentials(toolUse.Input)
                    : toolUse.Input;

                var result = await dispatcher
                    .DispatchAsync(toolUse.Name, input, ct)
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
        if (!dict.ContainsKey("model"))    dict["model"]    = JsonSerializer.SerializeToElement(model);
        if (!dict.ContainsKey("baseUrl"))  dict["baseUrl"]  = JsonSerializer.SerializeToElement(baseUrl);
        if (!dict.ContainsKey("apiKey"))   dict["apiKey"]   = JsonSerializer.SerializeToElement(apiKey);
        if (!dict.ContainsKey("provider")) dict["provider"] = JsonSerializer.SerializeToElement(provider);
        return JsonSerializer.SerializeToElement(dict);
    }

    private static IReadOnlyList<LlmToolDef> BuildTools()
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

            new(McpToolNames.AgentSpawn, "Spawn a worker agent to execute a specific task.",
                Schema(["agentType", "workUnitId", "taskId"], new()
                {
                    ["agentType"]  = Str("Agent type: worker"),
                    ["workUnitId"] = Str("Work unit ID"),
                    ["taskId"]     = Str("Task ID to assign to the worker"),
                    ["model"]      = Str("LLM model name (pass your own model)"),
                    ["baseUrl"]    = Str("LLM base URL (pass your own baseUrl)"),
                    ["apiKey"]     = Str("API key (pass your own apiKey)"),
                    ["provider"]   = Str("LLM provider: anthropic or openai (pass your own provider)")
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
        ];
    }
}
