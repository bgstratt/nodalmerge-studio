using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Merge;
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

    private static void MapTaskEndpoints(WebApplication app)
    {
        app.MapGet("/studio/tasks", async (
            [FromQuery] string? workUnitId,
            ITaskService tasks,
            CancellationToken ct) =>
        {
            var list = await tasks.ListAsync(workUnitId, ct).ConfigureAwait(false);
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
                    ? new { proposalId = id, status = "Unknown", goal = (string?)null, summary = (string?)null }
                    : new
                    {
                        proposalId = constituent.ProposalId,
                        status = constituent.Status.ToString(),
                        goal = (string?)constituent.Goal,
                        summary = (string?)constituent.Summary,
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
            IMergeService merge,
            IStudioNodeStore nodeStore,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.SourceBranch))
                return Results.BadRequest(new { error = "sourceBranch is required." });
            if (string.IsNullOrWhiteSpace(body.TargetBranch))
                return Results.BadRequest(new { error = "targetBranch is required." });
            if (string.IsNullOrWhiteSpace(body.Summary))
                return Results.BadRequest(new { error = "summary is required." });

            // Idempotent: return cached result for same X-Command-Id header.
            var commandId = request.Headers["X-Command-Id"].FirstOrDefault();
            if (commandId is not null)
            {
                var cached = await nodeStore.ReadNodeAsync(StudioNodeKind.CommandResultV1, commandId, ct)
                    .ConfigureAwait(false);
                if (cached is not null)
                    return Results.Text(cached, "application/json");
            }

            var proposal = new MergeProposal(
                $"MP-{Guid.NewGuid():N}",
                body.SourceBranch,
                body.TargetBranch,
                body.Goal ?? body.Summary,
                body.Summary,
                body.ChangeDescription ?? body.Summary,
                null, null, null,
                MergeProposalStatus.Draft);
            var created = await merge.ProposeAsync(proposal, ct).ConfigureAwait(false);

            if (commandId is not null)
                await nodeStore.WriteNodeAsync(StudioNodeKind.CommandResultV1, commandId,
                    JsonSerializer.Serialize(created), ct).ConfigureAwait(false);

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
            IWorkScheduler scheduler,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.WorkUnitId))
                return Results.BadRequest(new { error = "workUnitId is required." });
            if (string.IsNullOrWhiteSpace(body.ProfileId))
                return Results.BadRequest(new { error = "profileId is required." });

            await scheduler.EnqueueAsync(body.WorkUnitId, body.ProfileId, body.TaskId, sessionId: body.SessionId, ct: ct)
                .ConfigureAwait(false);
            return Results.Ok(new { workUnitId = body.WorkUnitId, profileId = body.ProfileId, taskId = body.TaskId, sessionId = body.SessionId, status = "enqueued" });
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
    }
}
