using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Merge;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Host;

public static class StudioRestEndpoints
{
    // ── Request bodies ─────────────────────────────────────────────────────

    private sealed record CreateWorkUnitBody(
        string Goal,
        string Owner,
        string? BranchId = null,
        string? SuccessCriteria = null,
        string? RepositoryPath = null,
        string? ParentWorkUnitId = null,
        IReadOnlyList<string>? DependsOn = null,
        IReadOnlyList<string>? FileScope = null);

    private sealed record SpawnAgentBody(
        string AgentType,
        string WorkUnitId,
        string? TaskId = null,
        string? Model = null,
        string? BaseUrl = null,
        string? ApiKey = null,
        string? Provider = null,
        string? ProfileId = null,
        string? AutoReviewProfileId = null);

    private sealed record ProposeMergeBody(
        string SourceBranch,
        string TargetBranch,
        string Summary,
        string? Goal = null,
        string? ChangeDescription = null);

    private sealed record ReviewBody(string Decision);

    private sealed record BranchProposalBody(
        string Goal,
        string ProfileId,
        string? SessionId = null);

    private sealed record CreateBranchBody(
        string Name,
        string? FromBranchId = null);

    private sealed record CreateAgentProfileBody(
        string AgentProfileId,
        string Name,
        PipelineStage Stage,
        string SystemPrompt,
        IReadOnlyList<string> AllowedTools,
        int MaxIterations,
        IReadOnlyList<string>? FileScopePatterns = null);

    private sealed record UpdateAgentProfileBody(
        string Name,
        PipelineStage Stage,
        string SystemPrompt,
        IReadOnlyList<string> AllowedTools,
        int MaxIterations,
        IReadOnlyList<string>? FileScopePatterns = null);

    private sealed record MarkKnownGoodBody(
        string BranchId,
        string NodeId,
        string Description,
        string? CreatedBy = null);

    private sealed record CheckoutKnownGoodBody(string StateId);

    private sealed record EnqueueBody(
        string WorkUnitId,
        string ProfileId,
        string? TaskId = null,
        string? Model = null,
        string? BaseUrl = null,
        string? ApiKey = null,
        string? Provider = null,
        string? SessionId = null);

    private sealed record CreateSessionBody(
        string RootWorkUnitId,
        IReadOnlyList<string> ProfileIds,
        string? ModelConfigJson = null);

    private sealed record BranchSessionBody(
        string? ParentEventId = null,
        string? Goal = null);

    private sealed record UpdateOptionsBody(
        bool UseLlmProfileSelection,
        bool BlockOverlappingFileScope = false,
        int MaxConcurrentWorkers = 3,
        int SchedulerPollIntervalMs = 2_000);

    private sealed record BuildRequestBody(
        string? BuildCommand = null,
        int TimeoutSeconds = 300);

    private sealed record TestRequestBody(
        string? TestCommand = null,
        int TimeoutSeconds = 300);

    private sealed record RunRequestBody(
        string? RunCommand = null,
        int TimeoutSeconds = 120);

    private sealed record RollbackRequestBody(string KnownGoodStateId);

    private sealed record CreateGoalBody(
        string Goal,
        string? WorkUnitId = null);

    private sealed record RecordDecisionBody(
        string ProposalId,
        string Outcome,
        string? Rationale = null,
        string? ReviewerAgentId = null,
        string? ReviewerModel = null,
        string? ReviewerProvider = null,
        double? Confidence = null);

    private sealed record CreateTrajectoryBody(
        string WorkUnitId,
        string Phase,
        string? AgentId = null,
        string? Model = null,
        string? Provider = null);

    private sealed record CreateHypothesisBody(
        string Goal,
        string ForkType,
        string? ParentWorkUnitId = null,
        string? BranchedFromProposalId = null,
        string? Rationale = null);

    private sealed record RecordReasoningBody(
        string WorkUnitId,
        string AgentId,
        string InputStage,
        string Action,
        string? Reasoning = null,
        string? ProjectionSnapshotJson = null,
        string? AgentModel = null,
        string? AgentProvider = null);

    // ── Registration ───────────────────────────────────────────────────────

    public static WebApplication MapStudioRestEndpoints(this WebApplication app)
    {
        MapWorkspaceEndpoints(app);
        MapWorkUnitEndpoints(app);
        MapTaskEndpoints(app);
        MapAgentEndpoints(app);
        MapMergeEndpoints(app);
        MapBranchEndpoints(app);
        MapStateEndpoints(app);
        MapNodeStoreEndpoints(app);
        MapAgentProfileEndpoints(app);
        MapSchedulerEndpoints(app);
        MapSessionEndpoints(app);
        MapEventStreamEndpoints(app);
        MapSessionStateEndpoints(app);
        MapArtifactEndpoints(app);
        MapDeadLetterEndpoints(app);
        MapOptionsEndpoints(app);
        MapPolicyEndpoints(app);
        MapProjectionEndpoints(app);
        MapSnapshotEndpoints(app);
        MapReplayEndpoints(app);
        MapGoalEndpoints(app);
        MapDecisionEndpoints(app);
        MapEvidenceEndpoints(app);
        MapTrajectoryEndpoints(app);
        MapHypothesisEndpoints(app);
        MapReasoningEndpoints(app);
        MapModelEndpoints(app);
        return app;
    }

    // ── /studio/policies — Slice 14a, visibility only (no per-rule toggle yet) ─────────────

