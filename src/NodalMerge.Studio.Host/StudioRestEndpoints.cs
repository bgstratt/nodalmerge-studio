using Microsoft.AspNetCore.Mvc;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Host;

public static class StudioRestEndpoints
{
    // ── Request bodies ─────────────────────────────────────────────────────

    private sealed record CreateWorkUnitBody(
        string Goal,
        string Owner,
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
        string? ProfileId = null);

    private sealed record ProposeMergeBody(
        string SourceBranch,
        string TargetBranch,
        string Summary,
        string? Goal = null,
        string? ChangeDescription = null);

    private sealed record ReviewBody(string Decision);

    private sealed record CreateBranchBody(
        string Name,
        string? FromBranchId = null);

    private sealed record CreateAgentProfileBody(
        string AgentProfileId,
        string Name,
        PipelineStage Stage,
        string SystemPrompt,
        IReadOnlyList<string> AllowedTools,
        int MaxIterations);

    private sealed record UpdateAgentProfileBody(
        string Name,
        PipelineStage Stage,
        string SystemPrompt,
        IReadOnlyList<string> AllowedTools,
        int MaxIterations);

    private sealed record MarkKnownGoodBody(
        string BranchId,
        string NodeId,
        string Description,
        string? CreatedBy = null);

    private sealed record CheckoutKnownGoodBody(string StateId);

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
        return app;
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

    private static void MapWorkUnitEndpoints(WebApplication app)
    {
        app.MapGet("/studio/workunits", async (
            [FromQuery] string? branchId,
            IWorkUnitService workUnits,
            CancellationToken ct) =>
        {
            var list = await workUnits.ListAsync(branchId, ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        app.MapGet("/studio/workunits/{workUnitId}", async (
            string workUnitId,
            IWorkUnitService workUnits,
            CancellationToken ct) =>
        {
            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            return wu is null
                ? Results.NotFound(new { error = $"Work unit '{workUnitId}' not found." })
                : Results.Ok(wu);
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

        app.MapPost("/studio/workunits", async (
            CreateWorkUnitBody body,
            IOrchestratorService orchestrator,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Goal))
                return Results.BadRequest(new { error = "goal is required." });
            if (string.IsNullOrWhiteSpace(body.Owner))
                return Results.BadRequest(new { error = "owner is required." });

            var wu = await orchestrator
                .CreateWorkUnitAsync(body.Goal, body.Owner, body.SuccessCriteria, body.RepositoryPath,
                    body.ParentWorkUnitId, body.DependsOn, body.FileScope, ct)
                .ConfigureAwait(false);
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

            var agentId = await agents.SpawnAsync(body.AgentType, body.WorkUnitId, body.TaskId, body.Model, body.BaseUrl, body.ApiKey, body.Provider, body.ProfileId, ct).ConfigureAwait(false);
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

        app.MapPost("/studio/merges", async (
            ProposeMergeBody body,
            IMergeService merge,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.SourceBranch))
                return Results.BadRequest(new { error = "sourceBranch is required." });
            if (string.IsNullOrWhiteSpace(body.TargetBranch))
                return Results.BadRequest(new { error = "targetBranch is required." });
            if (string.IsNullOrWhiteSpace(body.Summary))
                return Results.BadRequest(new { error = "summary is required." });

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
                body.MaxIterations > 0 ? body.MaxIterations : 20);
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
                    body.MaxIterations > 0 ? body.MaxIterations : 20);
                var updated = await profiles.UpdateAsync(profile, ct).ConfigureAwait(false);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Agent profile '{profileId}' not found." });
            }
        });
    }
}
