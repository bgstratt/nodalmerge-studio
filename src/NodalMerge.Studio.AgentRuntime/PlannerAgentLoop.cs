using NodalMerge.Studio.Contracts.Domain;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class PlannerAgentLoop(
    string agentId,
    string workUnitId,
    IAgentToolClient client,
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
        2. Call nm_v1_artifact_query for this work unit (and its ancestors, included by default) to
           check for prior Constraints or Decisions before you start slicing — don't re-derive or
           contradict something an earlier work unit already established.
        3. Call nm_v1_workspace_profile_get with your branchId to see the repo's detected project
           roots (path + stack, e.g. "backend" = dotnet, "frontend" = npm) — a repo can contain
           more than one project. Use this when slicing: a slice about an "endpoint" or "API"
           belongs under a backend root, a slice about a "component" or "page" belongs under a
           frontend root.
        4. Call nm_v1_workspace_list to see existing files in your branch. Always pass your
           workUnitId on every workspace tool call alongside branchId — the server resolves the
           authoritative branch from workUnitId, so this protects you if you ever misremember the
           branchId string.
          5. For each slice you're about to define, use semantic tools as the authoritative source
              for symbol relationships:
              - nm_v1_workspace_symbol_definition for "where is this symbol defined?"
              - nm_v1_workspace_symbol_references for "where is this used/called?"
              - nm_v1_workspace_symbol_implementation for "what implements this interface/abstract member?"
              Use nm_v1_workspace_search for text/content questions (comments, literals, config keys,
              docs) and to locate non-symbol strings. Derive fileScope from ACTUAL matched paths, not
              from inferring a path from the goal text. Only fall back to a guessed/new path when
              semantic/search results are empty, which means the slice is genuinely introducing new code.
              If you need to read several hit files to confirm a slice's scope, fetch them with one
              nm_v1_workspace_read_many call instead of several sequential nm_v1_workspace_read calls.
        6. Decompose the goal into independent slices. Each slice must have:
           - sliceId: short unique id (e.g. "s1", "s2")
           - goal: what this slice accomplishes
           - fileScope: list of file paths this slice may touch (search-verified per step 5)
           - dependsOn: list of sliceIds that must complete first (empty for independent slices)
           - steps: ordered implementation steps for the worker
        7. Record the plan using nm_v1_artifact_record_plan with your workUnitId and the plan JSON content.
           The plan JSON must follow this exact shape:
           { "slices": [ { "sliceId": "...", "goal": "...", "fileScope": [...], "dependsOn": [...], "steps": [...] } ] }
        8. Stop — the orchestrator will fan out child workers from your plan.
          9. If key requirements are ambiguous and slicing would require guessing, call
              nm_v1_clarification_request and stop immediately.

        Rules:
        - Prefer parallel slices with non-overlapping fileScope when possible.
                - When semantic tools are available in your profile, they are authoritative for
                    definition/reference/implementation questions. Do not use nm_v1_workspace_search for
                    those relationship queries.
        - Use dependsOn whenever one slice truly needs another's output — and that's not limited to
          slices touching the same files. If a slice introduces an abstraction, contract, schema,
          interface, service, model, or migration that another slice's steps will need to call,
          import, or build on, the consuming slice must declare dependsOn the producing slice even
          when their fileScope lists are completely disjoint. Don't rely on file overlap alone to
          infer ordering — a worker is only allowed to start once every slice it dependsOn has
          actually been merged, so an undeclared dependency means it starts too early, against
          stale or missing content.
        - Write valid JSON only — no markdown fences in the file content.
        - fileScope is a routing hint, not a hard permission boundary — workers are no longer
          blocked from writing outside it. Still make it the exact, real relative paths returned by
          nm_v1_workspace_search or nm_v1_workspace_list (step 5), never a filename guessed from the
          goal text: it's used to route the slice to a matching specialist profile and to warn about
          sibling overlap. If the goal mentions a file by name only (e.g. "update app.tsx"), find its
          actual path by matching the filename case-insensitively (e.g. "web-react/src/App.tsx") and
          use that full path. Only use the goal's literal name as-is when no matching file or symbol
          exists anywhere in the search/listing results — i.e. it's genuinely a new file.
        - A single slice's fileScope should stay within one project root from step 3 whenever the
          goal allows it — a worker that only looked at one root's files shouldn't be handed a
          slice that also needs changes in another root.
        - If nm_v1_workspace_list returns zero files for the entire branch, this is a genuinely
          empty repo — do not guess conventional framework paths (e.g. "src/App.tsx",
          "Program.cs") out of thin air. Either write a single slice with fileScope: [] whose first
          step is "scaffold the project structure," or make scaffolding its own slice that every
          feature slice dependsOn, so nothing else starts until real files and real paths exist to
          target.
                - Never wait for human input inside the loop; use nm_v1_clarification_request to pause.
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
            var response = await client.SendAsync(messages, _tools, _systemPrompt, ct)
                .ConfigureAwait(false);

            messages.Add(new NmMessage("assistant", response.Content));

            if (response.StopReason == "end_turn")
            {
                await ConversationLogRecorder.RecordTurnAsync(
                    conversationLog, workUnitId, agentId, "Planner", null, i, response, [], sessionId, ct,
                    client.Provider, client.Model).ConfigureAwait(false);
                completedNaturally = true;
                break;
            }

            if (response.StopReason != "tool_use")
                break;

            var toolResults = new List<NmContent>();
            var awaitingClarification = false;
            foreach (var block in response.Content)
            {
                if (block is not NmToolUse toolUse) continue;

                onActivity?.Invoke(ActivityLabeler.Describe(toolUse.Name, toolUse.Input));
                var result = await client
                    .DispatchAsync(toolUse.Name, toolUse.Input, _allowedTools, ct, sessionId)
                    .ConfigureAwait(false);

                toolResults.Add(new NmToolResult(toolUse.Id, result));

                if (IsAwaitingClarificationResult(result))
                    awaitingClarification = true;
            }

            await ConversationLogRecorder.RecordTurnAsync(
                conversationLog, workUnitId, agentId, "Planner", null, i, response, toolResults, sessionId, ct,
                client.Provider, client.Model).ConfigureAwait(false);

            if (awaitingClarification)
            {
                onActivity?.Invoke(null);
                return AgentLoopCompletion.AwaitingClarification;
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

    private static bool IsAwaitingClarificationResult(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("awaitingClarification", out var flag)
                && flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
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
        static object Bool(string desc) => new { type = "boolean", description = desc };
        static object Int(string desc) => new { type = "integer", description = desc };
        static object StrArray(string desc) => new { type = "array", items = new { type = "string" }, description = desc };

        static object Schema(string[] required, Dictionary<string, object> props) => required.Length > 0
            ? new { type = "object", properties = props, required }
            : (object)new { type = "object", properties = props };

        return
        [
            new(McpToolNames.WorkUnitGet, "Get a work unit by ID.",
                Schema(["workUnitId"], new() { ["workUnitId"] = Str("Work unit ID") })),

            new(McpToolNames.ArtifactQuery, "Search knowledge artifacts (Research, Decision, Constraint) for this work unit and its ancestors. Call before slicing so you don't re-derive or contradict something already established.",
                Schema(["workUnitId"], new()
                {
                    ["workUnitId"] = Str("Work unit ID to search from"),
                    ["type"]       = Str("Filter by type: Research | Decision | Constraint (optional)"),
                    ["keywords"]   = Str("Space-separated keywords to match against title and body (optional)"),
                })),

            new(McpToolNames.ClarificationRequest, "Request a human clarification. Use this when requirements are ambiguous enough that slicing would otherwise guess intent.",
                Schema(["workUnitId", "question"], new()
                {
                    ["workUnitId"]         = Str("Your work unit ID"),
                    ["question"]           = Str("Concrete question for the human"),
                    ["context"]            = Str("Short context needed to answer (optional)"),
                    ["blocking"]           = Bool("Pause execution awaiting response (optional, default true)"),
                    ["options"]            = StrArray("Constrained answer options (optional)"),
                    ["requestedByAgentId"] = Str("Agent ID for audit trail (optional)")
                })),

            new(McpToolNames.WorkspaceSummary, "Get a summary of the current workspace state.",
                Schema([], new() { ["branchId"] = Str("Branch ID filter (optional)") })),

            new(McpToolNames.WorkspaceStatus, "Get a concise workspace status view with changed files and proposal summaries.",
                Schema([], new()
                {
                    ["branchId"] = Str("Branch ID filter (optional)"),
                    ["workUnitId"] = Str("Work unit ID to resolve the authoritative branch and current proposal chain (optional)"),
                    ["limit"] = Str("Maximum changed-file entries to return (optional, default 50)"),
                    ["offset"] = Str("Changed-file page offset (optional, default 0)"),
                })),

            new(McpToolNames.DocFetch, "Fetch external documentation from an allowlisted URL and record a source artifact with hash/snapshot metadata for reproducibility.",
                Schema(["url", "reason", "workUnitId"], new()
                {
                    ["url"] = Str("Absolute URL to fetch (for example a learn.microsoft.com API page)"),
                    ["reason"] = Str("Why this source is needed for planning or implementation"),
                    ["workUnitId"] = Str("Your work unit ID so lineage and audit events are attached correctly"),
                })),

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

            new(McpToolNames.WorkspaceReadMany, "Read several files in one call instead of multiple sequential nm_v1_workspace_read calls — use this after nm_v1_workspace_search returns several hit files you intend to inspect. A path that doesn't exist comes back with found=false rather than failing the whole call.",
                Schema(["branchId", "paths"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["paths"]      = StrArray("Relative file paths to read (1-50 entries)")
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

            new(McpToolNames.WorkspaceSearch, "Search file CONTENTS across the branch (content grep) — distinct from nm_v1_workspace_list, which only matches filenames. Use this for text/content questions (comments, literals, config keys, docs). For symbol definition/reference/implementation relationships, use the semantic nm_v1_workspace_symbol_* tools.",
                Schema(["branchId", "query"], new()
                {
                    ["branchId"]      = Str("Branch ID"),
                    ["workUnitId"]    = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["query"]         = Str("Text to search for (literal substring by default; set regex=true to treat it as a .NET regex)"),
                    ["path"]          = Str("Sub-directory to search (optional, omit to search the entire branch)"),
                    ["filePattern"]   = Str("Restrict which files are scanned, using the same filename/wildcard syntax as nm_v1_workspace_list (optional)"),
                    ["regex"]         = Bool("Treat query as a regex instead of a literal string (optional, default false)"),
                    ["caseSensitive"] = Bool("Case-sensitive match (optional, default false)"),
                    ["contextLines"]  = Int("Lines of context before/after each match to include in its snippet (optional, default 3, max 20)"),
                    ["maxResults"]    = Int("Stop after this many matches (optional, default 200)"),
                })),

            new(McpToolNames.WorkspaceSymbolDefinition, "Find symbol definition locations with compiler-backed semantic navigation. Prefer this for definition questions.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["symbol"]     = Str("Symbol name to resolve (optional when path+line are provided), e.g. IUserRepository"),
                    ["path"]       = Str("Relative file path for location-based lookup (optional)"),
                    ["line"]       = Int("1-based line number for location-based lookup (optional)"),
                    ["column"]     = Int("1-based column number for location-based lookup (optional)"),
                    ["maxResults"] = Int("Maximum results to return (optional, default 200)"),
                })),

            new(McpToolNames.WorkspaceSymbolReferences, "Find symbol reference locations with compiler-backed semantic navigation. Prefer this for usage/reference questions.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["symbol"]     = Str("Symbol name to resolve (optional when path+line are provided), e.g. IUserRepository"),
                    ["path"]       = Str("Relative file path for location-based lookup (optional)"),
                    ["line"]       = Int("1-based line number for location-based lookup (optional)"),
                    ["column"]     = Int("1-based column number for location-based lookup (optional)"),
                    ["maxResults"] = Int("Maximum results to return (optional, default 200)"),
                })),

            new(McpToolNames.WorkspaceSymbolImplementation, "Find symbol implementation locations with compiler-backed semantic navigation (interfaces/abstract members).",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["symbol"]     = Str("Symbol name to resolve (optional when path+line are provided), e.g. IUserRepository"),
                    ["path"]       = Str("Relative file path for location-based lookup (optional)"),
                    ["line"]       = Int("1-based line number for location-based lookup (optional)"),
                    ["column"]     = Int("1-based column number for location-based lookup (optional)"),
                    ["maxResults"] = Int("Maximum results to return (optional, default 200)"),
                })),

            new(McpToolNames.ArtifactRecordPlan, "Record a Plan artifact directly in the DAG artifact lineage for this work unit. Use this to submit a completed plan instead of writing plan.json to the workspace.",
                Schema(["workUnitId", "planContent"], new()
                {
                    ["workUnitId"]  = Str("Your work unit ID"),
                    ["planContent"] = Str("The plan JSON content exactly as described in step 7"),
                })),
        ];
    }
}
