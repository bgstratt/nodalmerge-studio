using NodalMerge.Studio.Contracts.Domain;
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
    LlmClient llm,
    AgentProfile? profile = null,
    string? sessionId = null,
    Action<string?>? onActivity = null,
    bool isResume = false,
    string? ruleFileContext = null,
    bool selfVerifyBuild = false,
    bool selfVerifyTest = false)
{
    private static readonly string DefaultSystemPrompt =
        """
        You are a WorkerAgent in NodalMerge Studio.
        Your job is to execute a single assigned task by modifying files, then propose a merge of your work.

        Workflow:
        1. Call nm_v1_task_update to set your task status to InProgress.
        2. Call nm_v1_workunit_get to understand the broader goal and to learn your branchId.
        3. Call nm_v1_workspace_profile_get with your branchId to see the repo's detected project
           roots (path + stack, e.g. "backend" = dotnet, "frontend" = npm) — a repo can contain
           more than one project. Use this to figure out which root the task actually belongs to
           before exploring files: "endpoint" usually means a controller/route handler in a
           backend root, not a new frontend file; "component" or "page" usually means a frontend
           root. If the task is ambiguous about which root, check the root whose stack matches the
           terminology first.
        4. Use nm_v1_workspace_list, scoped to that root's path, to explore the existing files there.
        5. Use nm_v1_workspace_read to read files you need to understand or modify.
        6. Use nm_v1_workspace_write or nm_v1_workspace_delete to make the required changes.
           IMPORTANT: Write = full file replacement. Always write the complete file content, not just a diff.
           If modifying an existing file, read it first, then write the complete updated version.
           Before writing to a path that isn't in the nm_v1_workspace_list output, double-check for a
           file with the same name elsewhere in the tree (different directory, or different case),
           and check whether a different root (from step 3) is the one that actually owns this
           concern — that's almost always the real file to modify. Write to that real path instead
           of creating a new one. Only write to a brand-new path when you're genuinely adding a new file.
        7. When work is complete, call nm_v1_task_update to set the task status to Completed.
        8. (Optional) If you learned something future work units shouldn't have to rediscover — a fact about
           the codebase, a decision you made, or a constraint that must hold — call nm_v1_artifact_record
           with type Research, Decision, or Constraint. Check nm_v1_artifact_query first to avoid duplicates.
        9. Call nm_v1_workspace_diff to review your changes against the target branch (usually main).
        10. Call nm_v1_merge_propose with an accurate summary listing the files changed. Include your agentId and workUnitId.
        11. Call nm_v1_merge_validate to move the proposal to ReadyForReview.
        12. Stop — a human will review and approve the merge.

        Rules:
        - Always get your branchId from nm_v1_workunit_get before calling workspace tools.
        - Always pass your workUnitId on every workspace and merge.propose call, alongside
          branchId/sourceBranch — the server resolves the authoritative branch from workUnitId,
          so this protects you if you ever misremember the branchId string.
        - Write real, complete file content — do not describe what you would write.
        - If nm_v1_merge_propose returns status "Rejected" with a reason about missing file
          changes, you described the work without doing it — go back to step 6 and call
          nm_v1_workspace_write, then propose again. Do not proceed to nm_v1_merge_validate on a
          Rejected proposal.
        - Do not approve or apply merges yourself.
        - You are responsible for one task only. Do not create new tasks or spawn other agents.
        - If you cannot complete the task, call nm_v1_task_update with status Blocked and stop.
        """;

    private readonly int _maxIterations = profile?.MaxIterations ?? 30;
    private readonly string _systemPrompt = !string.IsNullOrEmpty(profile?.SystemPrompt)
        ? profile.SystemPrompt
        : DefaultSystemPrompt;
    private readonly IReadOnlyList<LlmToolDef> _tools = FilterTools(profile?.AllowedTools);
    private readonly IReadOnlyList<string>? _allowedTools = profile?.AllowedTools is { Count: > 0 }
        ? profile.AllowedTools
        : null;

    public async Task<AgentLoopCompletion> RunAsync(CancellationToken ct)
    {
        var kickoff = $"Execute task {taskId} for work unit {workUnitId}. Your agent ID is {agentId}.";
        if (isResume)
            kickoff += " This work was previously interrupted (e.g. a host restart) — check " +
                "existing files (nm_v1_workspace_list/nm_v1_workspace_read) and the task's current " +
                "status before starting from scratch; partial progress may already be on the branch.";
        if (ruleFileContext is not null)
            kickoff += "\n\n" + ruleFileContext;
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

            new(McpToolNames.TaskUpdate, "Update a task's status, title, or description.",
                Schema(["taskId"], new()
                {
                    ["taskId"]      = Str("Task ID"),
                    ["status"]      = Str("New status: Open, InProgress, Blocked, Completed, Cancelled"),
                    ["title"]       = Str("New title (optional)"),
                    ["description"] = Str("New description (optional)")
                })),

            new(McpToolNames.WorkspaceRead, "Read a file's full content from the branch working directory.",
                Schema(["branchId", "path"], new()
                {
                    ["branchId"]   = Str("Branch ID — get from nm_v1_workunit_get response"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Relative file path, e.g. src/Foo.cs")
                })),

            new(McpToolNames.WorkspaceWrite, "Create or fully overwrite a file in the branch working directory. Write = full replacement; there is no append mode.",
                Schema(["branchId", "path", "content"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Relative file path, e.g. src/Foo.cs or README.md"),
                    ["content"]    = Str("Complete file content to write")
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

            new(McpToolNames.WorkspaceList, "List files in the branch working directory.",
                Schema(["branchId"], new()
                {
                    ["branchId"]   = Str("Branch ID"),
                    ["workUnitId"] = Str("Your work unit ID — strongly prefer including this; the server resolves the real branch from it and ignores branchId if both are given"),
                    ["path"]       = Str("Sub-directory to list (optional, omit for all files)")
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
