using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using StudioArtifactStatus = NodalMerge.Studio.Contracts.Domain.ArtifactStatus;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class McpToolDispatcher(
    IWorkUnitService workUnits,
    IOrchestratorService orchestrator,
    ITaskService tasks,
    IBranchService branches,
    IMergeService merge,
    IWorkspaceService workspace,
    IFileWorkspaceService fileWorkspace,
    IAgentWorkspaceService agentWorkspaces,
    ISnapshotService snapshots,
    IAgentControlService agentControl,
    IArtifactLineageService artifactLineage,
    IProjectionManager projections,
    IWorkScheduler scheduler,
    IExecutionEventStream events,
    IIntentGraphService intentGraph)
{
    public async Task<string> DispatchAsync(
        string toolName,
        JsonElement input,
        IReadOnlyList<string>? allowedTools,
        CancellationToken ct,
        string? sessionId = null)
    {
        if (allowedTools is { Count: > 0 } && !allowedTools.Contains(toolName))
            return ToError($"Tool '{toolName}' is not permitted by this agent's profile.");

        try
        {
            return toolName switch
            {
                McpToolNames.WorkUnitGet      => await WorkUnitGetAsync(input, ct),
                McpToolNames.WorkUnitCreate   => await WorkUnitCreateAsync(input, ct),
                McpToolNames.WorkUnitUpdate   => await WorkUnitUpdateAsync(input, ct, sessionId),
                McpToolNames.WorkUnitList     => await WorkUnitListAsync(input, ct),
                McpToolNames.TaskCreate       => await TaskCreateAsync(input, ct),
                McpToolNames.TaskList         => await TaskListAsync(input, ct),
                McpToolNames.TaskUpdate       => await TaskUpdateAsync(input, ct),
                McpToolNames.TaskAssign       => await TaskAssignAsync(input, ct),
                McpToolNames.BranchCreate     => await BranchCreateAsync(input, ct),
                McpToolNames.BranchList       => await BranchListAsync(ct),
                McpToolNames.BranchStatus     => await BranchStatusAsync(input, ct),
                McpToolNames.AgentSpawn       => await AgentSpawnAsync(input, ct),
                McpToolNames.AgentStatus      => await AgentStatusAsync(input, ct),
                McpToolNames.AgentStop        => await AgentStopAsync(input, ct),
                McpToolNames.MergePropose     => await MergeProposeAsync(input, ct, sessionId),
                McpToolNames.MergeValidate    => await MergeValidateAsync(input, ct),
                McpToolNames.MergeReview      => await MergeReviewAsync(input, ct),
                McpToolNames.WorkspaceSummary => await WorkspaceSummaryAsync(input, ct),
                McpToolNames.SnapshotGet      => await SnapshotGetAsync(input, ct),
                McpToolNames.ProjectionGet    => await ProjectionGetAsync(input, ct),
                McpToolNames.MergeApply       => await MergeApplyAsync(input, ct, sessionId),
                McpToolNames.WorkspaceRead    => await WorkspaceReadAsync(input, ct),
                McpToolNames.WorkspaceWrite   => await WorkspaceWriteAsync(input, ct),
                McpToolNames.WorkspaceDelete  => await WorkspaceDeleteAsync(input, ct),
                McpToolNames.WorkspaceList    => await WorkspaceListAsync(input, ct),
                McpToolNames.WorkspaceDiff    => await WorkspaceDiffAsync(input, ct),
                McpToolNames.WorkspaceExists  => await WorkspaceExistsAsync(input, ct),
                McpToolNames.SchedulerEnqueue => await SchedulerEnqueueAsync(input, ct, sessionId),
                McpToolNames.SchedulerPending => await SchedulerPendingAsync(ct),
                McpToolNames.IntentRecord     => await IntentRecordAsync(input, ct),
                McpToolNames.ArtifactRecord   => await ArtifactRecordAsync(input, ct),
                McpToolNames.ArtifactQuery    => await ArtifactQueryAsync(input, ct),
                McpToolNames.ArtifactList     => await ArtifactListAsync(input, ct),
                _ => ToError($"Tool '{toolName}' is not dispatched by the agent runtime.")
            };
        }
        catch (Exception ex)
        {
            return ToError(ex.Message);
        }
    }

    private async Task<string> WorkUnitGetAsync(JsonElement input, CancellationToken ct)
    {
        var wu = await workUnits.GetAsync(Str(input, "workUnitId")!, ct).ConfigureAwait(false);
        return wu is null ? ToError("Work unit not found.") : ToJson(wu);
    }

    private async Task<string> WorkUnitCreateAsync(JsonElement input, CancellationToken ct)
    {
        var wu = await orchestrator.CreateWorkUnitAsync(
            Str(input, "goal")!, Str(input, "owner") ?? "studio",
            Str(input, "successCriteria"), cancellationToken: ct).ConfigureAwait(false);
        wu = wu with { BranchId = Str(input, "branchId") ?? wu.BranchId };
        await workUnits.CreateAsync(wu, ct).ConfigureAwait(false);
        return ToJson(new { workUnitId = wu.WorkUnitId, branchId = wu.BranchId });
    }

    private async Task<string> WorkUnitUpdateAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var workUnitId = Str(input, "workUnitId")!;
        var statusStr = Str(input, "status");
        if (statusStr is not null && Enum.TryParse<WorkUnitStatus>(statusStr, true, out var s))
            await workUnits.UpdateStatusAsync(workUnitId, s, sessionId, ct).ConfigureAwait(false);
        var assignedAgent = Str(input, "assignedAgent");
        if (assignedAgent is not null)
            await orchestrator.AssignWorkAsync(workUnitId, assignedAgent, ct).ConfigureAwait(false);
        var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        return ToJson(wu);
    }

    private async Task<string> WorkUnitListAsync(JsonElement input, CancellationToken ct)
    {
        var list = await workUnits.ListAsync(Str(input, "branchId"), ct).ConfigureAwait(false);
        return ToJson(list.Select(w => w.WorkUnitId));
    }

    private async Task<string> TaskCreateAsync(JsonElement input, CancellationToken ct)
    {
        var workUnitId = Str(input, "workUnitId")!;
        var task = new StudioTask(
            Guid.NewGuid().ToString("N"),
            workUnitId,
            Str(input, "title")!,
            Str(input, "description")!,
            StudioTaskStatus.Open,
            null,
            Int(input, "priority") ?? 0);
        var created = await tasks.CreateAsync(task, ct).ConfigureAwait(false);
        await artifactLineage.RecordAsync(new ArtifactRef(
            created.TaskId,
            ArtifactType.Task,
            workUnitId,
            StudioArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            workUnitId,
            null), ct).ConfigureAwait(false);
        return ToJson(new { taskId = created.TaskId });
    }

    private async Task<string> TaskListAsync(JsonElement input, CancellationToken ct)
    {
        var list = await tasks.ListAsync(Str(input, "workUnitId"), ct).ConfigureAwait(false);
        return ToJson(list);
    }

    private async Task<string> TaskUpdateAsync(JsonElement input, CancellationToken ct)
    {
        var taskId = Str(input, "taskId")!;
        var existing = await tasks.GetAsync(taskId, ct).ConfigureAwait(false);
        if (existing is null) return ToError($"Task '{taskId}' not found.");
        var statusStr = Str(input, "status");
        var status = statusStr is not null && Enum.TryParse<StudioTaskStatus>(statusStr, true, out var s)
            ? s : existing.Status;
        var updated = existing with
        {
            Title = Str(input, "title") ?? existing.Title,
            Description = Str(input, "description") ?? existing.Description,
            Status = status,
            Priority = Int(input, "priority") ?? existing.Priority
        };
        var result = await tasks.UpdateAsync(updated, ct).ConfigureAwait(false);
        return ToJson(result);
    }

    private async Task<string> TaskAssignAsync(JsonElement input, CancellationToken ct)
    {
        var assigned = await tasks.AssignAsync(
            Str(input, "taskId")!, Str(input, "agentId")!, ct).ConfigureAwait(false);
        return ToJson(assigned);
    }

    private async Task<string> BranchCreateAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = await branches.CreateBranchAsync(
            Str(input, "name")!, Str(input, "fromBranch"), ct).ConfigureAwait(false);
        return ToJson(new { branchId });
    }

    private async Task<string> BranchListAsync(CancellationToken ct)
    {
        var list = await branches.ListBranchesAsync(ct).ConfigureAwait(false);
        return ToJson(list);
    }

    private async Task<string> BranchStatusAsync(JsonElement input, CancellationToken ct)
    {
        var status = await branches.GetStatusAsync(Str(input, "branchId")!, ct).ConfigureAwait(false);
        return ToJson(status);
    }

    private async Task<string> AgentSpawnAsync(JsonElement input, CancellationToken ct)
    {
        var agentId = await agentControl.SpawnAsync(
            Str(input, "agentType")!, Str(input, "workUnitId")!,
            Str(input, "taskId"), Str(input, "model"), Str(input, "baseUrl"), Str(input, "apiKey"),
            Str(input, "provider"), Str(input, "profileId"), autoReviewProfileId: null, ct).ConfigureAwait(false);
        return ToJson(new { agentId });
    }

    private async Task<string> AgentStatusAsync(JsonElement input, CancellationToken ct)
    {
        var agentId = Str(input, "agentId")!;
        var status = await agentControl.GetStatusAsync(agentId, ct).ConfigureAwait(false);
        return ToJson(new { agentId, status });
    }

    private async Task<string> AgentStopAsync(JsonElement input, CancellationToken ct)
    {
        var agentId = Str(input, "agentId")!;
        await agentControl.StopAsync(agentId, ct).ConfigureAwait(false);
        return ToJson(new { agentId, status = "stopped" });
    }

    private async Task<string> MergeProposeAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var proposalId   = $"MP-{Guid.NewGuid():N}";
        var summary      = Str(input, "summary")!;
        var sourceBranch = Str(input, "sourceBranch")!;
        var targetBranch = Str(input, "targetBranch")!;
        var workUnitId   = Str(input, "workUnitId");
        var agentId      = Str(input, "agentId");

        string? workspaceChanges = null;
        DateTimeOffset? diffGeneratedAt = null;
        try
        {
            workspaceChanges = await fileWorkspace.DiffAsync(sourceBranch, targetBranch, ct).ConfigureAwait(false);
            diffGeneratedAt  = DateTimeOffset.UtcNow;
        }
        catch { /* branch dirs may not exist yet; diff is optional */ }

        var filesTouched = ParseFilesTouched(workspaceChanges);
        if (filesTouched.Count == 0)
        {
            try
            {
                filesTouched = (await fileWorkspace.ListAsync(sourceBranch, ct: ct).ConfigureAwait(false)).ToList();
            }
            catch { /* best-effort */ }
        }

        var proposal = new MergeProposal(
            proposalId,
            sourceBranch,
            targetBranch,
            Str(input, "goal") ?? summary,
            summary,
            Str(input, "changeDescription") ?? summary,
            null, null, null,
            MergeProposalStatus.Draft,
            WorkspaceChanges: workspaceChanges,
            DiffGeneratedAt:  diffGeneratedAt,
            AgentId:          agentId,
            Model:            Str(input, "model"),
            Provider:         Str(input, "provider"),
            SessionId:        sessionId,
            WorkUnitId:       workUnitId,
            FilesTouched:     filesTouched);
        var created = await merge.ProposeAsync(proposal, ct).ConfigureAwait(false);

        if (workUnitId is not null)
        {
            var chain = await artifactLineage.GetChainAsync(workUnitId, ct).ConfigureAwait(false);
            var taskRef = chain.LastOrDefault(a => a.Type == ArtifactType.Task);
            var parentArtifactId = taskRef?.ArtifactId ?? workUnitId;

            await artifactLineage.RecordAsync(new ArtifactRef(
                created.ProposalId,
                ArtifactType.MergeProposal,
                parentArtifactId,
                StudioArtifactStatus.Active,
                DateTimeOffset.UtcNow,
                workUnitId,
                agentId), ct).ConfigureAwait(false);

            if (sessionId is not null)
            {
                await events.AppendAsync(
                    sessionId,
                    workUnitId,
                    ExecutionEventKind.ArtifactProposed,
                    new ArtifactProposedPayload(created.ProposalId, workUnitId, filesTouched),
                    ct: ct).ConfigureAwait(false);
            }

            // Best-effort lifecycle side effect — a proposal being raised means the work unit's
            // execution stage is done. Not worth failing the propose call over an illegal transition
            // (e.g. the legacy direct-spawn path never reaches WorkUnitStatus.Executing).
            try
            {
                await workUnits.UpdateStatusAsync(workUnitId, WorkUnitStatus.Proposed, sessionId, ct).ConfigureAwait(false);
                await workUnits.SetCurrentStageAsync(workUnitId, PipelineStage.Review, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }
            catch (KeyNotFoundException) { }
        }

        return ToJson(new { proposalId = created.ProposalId, status = created.Status.ToString() });
    }

    // The workspace diff is the plain-text format produced by FileSystemWorkspaceService.DiffAsync
    // (10c as-built: not a real git worktree, so no "+++ b/"-style unified diff to parse).
    private static IReadOnlyList<string> ParseFilesTouched(string? workspaceChanges)
    {
        if (string.IsNullOrEmpty(workspaceChanges))
            return [];

        var files = new List<string>();
        foreach (var rawLine in workspaceChanges.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var file =
                line.StartsWith("+++ ADDED: ", StringComparison.Ordinal)   ? line["+++ ADDED: ".Length..] :
                line.StartsWith("~~~ MODIFIED: ", StringComparison.Ordinal) ? line["~~~ MODIFIED: ".Length..] :
                line.StartsWith("--- DELETED: ", StringComparison.Ordinal) ? line["--- DELETED: ".Length..] :
                null;
            if (file is not null)
                files.Add(file);
        }

        return files;
    }

    private async Task<string> MergeValidateAsync(JsonElement input, CancellationToken ct)
    {
        var proposal = await merge.ValidateAsync(Str(input, "proposalId")!, ct).ConfigureAwait(false);
        return ToJson(proposal);
    }

    private async Task<string> MergeReviewAsync(JsonElement input, CancellationToken ct)
    {
        var proposalId = Str(input, "proposalId");
        var decisionStr = Str(input, "decision");
        if (proposalId is null || decisionStr is null)
            return ToError("proposalId and decision are required.");

        if (!Enum.TryParse<MergeProposalStatus>(decisionStr, ignoreCase: true, out var decision) ||
            decision is not (MergeProposalStatus.Approved or MergeProposalStatus.Rejected))
        {
            return ToError("Decision must be 'Approved' or 'Rejected'.");
        }

        var automated = input.TryGetProperty("automated", out var autoEl) &&
                        autoEl.ValueKind == JsonValueKind.True;

        try
        {
            if (automated)
            {
                var verificationResults = Str(input, "verificationResults");
                if (string.IsNullOrWhiteSpace(verificationResults))
                    return ToError("verificationResults is required for automated review.");

                var proposal = await merge
                    .AutomatedReviewAsync(proposalId, decision, verificationResults, Str(input, "reviewerAgentId"), ct)
                    .ConfigureAwait(false);
                return ToJson(proposal);
            }

            var reviewed = await merge.ReviewAsync(proposalId, decision, ct).ConfigureAwait(false);
            return ToJson(reviewed);
        }
        catch (KeyNotFoundException)
        {
            return ToError($"Proposal '{proposalId}' was not found.");
        }
        catch (InvalidOperationException ex)
        {
            return ToError(ex.Message);
        }
    }

    private async Task<string> WorkspaceSummaryAsync(JsonElement input, CancellationToken ct)
    {
        var summary = await workspace.GetSummaryAsync(Str(input, "branchId"), ct).ConfigureAwait(false);
        return ToJson(summary);
    }

    private async Task<string> WorkspaceReadAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = Str(input, "branchId");
        var path     = Str(input, "path");
        if (branchId is null || path is null) return ToError("branchId and path are required.");
        try
        {
            var content = await fileWorkspace.ReadAsync(branchId, path, ct).ConfigureAwait(false);
            return content is null
                ? ToError($"File '{path}' not found in branch '{branchId}'.")
                : ToJson(new { content });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceWriteAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = Str(input, "branchId");
        var path     = Str(input, "path");
        var content  = Str(input, "content");
        if (branchId is null || path is null || content is null) return ToError("branchId, path, and content are required.");

        var scopeError = await CheckFileScopeAsync(branchId, path, ct).ConfigureAwait(false);
        if (scopeError is not null) return scopeError;

        try
        {
            await fileWorkspace.WriteAsync(branchId, path, content, ct).ConfigureAwait(false);
            return ToJson(new { written = true, path });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceDeleteAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = Str(input, "branchId");
        var path     = Str(input, "path");
        if (branchId is null || path is null) return ToError("branchId and path are required.");

        var scopeError = await CheckFileScopeAsync(branchId, path, ct).ConfigureAwait(false);
        if (scopeError is not null) return scopeError;

        try
        {
            await fileWorkspace.DeleteAsync(branchId, path, ct).ConfigureAwait(false);
            return ToJson(new { deleted = true, path });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string?> CheckFileScopeAsync(string branchId, string path, CancellationToken ct)
    {
        var owners = await workUnits.ListAsync(branchId, ct).ConfigureAwait(false);
        var owner = owners.FirstOrDefault();
        if (owner is null || owner.FileScope.Count == 0)
            return null;

        var allowed = await agentWorkspaces
            .ValidateWriteAsync(owner.WorkUnitId, path, owner.FileScope, ct)
            .ConfigureAwait(false);

        return allowed ? null : ToError($"File {path} is outside your declared scope {string.Join(", ", owner.FileScope)}.");
    }

    private async Task<string> WorkspaceListAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = Str(input, "branchId");
        if (branchId is null) return ToError("branchId is required.");
        try
        {
            var files = await fileWorkspace.ListAsync(branchId, Str(input, "path"), ct).ConfigureAwait(false);
            return ToJson(new { files });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceDiffAsync(JsonElement input, CancellationToken ct)
    {
        var sourceBranchId = Str(input, "branchId") ?? Str(input, "sourceBranchId");
        var targetBranchId = Str(input, "targetBranchId");
        if (sourceBranchId is null || targetBranchId is null) return ToError("branchId and targetBranchId are required.");
        try
        {
            var diff = await fileWorkspace.DiffAsync(sourceBranchId, targetBranchId, ct).ConfigureAwait(false);
            return ToJson(new { diff });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceExistsAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = Str(input, "branchId");
        var path     = Str(input, "path");
        if (branchId is null || path is null) return ToError("branchId and path are required.");
        try
        {
            var exists = await fileWorkspace.ExistsAsync(branchId, path, ct).ConfigureAwait(false);
            return ToJson(new { exists });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> SnapshotGetAsync(JsonElement input, CancellationToken ct)
    {
        var snap = await snapshots.GetAsync(
            Str(input, "agentId")!, Str(input, "workUnitId")!, ct).ConfigureAwait(false);
        return ToJson(snap);
    }

    private async Task<string> ProjectionGetAsync(JsonElement input, CancellationToken ct)
    {
        var typeStr  = Str(input, "projectionType") ?? Str(input, "type") ?? "WorkUnit";
        var levelStr = Str(input, "projectionLevel") ?? Str(input, "level") ?? "Normal";
        if (!Enum.TryParse<ProjectionType>(typeStr, ignoreCase: true, out var projType))
            return ToError($"Unknown projectionType '{typeStr}'.");
        if (!Enum.TryParse<ProjectionLevel>(levelStr, ignoreCase: true, out var projLevel))
            return ToError($"Unknown projectionLevel '{levelStr}'.");
        var result = await projections.GetAsync(
            new ProjectionRequest(projType, projLevel, Str(input, "workUnitId"), Str(input, "branchId"), Str(input, "agentId")),
            ct).ConfigureAwait(false);
        return result.DataJson;
    }

    private async Task<string> MergeApplyAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        try
        {
            var result = await merge.ApplyAsync(Str(input, "proposalId")!, ct).ConfigureAwait(false);
            return ToJson(new { proposalId = result.ProposalId, status = result.Status.ToString() });
        }
        catch (KeyNotFoundException ex) { return ToError(ex.Message); }
        catch (InvalidOperationException ex) { return ToError(ex.Message); }
    }

    private async Task<string> SchedulerEnqueueAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var workUnitId = Str(input, "workUnitId");
        var profileId  = Str(input, "profileId");
        if (workUnitId is null || profileId is null)
            return ToError("workUnitId and profileId are required.");

        await scheduler.EnqueueAsync(
            workUnitId,
            profileId,
            taskId:    Str(input, "taskId"),
            model:     Str(input, "model"),
            baseUrl:   Str(input, "baseUrl"),
            apiKey:    Str(input, "apiKey"),
            provider:  Str(input, "provider"),
            sessionId: sessionId,
            ct:        ct).ConfigureAwait(false);

        return ToJson(new { workUnitId, profileId, status = "enqueued" });
    }

    private async Task<string> SchedulerPendingAsync(CancellationToken ct)
    {
        var items = await scheduler.ListPendingAsync(ct).ConfigureAwait(false);
        return ToJson(items);
    }

    private async Task<string> IntentRecordAsync(JsonElement input, CancellationToken ct)
    {
        var workUnitId = Str(input, "workUnitId");
        var targetPath = Str(input, "targetPath");
        if (workUnitId is null || targetPath is null)
            return ToError("workUnitId and targetPath are required.");

        var intent = new ChangeIntent(
            IntentId:          $"CI-{Guid.NewGuid():N}",
            WorkUnitId:        workUnitId,
            IntentType:        Str(input, "intentType") ?? "modify",
            TargetPath:        targetPath,
            RegionDescriptor:  Str(input, "regionDescriptor") ?? string.Empty,
            BaseSnapshotHash:  Str(input, "baseSnapshotHash") ?? string.Empty,
            FilesTouchedHint:  null,
            CreatedAt:         DateTimeOffset.UtcNow);

        await intentGraph.RecordIntentAsync(intent, ct).ConfigureAwait(false);

        return ToJson(new { intentId = intent.IntentId });
    }

    private async Task<string> ArtifactRecordAsync(JsonElement input, CancellationToken ct)
    {
        var unitId = Str(input, "workUnitId");
        var typeStr = Str(input, "type");
        var title = Str(input, "title");
        var body = Str(input, "body");
        if (unitId is null || typeStr is null || title is null || body is null)
            return ToError("workUnitId, type, title, and body are required.");

        if (!Enum.TryParse<ArtifactType>(typeStr, ignoreCase: true, out var artifactType) ||
            artifactType is not (ArtifactType.Research or ArtifactType.Decision or ArtifactType.Constraint))
            return ToError("type must be one of: Research, Decision, Constraint.");

        var artifact = new ArtifactRef(
            $"KA-{Guid.NewGuid():N}",
            artifactType,
            Str(input, "parentArtifactId") ?? unitId,
            StudioArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            unitId,
            null,
            title,
            body);

        var recorded = await artifactLineage.RecordAsync(artifact, ct).ConfigureAwait(false);
        return ToJson(new { artifactId = recorded.ArtifactId });
    }

    private async Task<string> ArtifactQueryAsync(JsonElement input, CancellationToken ct)
    {
        var unitId = Str(input, "workUnitId");
        if (unitId is null)
            return ToError("workUnitId is required.");

        var typeStr = Str(input, "type");
        ArtifactType? filterType = null;
        if (typeStr is not null)
        {
            if (!Enum.TryParse<ArtifactType>(typeStr, ignoreCase: true, out var parsed))
                return ToError($"Unknown artifact type '{typeStr}'.");
            filterType = parsed;
        }

        var keywords = Str(input, "keywords");
        var chain = await CollectChainWithAncestorsAsync(unitId, ct).ConfigureAwait(false);

        var filtered = chain
            .Where(a => filterType is null || a.Type == filterType)
            .Where(a => keywords is null || MatchesKeywords(a, keywords))
            .ToList();

        return ToJson(filtered);
    }

    private async Task<string> ArtifactListAsync(JsonElement input, CancellationToken ct)
    {
        var unitId = Str(input, "workUnitId");
        if (unitId is null)
            return ToError("workUnitId is required.");

        var includeAncestors = Bool(input, "includeAncestors") ?? true;
        var list = includeAncestors
            ? await CollectChainWithAncestorsAsync(unitId, ct).ConfigureAwait(false)
            : await artifactLineage.GetChainAsync(unitId, ct).ConfigureAwait(false);

        return ToJson(list);
    }

    // Walks ParentWorkUnitId root-first, folding in every ancestor's own chain ahead of this
    // work unit's — same inheritance model as ProjectionManager.BuildAgentWorkspaceAsync (10g).
    private async Task<IReadOnlyList<ArtifactRef>> CollectChainWithAncestorsAsync(string workUnitId, CancellationToken ct)
    {
        var ancestorIds = new List<string>();
        var currentId = workUnitId;
        while (currentId is not null)
        {
            ancestorIds.Add(currentId);
            var unit = await workUnits.GetAsync(currentId, ct).ConfigureAwait(false);
            currentId = unit?.ParentWorkUnitId;
        }

        var seen = new HashSet<string>();
        var combined = new List<ArtifactRef>();
        foreach (var id in Enumerable.Reverse(ancestorIds))
        {
            var chain = await artifactLineage.GetChainAsync(id, ct).ConfigureAwait(false);
            foreach (var artifact in chain)
                if (seen.Add(artifact.ArtifactId))
                    combined.Add(artifact);
        }

        return combined;
    }

    private static bool MatchesKeywords(ArtifactRef artifact, string keywords) =>
        keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(k => $"{artifact.Title} {artifact.Body}".Contains(k, StringComparison.OrdinalIgnoreCase));

    private static string? Str(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static int? Int(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && p.TryGetInt32(out var v) ? v : null;

    private static bool? Bool(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && (p.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? p.GetBoolean() : null;

    private static string ToJson(object? data) => JsonSerializer.Serialize(data);
    private static string ToError(string message) => JsonSerializer.Serialize(new { error = message });
}
