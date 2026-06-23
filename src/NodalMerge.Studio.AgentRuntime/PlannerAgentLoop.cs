using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class PlannerAgentLoop(
    string agentId,
    string workUnitId,
    string provider,
    string model,
    string baseUrl,
    string apiKey,
    McpToolDispatcher dispatcher,
    LlmClient llm,
    AgentProfile? profile = null,
    string? sessionId = null,
    Action<string?>? onActivity = null,
    string? ruleFileContext = null,
    string? constraintsContext = null,
    IConversationLogService? conversationLog = null)
{
    private static readonly string DefaultSystemPrompt =
        """
        You are a PlannerAgent in NodalMerge Studio.
        Your job is to decompose the work unit's goal into parallelizable slices and write a plan.json file.

        Workflow:
        1. Call nm_v1_workunit_get to understand the goal and learn your branchId.
        2. Call nm_v1_workspace_profile_get with your branchId to see the repo's detected project
           roots (path + stack, e.g. "backend" = dotnet, "frontend" = npm) — a repo can contain
           more than one project. Use this when slicing: a slice about an "endpoint" or "API"
           belongs under a backend root, a slice about a "component" or "page" belongs under a
           frontend root.
        3. Call nm_v1_workspace_list to see existing files in your branch. Always pass your
           workUnitId on every workspace tool call alongside branchId — the server resolves the
           authoritative branch from workUnitId, so this protects you if you ever misremember the
           branchId string.
        4. Decompose the goal into independent slices. Each slice must have:
           - sliceId: short unique id (e.g. "s1", "s2")
           - goal: what this slice accomplishes
           - fileScope: list of file paths this slice may touch
           - dependsOn: list of sliceIds that must complete first (empty for independent slices)
           - steps: ordered implementation steps for the worker
        5. Write plan.json to your branch using nm_v1_workspace_write with this exact JSON shape:
           { "slices": [ { "sliceId": "...", "goal": "...", "fileScope": [...], "dependsOn": [...], "steps": [...] } ] }
        6. Stop — the orchestrator will fan out child workers from your plan.

        Rules:
        - Prefer parallel slices with non-overlapping fileScope when possible.
        - Use dependsOn only when one slice truly needs another's output.
        - Write valid JSON only — no markdown fences in the file content.
        - fileScope entries must be the exact, real relative paths from nm_v1_workspace_list — never
          a filename guessed from the goal text. If the goal mentions a file by name only (e.g.
          "update app.tsx"), find its actual path in the listing by matching the filename
          case-insensitively (e.g. "web-react/src/App.tsx") and use that full path. Only use the
          goal's literal name as-is when no matching file exists anywhere in the listing — i.e. it's
          genuinely a new file.
        - A single slice's fileScope should stay within one project root from step 2 whenever the
          goal allows it — a worker that only looked at one root's files shouldn't be handed a
          slice that also needs changes in another root.
        """;

    private readonly int _maxIterations = profile?.MaxIterations ?? 15;
    private readonly string _systemPrompt = !string.IsNullOrEmpty(profile?.SystemPrompt)
        ? profile.SystemPrompt
        : DefaultSystemPrompt;
    private readonly IReadOnlyList<LlmToolDef> _tools = FilterTools(profile?.AllowedTools);
    private readonly IReadOnlyList<string>? _allowedTools = profile?.AllowedTools is { Count: > 0 }
        ? profile.AllowedTools
        : null;

    public async Task<AgentLoopCompletion> RunAsync(CancellationToken ct)
    {
        var kickoff = $"Plan work unit {workUnitId}. Your agent ID is {agentId}. Write plan.json when done.";
        if (constraintsContext is not null)
            kickoff += "\n\n" + constraintsContext;
        if (ruleFileContext is not null)
            kickoff += "\n\n" + ruleFileContext;

        var messages = new List<NmMessage>
        {
            new("user", [new NmText(kickoff)])
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
                await ConversationLogRecorder.RecordTurnAsync(
                    conversationLog, workUnitId, agentId, "Planner", null, i, response, [], sessionId, ct,
                    provider, model).ConfigureAwait(false);
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

            await ConversationLogRecorder.RecordTurnAsync(
                conversationLog, workUnitId, agentId, "Planner", null, i, response, toolResults, sessionId, ct,
                provider, model).ConfigureAwait(false);

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

            new(McpToolNames.WorkspaceSummary, "Get a summary of the current workspace state.",
                Schema([], new() { ["branchId"] = Str("Branch ID filter (optional)") })),

            new(McpToolNames.WorkspaceProfileGet, "Get the detected project roots for a branch (path + stack, e.g. \"backend\"=dotnet, \"frontend\"=npm). Call this before slicing when a repo might hold more than one project.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                })),

            new(McpToolNames.WorkspaceRead, "Read a file from the branch working directory.",
                Schema(["branchId", "path"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Relative file path")
                })),

            new(McpToolNames.WorkspaceWrite, "Create or fully overwrite a file in the branch working directory.",
                Schema(["branchId", "path", "content"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Relative file path"),
                    ["content"]    = Str("Full file content")
                })),

            new(McpToolNames.WorkspaceList, "List files in the branch working directory. To find a specific existing file by name, omit path and set pattern to that filename.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Subdirectory (optional, omit to search the entire branch)"),
                    ["pattern"]    = Str("Filter to paths matching this filename or wildcard pattern (* and ?), case-insensitive (optional)")
                })),
        ];
    }
}