    private static void MapPolicyEndpoints(WebApplication app)
    {
        app.MapGet("/studio/policies", async (
            IPolicyGateService policyGate,
            CancellationToken ct) =>
        {
            var ruleIds = await policyGate.ListRuleIdsAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { ruleIds });
        });
    }

    // ── /studio/options — Slice 12d settings toggle ────────────────────────

    private static void MapOptionsEndpoints(WebApplication app)
    {
        app.MapGet("/studio/options", (WorkspaceOptions options) =>
            Results.Ok(new
            {
                useLlmProfileSelection = options.UseLlmProfileSelection,
                blockOverlappingFileScope = options.BlockOverlappingFileScope,
                maxConcurrentWorkers = options.MaxConcurrentWorkers,
                schedulerPollIntervalMs = options.SchedulerPollIntervalMs,
                requireBuildBeforeProposal = options.RequireBuildBeforeProposal,
                requireTestBeforeProposal = options.RequireTestBeforeProposal,
                buildCommand = options.BuildCommand,
                testCommand = options.TestCommand,
                executionTimeoutSeconds = options.ExecutionTimeoutSeconds,
                postMergeExecutionMode = options.PostMergeExecutionMode,
            }));

        app.MapPost("/studio/options", async (
            UpdateOptionsBody body,
            WorkspaceOptions options,
            RuntimeSettingsService runtimeSettings,
            CancellationToken ct) =>
        {
            // Slice 14e — the scheduler poll loop reads these straight off this same
            // WorkspaceOptions singleton every iteration (InMemoryAgentRuntimeService), so a
            // bad value here doesn't just get rejected, it wedges the scheduler (0 workers =
            // nothing ever gets picked up; a 0/negative delay = a busy-loop).
            if (body.MaxConcurrentWorkers < 1)
                return Results.BadRequest(new { error = "maxConcurrentWorkers must be at least 1." });
            if (body.SchedulerPollIntervalMs < 100)
                return Results.BadRequest(new { error = "schedulerPollIntervalMs must be at least 100." });

            options.UseLlmProfileSelection = body.UseLlmProfileSelection;
            options.BlockOverlappingFileScope = body.BlockOverlappingFileScope;
            options.MaxConcurrentWorkers = body.MaxConcurrentWorkers;
            options.SchedulerPollIntervalMs = body.SchedulerPollIntervalMs;
            await runtimeSettings.PersistAsync(ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                useLlmProfileSelection = options.UseLlmProfileSelection,
                blockOverlappingFileScope = options.BlockOverlappingFileScope,
                maxConcurrentWorkers = options.MaxConcurrentWorkers,
                schedulerPollIntervalMs = options.SchedulerPollIntervalMs,
                requireBuildBeforeProposal = options.RequireBuildBeforeProposal,
                requireTestBeforeProposal = options.RequireTestBeforeProposal,
                buildCommand = options.BuildCommand,
                testCommand = options.TestCommand,
                executionTimeoutSeconds = options.ExecutionTimeoutSeconds,
                postMergeExecutionMode = options.PostMergeExecutionMode,
            });
        });
    }

    // ── /studio/workspace-summary ──────────────────────────────────────────

    private static void MapWorkspaceEndpoints(WebApplication app)
    {
        app.MapGet("/studio/workspace-summary", async (
            [FromQuery] string? branchId,
            IWorkspaceService workspace,
            CancellationToken ct) =>
        {
            var summary = await workspace.GetSummaryAsync(branchId, ct).ConfigureAwait(false);
            return Results.Ok(summary);
        });

        // ── Slice 16d — workspace execution REST endpoints (MCP parity) ────

        app.MapPost("/studio/workspace/{branchId}/build", async (
            string branchId,
            BuildRequestBody body,
            IWorkspaceExecutionCommandService cmd,
            CancellationToken ct) =>
        {
            var result = await cmd.BuildAsync(branchId, body.BuildCommand, body.TimeoutSeconds, ct).ConfigureAwait(false);
            return Results.Ok(result);
        });

        app.MapPost("/studio/workspace/{branchId}/test", async (
            string branchId,
            TestRequestBody body,
            IWorkspaceExecutionCommandService cmd,
            CancellationToken ct) =>
        {
            var result = await cmd.TestAsync(branchId, body.TestCommand, body.TimeoutSeconds, ct).ConfigureAwait(false);
            return Results.Ok(result);
        });

        app.MapPost("/studio/workspace/{branchId}/exec", async (
            string branchId,
            WorkspaceExecutionRequest body,
            IWorkspaceExecutionCommandService cmd,
            CancellationToken ct) =>
        {
            var result = await cmd.ExecAsync(branchId, body, ct).ConfigureAwait(false);
            return Results.Ok(result);
        });

        app.MapPost("/studio/workspace/{branchId}/run", async (
            string branchId,
            RunRequestBody body,
            IWorkspaceExecutionCommandService cmd,
            CancellationToken ct) =>
        {
            var result = await cmd.RunAsync(branchId, body.RunCommand, body.TimeoutSeconds, environmentVariables: null, ct: ct).ConfigureAwait(false);
            return Results.Ok(result);
        });

        app.MapGet("/studio/workspace/{branchId}/exec/latest", async (
            string branchId,
            IWorkspaceExecutionCommandService cmd,
            CancellationToken ct) =>
        {
            var result = await cmd.GetLatestAsync(branchId, ct).ConfigureAwait(false);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        });

        app.MapGet("/studio/workspace/{branchId}/path", async (
            string branchId,
            IWorkspaceExecutionCommandService cmd,
            CancellationToken ct) =>
        {
            var path = await cmd.GetBranchPathAsync(branchId, ct).ConfigureAwait(false);
            return path is not null
                ? Results.Ok(new { branchId, workingDirectory = path, exists = true })
                : Results.Ok(new { branchId, workingDirectory = (string?)null, exists = false });
        });

        // Slice 16m — output download endpoint. Returns cached (truncated) stdout/stderr from a
        // persisted execution result node. resultId is the ExecutionResultV1 entity ID (form:
        // "exec/{branchId}/20260618120000").
        app.MapGet("/studio/workspace/{branchId}/exec/{resultId}/output", async (
            string branchId,
            string resultId,
            IWorkspaceExecutionCommandService cmd,
            CancellationToken ct) =>
        {
            var output = await cmd.GetOutputAsync(branchId, resultId, ct).ConfigureAwait(false);
            return output is not null ? Results.Ok(output) : Results.NotFound();
        });
    }

    // ── /studio/workunits ─────────────────────────────────────────────────

    private static object ToWorkUnitResponse(WorkUnit wu, int proposalCount) => new
    {
        workUnitId = wu.WorkUnitId,
        goal = wu.Goal,
        branchId = wu.BranchId,
        status = wu.Status,
        createdAt = wu.CreatedAt,
        updatedAt = wu.UpdatedAt,
        owner = wu.Owner,
        assignedAgent = wu.AssignedAgent,
        successCriteria = wu.SuccessCriteria,
        metadata = wu.Metadata,
        parentWorkUnitId = wu.ParentWorkUnitId,
        dependsOn = wu.DependsOn,
        fileScope = wu.FileScope,
        currentStage = wu.CurrentStage,
        executionInfo = wu.ExecutionInfo,
        fanOutInfo = wu.FanOutInfo,
        branchedFromProposalId = wu.BranchedFromProposalId,
        forkType = wu.ForkType?.ToString(),
        proposalCount,
    };

    private static void MapWorkUnitEndpoints(WebApplication app)
    {
        app.MapGet("/studio/workunits", async (
            [FromQuery] string? branchId,
            IWorkUnitService workUnits,
            IMergeService merge,
            CancellationToken ct) =>
        {
            var list = await workUnits.ListAsync(branchId, ct).ConfigureAwait(false);
            var allProposals = await merge.ListAsync(cancellationToken: ct).ConfigureAwait(false);
            var counts = allProposals
                .Where(p => p.WorkUnitId is not null)
                .GroupBy(p => p.WorkUnitId!)
                .ToDictionary(g => g.Key, g => g.Count());
            return Results.Ok(list.Select(wu => ToWorkUnitResponse(wu, counts.GetValueOrDefault(wu.WorkUnitId))));
        });

        app.MapGet("/studio/workunits/{workUnitId}", async (
            string workUnitId,
            IWorkUnitService workUnits,
            IMergeService merge,
            CancellationToken ct) =>
        {
            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." });
            var proposals = await merge.ListAsync(cancellationToken: ct).ConfigureAwait(false);
            var proposalCount = proposals.Count(p => p.WorkUnitId == workUnitId);
            return Results.Ok(ToWorkUnitResponse(wu, proposalCount));
        });

        app.MapGet("/studio/workunits/{workUnitId}/children", async (
            string workUnitId,
            IWorkUnitService workUnits,
            CancellationToken ct) =>
        {
            var parent = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (parent is null)
                return Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." });
            var children = await workUnits.GetChildrenAsync(workUnitId, ct).ConfigureAwait(false);
            return Results.Ok(children);
        });

        app.MapGet("/studio/workunits/{workUnitId}/artifacts", async (
            string workUnitId,
            IWorkUnitService workUnits,
            IArtifactLineageService artifacts,
            CancellationToken ct) =>
        {
            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." });
            var chain = await artifacts.GetChainAsync(workUnitId, ct).ConfigureAwait(false);
            return Results.Ok(chain);
        });

        app.MapGet("/studio/workunits/{workUnitId}/orchestration-events", async (
            string workUnitId,
            IWorkUnitService workUnits,
            IOrchestrationDecisionLogService decisionLog,
            CancellationToken ct) =>
        {
            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." });
            var events = await decisionLog.GetEventsAsync(workUnitId, ct).ConfigureAwait(false);
            return Results.Ok(events);
        });

        app.MapGet("/studio/workunits/{workUnitId}/intents", async (
            string workUnitId,
            IWorkUnitService workUnits,
            IIntentGraphService intents,
            CancellationToken ct) =>
        {
            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." });
            var list = await intents.QueryIntentsAsync(workUnitId, ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        // Surfaces the merger's conflict report (11c) — only ever written when WorkUnitStatus
        // is Reviewing, since MergeReconciliationService.cs is the sole place that status is set.
        app.MapGet("/studio/workunits/{workUnitId}/conflict-report", async (
            string workUnitId,
            IWorkUnitService workUnits,
            IFileWorkspaceService fileWorkspace,
            CancellationToken ct) =>
        {
            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." });

            var content = await fileWorkspace
                .ReadAsync(wu.BranchId, MergeReconciliationService.ConflictReportFileName, ct)
                .ConfigureAwait(false);
            if (content is null)
                return Results.NotFound(new { error = "No conflict report exists for this work unit." });

            return Results.Ok(new { workUnitId, status = wu.Status.ToString(), content });
        });

        app.MapGet("/studio/workunits/{workUnitId}/proposal-dag", async (
            string workUnitId,
            IWorkUnitService workUnits,
            IArtifactLineageService artifacts,
            IMergeService merge,
            CancellationToken ct) =>
        {
            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." });

            var chain = await artifacts.GetChainAsync(workUnitId, ct).ConfigureAwait(false);
            var proposals = new List<object>();
            foreach (var proposalRef in chain.Where(a => a.Type == ArtifactType.MergeProposal))
            {
                var proposal = await merge.GetAsync(proposalRef.ArtifactId, ct).ConfigureAwait(false);
                proposals.Add(new
                {
                    proposalId = proposalRef.ArtifactId,
                    status = proposalRef.Status.ToString(),
                    baseState = $"base/{proposalRef.ArtifactId}",
                    producedState = proposal?.SourceBranch,
                    filesTouched = proposal?.FilesTouched ?? [],
                });
            }

            var children = await workUnits.GetChildrenAsync(workUnitId, ct).ConfigureAwait(false);
            var branches = children
                .Where(c => c.BranchedFromProposalId is not null)
                .Select(c => new
                {
                    workUnitId = c.WorkUnitId,
                    goal = c.Goal,
                    status = c.Status.ToString(),
                    branchedFromProposalId = c.BranchedFromProposalId,
                })
                .ToList();

            object? mergeProposal = null;
            foreach (var proposalRef in chain.Where(a => a.Type == ArtifactType.MergeProposal))
            {
                var proposal = await merge.GetAsync(proposalRef.ArtifactId, ct).ConfigureAwait(false);
                if (proposal?.ReconciledFrom.Count > 0)
                {
                    mergeProposal = new
                    {
                        proposalId = proposal.ProposalId,
                        status = proposalRef.Status.ToString(),
                        reconciledFrom = proposal.ReconciledFrom,
                        filesTouched = proposal.FilesTouched,
                    };
                    break;
                }
            }

            return Results.Ok(new { workUnitId, proposals, branches, mergeProposal });
        });

        app.MapPost("/studio/workunits", async (
            CreateWorkUnitBody body,
            IWorkUnitCommandService workUnitCommands,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Goal))
                return Results.BadRequest(new { error = "goal is required." });
            if (string.IsNullOrWhiteSpace(body.Owner))
                return Results.BadRequest(new { error = "owner is required." });

            var wu = await workUnitCommands.CreateAsync(
                new WorkUnitCreateCommand(body.Goal, body.Owner, body.BranchId, body.SuccessCriteria,
                    body.RepositoryPath, body.ParentWorkUnitId, body.DependsOn, body.FileScope),
                ct).ConfigureAwait(false);
            return Results.Ok(wu);
        });
    }

        // ── /studio/tasks ─────────────────────────────────────────────────────────

    private sealed record CreateTaskBody(
        string WorkUnitId,
        string Title,
        string Description,
        int Priority = 0);

    private sealed record UpdateTaskBody(
        string? Title = null,
        string? Description = null,
        string? Status = null,
        int? Priority = null);

    private sealed record AssignTaskBody(string AgentId);

    private static void MapTaskEndpoints(WebApplication app)
    {
        app.MapGet("/studio/tasks", async (
            [FromQuery] string? workUnitId,
            ITaskCommandService taskCommands,
            CancellationToken ct) =>
        {
            var list = await taskCommands.ListAsync(workUnitId, ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        app.MapGet("/studio/tasks/{taskId}", async (
            string taskId,
            ITaskService tasks,
            CancellationToken ct) =>
        {
            var task = await tasks.GetAsync(taskId, ct).ConfigureAwait(false);
            return task is null
                ? Results.NotFound(new { error = $"Task '{taskId}' not found." })
                : Results.Ok(task);
        });

        app.MapPost("/studio/tasks", async (
            CreateTaskBody body,
            ITaskCommandService taskCommands,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.WorkUnitId))
                return Results.BadRequest(new { error = "workUnitId is required." });
            if (string.IsNullOrWhiteSpace(body.Title))
                return Results.BadRequest(new { error = "title is required." });
            if (string.IsNullOrWhiteSpace(body.Description))
                return Results.BadRequest(new { error = "description is required." });

            var task = await taskCommands.CreateAsync(
                new TaskCreateCommand(body.WorkUnitId, body.Title, body.Description, body.Priority),
                ct).ConfigureAwait(false);
            return Results.Ok(task);
        });

        app.MapPut("/studio/tasks/{taskId}", async (
            string taskId,
            UpdateTaskBody body,
            ITaskCommandService taskCommands,
            CancellationToken ct) =>
        {
            try
            {
                var updated = await taskCommands.UpdateAsync(
                    taskId, body.Title, body.Description, body.Status, body.Priority, ct).ConfigureAwait(false);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Task '{taskId}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/studio/tasks/{taskId}/assign", async (
            string taskId,
            AssignTaskBody body,
            ITaskCommandService taskCommands,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.AgentId))
                return Results.BadRequest(new { error = "agentId is required." });

            try
            {
                var assigned = await taskCommands.AssignAsync(taskId, body.AgentId, ct).ConfigureAwait(false);
                return Results.Ok(assigned);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Task '{taskId}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    // ── /studio/agents ─────────────────────────────────────────────────────

    private static void MapAgentEndpoints(WebApplication app)
    {
        app.MapGet("/studio/agents", async (
            [FromQuery] bool all,
            IAgentControlService agents,
            CancellationToken ct) =>
        {
            var list = all
                ? await agents.ListAllAsync(ct).ConfigureAwait(false)
                : await agents.ListActiveAsync(ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        app.MapPost("/studio/agents/spawn", async (
            SpawnAgentBody body,
            IAgentControlService agents,
            IWorkUnitService workUnits,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.AgentType))
                return Results.BadRequest(new { error = "agentType is required." });
            if (string.IsNullOrWhiteSpace(body.WorkUnitId))
                return Results.BadRequest(new { error = "workUnitId is required." });

            var wu = await workUnits.GetAsync(body.WorkUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{body.WorkUnitId}' not found." });

            var agentId = await agents.SpawnAsync(
                body.AgentType, body.WorkUnitId, body.TaskId, body.Model, body.BaseUrl, body.ApiKey,
                body.Provider, body.ProfileId, body.AutoReviewProfileId, ct).ConfigureAwait(false);
            return Results.Ok(new { agentId, agentType = body.AgentType, workUnitId = body.WorkUnitId, branchId = wu.BranchId });
        });

        // Slice 15e — single-agent status read (list/spawn/pause/resume/stop already existed; this
        // fills the gap matching MCP's nm_v1_agent_status and the agent-loop dispatcher equivalent).
        app.MapGet("/studio/agents/{agentId}/status", async (
            string agentId,
            IAgentControlService agents,
            CancellationToken ct) =>
        {
            var status = await agents.GetStatusAsync(agentId, ct).ConfigureAwait(false);
            return Results.Ok(new { agentId, status });
        });

        app.MapPost("/studio/agents/{agentId}/pause", async (
            string agentId,
            IAgentControlService agents,
            CancellationToken ct) =>
        {
            try
            {
                await agents.PauseAsync(agentId, ct).ConfigureAwait(false);
                return Results.Ok(new { agentId, status = "paused" });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Agent '{agentId}' not found." });
            }
        });

        app.MapPost("/studio/agents/{agentId}/resume", async (
            string agentId,
            IAgentControlService agents,
            CancellationToken ct) =>
        {
            try
            {
                await agents.ResumeAsync(agentId, ct).ConfigureAwait(false);
                return Results.Ok(new { agentId, status = "active" });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Agent '{agentId}' not found." });
            }
        });

        app.MapPost("/studio/agents/{agentId}/stop", async (
            string agentId,
            IAgentControlService agents,
            CancellationToken ct) =>
        {
            try
            {
                await agents.StopAsync(agentId, ct).ConfigureAwait(false);
                return Results.Ok(new { agentId, status = "stopped" });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Agent '{agentId}' not found." });
            }
        });
    }

    // ── /studio/merges ─────────────────────────────────────────────────────

    private static void MapMergeEndpoints(WebApplication app)
    {
        app.MapGet("/studio/merges", async (
            [FromQuery] string? sourceBranch,
            IMergeService merge,
            CancellationToken ct) =>
        {
            var list = await merge.ListAsync(sourceBranch, ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        app.MapGet("/studio/merges/{proposalId}", async (
            string proposalId,
            IMergeService merge,
            CancellationToken ct) =>
        {
            var proposal = await merge.GetAsync(proposalId, ct).ConfigureAwait(false);
            return proposal is null
                ? Results.NotFound(new { error = $"Proposal '{proposalId}' not found." })
                : Results.Ok(proposal);
        });

        // Resolves a reconciled proposal's `reconciledFrom` IDs to full status/goal/summary —
        // the Merge Review panel otherwise has nothing but bare IDs to show for Superseded constituents.
        app.MapGet("/studio/merges/{proposalId}/constituents", async (
            string proposalId,
            IMergeService merge,
            CancellationToken ct) =>
        {
            var proposal = await merge.GetAsync(proposalId, ct).ConfigureAwait(false);
            if (proposal is null)
                return Results.NotFound(new { error = $"Proposal '{proposalId}' not found." });

            var constituents = new List<object>();
            foreach (var id in proposal.ReconciledFrom)
            {
                var constituent = await merge.GetAsync(id, ct).ConfigureAwait(false);
                constituents.Add(constituent is null
                    ? new { proposalId = id, status = "Unknown", goal = (string?)null, summary = (string?)null, model = (string?)null, confidence = (double?)null, rationale = (string?)null }
                    : new
                    {
                        proposalId = constituent.ProposalId,
                        status = constituent.Status.ToString(),
                        goal = (string?)constituent.Goal,
                        summary = (string?)constituent.Summary,
                        model = (string?)constituent.Model,
                        provider = (string?)constituent.Provider,
                        confidence = constituent.Confidence,
                        rationale = (string?)constituent.ChangeDescription,
                        agentId = (string?)constituent.AgentId,
                        workspaceChanges = (string?)constituent.WorkspaceChanges,
                    });
            }

            return Results.Ok(constituents);
        });

        app.MapGet("/studio/merges/{proposalId}/file-changes", async (
            string proposalId,
            IProposalReviewService review,
            CancellationToken ct) =>
        {
            var changes = await review.GetFileChangesAsync(proposalId, ct).ConfigureAwait(false);
            return Results.Ok(new { proposalId, fileChanges = changes });
        });

        app.MapPost("/studio/merges", async (
            ProposeMergeBody body,
            HttpRequest request,
            IMergeCommandService mergeCommands,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.SourceBranch))
                return Results.BadRequest(new { error = "sourceBranch is required." });
            if (string.IsNullOrWhiteSpace(body.TargetBranch))
                return Results.BadRequest(new { error = "targetBranch is required." });
            if (string.IsNullOrWhiteSpace(body.Summary))
                return Results.BadRequest(new { error = "summary is required." });

            var commandId = request.Headers["X-Command-Id"].FirstOrDefault();

            var created = await mergeCommands.ProposeAsync(
                body.SourceBranch, body.TargetBranch, body.Summary,
                body.Goal, body.ChangeDescription,
                commandId: commandId, cancellationToken: ct).ConfigureAwait(false);

            return Results.Ok(created);
        });

        app.MapPost("/studio/merges/{proposalId}/validate", async (
            string proposalId,
            IMergeService merge,
            CancellationToken ct) =>
        {
            try
            {
                var result = await merge.ValidateAsync(proposalId, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Proposal '{proposalId}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/studio/merges/{proposalId}/review", async (
            string proposalId,
            ReviewBody body,
            IMergeService merge,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<MergeProposalStatus>(body.Decision, ignoreCase: true, out var status) ||
                status is not (MergeProposalStatus.Approved or MergeProposalStatus.Rejected))
            {
                return Results.BadRequest(new { error = "Decision must be 'Approved' or 'Rejected'." });
            }
            try
            {
                var result = await merge.ReviewAsync(proposalId, status, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Proposal '{proposalId}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/studio/merges/{proposalId}/apply", async (
            string proposalId,
            IMergeService merge,
            CancellationToken ct) =>
        {
            try
            {
                var result = await merge.ApplyAsync(proposalId, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Proposal '{proposalId}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/studio/merges/compare", async (
            [FromQuery] string? ids,
            IMergeService merge,
            CancellationToken ct) =>
        {
            var idList = (ids ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (idList.Length != 2)
                return Results.BadRequest(new { error = "ids must contain exactly two comma-separated proposal IDs." });

            var a = await merge.GetAsync(idList[0], ct).ConfigureAwait(false);
            if (a is null) return Results.NotFound(new { error = $"Proposal '{idList[0]}' not found." });
            var b = await merge.GetAsync(idList[1], ct).ConfigureAwait(false);
            if (b is null) return Results.NotFound(new { error = $"Proposal '{idList[1]}' not found." });

            if (a.WorkUnitId is null || a.WorkUnitId != b.WorkUnitId)
                return Results.BadRequest(new { error = "Proposals must share the same originating work unit (same base state) to compare." });

            var overlapping = a.FilesTouched.Intersect(b.FilesTouched, StringComparer.OrdinalIgnoreCase).ToList();

            return Results.Ok(new
            {
                proposalIdA = a.ProposalId,
                proposalIdB = b.ProposalId,
                overlappingFiles = overlapping,
                diffA = a.WorkspaceChanges,
                diffB = b.WorkspaceChanges,
            });
        });

        app.MapPost("/studio/merges/{proposalId}/branch", async (
            string proposalId,
            BranchProposalBody body,
            IMergeService merge,
            IOrchestratorService orchestrator,
            IWorkScheduler scheduler,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Goal))
                return Results.BadRequest(new { error = "goal is required." });
            if (string.IsNullOrWhiteSpace(body.ProfileId))
                return Results.BadRequest(new { error = "profileId is required." });

            var proposal = await merge.GetAsync(proposalId, ct).ConfigureAwait(false);
            if (proposal is null)
                return Results.NotFound(new { error = $"Proposal '{proposalId}' not found." });

            var newWorkUnit = await orchestrator.CreateWorkUnitAsync(
                body.Goal,
                owner: "user",
                parentWorkUnitId: proposal.WorkUnitId,
                seedFromBranchId: $"base/{proposalId}",
                branchedFromProposalId: proposalId,
                cancellationToken: ct).ConfigureAwait(false);

            await scheduler.EnqueueAsync(
                newWorkUnit.WorkUnitId, body.ProfileId, sessionId: body.SessionId, ct: ct).ConfigureAwait(false);

            return Results.Ok(new { workUnitId = newWorkUnit.WorkUnitId });
        });

        // Workspace replay (Slice 12a) — restores the workspace to a proposal's pre-change state
        // without starting a new agent run. There is no agent-loop equivalent of "checkout"; the
        // closest durable primitive is forking a fresh branch from the propose-time snapshot
        // (`base/{proposalId}`, written by InMemoryMergeService at propose time) via
        // IBranchService.CreateBranchAsync. The extension reads file content for display from the
        // already-cached file-changes (IProposalReviewService), not by re-reading this branch.
        app.MapPost("/studio/merges/{proposalId}/restore-workspace", async (
            string proposalId,
            IMergeService merge,
            IBranchService branches,
            CancellationToken ct) =>
        {
            var proposal = await merge.GetAsync(proposalId, ct).ConfigureAwait(false);
            if (proposal is null)
                return Results.NotFound(new { error = $"Proposal '{proposalId}' not found." });

            var branchId = await branches.CreateBranchAsync(
                $"restore/{proposalId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                fromBranchId: $"base/{proposalId}",
                ct).ConfigureAwait(false);

            return Results.Ok(new { branchId, proposalId });
        });
    }

    // ── /studio/branches ──────────────────────────────────────────────────

    private static void MapBranchEndpoints(WebApplication app)
    {
        app.MapGet("/studio/branches", async (
            IBranchService branches,
            CancellationToken ct) =>
        {
            var list = await branches.ListBranchesAsync(ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        app.MapPost("/studio/branches", async (
            CreateBranchBody body,
            IBranchService branches,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "name is required." });

            var branchId = await branches.CreateBranchAsync(body.Name, body.FromBranchId, ct)
                .ConfigureAwait(false);
            return Results.Ok(new { branchId, name = body.Name, fromBranchId = body.FromBranchId });
        });

        // Slice 15a — REST/MCP parity (nm_v1_branch_checkout / nm_v1_branch_status had no REST route).
        app.MapPost("/studio/branches/{branchId}/checkout", async (
            string branchId,
            IBranchService branches,
            CancellationToken ct) =>
        {
            await branches.CheckoutBranchAsync(branchId, ct).ConfigureAwait(false);
            return Results.Ok(new { branchId, status = "checked_out" });
        });

        app.MapGet("/studio/branches/{branchId}/status", async (
            string branchId,
            IBranchService branches,
            CancellationToken ct) =>
        {
            var status = await branches.GetStatusAsync(branchId, ct).ConfigureAwait(false);
            return Results.Ok(status);
        });
    }

    // ── /studio/nodes ─────────────────────────────────────────────────────

    private static void MapNodeStoreEndpoints(WebApplication app)
    {
        // GET /studio/nodes?kind=studio/work-unit/v1&entityId=<id>
        app.MapGet("/studio/nodes", async (
            [FromQuery] string kind,
            [FromQuery] string entityId,
            IStudioNodeStore nodeStore,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(entityId))
                return Results.BadRequest(new { error = "kind and entityId query parameters are required." });

            var json = await nodeStore.ReadNodeAsync(kind, entityId, ct).ConfigureAwait(false);
            return json is null
                ? Results.NotFound(new { error = $"Node '{kind}/{entityId}' not found." })
                : Results.Text(json, "application/json");
        });
    }

    // ── /studio/state ─────────────────────────────────────────────────────

    private static void MapStateEndpoints(WebApplication app)
    {
        app.MapPost("/studio/state/markKnownGood", async (
            MarkKnownGoodBody body,
            IKnownGoodStateService kgs,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.BranchId))
                return Results.BadRequest(new { error = "branchId is required." });
            if (string.IsNullOrWhiteSpace(body.Description))
                return Results.BadRequest(new { error = "description is required." });

            var state = new KnownGoodState(
                $"KGS-{Guid.NewGuid():N}",
                body.BranchId,
                body.Description,
                null,
                DateTimeOffset.UtcNow,
                body.CreatedBy ?? "user");
            var result = await kgs.MarkKnownGoodAsync(state, ct).ConfigureAwait(false);
            return Results.Ok(result);
        });

        app.MapGet("/studio/state/knownGood/{branchId}", async (
            string branchId,
            IKnownGoodStateService kgs,
            CancellationToken ct) =>
        {
            var list = await kgs.FindKnownGoodAsync(branchId, ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        app.MapPost("/studio/state/checkoutKnownGood", async (
            CheckoutKnownGoodBody body,
            IKnownGoodStateService kgs,
            CancellationToken ct) =>
        {
            var result = await kgs.CheckoutKnownGoodAsync(body.StateId, ct).ConfigureAwait(false);
            return result is null
                ? Results.NotFound(new { error = $"Known good state '{body.StateId}' not found." })
                : Results.Ok(result);
        });
    }

    // ── /studio/scheduler ─────────────────────────────────────────────────────

    private static void MapSchedulerEndpoints(WebApplication app)
    {
        app.MapGet("/studio/scheduler/pending", async (
            [FromQuery] int? includeIntentGraph,
            IWorkScheduler scheduler,
            IIntentGraphService intents,
            CancellationToken ct) =>
        {
            var items = await scheduler.ListPendingAsync(ct).ConfigureAwait(false);
            if (includeIntentGraph != 1)
                return Results.Ok(items);

            var enriched = new List<object>();
            foreach (var item in items)
            {
                var itemIntents = await intents.QueryIntentsAsync(item.WorkUnitId, ct).ConfigureAwait(false);
                enriched.Add(new { item, intents = itemIntents });
            }
            return Results.Ok(enriched);
        });

        app.MapPost("/studio/scheduler/enqueue", async (
            EnqueueBody body,
            ISchedulerCommandService scheduler,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.WorkUnitId))
                return Results.BadRequest(new { error = "workUnitId is required." });
            if (string.IsNullOrWhiteSpace(body.ProfileId))
                return Results.BadRequest(new { error = "profileId is required." });

            var item = await scheduler.EnqueueAsync(
                body.WorkUnitId, body.ProfileId, body.TaskId, body.Model, body.BaseUrl, body.ApiKey, body.Provider, body.SessionId, ct)
                .ConfigureAwait(false);
            return Results.Ok(new { workUnitId = item.WorkUnitId, profileId = item.ProfileId, taskId = item.TaskId, sessionId = item.SessionId, status = "enqueued" });
        });
    }

    // ── /studio/sessions ──────────────────────────────────────────────────────

    private static void MapSessionEndpoints(WebApplication app)
    {
        app.MapPost("/studio/sessions", async (
            CreateSessionBody body,
            IExecutionSessionService sessions,
            IWorkUnitService workUnits,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.RootWorkUnitId))
                return Results.BadRequest(new { error = "rootWorkUnitId is required." });
            if (body.ProfileIds is null || body.ProfileIds.Count == 0)
                return Results.BadRequest(new { error = "profileIds is required." });

            var wu = await workUnits.GetAsync(body.RootWorkUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{body.RootWorkUnitId}' not found." });

            var session = await sessions.CreateAsync(
                body.RootWorkUnitId,
                body.ModelConfigJson ?? "{}",
                body.ProfileIds,
                ct: ct).ConfigureAwait(false);
            return Results.Ok(session);
        });

        app.MapGet("/studio/sessions", async (
            IExecutionSessionService sessions,
            CancellationToken ct) =>
        {
            var list = await sessions.ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        app.MapGet("/studio/sessions/{sessionId}", async (
            string sessionId,
            IExecutionSessionService sessions,
            CancellationToken ct) =>
        {
            var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
            return session is null
                ? Results.NotFound(new { error = $"Session '{sessionId}' not found." })
                : Results.Ok(session);
        });

        app.MapPost("/studio/sessions/{sessionId}/pause", async (
            string sessionId,
            IExecutionSessionService sessions,
            CancellationToken ct) =>
        {
            try
            {
                await sessions.SetStatusAsync(sessionId, ExecutionSessionStatus.Paused, ct).ConfigureAwait(false);
                return Results.Ok(new { sessionId, status = "Paused" });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
            }
        });

        app.MapPost("/studio/sessions/{sessionId}/resume", async (
            string sessionId,
            IExecutionSessionService sessions,
            CancellationToken ct) =>
        {
            try
            {
                await sessions.SetStatusAsync(sessionId, ExecutionSessionStatus.Active, ct).ConfigureAwait(false);
                return Results.Ok(new { sessionId, status = "Active" });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
            }
        });

        app.MapPost("/studio/sessions/{sessionId}/abandon", async (
            string sessionId,
            IExecutionSessionService sessions,
            CancellationToken ct) =>
        {
            try
            {
                await sessions.SetStatusAsync(sessionId, ExecutionSessionStatus.Abandoned, ct).ConfigureAwait(false);
                return Results.Ok(new { sessionId, status = "Abandoned" });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
            }
        });

        // Slice 0a — the Studio Shell's session picker needs to scope the Artifact Explorer's
        // DAG view to one session at a time. A session has no direct WorkUnitId membership
        // list (only its RootWorkUnitId) — membership is the root plus its full descendant
        // tree, walked here server-side so the extension doesn't do N+1 recursive calls.
        app.MapGet("/studio/sessions/{sessionId}/workunits", async (
            string sessionId,
            IExecutionSessionService sessions,
            IWorkUnitService workUnits,
            IMergeService merge,
            CancellationToken ct) =>
        {
            var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
            if (session is null)
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });

            var root = await workUnits.GetAsync(session.RootWorkUnitId, ct).ConfigureAwait(false);
            if (root is null)
                return Results.NotFound(new { error = $"Root work unit '{session.RootWorkUnitId}' not found." });

            var allProposals = await merge.ListAsync(cancellationToken: ct).ConfigureAwait(false);
            var counts = allProposals
                .Where(p => p.WorkUnitId is not null)
                .GroupBy(p => p.WorkUnitId!)
                .ToDictionary(g => g.Key, g => g.Count());

            var tree = new List<WorkUnit> { root };
            var frontier = new Queue<string>([root.WorkUnitId]);
            while (frontier.Count > 0)
            {
                var children = await workUnits.GetChildrenAsync(frontier.Dequeue(), ct).ConfigureAwait(false);
                foreach (var child in children)
                {
                    tree.Add(child);
                    frontier.Enqueue(child.WorkUnitId);
                }
            }

            return Results.Ok(tree.Select(wu => ToWorkUnitResponse(wu, counts.GetValueOrDefault(wu.WorkUnitId))));
        });

        app.MapPost("/studio/sessions/{sessionId}/branch", async (
            string sessionId,
            BranchSessionBody body,
            IExecutionSessionService sessions,
            CancellationToken ct) =>
        {
            var parent = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
            if (parent is null)
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });

            var child = await sessions.CreateAsync(
                parent.RootWorkUnitId,
                parent.ModelConfigSnapshotJson,
                parent.ProfileIdSet,
                parentSessionId: sessionId,
                parentEventId: body.ParentEventId,
                ct: ct).ConfigureAwait(false);
            return Results.Ok(child);
        });
    }

    // ── /studio/dead-letter ───────────────────────────────────────────────────

    private static void MapDeadLetterEndpoints(WebApplication app)
    {
        app.MapGet("/studio/dead-letter", async (
            IDeadLetterService deadLetter,
            CancellationToken ct) =>
        {
            var list = await deadLetter.ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        app.MapGet("/studio/dead-letter/{entryId}", async (
            string entryId,
            IDeadLetterService deadLetter,
            CancellationToken ct) =>
        {
            var entry = await deadLetter.GetAsync(entryId, ct).ConfigureAwait(false);
            return entry is null
                ? Results.NotFound(new { error = $"Dead-letter entry '{entryId}' not found." })
                : Results.Ok(entry);
        });

        app.MapPost("/studio/dead-letter/{entryId}/retry", async (
            string entryId,
            IDeadLetterService deadLetter,
            CancellationToken ct) =>
        {
            var result = await deadLetter.RetryAsync(entryId, ct).ConfigureAwait(false);
            return result.Outcome switch
            {
                DeadLetterRetryOutcome.Retried => Results.Ok(result),
                DeadLetterRetryOutcome.NotFound => Results.NotFound(new { error = result.Message }),
                DeadLetterRetryOutcome.MaxAttemptsReached => Results.Conflict(new { error = result.Message }),
                DeadLetterRetryOutcome.InvalidState => Results.BadRequest(new { error = result.Message }),
                _ => Results.BadRequest(new { error = result.Message ?? "Retry failed." }),
            };
        });
    }

    // ── /studio/agent-profiles ────────────────────────────────────────────────

    private static void MapAgentProfileEndpoints(WebApplication app)
    {
        app.MapGet("/studio/agent-profiles", async (
            IAgentProfileService profiles,
            CancellationToken ct) =>
        {
            var list = await profiles.ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        app.MapGet("/studio/agent-profiles/{profileId}", async (
            string profileId,
            IAgentProfileService profiles,
            CancellationToken ct) =>
        {
            var profile = await profiles.GetAsync(profileId, ct).ConfigureAwait(false);
            return profile is null
                ? Results.NotFound(new { error = $"Agent profile '{profileId}' not found." })
                : Results.Ok(profile);
        });

        app.MapPost("/studio/agent-profiles", async (
            CreateAgentProfileBody body,
            IAgentProfileService profiles,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.AgentProfileId))
                return Results.BadRequest(new { error = "agentProfileId is required." });
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "name is required." });

            var profile = new AgentProfile(
                body.AgentProfileId,
                body.Name,
                body.Stage,
                body.SystemPrompt ?? string.Empty,
                body.AllowedTools ?? [],
                body.MaxIterations > 0 ? body.MaxIterations : 20,
                body.FileScopePatterns ?? []);
            var created = await profiles.CreateAsync(profile, ct).ConfigureAwait(false);
            return Results.Ok(created);
        });

        app.MapPut("/studio/agent-profiles/{profileId}", async (
            string profileId,
            UpdateAgentProfileBody body,
            IAgentProfileService profiles,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "name is required." });
            try
            {
                var profile = new AgentProfile(
                    profileId,
                    body.Name,
                    body.Stage,
                    body.SystemPrompt ?? string.Empty,
                    body.AllowedTools ?? [],
                    body.MaxIterations > 0 ? body.MaxIterations : 20,
                    body.FileScopePatterns ?? []);
                var updated = await profiles.UpdateAsync(profile, ct).ConfigureAwait(false);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Agent profile '{profileId}' not found." });
            }
        });
    }

    // ── /studio/sessions/{id}/events + /studio/events ─────────────────────

    private static void MapEventStreamEndpoints(WebApplication app)
    {
        app.MapGet("/studio/sessions/{sessionId}/events", async (
            string sessionId,
            IExecutionEventStream eventStream,
            [FromQuery] string? since,
            CancellationToken ct) =>
        {
            DateTimeOffset? sinceDto = null;
            if (since is not null && DateTimeOffset.TryParse(since, out var parsed))
                sinceDto = parsed;

            var events = await eventStream.GetSessionEventsAsync(sessionId, sinceDto, ct).ConfigureAwait(false);
            return Results.Ok(events);
        });

        app.MapGet("/studio/events/{eventId}", async (
            string eventId,
            IExecutionEventStream eventStream,
            CancellationToken ct) =>
        {
            var ev = await eventStream.GetAsync(eventId, ct).ConfigureAwait(false);
            return ev is null
                ? Results.NotFound(new { error = $"Event '{eventId}' not found." })
                : Results.Ok(ev);
        });
    }

    // ── /studio/sessions/{id}/state ────────────────────────────────────────

    private static void MapSessionStateEndpoints(WebApplication app)
    {
        app.MapGet("/studio/sessions/{sessionId}/state", async (
            string sessionId,
            [FromQuery] string? upToEvent,
            [FromQuery] string? asOf,
            IStateReconstructionService reconstruction,
            CancellationToken ct) =>
        {
            try
            {
                if (upToEvent is not null)
                {
                    var snapshot = await reconstruction.GetStateAtAsync(sessionId, upToEvent, ct).ConfigureAwait(false);
                    return Results.Ok(snapshot);
                }

                if (asOf is not null && DateTimeOffset.TryParse(asOf, out var asOfDto))
                {
                    var snapshot = await reconstruction.GetStateAtTimeAsync(sessionId, asOfDto, ct).ConfigureAwait(false);
                    return Results.Ok(snapshot);
                }

                return Results.BadRequest(new { error = "Either upToEvent or asOf (ISO 8601) is required." });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }

    // ── /studio/artifacts ──────────────────────────────────────────────────

    private sealed record CreateArtifactBody(
        string WorkUnitId,
        string Type,
        string Title,
        string Body,
        string? ParentArtifactId = null);

    private static void MapArtifactEndpoints(WebApplication app)
    {
        app.MapGet("/studio/artifacts/{artifactId}", async (
            string artifactId,
            IArtifactLineageService artifacts,
            CancellationToken ct) =>
        {
            var artifact = await artifacts.GetAsync(artifactId, ct).ConfigureAwait(false);
            return artifact is null
                ? Results.NotFound(new { error = $"Artifact '{artifactId}' not found." })
                : Results.Ok(artifact);
        });

        app.MapGet("/studio/artifacts/{artifactId}/children", async (
            string artifactId,
            IArtifactLineageService artifacts,
            CancellationToken ct) =>
        {
            var children = await artifacts.GetChildrenAsync(artifactId, ct).ConfigureAwait(false);
            return Results.Ok(children);
        });

        // Slice 15f — knowledge artifact Record/Query/List via REST (previously only MCP).
        app.MapPost("/studio/artifacts", async (
            CreateArtifactBody body,
            IArtifactCommandService artifactCommands,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.WorkUnitId))
                return Results.BadRequest(new { error = "workUnitId is required." });
            if (string.IsNullOrWhiteSpace(body.Type))
                return Results.BadRequest(new { error = "type is required." });
            if (string.IsNullOrWhiteSpace(body.Title))
                return Results.BadRequest(new { error = "title is required." });
            if (string.IsNullOrWhiteSpace(body.Body))
                return Results.BadRequest(new { error = "body is required." });

            try
            {
                var recorded = await artifactCommands.RecordAsync(
                    body.WorkUnitId, body.Type, body.Title, body.Body, body.ParentArtifactId, ct).ConfigureAwait(false);
                return Results.Ok(recorded);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/studio/artifacts", async (
            [FromQuery] string workUnitId,
            [FromQuery] string? type,
            [FromQuery] string? keywords,
            [FromQuery] bool? includeAncestors,
            IArtifactCommandService artifactCommands,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(workUnitId))
                return Results.BadRequest(new { error = "workUnitId query parameter is required." });

            try
            {
                // If keywords or type are given, it's a query; otherwise a list.
                if (keywords is not null || type is not null)
                {
                    var results = await artifactCommands.QueryAsync(
                        workUnitId, type, keywords, ct).ConfigureAwait(false);
                    return Results.Ok(results);
                }

                var list = await artifactCommands.ListAsync(
                    workUnitId, includeAncestors ?? true, ct).ConfigureAwait(false);
                return Results.Ok(list);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    // ── /studio/projections — Slice 6.5 deferred: projection REST parity ───

    private static void MapProjectionEndpoints(WebApplication app)
    {
        // List available projection types and levels (mirrors nm_v1_projection_list)
        app.MapGet("/studio/projections", () =>
            Results.Ok(new
            {
                types = ProjectionCatalog.Types,
                levels = ProjectionCatalog.Levels
            }));

        // Get a projection by type (mirrors nm_v1_projection_get)
        app.MapGet("/studio/projections/{projectionType}", async (
            string projectionType,
            [FromQuery] string? level,
            [FromQuery] string? workUnitId,
            [FromQuery] string? branchId,
            [FromQuery] string? agentId,
            IProjectionManager projections,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<ProjectionType>(projectionType, ignoreCase: true, out var type) ||
                !Enum.TryParse<ProjectionLevel>(level ?? "Normal", ignoreCase: true, out var projectionLevel))
            {
                return Results.BadRequest(new { error = "Invalid projectionType or projectionLevel." });
            }

            try
            {
                var result = await projections.GetAsync(
                    new ProjectionRequest(type, projectionLevel, workUnitId, branchId, agentId),
                    ct).ConfigureAwait(false);

                return Results.Ok(new
                {
                    contractVersion = "v1",
                    projectionType = result.Type.ToString(),
                    level = result.Level.ToString(),
                    data = JsonSerializer.Deserialize<object>(result.DataJson)
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }

    // ── /studio/snapshots — Slice 6.5 deferred: snapshot REST parity ────────

    private static void MapSnapshotEndpoints(WebApplication app)
    {
        // Get an execution snapshot for an agent (mirrors nm_v1_snapshot_get)
        app.MapGet("/studio/snapshots/{agentId}", async (
            string agentId,
            [FromQuery] string? workUnitId,
            ISnapshotService snapshots,
            CancellationToken ct) =>
        {
            if (workUnitId is null)
                return Results.BadRequest(new { error = "workUnitId is required." });

            try
            {
                var snapshot = await snapshots.GetAsync(agentId, workUnitId, ct).ConfigureAwait(false);
                return Results.Ok(new
                {
                    agentId,
                    workUnitId,
                    currentGoal = snapshot.CurrentGoal,
                    currentTask = snapshot.CurrentTask,
                    failureCount = snapshot.FailureCount,
                    rollbackCount = snapshot.RollbackCount,
                    nextSuggestedAction = snapshot.NextSuggestedAction
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Compare execution snapshots between agents (mirrors nm_v1_snapshot_compare)
        app.MapGet("/studio/snapshots/{agentId}/compare/{otherAgentId}", async (
            string agentId,
            string otherAgentId,
            [FromQuery] string? workUnitId,
            ISnapshotService snapshots,
            CancellationToken ct) =>
        {
            if (workUnitId is null)
                return Results.BadRequest(new { error = "workUnitId is required." });

            try
            {
                var comparison = await snapshots.CompareAsync(agentId, workUnitId, otherAgentId, ct).ConfigureAwait(false);
                return Results.Ok(JsonSerializer.Deserialize<object>(comparison)!);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }

    // ── /studio/replay ─────────────────────────────────────────────────────

    private static void MapReplayEndpoints(WebApplication app)
    {
        // Slice 13h — DAG Replay frontend connects here instead of the engine's /ws/runtime
        // WebSocket for timeline node data. Returns timeline entries (artifacts + orchestration
        // events) grouped by branch, plus the set of known branches so the frontend can render
        // them as DAG lanes without a separate init payload.
        app.MapGet("/studio/replay/timeline", async (
            [FromQuery] string? branchId,
            IBranchService branches,
            IReplayService replay,
            CancellationToken ct) =>
        {
            var branchIds = await branches.ListBranchesAsync(ct).ConfigureAwait(false);
            var targetBranches = branchId is { Length: > 0 }
                ? new[] { branchId }
                : branchIds.ToArray();

            var timelines = new List<object>();
            foreach (var b in targetBranches)
            {
                var json = await replay.RangeAsync(b, cancellationToken: ct).ConfigureAwait(false);
                timelines.Add(JsonSerializer.Deserialize<object>(json)!);
            }

            return Results.Ok(new { branches = targetBranches, timelines });
        });

        app.MapGet("/studio/replay/timeline/{branchId}", async (
            string branchId,
            IReplayService replay,
            CancellationToken ct) =>
        {
            var json = await replay.RangeAsync(branchId, cancellationToken: ct).ConfigureAwait(false);
            return Results.Ok(JsonSerializer.Deserialize<object>(json)!);
        });

        // ── Slice 6.5 deferred: replay REST parity ──────────────────────────

        // Range replay with optional from/to node (mirrors nm_v1_replay_range)
        app.MapGet("/studio/replay/range/{branchId}", async (
            string branchId,
            [FromQuery] string? fromNode,
            [FromQuery] string? toNode,
            IReplayService replay,
            CancellationToken ct) =>
        {
            var json = await replay.RangeAsync(branchId, fromNode, toNode, ct).ConfigureAwait(false);
            return Results.Ok(JsonSerializer.Deserialize<object>(json)!);
        });

        // Rollback to a known good state (mirrors nm_v1_replay_rollback)
        app.MapPost("/studio/replay/rollback/{branchId}", async (
            string branchId,
            RollbackRequestBody body,
            IReplayService replay,
            CancellationToken ct) =>
        {
            var json = await replay.RollbackAsync(branchId, body.KnownGoodStateId, ct).ConfigureAwait(false);
            return Results.Ok(JsonSerializer.Deserialize<object>(json)!);
        });

        // Inspect replay history (mirrors nm_v1_replay_inspect)
        app.MapGet("/studio/replay/inspect/{branchId}", async (
            string branchId,
            [FromQuery] string? nodeId,
            IReplayService replay,
            CancellationToken ct) =>
        {
            var json = await replay.InspectAsync(branchId, nodeId, ct).ConfigureAwait(false);
            return Results.Ok(JsonSerializer.Deserialize<object>(json)!);
        });
    }

    // ── /studio/goals — 6.5 deferred: goal REST parity ─────────────────────

    private static void MapGoalEndpoints(WebApplication app)
    {
        // Create a goal (mirrors nm_v1_goal_create)
        app.MapPost("/studio/goals", async (
            CreateGoalBody body,
            IWorkUnitService workUnits,
            IWorkUnitCommandService workUnitCommands,
            IGoalNodeService goalNodes,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Goal))
                return Results.BadRequest(new { error = "goal is required." });

            WorkUnit workUnit;
            if (body.WorkUnitId is not null)
            {
                var wu = await workUnits.GetAsync(body.WorkUnitId, ct).ConfigureAwait(false);
                if (wu is null)
                    return Results.NotFound(new { error = $"Work unit '{body.WorkUnitId}' not found." });
                workUnit = wu;
            }
            else
            {
                workUnit = await workUnitCommands.CreateAsync(
                    new WorkUnitCreateCommand(body.Goal, "studio"), ct).ConfigureAwait(false);
            }

            var goalNode = new GoalNode(
                GoalId: workUnit.WorkUnitId,
                Goal: workUnit.Goal,
                WorkUnitId: workUnit.WorkUnitId,
                BranchId: workUnit.BranchId,
                Status: GoalStatus.Exploring,
                CreatedAt: workUnit.CreatedAt,
                UpdatedAt: workUnit.UpdatedAt,
                Owner: workUnit.Owner,
                ParentGoalId: workUnit.ParentWorkUnitId);
            await goalNodes.RecordAsync(goalNode, ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                goalId = workUnit.WorkUnitId,
                goal = workUnit.Goal,
                workUnitId = workUnit.WorkUnitId,
                branchId = workUnit.BranchId,
                status = goalNode.Status.ToString(),
                createdAt = workUnit.CreatedAt
            });
        });

        // List goals (mirrors nm_v1_goal_list)
        app.MapGet("/studio/goals", async (
            IGoalNodeService goalNodes,
            IWorkUnitService workUnits,
            CancellationToken ct) =>
        {
            var storedGoals = await goalNodes.ListAsync(ct).ConfigureAwait(false);
            if (storedGoals.Count > 0)
            {
                var goals = storedGoals.Select(g => new
                {
                    goalId = g.GoalId,
                    goal = g.Goal,
                    workUnitId = g.WorkUnitId,
                    branchId = g.BranchId,
                    status = g.Status.ToString(),
                    parentGoalId = g.ParentGoalId,
                    createdAt = g.CreatedAt
                }).ToList();
                return Results.Ok(new { goals, source = "goal-store" });
            }

            var items = await workUnits.ListAsync(branchId: null, ct).ConfigureAwait(false);
            var fallback = items.Select(wu => new
            {
                goalId = wu.WorkUnitId,
                goal = wu.Goal,
                workUnitId = wu.WorkUnitId,
                branchId = wu.BranchId,
                status = wu.Status.ToString(),
                parentWorkUnitId = wu.ParentWorkUnitId,
                createdAt = wu.CreatedAt
            }).ToList();
            return Results.Ok(new { goals = fallback, source = "work-units" });
        });
    }

    // ── /studio/decisions — 6.5 deferred: decision REST parity ──────────────

    private static void MapDecisionEndpoints(WebApplication app)
    {
        // Record a decision (mirrors nm_v1_decision_record)
        app.MapPost("/studio/decisions", async (
            RecordDecisionBody body,
            IMergeService merges,
            IDecisionNodeService decisionNodes,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.ProposalId))
                return Results.BadRequest(new { error = "proposalId is required." });
            if (string.IsNullOrWhiteSpace(body.Outcome))
                return Results.BadRequest(new { error = "outcome is required." });

            var proposal = await merges.GetAsync(body.ProposalId, ct).ConfigureAwait(false);
            if (proposal is null)
                return Results.NotFound(new { error = $"Proposal '{body.ProposalId}' not found." });

            if (!Enum.TryParse<DecisionOutcome>(body.Outcome, ignoreCase: true, out var decision))
                return Results.BadRequest(new { error = "Invalid outcome. Use Accepted, Rejected, Deferred, or Superseded." });

            var mergeStatus = decision switch
            {
                DecisionOutcome.Accepted => MergeProposalStatus.Approved,
                DecisionOutcome.Rejected => MergeProposalStatus.Rejected,
                DecisionOutcome.Superseded => MergeProposalStatus.Superseded,
                _ => proposal.Status
            };

            if (mergeStatus != proposal.Status)
                proposal = await merges.ReviewAsync(body.ProposalId, mergeStatus, ct).ConfigureAwait(false);

            var decisionNode = new DecisionNode(
                DecisionId: $"dec-{Guid.NewGuid():N}",
                WorkUnitId: proposal.WorkUnitId ?? string.Empty,
                ProposalId: body.ProposalId,
                Outcome: decision,
                ReviewerAgentId: body.ReviewerAgentId,
                ReviewerModel: body.ReviewerModel,
                ReviewerProvider: body.ReviewerProvider,
                Confidence: body.Confidence,
                Rationale: body.Rationale,
                DecidedAt: DateTimeOffset.UtcNow);
            await decisionNodes.RecordAsync(decisionNode, ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                decisionId = decisionNode.DecisionId,
                proposalId = body.ProposalId,
                outcome = decision.ToString(),
                status = proposal.Status.ToString(),
                confidence = body.Confidence,
                rationale = body.Rationale,
                decidedAt = decisionNode.DecidedAt
            });
        });

        // List decisions (mirrors nm_v1_decision_list)
        app.MapGet("/studio/decisions", async (
            [FromQuery] string? workUnitId,
            IMergeService merges,
            IDecisionNodeService decisionNodes,
            CancellationToken ct) =>
        {
            if (workUnitId is not null)
            {
                var storedDecisions = await decisionNodes.ListByWorkUnitAsync(workUnitId, ct).ConfigureAwait(false);
                if (storedDecisions.Count > 0)
                {
                    var decisions = storedDecisions.Select(d => new
                    {
                        decisionId = d.DecisionId,
                        proposalId = d.ProposalId,
                        workUnitId = d.WorkUnitId,
                        outcome = d.Outcome.ToString(),
                        confidence = d.Confidence,
                        decidedAt = d.DecidedAt
                    }).ToList();
                    return Results.Ok(new { decisions, source = "decision-store" });
                }
            }

            var proposals = await merges.ListAsync(sourceBranch: null, ct).ConfigureAwait(false);
            var fallback = proposals.Select(p => new
            {
                decisionId = $"dec-{p.ProposalId}",
                proposalId = p.ProposalId,
                workUnitId = p.WorkUnitId,
                sourceBranch = p.SourceBranch,
                status = p.Status.ToString(),
                confidence = p.Confidence,
                agentId = p.AgentId,
                model = p.Model,
                provider = p.Provider
            }).ToList();
            return Results.Ok(new { decisions = fallback, source = "proposals" });
        });
    }

    // ── /studio/evidence — 6.5 deferred: evidence REST parity ───────────────

    private static void MapEvidenceEndpoints(WebApplication app)
    {
        // Attach build/test evidence (mirrors nm_v1_evidence_attach)
        app.MapPost("/studio/evidence/attach", async (
            [FromQuery] string workUnitId,
            IWorkUnitService workUnits,
            IWorkspaceExecutionCommandService execCommands,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(workUnitId))
                return Results.BadRequest(new { error = "workUnitId is required." });

            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." });

            var execResult = await execCommands.GetLatestAsync(wu.BranchId, ct).ConfigureAwait(false);
            if (execResult is null)
                return Results.Ok(new { evidence = Array.Empty<object>(), message = "No execution results available." });

            var evidence = new List<object>();
            foreach (var build in execResult.Builds)
            {
                evidence.Add(new
                {
                    evidenceId = $"ev-bld-{Guid.NewGuid():N}",
                    kind = "Build",
                    summary = build.Success
                        ? $"{build.BuildSystem ?? "build"}: passed"
                        : $"{build.BuildSystem ?? "build"}: failed (exit {build.ExitCode})",
                    attachedAt = DateTimeOffset.UtcNow
                });
            }
            foreach (var test in execResult.Tests)
            {
                evidence.Add(new
                {
                    evidenceId = $"ev-tst-{Guid.NewGuid():N}",
                    kind = "Test",
                    summary = test.Success
                        ? $"{test.BuildSystem ?? "test"}: passed ({test.Passed}/{test.TotalTests})"
                        : $"{test.BuildSystem ?? "test"}: failed ({test.Passed}/{test.TotalTests})",
                    attachedAt = DateTimeOffset.UtcNow
                });
            }

            return Results.Ok(new { evidence, allSucceeded = execResult.AllSucceeded });
        });

        // List evidence (mirrors nm_v1_evidence_list)
        app.MapGet("/studio/evidence", async (
            [FromQuery] string workUnitId,
            IWorkUnitService workUnits,
            IWorkspaceExecutionCommandService execCommands,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(workUnitId))
                return Results.BadRequest(new { error = "workUnitId is required." });

            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." });

            var execResult = await execCommands.GetLatestAsync(wu.BranchId, ct).ConfigureAwait(false);
            if (execResult is null)
                return Results.Ok(new { evidence = Array.Empty<object>() });

            var evidence = new List<object>();
            foreach (var build in execResult.Builds)
            {
                evidence.Add(new
                {
                    evidenceId = $"ev-bld-{Guid.NewGuid():N}",
                    kind = "Build",
                    buildSystem = build.BuildSystem,
                    command = build.Command,
                    success = build.Success,
                    exitCode = build.ExitCode,
                    attachedAt = execResult.ExecutedAt
                });
            }
            foreach (var test in execResult.Tests)
            {
                evidence.Add(new
                {
                    evidenceId = $"ev-tst-{Guid.NewGuid():N}",
                    kind = "Test",
                    buildSystem = test.BuildSystem,
                    command = test.Command,
                    success = test.Success,
                    totalTests = test.TotalTests,
                    passed = test.Passed,
                    failed = test.Failed,
                    skipped = test.Skipped,
                    attachedAt = execResult.ExecutedAt
                });
            }

            return Results.Ok(new { evidence });
        });
    }

    // ── /studio/trajectory — 6.5 deferred: trajectory REST parity ───────────

    private static void MapTrajectoryEndpoints(WebApplication app)
    {
        // Create trajectory (mirrors nm_v1_trajectory_create)
        app.MapPost("/studio/trajectory", async (
            CreateTrajectoryBody body,
            IWorkUnitService workUnits,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.WorkUnitId))
                return Results.BadRequest(new { error = "workUnitId is required." });
            if (string.IsNullOrWhiteSpace(body.Phase))
                return Results.BadRequest(new { error = "phase is required." });

            var wu = await workUnits.GetAsync(body.WorkUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{body.WorkUnitId}' not found." });

            if (!Enum.TryParse<TrajectoryPhase>(body.Phase, ignoreCase: true, out var trajectoryPhase))
                return Results.BadRequest(new { error = "Invalid phase. Use GoalDefined, Decomposed, Executing, Proposed, Converging, Converged, Forked, or Abandoned." });

            return Results.Ok(new
            {
                trajectoryId = wu.WorkUnitId,
                workUnitId = body.WorkUnitId,
                branchId = wu.BranchId,
                goal = wu.Goal,
                phase = trajectoryPhase.ToString(),
                agentId = body.AgentId,
                model = body.Model,
                provider = body.Provider,
                startedAt = wu.CreatedAt
            });
        });

        // Replay trajectory (mirrors nm_v1_trajectory_replay)
        app.MapGet("/studio/trajectory/replay", async (
            [FromQuery] string? workUnitId,
            [FromQuery] string? branchId,
            [FromQuery] string? mode,
            IWorkUnitService workUnits,
            IReplayService replay,
            CancellationToken ct) =>
        {
            var normalizedMode = mode?.Trim().ToLowerInvariant() ?? "linear";

            if (normalizedMode == "branchexplorer")
            {
                var allUnits = await workUnits.ListAsync(branchId: null, ct).ConfigureAwait(false);
                var byBranch = allUnits.GroupBy(wu => wu.BranchId).Select(g => new
                {
                    branchId = g.Key,
                    workUnitCount = g.Count(),
                    goals = g.Select(wu => new
                    {
                        workUnitId = wu.WorkUnitId,
                        goal = wu.Goal,
                        status = wu.Status.ToString(),
                        phase = wu.CurrentStage?.ToString(),
                        hasChildren = allUnits.Any(c => c.ParentWorkUnitId == wu.WorkUnitId),
                        createdAt = wu.CreatedAt
                    }).OrderBy(w => w.createdAt).ToList()
                }).OrderByDescending(b => b.workUnitCount).ToList();
                return Results.Ok(new { mode = "BranchExplorer", branches = byBranch });
            }

            if (normalizedMode == "counterfactual")
            {
                var allUnits = await workUnits.ListAsync(branchId: null, ct).ConfigureAwait(false);
                WorkUnit? target = null;
                if (workUnitId is not null)
                    target = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);

                var counterfactuals = new List<object>();
                if (target?.ParentWorkUnitId is not null)
                {
                    var siblings = allUnits.Where(wu => wu.ParentWorkUnitId == target.ParentWorkUnitId && wu.WorkUnitId != target.WorkUnitId).ToList();
                    counterfactuals.AddRange(siblings.Select(s => new
                    {
                        workUnitId = s.WorkUnitId,
                        goal = s.Goal,
                        status = s.Status.ToString(),
                        relationship = "sibling",
                        forkType = s.ForkType?.ToString(),
                        branchedFromProposalId = s.BranchedFromProposalId,
                        createdAt = s.CreatedAt
                    }));
                }
                var forkedFromProposals = allUnits.Where(wu => wu.BranchedFromProposalId is not null && wu.WorkUnitId != workUnitId).ToList();
                counterfactuals.AddRange(forkedFromProposals.Select(f => new
                {
                    workUnitId = f.WorkUnitId,
                    goal = f.Goal,
                    status = f.Status.ToString(),
                    relationship = "forked-from-proposal",
                    forkType = f.ForkType?.ToString(),
                    branchedFromProposalId = f.BranchedFromProposalId,
                    createdAt = f.CreatedAt
                }));

                return Results.Ok(new
                {
                    mode = "Counterfactual",
                    targetWorkUnitId = workUnitId,
                    targetGoal = target?.Goal,
                    alternatives = counterfactuals
                });
            }

            // Linear mode
            var targetBranchId = branchId;
            if (targetBranchId is null && workUnitId is not null)
            {
                var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
                targetBranchId = wu?.BranchId;
            }

            if (targetBranchId is not null)
            {
                var replayJson = await replay.RangeAsync(targetBranchId, cancellationToken: ct).ConfigureAwait(false);
                return Results.Ok(new { mode = "Linear", replayJson });
            }

            var allUnits2 = await workUnits.ListAsync(branchId: null, ct).ConfigureAwait(false);
            var nodes = allUnits2.Select(wu => new
            {
                workUnitId = wu.WorkUnitId,
                branchId = wu.BranchId,
                goal = wu.Goal,
                phase = wu.CurrentStage?.ToString() ?? wu.Status.ToString(),
                startedAt = wu.CreatedAt,
                completedAt = wu.Status is WorkUnitStatus.Completed or WorkUnitStatus.Merged ? (DateTimeOffset?)wu.UpdatedAt : null
            }).ToList();
            return Results.Ok(new { mode = "Linear", nodes });
        });
    }

    // ── /studio/hypotheses — 6.5 deferred: hypothesis REST parity ───────────

    private static void MapHypothesisEndpoints(WebApplication app)
    {
        // Fork hypothesis (mirrors nm_v1_hypothesis_fork)
        app.MapPost("/studio/hypotheses/fork", async (
            CreateHypothesisBody body,
            IWorkUnitCommandService workUnitCommands,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Goal))
                return Results.BadRequest(new { error = "goal is required." });
            if (string.IsNullOrWhiteSpace(body.ForkType))
                return Results.BadRequest(new { error = "forkType is required." });

            if (!Enum.TryParse<HypothesisForkType>(body.ForkType, ignoreCase: true, out var hypothesisType))
                return Results.BadRequest(new { error = "Invalid forkType. Use Code, Reasoning, Model, Research, Architecture, or Product." });

            var workUnit = await workUnitCommands.CreateAsync(
                new WorkUnitCreateCommand(body.Goal, "studio", ParentWorkUnitId: body.ParentWorkUnitId, ForkType: hypothesisType),
                ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                hypothesisId = workUnit.WorkUnitId,
                workUnitId = workUnit.WorkUnitId,
                goal = workUnit.Goal,
                forkType = hypothesisType.ToString(),
                parentWorkUnitId = body.ParentWorkUnitId,
                branchedFromProposalId = body.BranchedFromProposalId,
                rationale = body.Rationale,
                status = "Active",
                createdAt = workUnit.CreatedAt
            });
        });

        // List hypotheses (mirrors nm_v1_hypothesis_list)
        app.MapGet("/studio/hypotheses", async (
            [FromQuery] string? parentWorkUnitId,
            IWorkUnitService workUnits,
            CancellationToken ct) =>
        {
            IReadOnlyList<WorkUnit> items;
            if (parentWorkUnitId is not null)
                items = await workUnits.GetChildrenAsync(parentWorkUnitId, ct).ConfigureAwait(false);
            else
                items = await workUnits.ListAsync(branchId: null, ct).ConfigureAwait(false);

            var hypotheses = items.Select(wu => new
            {
                hypothesisId = wu.WorkUnitId,
                workUnitId = wu.WorkUnitId,
                goal = wu.Goal,
                forkType = wu.ForkType?.ToString() ?? "Unknown",
                status = wu.Status.ToString(),
                parentWorkUnitId = wu.ParentWorkUnitId,
                branchedFromProposalId = wu.BranchedFromProposalId,
                createdAt = wu.CreatedAt
            }).ToList();

            return Results.Ok(new { hypotheses });
        });
    }

    // ── /studio/reasoning — 6.5 deferred: reasoning REST parity ─────────────

    private static void MapReasoningEndpoints(WebApplication app)
    {
        // Record reasoning commit (mirrors nm_v1_reasoning_record)
        app.MapPost("/studio/reasoning", async (
            RecordReasoningBody body,
            IOrchestrationDecisionLogService decisionLog,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.WorkUnitId))
                return Results.BadRequest(new { error = "workUnitId is required." });
            if (string.IsNullOrWhiteSpace(body.AgentId))
                return Results.BadRequest(new { error = "agentId is required." });
            if (string.IsNullOrWhiteSpace(body.InputStage))
                return Results.BadRequest(new { error = "inputStage is required." });
            if (string.IsNullOrWhiteSpace(body.Action))
                return Results.BadRequest(new { error = "action is required." });

            if (!Enum.TryParse<PipelineStage>(body.InputStage, ignoreCase: true, out var stage))
                return Results.BadRequest(new { error = "Invalid inputStage." });

            if (!Enum.TryParse<OrchestrationAction>(body.Action, ignoreCase: true, out var orchestrationAction))
                return Results.BadRequest(new { error = "Invalid action." });

            var ev = await decisionLog.RecordAsync(
                body.WorkUnitId, body.AgentId, stage,
                body.ProjectionSnapshotJson ?? "{}", orchestrationAction,
                [], body.Reasoning, ct: ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                commitId = ev.EventId,
                workUnitId = body.WorkUnitId,
                agentId = body.AgentId,
                agentModel = body.AgentModel,
                agentProvider = body.AgentProvider,
                inputStage = stage.ToString(),
                action = orchestrationAction.ToString(),
                reasoning = body.Reasoning,
                committedAt = ev.OccurredAt
            });
        });
    }

    // ── /studio/models — 6.5 deferred: model comparison REST parity ─────────

    private static void MapModelEndpoints(WebApplication app)
    {
        // Compare model outputs (mirrors nm_v1_model_compare)
        app.MapGet("/studio/models/compare", async (
            [FromQuery] string proposalIdA,
            [FromQuery] string proposalIdB,
            IMergeService merges,
            IProposalReviewService proposalReview,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(proposalIdA) || string.IsNullOrWhiteSpace(proposalIdB))
                return Results.BadRequest(new { error = "proposalIdA and proposalIdB are required." });

            var proposalA = await merges.GetAsync(proposalIdA, ct).ConfigureAwait(false);
            if (proposalA is null)
                return Results.NotFound(new { error = $"Proposal '{proposalIdA}' not found." });

            var proposalB = await merges.GetAsync(proposalIdB, ct).ConfigureAwait(false);
            if (proposalB is null)
                return Results.NotFound(new { error = $"Proposal '{proposalIdB}' not found." });

            var results = await Task.WhenAll(
                proposalReview.GetFileChangesAsync(proposalIdA, ct),
                proposalReview.GetFileChangesAsync(proposalIdB, ct)
            ).ConfigureAwait(false);
            var changesA = results[0];
            var changesB = results[1];

            var filesByPathA = changesA.ToDictionary(fc => fc.Path);
            var filesByPathB = changesB.ToDictionary(fc => fc.Path);
            var allPaths = filesByPathA.Keys.Union(filesByPathB.Keys).Distinct().ToList();

            var divergedFiles = allPaths.Select(path =>
            {
                filesByPathA.TryGetValue(path, out var fcA);
                filesByPathB.TryGetValue(path, out var fcB);
                var hunksA = fcA?.Hunks.SelectMany(h => h.Lines.Select(l => l.Text)).ToList() ?? [];
                var hunksB = fcB?.Hunks.SelectMany(h => h.Lines.Select(l => l.Text)).ToList() ?? [];
                var overlapping = hunksA.Intersect(hunksB).ToList();
                return new
                {
                    path,
                    diffA = fcA?.AfterContent ?? fcA?.BeforeContent ?? "(no output)",
                    diffB = fcB?.AfterContent ?? fcB?.BeforeContent ?? "(no output)",
                    overlappingLines = overlapping
                };
            }).ToList();

            return Results.Ok(new
            {
                modelA = proposalA.Model ?? proposalA.AgentId ?? "unknown",
                modelB = proposalB.Model ?? proposalB.AgentId ?? "unknown",
                divergedFiles,
                comparedAt = DateTimeOffset.UtcNow
            });
        });

        // Model replay (mirrors nm_v1_model_replay)
        app.MapGet("/studio/models/replay/{workUnitId}", async (
            string workUnitId,
            [FromQuery] string? compareModel,
            IWorkUnitService workUnits,
            IMergeService merges,
            CancellationToken ct) =>
        {
            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is null)
                return Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." });

            var allProposals = await merges.ListAsync(sourceBranch: null, ct).ConfigureAwait(false);
            var relevantProposals = allProposals
                .Where(p => p.WorkUnitId == workUnitId)
                .Select(p => new
                {
                    proposalId = p.ProposalId,
                    model = p.Model,
                    provider = p.Provider,
                    agentId = p.AgentId,
                    status = p.Status.ToString(),
                    confidence = p.Confidence
                })
                .ToList();

            return Results.Ok(new
            {
                workUnitId,
                goal = wu.Goal,
                model = compareModel,
                proposals = relevantProposals,
                replayMode = "ModelComparison"
            });
        });
    }
}
