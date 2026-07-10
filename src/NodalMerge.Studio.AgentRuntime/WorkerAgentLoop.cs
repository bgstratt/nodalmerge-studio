using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class WorkerAgentLoop(
    string agentId,
    string workUnitId,
    string taskId,
    IAgentToolClient client,
    AgentProfile? profile = null,
    string? sessionId = null,
    Action<string?>? onActivity = null,
    bool isResume = false,
    string? ruleFileContext = null,
    bool selfVerifyBuild = false,
    bool selfVerifyTest = false,
    string? promptGuidanceContext = null,
    IConversationLogService? conversationLog = null,
    IExecutionEventStream? events = null,
    // Phase 1.4 Continue-track — reconstructed assistant/tool-result turns from a prior attempt's
    // ConversationLogEntry rows (see ContinueService), inserted after the fresh kickoff message.
    // Null/empty for a normal fresh run. The existing ConversationCompactor machinery (elision +
    // rolling summary) applies to this exactly the same as it would to a long single run, which is
    // what makes resuming with full prior context safe now instead of reproducing the original
    // cost blowup unbounded.
    IReadOnlyList<NmMessage>? priorTurns = null,
    // Observability-only — see ConversationCompactor. Optional/nullable so call sites and tests
    // that don't wire a logger keep compiling unchanged.
    ILogger? logger = null)
{
    internal static readonly string DefaultSystemPrompt = AgentLoopPrompts.Worker;

    private readonly int _maxIterations = profile?.MaxIterations ?? 30;
    private readonly string _systemPrompt = !string.IsNullOrEmpty(profile?.SystemPrompt)
        ? profile.SystemPrompt
        : DefaultSystemPrompt;
    private readonly IReadOnlyList<LlmToolDef> _tools = FilterTools(profile?.AllowedTools);
    private readonly IReadOnlyList<string>? _allowedTools = profile?.AllowedTools is { Count: > 0 }
        ? profile.AllowedTools
        : null;

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
        var kickoff = $"Execute task {taskId} for work unit {workUnitId}. Your agent ID is {agentId}.";
        if (isResume)
            kickoff += " This work was previously interrupted (e.g. a host restart) — check " +
                "existing files (nm_v1_workspace_list/nm_v1_workspace_read) and the task's current " +
                "status before starting from scratch; partial progress may already be on the branch.";
        if (ruleFileContext is not null)
            kickoff += "\n\n" + ruleFileContext;
        if (promptGuidanceContext is not null)
            kickoff += "\n\n" + promptGuidanceContext;
        if (selfVerifyBuild || selfVerifyTest)
        {
            var what = selfVerifyBuild && selfVerifyTest ? "build and test" : selfVerifyBuild ? "build" : "test";
            kickoff += $"\n\nThis workspace requires a passing {what} before a merge proposal is " +
                "accepted. Call nm_v1_workspace_build / nm_v1_workspace_test scoped to the root(s) " +
                "you touched after writing files. If it fails, read the error output, fix it, and " +
                "retry before calling nm_v1_merge_propose.";
        }

        var messages = new List<NmMessage>
        {
            new("user", [new NmText(kickoff)])
        };

        if (priorTurns is { Count: > 0 })
        {
            messages.AddRange(priorTurns);

            const string continuationNotice =
                "[Continuing after hitting the iteration limit on a previous attempt — the turns " +
                "above are your own prior work on this task, not a new agent's. You have a fresh " +
                "iteration budget; pick up exactly where you left off rather than starting over.]";

            // Fold into the last message when it's already "user" (the common case — the prior
            // attempt's last recorded turn was its own tool results, with no assistant reply yet
            // since hitting MaxIterations cut it off before the next SendAsync call). Append a new
            // "user" message instead when the last is "assistant" (the rarer edge case: the prior
            // attempt's last cycle produced zero tool results and broke out of its loop without a
            // trailing tool-result turn) — either way, alternation stays valid.
            var last = messages[^1];
            if (last.Role == "user")
            {
                messages[^1] = last with { Content = [.. last.Content, new NmText(continuationNotice)] };
            }
            else
            {
                messages.Add(new NmMessage("user", [new NmText(continuationNotice)]));
            }
        }

        var completedNaturally = false;
        int? lastInputTokens = null;
        for (var i = 0; i < _maxIterations && !ct.IsCancellationRequested; i++)
        {
            ConversationCompactor.ElideStaleToolResults(messages, logger, agentId, workUnitId);
            await ConversationCompactor.ApplyRollingSummaryIfDueAsync(
                    messages, client, ct, logger, agentId, workUnitId, lastInputTokens)
                .ConfigureAwait(false);

            onActivity?.Invoke("Thinking...");
            var response = await client.SendAsync(
                    messages, _tools, _systemPrompt, ct,
                    attempt => OnTransientRetryAsync(attempt, ct))
                .ConfigureAwait(false);
            lastInputTokens = response.InputTokens;

            messages.Add(new NmMessage("assistant", response.Content));

            if (response.StopReason == "end_turn")
            {
                await ConversationLogRecorder.RecordTurnAsync(
                    conversationLog, workUnitId, agentId, "Worker", taskId, i, response, [], sessionId, ct,
                    client.Provider, client.Model).ConfigureAwait(false);
                completedNaturally = true;
                break;
            }

            if (response.StopReason != "tool_use")
                break;

            var toolResults = new List<NmContent>();
            var awaitingFileLease = false;
            var awaitingClarification = false;
            foreach (var block in response.Content)
            {
                if (block is not NmToolUse toolUse) continue;

                onActivity?.Invoke(ActivityLabeler.Describe(toolUse.Name, toolUse.Input));
                var result = await client
                    .DispatchAsync(toolUse.Name, toolUse.Input, _allowedTools, ct, sessionId)
                    .ConfigureAwait(false);

                toolResults.Add(new NmToolResult(toolUse.Id, result));

                if (IsAwaitingFileLeaseResult(result))
                    awaitingFileLease = true;
                if (IsAwaitingClarificationResult(result))
                    awaitingClarification = true;
            }

            await ConversationLogRecorder.RecordTurnAsync(
                conversationLog, workUnitId, agentId, "Worker", taskId, i, response, toolResults, sessionId, ct,
                client.Provider, client.Model).ConfigureAwait(false);

            // Phase 12 — a write hit a file another active sibling currently holds the lease on.
            // Exit now, on this same turn: don't send the conflict back to the LLM for "please
            // wait/retry" prose first — that would burn a model call for a wait that's resolved
            // entirely server-side (the scheduler parks this item; IFileLeaseService's
            // release-and-resume hook re-enqueues it with isResume:true once the holder merges).
            if (awaitingFileLease)
            {
                onActivity?.Invoke(null);
                return AgentLoopCompletion.AwaitingFileLease;
            }

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

    // Phase 12 — McpToolDispatcher.CheckFileLeaseAsync's conflict result is a JSON object with an
    // "awaitingFileLease" property, distinguishable from a plain ToError(...) string. Parsed
    // defensively: any non-object or unparseable result is just a normal tool result, not a signal.
    private static bool IsAwaitingFileLeaseResult(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("awaitingFileLease", out _);
        }
        catch (JsonException)
        {
            return false;
        }
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

            new(McpToolNames.TaskUpdate, "Update a task's status, title, or description.",
                Schema(["taskId"], new()
                {
                    ["taskId"]      = Str("Task ID"),
                    ["status"]      = Str("New status: Open, InProgress, Blocked, Completed, Cancelled"),
                    ["title"]       = Str("New title (optional)"),
                    ["description"] = Str("New description (optional)")
                })),

            new(McpToolNames.ClarificationRequest, "Request a human clarification. Use this when proceeding would require guessing intent; with blocking=true the run pauses until resumed.",
                Schema(["workUnitId", "question"], new()
                {
                    ["workUnitId"]         = Str("Your work unit ID"),
                    ["question"]           = Str("Concrete question for the human"),
                    ["context"]            = Str("Short context needed to answer (optional)"),
                    ["blocking"]           = Bool("Pause execution awaiting response (optional, default true)"),
                    ["options"]            = StrArray("Constrained answer options (optional)"),
                    ["requestedByAgentId"] = Str("Agent ID for audit trail (optional)")
                })),

            new(McpToolNames.WorkspaceRead, "Read a file from the branch working directory. Files over 2000 lines are windowed by default (first 2000 lines) — pass offset/limit to page through the rest, or narrow with nm_v1_workspace_search first if you only need a specific section.",
                Schema(["branchId", "path"], new()
                {
                    ["branchId"]   = Str("Branch ID — get from nm_v1_workunit_get response"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Relative file path, e.g. src/Foo.cs"),
                    ["offset"]     = Int("1-based line number to start reading from (optional, default 1)"),
                    ["limit"]      = Int("Max lines to return (optional, default 2000)")
                })),

            new(McpToolNames.WorkspaceReadMany, "Read several files in one call instead of multiple sequential nm_v1_workspace_read calls — use this after nm_v1_workspace_search returns several hit files you intend to inspect. A path that doesn't exist comes back with found=false rather than failing the whole call. Each file is capped at 2000 lines (truncated=true if cut off) — for a specific truncated file, follow up with nm_v1_workspace_read and offset/limit to page further.",
                Schema(["branchId", "paths"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["paths"]      = StrArray("Relative file paths to read (1-50 entries)")
                })),

            new(McpToolNames.WorkspaceWrite, "Create or fully overwrite a file in the branch working directory. Write = full replacement; there is no append mode.",
                Schema(["branchId", "path", "content"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Relative file path, e.g. src/Foo.cs or README.md"),
                    ["content"]    = Str("Complete file content to write")
                })),

            new(McpToolNames.WorkspaceReplace, "Make a targeted edit to an existing file without rewriting the whole thing: replaces oldText with newText. oldText must match the file's exact current content (whitespace-sensitive) and, by default, must be unique in the file — if it isn't, the call fails and tells you how many occurrences it actually found, so you can add surrounding lines to oldText for uniqueness or pass expectedMatches to confirm you intend more than one. Use this for a localized change to an existing file; use nm_v1_workspace_write instead for a brand-new file or a rewrite broad enough that no single old/new pair would be unique.",
                Schema(["branchId", "path", "oldText", "newText"], new()
                {
                    ["branchId"]        = Str("Branch ID"),
                    ["workUnitId"]      = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]            = Str("Relative file path of the existing file to edit"),
                    ["oldText"]         = Str("Exact existing text to replace (whitespace-sensitive). Must be unique in the file unless expectedMatches is set."),
                    ["newText"]         = Str("Replacement text"),
                    ["expectedMatches"] = Int("How many occurrences of oldText you expect to replace (optional, default 1 — i.e. oldText must be unique)"),
                })),

            new(McpToolNames.WorkspaceDelete, "Delete a file from the branch working directory.",
                Schema(["branchId", "path"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Relative file path to delete")
                })),

            new(McpToolNames.WorkspaceExists, "Check whether a file exists in the branch working directory.",
                Schema(["branchId", "path"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Relative file path to check")
                })),

            new(McpToolNames.WorkspaceList, "List files in the branch working directory. To find a specific existing file by name, omit path and set pattern to that filename — this searches the WHOLE branch recursively in one call, which is far more reliable than guessing a directory.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Sub-directory to list (optional, omit to search the entire branch)"),
                    ["pattern"]    = Str("Filter to paths matching this filename or wildcard pattern (* and ?), case-insensitive (optional) — e.g. \"WeatherForecastController.cs\" or \"*Controller*\"")
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

            new(McpToolNames.WorkspaceSymbolDefinition, "Find symbol definition locations with compiler-backed semantic navigation. Prefer this over text search for definition questions.",
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

            new(McpToolNames.WorkspaceSymbolReferences, "Find symbol reference locations with compiler-backed semantic navigation. Prefer this over text search for call-site/reference questions.",
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

            new(McpToolNames.WorkspaceSymbolImplementation, "Find symbol implementation locations with compiler-backed semantic navigation (for interfaces/abstract members).",
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

            new(McpToolNames.WorkspaceDiff, "Show the diff between this branch and the target branch.",
                Schema(["branchId", "targetBranchId"], new()
                {
                    ["branchId"]       = Str("Your working branch ID"),
                    ["workUnitId"]     = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["targetBranchId"] = Str("Branch to diff against (usually main)")
                })),

            new(McpToolNames.MergePropose, "Submit a merge proposal from a work branch.",
                Schema(["sourceBranch", "targetBranch", "summary"], new()
                {
                    ["sourceBranch"]      = Str("Source branch (your branchId)"),
                    ["targetBranch"]      = Str("Target branch (usually main)"),
                    ["summary"]           = Str("Summary of changes, including files modified"),
                    ["goal"]              = Str("Goal that was accomplished (optional)"),
                    ["changeDescription"] = Str("Detailed change description (optional)"),
                    ["agentId"]           = Str("Your agent ID for attribution (optional)"),
                    ["workUnitId"]        = Str("Your work unit ID — strongly recommended; the server resolves the real branch from it and ignores sourceBranch if both are given, and it's also used for artifact tracking"),
                    ["noFileChangesJustification"] = Str(
                        "Only set this if you have a genuine reason for proposing zero file changes " +
                        "(e.g. the task asked you to verify something that already works). Leave unset " +
                        "for normal work — if you wrote files via workspace.write, you don't need this."),
                })),

            new(McpToolNames.MergeValidate, "Validate a draft merge proposal, moving it to ReadyForReview.",
                Schema(["proposalId"], new() { ["proposalId"] = Str("Merge proposal ID") })),

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

            new(McpToolNames.DocFetch, "Fetch external documentation from an allowlisted URL and record a source artifact with hash/snapshot metadata for traceability.",
                Schema(["url", "reason", "workUnitId"], new()
                {
                    ["url"] = Str("Absolute URL to fetch (for example a learn.microsoft.com API page)"),
                    ["reason"] = Str("Why this source is needed for the current change"),
                    ["workUnitId"] = Str("Your work unit ID so lineage and audit events are attached correctly"),
                })),

            new(McpToolNames.WorkspaceProfileGet, "Get the detected project roots for a branch (path + stack, e.g. \"backend\"=dotnet, \"frontend\"=npm). Call this before exploring files when a repo might hold more than one project.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                })),

            new(McpToolNames.WorkspaceBuild, "Run build on a branch, auto-detecting the build command per WorkspaceProfile root. Use rootPath to scope to the one root you touched instead of rebuilding everything.",
                Schema(["branchId"], new()
                {
                    ["branchId"]       = Str("Branch ID — pass it directly; this tool does not resolve it from workUnitId"),
                    ["buildCommand"]   = Str("Explicit build command override (optional) — omit to auto-detect per root"),
                    ["timeoutSeconds"] = Str("Timeout in seconds (optional, default 300)"),
                    ["rootPath"]       = Str("Limit to one root's RelativePath from nm_v1_workspace_profile_get (optional) — omit to build every detected root"),
                })),

            new(McpToolNames.WorkspaceTest, "Run tests on a branch, auto-detecting the test command per WorkspaceProfile root. Use rootPath to scope to the one root you touched instead of testing everything.",
                Schema(["branchId"], new()
                {
                    ["branchId"]       = Str("Branch ID — pass it directly; this tool does not resolve it from workUnitId"),
                    ["testCommand"]    = Str("Explicit test command override (optional) — omit to auto-detect per root"),
                    ["timeoutSeconds"] = Str("Timeout in seconds (optional, default 300)"),
                    ["rootPath"]       = Str("Limit to one root's RelativePath from nm_v1_workspace_profile_get (optional) — omit to test every detected root"),
                })),

            new(McpToolNames.WorkspaceExec, "Run lint (and optionally build/test) on a branch in one call, auto-detecting the lint command per WorkspaceProfile root unless lintCommand is given. Use this with lint=true, build=false, test=false for a lint-only pass — nm_v1_workspace_build / nm_v1_workspace_test remain the tools for build/test verification.",
                Schema(["branchId"], new()
                {
                    ["branchId"]       = Str("Branch ID — pass it directly; this tool does not resolve it from workUnitId"),
                    ["build"]          = Bool("Whether to also run build (optional, default true — set false for a lint-only call)"),
                    ["test"]           = Bool("Whether to also run tests (optional, default true — set false for a lint-only call)"),
                    ["lint"]           = Bool("Whether to run lint (optional, default false — set true to lint)"),
                    ["buildCommand"]   = Str("Explicit build command override (optional)"),
                    ["testCommand"]    = Str("Explicit test command override (optional)"),
                    ["lintCommand"]    = Str("Explicit lint command override (optional) — omit to auto-detect"),
                    ["timeoutSeconds"] = Int("Timeout in seconds (optional, default 300)"),
                })),

            new(McpToolNames.ArtifactRecord, "Record a durable knowledge note (Research, Decision, or Constraint) so future work units don't have to rediscover it.",
                Schema(["workUnitId", "type", "title", "body"], new()
                {
                    ["workUnitId"]       = Str("Your work unit ID"),
                    ["type"]             = Str("Research | Decision | Constraint"),
                    ["title"]            = Str("Short title"),
                    ["body"]             = Str("The note content (markdown)"),
                    ["parentArtifactId"] = Str("Artifact to attach this under (optional, defaults to your work unit's Goal)"),
                })),

            new(McpToolNames.ArtifactQuery, "Search knowledge artifacts for this work unit and its ancestors by type and/or keyword.",
                Schema(["workUnitId"], new()
                {
                    ["workUnitId"] = Str("Work unit ID to search from"),
                    ["type"]       = Str("Filter by type: Research | Decision | Constraint (optional)"),
                    ["keywords"]   = Str("Space-separated keywords to match against title and body (optional)"),
                })),

            new(McpToolNames.ArtifactList, "List the full artifact chain for a work unit, including ancestors' artifacts by default.",
                Schema(["workUnitId"], new()
                {
                    ["workUnitId"]       = Str("Work unit ID"),
                    ["includeAncestors"] = Str("true/false — include ancestor work units' artifacts (optional, default true)"),
                })),
        ];
    }
}
