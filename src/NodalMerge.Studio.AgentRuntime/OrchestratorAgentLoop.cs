using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class OrchestratorAgentLoop(
    string agentId,
    string workUnitId,
    IAgentToolClient client,
    IArtifactLineageService artifactLineage,
    IProjectionManager projections,
    IOrchestrationDecisionLogService decisionLog,
    IFanOutService fanOut,
    IMergeReconciliationService mergeReconciliation,
    IAutomatedReviewGateService automatedReview,
    IMergeService merge,
    IWorkUnitService workUnits,
    IFindingService findings,
    AgentProfile? profile = null,
    string? sessionId = null,
    int stallDetectionCycles = 4,
    Action<string?>? onActivity = null,
    IConversationLogService? conversationLog = null,
    IAgentControlService? agentControl = null,
    IExecutionEventStream? events = null,
    // Observability-only — see ConversationCompactor. Optional/nullable so call sites and tests
    // that don't wire a logger keep compiling unchanged.
    ILogger? logger = null)
{
    internal static readonly string DefaultSystemPrompt = AgentLoopPrompts.Orchestrator;

    private readonly int _maxIterations = profile?.MaxIterations ?? 25;
    private readonly int _stallDetectionCycles = stallDetectionCycles;
    private readonly string _systemPrompt = !string.IsNullOrEmpty(profile?.SystemPrompt)
        ? profile.SystemPrompt
        : DefaultSystemPrompt;
    private readonly IReadOnlyList<LlmToolDef> _tools = FilterTools(profile?.AllowedTools);
    private readonly IReadOnlyList<string>? _allowedTools = profile?.AllowedTools is { Count: > 0 }
        ? profile.AllowedTools
        : null;

    // Records each transient-provider-error retry attempt as it happens, not just the terminal
    // dead-letter reason if every retry is exhausted. No-op when there's no sessionId to attribute
    // it to (matches the gating other session-scoped events already use in this codebase).
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
            new("user", [new NmText($"Begin orchestrating work unit {workUnitId}. Your agent ID is {agentId}.")])
        };

        var completedNaturally = false;
        var lastProjection = new AgentWorkspaceProjectionPayload(agentId, workUnitId, new ArtifactChain([]), []);
        var stallStreak = 0;
        // A routing tool call that actually advanced the work unit (enqueue planner/worker, apply
        // merge) is real progress even when its effect lands on the artifact chain a cycle later
        // (or never directly at all — enqueueing only flips WorkUnit.Status, which ProjectionDelta
        // doesn't track). Without this, the orchestrator's own recommended first-time flow — a
        // read-only workunit_get, then enqueue the planner, then stop — trips the stall detector
        // on the very next fetch, since neither of those two tool calls touches the artifact chain.
        var madeRoutingDecisionLastCycle = false;
        int? lastInputTokens = null;
        for (var i = 0; i < _maxIterations && !ct.IsCancellationRequested; i++)
        {
            var currentProjection = await FetchAgentWorkspaceProjectionAsync(ct).ConfigureAwait(false);
            var delta = ProjectionDelta.Compute(workUnitId, lastProjection, currentProjection);
            stallStreak = delta.AnyChange || madeRoutingDecisionLastCycle ? 0 : stallStreak + 1;
            madeRoutingDecisionLastCycle = false;
            lastProjection = currentProjection;

            if (stallStreak >= _stallDetectionCycles)
            {
                onActivity?.Invoke(null);
                return AgentLoopCompletion.Stalled;
            }

            // Inherited constraints (global, promoted via Knowledge Promotion, plus this work
            // unit's own ancestor chain) rarely change mid-run — fold them into the kickoff message
            // once rather than repeating them every cycle alongside the delta.
            if (i == 0 && currentProjection.InheritedConstraints.Count > 0)
                AppendConstraintsToOutgoingMessage(messages, currentProjection.InheritedConstraints);

            if (i == 0)
            {
                var promptGuidance = await findings.ListPromotedPromptGuidanceAsync(PipelineStage.Orchestrate, ct).ConfigureAwait(false);
                if (promptGuidance.Count > 0)
                    AppendPromptGuidanceToOutgoingMessage(messages, promptGuidance);
            }

            AppendDeltaToOutgoingMessage(messages, delta);

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
                var text = response.Content.OfType<NmText>().Select(t => t.Text).FirstOrDefault();
                var chain = await artifactLineage.GetChainAsync(workUnitId, ct).ConfigureAwait(false);
                var awaitingReview = chain.Any(a => a.Type == ArtifactType.MergeProposal && a.Status == ArtifactStatus.Active);
                var action = awaitingReview ? OrchestrationAction.AwaitReview : OrchestrationAction.NoOp;
                await RecordDecisionAsync(action, [], text, ct).ConfigureAwait(false);
                await ConversationLogRecorder.RecordTurnAsync(
                    conversationLog, workUnitId, agentId, "Orchestrator", null, i, response, [], sessionId, ct,
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

                var input = toolUse.Name is McpToolNames.AgentSpawn or McpToolNames.SchedulerEnqueue
                    ? InjectSpawnCredentials(toolUse.Input)
                    : toolUse.Input;

                onActivity?.Invoke(ActivityLabeler.Describe(toolUse.Name, toolUse.Input));
                var result = await client
                    .DispatchAsync(toolUse.Name, input, _allowedTools, ct, sessionId)
                    .ConfigureAwait(false);

                toolResults.Add(new NmToolResult(toolUse.Id, result));

                if (await RecordToolDecisionAsync(toolUse.Name, toolUse.Input, result, ct).ConfigureAwait(false))
                    madeRoutingDecisionLastCycle = true;
            }

            await ConversationLogRecorder.RecordTurnAsync(
                conversationLog, workUnitId, agentId, "Orchestrator", null, i, response, toolResults, sessionId, ct,
                client.Provider, client.Model).ConfigureAwait(false);

            if (toolResults.Count == 0)
                break;

            messages.Add(new NmMessage("user", toolResults));
        }

        onActivity?.Invoke(null);

        if (ct.IsCancellationRequested)
            return AgentLoopCompletion.Cancelled;

        if (!completedNaturally)
            return AgentLoopCompletion.MaxIterationsExceeded;

        await fanOut.TryFanOutFromPlanAsync(workUnitId, sessionId, ct).ConfigureAwait(false);

        // Rescue sweep — a child that is itself an orchestrator unit (reconciliation work units
        // are the canonical case) can end up with a recorded Plan whose fan-out never fired
        // (historically: the scheduler routed the post-planner handoff at the wrong orchestrator).
        // Such a child sits Executing forever with no children and no queue item — and reinvoking
        // THIS orchestrator was the human's only recovery lever, so make that lever actually reach
        // it. TryFanOutFromPlanAsync is idempotent and returns immediately for children with no
        // Plan artifact of their own (every ordinary leaf slice), so this is cheap.
        try
        {
            var childUnits = await workUnits.GetChildrenAsync(workUnitId, ct).ConfigureAwait(false);
            foreach (var child in childUnits)
            {
                if (child.Status is WorkUnitStatus.Executing or WorkUnitStatus.Active or WorkUnitStatus.Waiting)
                    await fanOut.TryFanOutFromPlanAsync(child.WorkUnitId, sessionId, ct).ConfigureAwait(false);
            }
        }
        catch { /* best-effort — the goal's own convergence sweep below still runs */ }

        var reconciliation = await mergeReconciliation.TryReconcileAsync(workUnitId, sessionId, ct).ConfigureAwait(false);
        await automatedReview.TryEnqueueReviewerAsync(workUnitId, sessionId, ct).ConfigureAwait(false);

        // The LLM turn above routinely ends with a "plan exists, fan-out is automatic" NoOp — the
        // real convergence scan is these post-loop calls, and they used to run silently. Record
        // what the reconciliation sweep actually concluded, so a human reinvoking the orchestrator
        // sees "waiting on child X" / "reconciled proposal created — review it" in the decision
        // log instead of an apparent dead stop with no explanation.
        var reconAction = reconciliation.Outcome switch
        {
            MergeReconciliationOutcome.Reconciled or MergeReconciliationOutcome.AlreadyReconciled
                => OrchestrationAction.AwaitReview,
            MergeReconciliationOutcome.Conflict => OrchestrationAction.Escalate,
            _ => OrchestrationAction.NoOp,
        };
        var reconReason = reconciliation.Outcome switch
        {
            MergeReconciliationOutcome.Reconciled =>
                $"Reconciled child proposals into {reconciliation.ReconciledProposalId} — awaiting workspace review.",
            MergeReconciliationOutcome.AlreadyReconciled =>
                $"Reconciled proposal {reconciliation.ReconciledProposalId} already exists — awaiting review/apply.",
            _ => reconciliation.Detail,
        };
        if (reconReason is not null)
        {
            try { await RecordDecisionAsync(reconAction, [], reconReason, ct).ConfigureAwait(false); }
            catch { /* the decision log is informational — never worth failing convergence over */ }
        }

        // Only complete the orchestrator work unit once the reconciled proposal has actually been
        // approved/merged — Reconciled means a fresh proposal was just created in Draft status
        // (pending review), and AlreadyReconciled only means a non-Rejected proposal already
        // exists, which could still be sitting ReadyForReview/UnderReview. Without checking the
        // proposal's own status, every successful reconciliation marked the orchestrator Completed
        // immediately, before the automated/human reviewer ever ran — and since Completed is a
        // terminal status (WorkUnitTransitions.CanTransition has no transition out of it), a later
        // rejection's retry (Executing) or dead-letter escalation (DeadLettered) write would then
        // silently fail (UpdateStatusAsync throws InvalidOperationException, swallowed by the
        // caller), leaving the work unit stuck at Completed forever.
        if (reconciliation.Outcome is MergeReconciliationOutcome.Reconciled or MergeReconciliationOutcome.AlreadyReconciled
            && reconciliation.ReconciledProposalId is { } reconciledProposalId)
        {
            var reconciledProposal = await merge.GetAsync(reconciledProposalId, ct).ConfigureAwait(false);
            if (reconciledProposal?.Status is MergeProposalStatus.Approved or MergeProposalStatus.Merged)
            {
                var orchestrator = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
                if (orchestrator?.Status != WorkUnitStatus.Completed)
                {
                    await workUnits.UpdateStatusAsync(workUnitId, WorkUnitStatus.Completed, cancellationToken: ct)
                        .ConfigureAwait(false);
                }
            }
        }

        return AgentLoopCompletion.Succeeded;
    }

    // Only tool calls that actually change execution routing become OrchestrationEvents —
    // investigative calls (workunit.get, projection.get, task.create) are not routing decisions.
    // Returns whether a decision was recorded, so the caller can count it as loop progress even
    // when (as with enqueueing) it doesn't itself touch the artifact chain.
    private async Task<bool> RecordToolDecisionAsync(string toolName, JsonElement input, string resultJson, CancellationToken ct)
    {
        OrchestrationAction? action;
        if (toolName == McpToolNames.SchedulerEnqueue)
        {
            var profileId = input.TryGetProperty("profileId", out var p) ? p.GetString() : "worker";
            action = string.Equals(profileId, "planner", StringComparison.OrdinalIgnoreCase)
                ? OrchestrationAction.SpawnPlanner
                : OrchestrationAction.Enqueue;
        }
        else
        {
            action = toolName switch
            {
                McpToolNames.AgentSpawn => OrchestrationAction.SpawnWorker,
                McpToolNames.MergeApply => OrchestrationAction.ApplyMerge,
                _                       => null,
            };
        }

        if (action is null)
            return false;

        var spawnedId = ExtractSpawnedId(action.Value, resultJson);
        await RecordDecisionAsync(action.Value, spawnedId is null ? [] : [spawnedId], toolName, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<AgentWorkspaceProjectionPayload> FetchAgentWorkspaceProjectionAsync(CancellationToken ct)
    {
        var result = await projections.GetAsync(
            new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: workUnitId, AgentId: agentId),
            ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(result.DataJson, JsonSerializerOptions.Web)!;
    }

    // Appended to the message about to be sent (the kickoff message on cycle 0, or the
    // tool-results message from the previous cycle) rather than a new standalone message, so the
    // conversation keeps strict user/assistant alternation. When the outgoing message is a
    // single NmText (the kickoff message on cycle 0), the delta is folded into that same NmText
    // rather than added as a second block — LlmClient serializes single-text messages as a plain
    // string (Anthropic shorthand), and turning that into a multi-block array would break any
    // code (fakes included) that expects to read the kickoff message's content as one string.
    private static void AppendDeltaToOutgoingMessage(List<NmMessage> messages, ProjectionDelta delta)
    {
        var deltaText = $"[Projection delta — what changed since last cycle]\n{JsonSerializer.Serialize(delta, JsonOpts)}";
        var last = messages[^1];
        IReadOnlyList<NmContent> newContent = last.Content is [NmText only]
            ? [new NmText($"{only.Text}\n\n{deltaText}")]
            : [.. last.Content, new NmText(deltaText)];
        messages[^1] = last with { Content = newContent };
    }

    // Promoted Knowledge Findings (and any work-unit-lineage Constraint artifacts) reach the model
    // here — this was previously computed by the projection but never read by any agent loop.
    private static void AppendConstraintsToOutgoingMessage(List<NmMessage> messages, IReadOnlyList<ArtifactRef> constraints)
    {
        var lines = constraints.Select(c => $"- {c.Title ?? c.ArtifactId}: {c.Body ?? ""}");
        var text = "[Known constraints — durable guidance from prior runs; apply unless this work unit's goal explicitly says otherwise]\n"
            + string.Join("\n", lines);
        var last = messages[^1];
        IReadOnlyList<NmContent> newContent = last.Content is [NmText only]
            ? [new NmText($"{only.Text}\n\n{text}")]
            : [.. last.Content, new NmText(text)];
        messages[^1] = last with { Content = newContent };
    }

    // Promoted PromptImprovement findings targeting this stage — scoped guidance, unlike the
    // universal constraints above. Same single-NmText-folding pattern as AppendConstraintsToOutgoingMessage.
    private static void AppendPromptGuidanceToOutgoingMessage(List<NmMessage> messages, IReadOnlyList<Finding> promptGuidance)
    {
        var lines = promptGuidance.Select(f => $"- {f.Title}: {f.Summary}");
        var text = "[Process guidance — promoted prompt improvements for this stage]\n"
            + string.Join("\n", lines);
        var last = messages[^1];
        IReadOnlyList<NmContent> newContent = last.Content is [NmText only]
            ? [new NmText($"{only.Text}\n\n{text}")]
            : [.. last.Content, new NmText(text)];
        messages[^1] = last with { Content = newContent };
    }

    private async Task RecordDecisionAsync(
        OrchestrationAction action, IReadOnlyList<string> spawnedIds, string? reason, CancellationToken ct)
    {
        // CurrentStage is the orchestrator's own work unit's stage (authoritative, set by the
        // scheduler/merge pipeline) — not derived from artifact presence. For a root orchestrator
        // work unit that never goes through the scheduler itself, this is null, so the decision
        // log correctly reads "Orchestrate" rather than guessing at a child's progress.
        var unit = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        var stage = unit?.CurrentStage ?? PipelineStage.Orchestrate;

        var projection = await projections.GetAsync(
            new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: workUnitId, AgentId: agentId),
            ct).ConfigureAwait(false);

        await decisionLog.RecordAsync(
            workUnitId, agentId, stage, projection.DataJson, action, spawnedIds, reason, sessionId, ct).ConfigureAwait(false);
    }

    private static string? ExtractSpawnedId(OrchestrationAction action, string resultJson)
    {
        var key = action switch
        {
            OrchestrationAction.Enqueue      => "workUnitId",
            OrchestrationAction.SpawnPlanner => "workUnitId",
            OrchestrationAction.SpawnWorker  => "agentId",
            OrchestrationAction.ApplyMerge   => "proposalId",
            _                                => null,
        };
        if (key is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private JsonElement InjectSpawnCredentials(JsonElement input)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input) ?? [];
        // Default profileId to "worker" if the model didn't specify one.
        if (!dict.ContainsKey("profileId"))
            dict["profileId"] = JsonSerializer.SerializeToElement("worker");
        var profileId = dict["profileId"].ValueKind == JsonValueKind.String ? dict["profileId"].GetString() : null;

        // Always overwrite credentials — the model must not hallucinate them. Per-stage overrides
        // (configured on the run's Agent Topology) take precedence over this loop's own
        // credentials, so e.g. Planning can use a different model than Orchestration/Execution.
        var stageCreds = agentControl?.GetCredentialsForStage(workUnitId, StageForProfileId(profileId));
        dict["model"]    = JsonSerializer.SerializeToElement(stageCreds?.Model ?? client.Model);
        dict["baseUrl"]  = JsonSerializer.SerializeToElement(stageCreds?.BaseUrl ?? client.BaseUrl);
        dict["apiKey"]   = JsonSerializer.SerializeToElement(stageCreds?.ApiKey ?? client.ApiKey);
        dict["provider"] = JsonSerializer.SerializeToElement(stageCreds?.Provider ?? client.Provider);
        return JsonSerializer.SerializeToElement(dict);
    }

    // The only enqueue/spawn calls this loop ever issues are the Planner (via
    // nm_v1_scheduler_enqueue, profileId="planner") and, on the legacy direct-spawn path, a Worker
    // (profileId defaults to "worker" above) — same string convention already used elsewhere in
    // this codebase (e.g. AutomatedReviewGateService's literal "worker"/"reviewer" checks).
    private static PipelineStage StageForProfileId(string? profileId) => profileId switch
    {
        "planner"  => PipelineStage.Plan,
        "reviewer" => PipelineStage.Review,
        _          => PipelineStage.Execute,
    };

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

            new(McpToolNames.AgentSpawn, "Spawn a worker agent directly (legacy). Prefer nm_v1_scheduler_enqueue for queue-driven execution.",
                Schema(["agentType", "workUnitId", "taskId"], new()
                {
                    ["agentType"]  = Str("Agent type: worker"),
                    ["workUnitId"] = Str("Work unit ID (use your own workUnitId)"),
                    ["taskId"]     = Str("Task ID to assign to the worker"),
                    ["profileId"]  = Str("Pipeline profile ID to load for the worker (optional, e.g. 'worker')"),
                })),

            new(McpToolNames.SchedulerEnqueue, "Enqueue a worker for queue-driven execution. The scheduler picks it up respecting concurrency limits. LLM credentials are injected automatically.",
                Schema(["workUnitId", "profileId"], new()
                {
                    ["workUnitId"] = Str("Work unit ID (use your own workUnitId)"),
                    ["profileId"]  = Str("Pipeline profile ID for the worker (e.g. 'worker')"),
                    ["taskId"]     = Str("Task ID to assign to the worker (optional)"),
                })),

            new(McpToolNames.IntentRecord, "Declare which file/region a task you are about to enqueue intends to change, so the scheduler can warn about overlaps with other work units before any worker writes anything. Optional — call once per task right before enqueuing its worker.",
                Schema(["workUnitId", "targetPath"], new()
                {
                    ["workUnitId"]       = Str("Work unit ID (use your own workUnitId)"),
                    ["targetPath"]       = Str("File path or logical target this task will touch"),
                    ["intentType"]       = Str("modify | create | delete | rename (optional, default modify)"),
                    ["regionDescriptor"] = Str("Narrower region within the file, e.g. 'method:CalculateTax' (optional — omit for whole-file)"),
                    ["baseSnapshotHash"] = Str("Snapshot hash this intent is based on, for optimistic strategies (optional)"),
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

            new(McpToolNames.WorkspaceStatus, "Get a concise workspace status view with changed files and proposal summaries.",
                Schema([], new()
                {
                    ["branchId"] = Str("Branch ID filter (optional)"),
                    ["workUnitId"] = Str("Work unit ID to resolve the authoritative branch and current proposal chain (optional)"),
                    ["limit"] = Str("Maximum changed-file entries to return (optional, default 50)"),
                    ["offset"] = Str("Changed-file page offset (optional, default 0)"),
                })),

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

            new(McpToolNames.ArtifactRecord, "Record a durable knowledge note (Research, Decision, or Constraint) so descendant work units inherit it automatically.",
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
