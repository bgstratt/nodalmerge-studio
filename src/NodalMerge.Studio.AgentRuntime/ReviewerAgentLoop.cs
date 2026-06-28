using NodalMerge.Studio.Contracts.Domain;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class ReviewerAgentLoop(
    string agentId,
    string workUnitId,
    string proposalId,
    IAgentToolClient client,
    AgentProfile? profile = null,
    string? sessionId = null,
    Action<string?>? onActivity = null,
    IReadOnlyList<string>? filesTouched = null,
    string? noFileChangesJustification = null,
    IConversationLogService? conversationLog = null)
{
    private static readonly string DefaultSystemPrompt =
        """
        You are a ReviewerAgent in NodalMerge Studio.
        Your job is to evaluate a merge proposal before it reaches human review.

        Workflow:
        1. Call nm_v1_projection_get with projectionType="AgentWorkspace" and the work unit ID to see artifacts.
        2. Call nm_v1_artifact_query for the work unit (ancestors included by default) and check the
           proposal's changes against any recorded Constraints — a change that violates a recorded
           constraint is grounds for rejection even if the diff otherwise looks reasonable. Note the
           artifact ID of every Constraint/Research artifact you actually weigh here, whether or not
           it ends up mattering to your decision — you'll cite them in step 7.
        3. Call nm_v1_merge_validate if the proposal is still Draft (usually already ReadyForReview).
        4. Read changed files with nm_v1_workspace_read from the proposal's source branch — or
           nm_v1_workspace_read_many in one call if filesTouched has several entries.
          5. Compare filesTouched against the original goal and plan fileScope. For symbol-relationship
              checks, semantic tools are authoritative:
              - nm_v1_workspace_symbol_definition for "where is this defined now?"
              - nm_v1_workspace_symbol_references for "did we update all call sites/usages?"
              - nm_v1_workspace_symbol_implementation for interface/abstract implementation coverage.
              Use nm_v1_workspace_search for text/content checks only (comments, literals, config keys,
              docs). nm_v1_workspace_diff shows only changed files; semantic tools verify relationship
              coverage across the branch.
        6. If the proposal touches buildable/testable code, verify it before approving:
           - Use nm_v1_workspace_profile_get to find which detected root(s) filesTouched falls under,
             and scope nm_v1_workspace_build/nm_v1_workspace_test to those root(s) via rootPath —
             never build/test every detected root.
           - Run only the project's normal fast/unit test command. Do NOT run integration or e2e
             test commands, and do not call nm_v1_workspace_exec's lint, unless the work unit's goal
             explicitly calls for it — those are the Worker's self-verify step or human review's job,
             not this automated pre-gate's.
           - Leave timeoutSeconds at its default; do not raise it to force a slow suite through.
           - Skip this step entirely if filesTouched contains nothing buildable/testable (e.g. only
             docs or config the detected roots don't cover).
        7. Call nm_v1_merge_review with automated=true, decision Approved or Rejected, verificationResults
           explaining your findings, and consideredArtifactIds listing every Constraint/Research
           artifact ID you weighed in step 2 (omit if you found none worth weighing). Approved means
           the proposal may proceed to human review; Rejected blocks it.
          8. If review intent is ambiguous and would require guessing policy/scope, call
              nm_v1_clarification_request and stop immediately.

        Rules:
        - Always set automated=true on merge.review — you are the pre-gate, not the human approver.
        - verificationResults must be a concise note (what you checked and why you approved/rejected) —
          include the build/test outcome from step 6 when you ran it.
        - Reject if required files are missing, changes are obviously wrong, scope does not match the
          goal, a recorded constraint is violated, a build fails, or a fast/unit test fails.
                - When semantic tools are available in your profile, they are authoritative for
                    definition/reference/implementation questions. Do not use nm_v1_workspace_search for
                    those relationship queries.
        - The kickoff message tells you the proposal's "Files touched." If that list is empty and no
          justification is given, the proposal contains only descriptive text with no concrete change —
          reject it. A justification is only valid if it credibly explains why no file change was needed
          for this specific goal (e.g. "verified existing behavior already satisfies this").
                - Never wait for human input inside the loop; use nm_v1_clarification_request to pause.
        """;

    // 10 -> 14: the build/test verification step (workspace_profile_get + scoped build + scoped
    // test) adds 2-3 tool calls on top of the original projection/artifact/diff/review sequence;
    // 14 keeps headroom without letting a stuck reviewer run much longer than before.
    private readonly int _maxIterations = profile?.MaxIterations ?? 14;
    private readonly string _systemPrompt = !string.IsNullOrEmpty(profile?.SystemPrompt)
        ? profile.SystemPrompt
        : DefaultSystemPrompt;
    private readonly IReadOnlyList<LlmToolDef> _tools = FilterTools(profile?.AllowedTools);
    private readonly IReadOnlyList<string>? _allowedTools = profile?.AllowedTools is { Count: > 0 }
        ? profile.AllowedTools
        : null;

    public async Task<AgentLoopCompletion> RunAsync(CancellationToken ct)
    {
        var filesTouchedNote = filesTouched is { Count: > 0 }
            ? string.Join(", ", filesTouched)
            : "NONE — no file changes were detected on the source branch.";
        var justificationNote = string.IsNullOrWhiteSpace(noFileChangesJustification)
            ? ""
            : $" Worker's justification for no file changes: \"{noFileChangesJustification}\"";

        var messages = new List<NmMessage>
        {
            new("user", [new NmText(
                $"Review merge proposal {proposalId} for work unit {workUnitId}. " +
                $"Your agent ID is {agentId}. Files touched: {filesTouchedNote}{justificationNote} " +
                "Submit automated review when done.")])
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
                    conversationLog, workUnitId, agentId, "Reviewer", null, i, response, [], sessionId, ct,
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
                conversationLog, workUnitId, agentId, "Reviewer", null, i, response, toolResults, sessionId, ct,
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

            new(McpToolNames.ArtifactQuery, "Search knowledge artifacts (Research, Decision, Constraint) for this work unit and its ancestors. Check before approving — a change that violates a recorded Constraint is grounds for rejection.",
                Schema(["workUnitId"], new()
                {
                    ["workUnitId"] = Str("Work unit ID to search from"),
                    ["type"]       = Str("Filter by type: Research | Decision | Constraint (optional)"),
                    ["keywords"]   = Str("Space-separated keywords to match against title and body (optional)"),
                })),

            new(McpToolNames.ClarificationRequest, "Request a human clarification when approval/rejection would otherwise depend on guessing policy or intent.",
                Schema(["workUnitId", "question"], new()
                {
                    ["workUnitId"]         = Str("Your work unit ID"),
                    ["question"]           = Str("Concrete question for the human"),
                    ["context"]            = Str("Short context needed to answer (optional)"),
                    ["blocking"]           = Bool("Pause execution awaiting response (optional, default true)"),
                    ["options"]            = StrArray("Constrained answer options (optional)"),
                    ["requestedByAgentId"] = Str("Agent ID for audit trail (optional)")
                })),

            new(McpToolNames.MergeValidate, "Validate a draft proposal, moving it to ReadyForReview.",
                Schema(["proposalId"], new() { ["proposalId"] = Str("Merge proposal ID") })),

            new(McpToolNames.MergeReview, "Submit automated pre-gate review (set automated=true).",
                Schema(["proposalId", "decision", "verificationResults"], new()
                {
                    ["proposalId"]            = Str("Merge proposal ID"),
                    ["decision"]              = Str("Approved or Rejected"),
                    ["verificationResults"]   = Str("Concise review notes"),
                    ["automated"]             = Str("Must be true for automated pre-gate review"),
                    ["consideredArtifactIds"] = StrArray("IDs of any recorded Constraint/Research artifacts you explicitly checked the proposal against in step 2, whether or not they were violated (optional)"),
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
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("The work unit under review — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Relative file path"),
                })),

            new(McpToolNames.WorkspaceReadMany, "Read several files in one call instead of multiple sequential nm_v1_workspace_read calls — use this after nm_v1_workspace_search returns several hit files you intend to inspect. A path that doesn't exist comes back with found=false rather than failing the whole call.",
                Schema(["branchId", "paths"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("The work unit under review — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["paths"]      = StrArray("Relative file paths to read (1-50 entries)")
                })),

            new(McpToolNames.WorkspaceList, "List files in the branch working directory. To find a specific existing file by name, omit path and set pattern to that filename.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("The work unit under review — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Sub-directory to list (optional, omit to search the entire branch)"),
                    ["pattern"]    = Str("Filter to paths matching this filename or wildcard pattern (* and ?), case-insensitive (optional)"),
                })),

            new(McpToolNames.WorkspaceSearch, "Search file CONTENTS across the branch (content grep) — distinct from nm_v1_workspace_list, which only matches filenames. Use this for text/content checks (comments, literals, config keys, docs). For symbol definition/reference/implementation relationships, use the semantic nm_v1_workspace_symbol_* tools.",
                Schema(["branchId", "query"], new()
                {
                    ["branchId"]      = Str("Proposal's source branch"),
                    ["workUnitId"]    = Str("The work unit under review — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["query"]         = Str("Text to search for (literal substring by default; set regex=true to treat it as a .NET regex)"),
                    ["path"]          = Str("Sub-directory to search (optional, omit to search the entire branch)"),
                    ["filePattern"]   = Str("Restrict which files are scanned, using the same filename/wildcard syntax as nm_v1_workspace_list (optional)"),
                    ["regex"]         = Bool("Treat query as a regex instead of a literal string (optional, default false)"),
                    ["caseSensitive"] = Bool("Case-sensitive match (optional, default false)"),
                    ["contextLines"]  = Int("Lines of context before/after each match to include in its snippet (optional, default 3, max 20)"),
                    ["maxResults"]    = Int("Stop after this many matches (optional, default 200)"),
                })),

            new(McpToolNames.WorkspaceSymbolDefinition, "Find symbol definition locations with compiler-backed semantic navigation. Prefer this over text search for definition checks.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Proposal's source branch"),
                    ["workUnitId"] = Str("The work unit under review — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["symbol"]     = Str("Symbol name to resolve (optional when path+line are provided), e.g. IUserRepository"),
                    ["path"]       = Str("Relative file path for location-based lookup (optional)"),
                    ["line"]       = Int("1-based line number for location-based lookup (optional)"),
                    ["column"]     = Int("1-based column number for location-based lookup (optional)"),
                    ["maxResults"] = Int("Maximum results to return (optional, default 200)"),
                })),

            new(McpToolNames.WorkspaceSymbolReferences, "Find symbol reference locations with compiler-backed semantic navigation. Prefer this for usage/call-site checks.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Proposal's source branch"),
                    ["workUnitId"] = Str("The work unit under review — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["symbol"]     = Str("Symbol name to resolve (optional when path+line are provided), e.g. IUserRepository"),
                    ["path"]       = Str("Relative file path for location-based lookup (optional)"),
                    ["line"]       = Int("1-based line number for location-based lookup (optional)"),
                    ["column"]     = Int("1-based column number for location-based lookup (optional)"),
                    ["maxResults"] = Int("Maximum results to return (optional, default 200)"),
                })),

            new(McpToolNames.WorkspaceSymbolImplementation, "Find symbol implementation locations with compiler-backed semantic navigation (interfaces/abstract members).",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Proposal's source branch"),
                    ["workUnitId"] = Str("The work unit under review — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["symbol"]     = Str("Symbol name to resolve (optional when path+line are provided), e.g. IUserRepository"),
                    ["path"]       = Str("Relative file path for location-based lookup (optional)"),
                    ["line"]       = Int("1-based line number for location-based lookup (optional)"),
                    ["column"]     = Int("1-based column number for location-based lookup (optional)"),
                    ["maxResults"] = Int("Maximum results to return (optional, default 200)"),
                })),

            new(McpToolNames.DocFetch, "Fetch external documentation from an allowlisted URL and record a source artifact with hash/snapshot metadata.",
                Schema(["url", "reason", "workUnitId"], new()
                {
                    ["url"] = Str("Absolute URL to fetch (for example a learn.microsoft.com API page)"),
                    ["reason"] = Str("Why this source matters for the review decision"),
                    ["workUnitId"] = Str("The work unit under review so lineage and audit events are attached correctly"),
                })),

            new(McpToolNames.WorkspaceDiff, "Show the diff between this branch and the target branch.",
                Schema(["branchId", "targetBranchId"], new()
                {
                    ["branchId"]       = Str("Proposal's source branch"),
                    ["workUnitId"]     = Str("The work unit under review — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["targetBranchId"] = Str("Proposal's target branch"),
                })),

            new(McpToolNames.WorkspaceProfileGet, "Get the detected project roots for a branch (path + stack). Use this to find which root(s) filesTouched falls under before scoping build/test verification to them.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Proposal's source branch"),
                    ["workUnitId"] = Str("The work unit under review — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                })),

            new(McpToolNames.WorkspaceBuild, "Run build on the proposal's source branch, scoped via rootPath to the root(s) filesTouched falls under — never build every detected root for an automated pre-gate review.",
                Schema(["branchId"], new()
                {
                    ["branchId"]       = Str("Proposal's source branch — pass it directly; this tool does not resolve it from workUnitId"),
                    ["buildCommand"]   = Str("Explicit build command override (optional) — omit to auto-detect per root"),
                    ["timeoutSeconds"] = Str("Timeout in seconds (optional, default 300) — leave at default"),
                    ["rootPath"]       = Str("Limit to one root's RelativePath from nm_v1_workspace_profile_get — required scoping for review, do not omit unless there is exactly one root"),
                })),

            new(McpToolNames.WorkspaceTest, "Run the project's normal fast/unit test command on the proposal's source branch, scoped via rootPath. Do NOT use this to run integration or e2e suites — only the default auto-detected (or explicitly fast) test command.",
                Schema(["branchId"], new()
                {
                    ["branchId"]       = Str("Proposal's source branch — pass it directly; this tool does not resolve it from workUnitId"),
                    ["testCommand"]    = Str("Explicit test command override (optional) — only set this to a FAST/unit command, never an integration/e2e command"),
                    ["timeoutSeconds"] = Str("Timeout in seconds (optional, default 300) — leave at default"),
                    ["rootPath"]       = Str("Limit to one root's RelativePath from nm_v1_workspace_profile_get — required scoping for review, do not omit unless there is exactly one root"),
                })),
        ];
    }
}
