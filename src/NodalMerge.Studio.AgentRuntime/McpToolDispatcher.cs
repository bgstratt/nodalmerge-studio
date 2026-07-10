using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class McpToolDispatcher(
    IWorkUnitService workUnits,
    IOrchestratorService orchestrator,
    IWorkUnitCommandService workUnitCommands,
    ITaskCommandService taskCommands,
    IBranchService branches,
    IMergeService merge,
    IMergeCommandService mergeCommands,
    IWorkspaceService workspace,
    IFileWorkspaceService fileWorkspace,
    ISnapshotService snapshots,
    IAgentControlService agentControl,
    IArtifactCommandService artifactCommands,
    IProjectionManager projections,
    ISchedulerCommandService scheduler,
    IClarificationCommandService clarifications,
    IExecutionEventStream events,
    IIntentGraphService intentGraph,
    IDocFetchCommandService docFetch,
    IWorkspaceExecutionCommandService executionCommands,
    IWorkspaceProfileService workspaceProfiles,
    IFileLeaseService fileLease,
    IWorkspaceSemanticNavigationService semanticNavigation,
    NodalMerge.Studio.Storage.IRepositoryRegistryService repositories,
    IProjectionSnapshotService projectionSnapshots,
    IRepositorySyncService repositorySync,
    NodalMerge.Studio.Storage.WorkspaceOptions workspaceOptions,
    // Phase 12 — optional CAS-backed repository tools
    IRepositorySnapshotService? repoSnapshots = null,
    NodalMerge.Host.Abstractions.Providers.IBlobStoreProvider? repoBlobStore = null,
    IRepositoryOpService? repoOpEmitter = null)
{
    // Phase 9g — read-before-write enforcement. McpToolDispatcher is registered as a singleton
    // (InMemoryAgentRuntimeService.cs) shared across every agent/run, so this cache lives here
    // rather than per-loop: once any agent reads a path in a branch, that path stays "seen" for
    // the rest of the branch's life. Per-(branch, path), not per-agent-session — the goal (don't
    // blindly overwrite content nobody looked at) doesn't need session granularity, and threading
    // agent/session identity through every workspace call for this would be unjustified complexity.
    private readonly ConcurrentDictionary<string, byte> _readPaths = new();

    private static string ReadCacheKey(string branchId, string path) => $"{branchId}:{path}";

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
                McpToolNames.WorkUnitDependents => await WorkUnitDependentsAsync(input, ct),
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
                McpToolNames.WorkspaceStatus  => await WorkspaceStatusAsync(input, ct),
                McpToolNames.DocFetch         => await DocFetchAsync(input, ct, sessionId),
                McpToolNames.SnapshotGet      => await SnapshotGetAsync(input, ct),
                McpToolNames.ProjectionGet    => await ProjectionGetAsync(input, ct, sessionId),
                McpToolNames.MergeApply       => await MergeApplyAsync(input, ct, sessionId),
                McpToolNames.WorkspaceRead    => await WorkspaceReadAsync(input, ct, sessionId),
                McpToolNames.WorkspaceReadMany => await WorkspaceReadManyAsync(input, ct, sessionId),
                McpToolNames.WorkspaceWrite   => await WorkspaceWriteAsync(input, ct, sessionId),
                McpToolNames.WorkspaceDelete  => await WorkspaceDeleteAsync(input, ct, sessionId),
                McpToolNames.WorkspaceList    => await WorkspaceListAsync(input, ct),
                McpToolNames.WorkspaceSearch  => await WorkspaceSearchAsync(input, ct, sessionId),
                McpToolNames.WorkspaceSymbolDefinition => await WorkspaceSymbolDefinitionAsync(input, ct),
                McpToolNames.WorkspaceSymbolReferences => await WorkspaceSymbolReferencesAsync(input, ct),
                McpToolNames.WorkspaceSymbolImplementation => await WorkspaceSymbolImplementationAsync(input, ct),
                McpToolNames.WorkspaceReplace => await WorkspaceReplaceAsync(input, ct, sessionId),
                McpToolNames.WorkspaceDiff    => await WorkspaceDiffAsync(input, ct),
                McpToolNames.WorkspaceExists  => await WorkspaceExistsAsync(input, ct),
                McpToolNames.SchedulerEnqueue => await SchedulerEnqueueAsync(input, ct, sessionId),
                McpToolNames.SchedulerPending => await SchedulerPendingAsync(ct),
                McpToolNames.ClarificationRequest => await ClarificationRequestAsync(input, ct, sessionId),
                McpToolNames.IntentRecord     => await IntentRecordAsync(input, ct),
                McpToolNames.ArtifactRecord     => await ArtifactRecordAsync(input, ct),
                McpToolNames.ArtifactRecordPlan => await ArtifactRecordPlanAsync(input, ct),
                McpToolNames.ArtifactQuery      => await ArtifactQueryAsync(input, ct),
                McpToolNames.ArtifactList       => await ArtifactListAsync(input, ct),
                McpToolNames.ArtifactInvalidate => await ArtifactInvalidateAsync(input, ct, sessionId),
                McpToolNames.ProjectionSnapshotCapture => await ProjectionSnapshotCaptureAsync(input, ct),
                McpToolNames.ProjectionSnapshotGet     => await ProjectionSnapshotGetAsync(input, ct),
                McpToolNames.ProjectionSnapshotList    => await ProjectionSnapshotListAsync(input, ct),
                McpToolNames.ProjectionStaleCheck      => await ProjectionStaleCheckAsync(input, ct),
                McpToolNames.ProjectionCompare         => await ProjectionCompareAsync(input, ct),
                McpToolNames.ProjectionMaterialize     => await ProjectionMaterializeAsync(input, ct),
                McpToolNames.WorkspaceBuild   => await WorkspaceBuildAsync(input, ct),
                McpToolNames.WorkspaceTest    => await WorkspaceTestAsync(input, ct),
                McpToolNames.WorkspaceExec    => await WorkspaceExecAsync(input, ct),
                McpToolNames.WorkspaceRun     => await WorkspaceRunAsync(input, ct),
                McpToolNames.WorkspaceRunStop => await WorkspaceRunStopAsync(input, ct),
                McpToolNames.WorkspaceExecStatus => await WorkspaceExecStatusAsync(input, ct),
                McpToolNames.WorkspacePath    => await WorkspacePathAsync(input, ct),
                McpToolNames.WorkspaceProfileGet    => await WorkspaceProfileGetAsync(input, ct),
                McpToolNames.WorkspaceProfileRescan => await WorkspaceProfileRescanAsync(input, ct),
                McpToolNames.WorkspaceSwitch  => await WorkspaceSwitchAsync(input, ct),
                McpToolNames.WorkspaceCapabilities => WorkspaceCapabilities(),
                McpToolNames.RepositoryRegister => await RepositoryRegisterAsync(input, ct),
                McpToolNames.RepositoryList     => await RepositoryListAsync(ct),
                McpToolNames.RepositoryCreate   => await RepositoryCreateAsync(input, ct),
                McpToolNames.RepositoryClone    => await RepositoryCloneAsync(input, ct),
                McpToolNames.RepositoryReadFile  => await RepositoryReadFileAsync(input, ct),
                McpToolNames.RepositoryListFiles => await RepositoryListFilesAsync(input, ct),
                // Phase 12 — CAS-backed repository tools
                McpToolNames.RepositoryBlobList   => await RepositoryBlobListAsync(input, ct),
                McpToolNames.RepositoryBlobRead   => await RepositoryBlobReadAsync(input, ct),
                McpToolNames.RepositoryBlobWrite  => await RepositoryBlobWriteAsync(input, ct),
                McpToolNames.RepositoryBlobSearch => await RepositoryBlobSearchAsync(input, ct),
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
        var wu = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand(
                Str(input, "goal")!,
                Str(input, "owner") ?? "studio",
                Str(input, "branchId"),
                Str(input, "successCriteria"),
                RepositoryPath: Str(input, "repositoryPath"),
                ParentWorkUnitId: Str(input, "parentWorkUnitId"),
                DependsOn: StrArray(input, "dependsOn"),
                FileScope: StrArray(input, "fileScope"),
                RepositoryId: Str(input, "repositoryId"),
                ReferenceFiles: FileReferenceArray(input, "referenceFiles")),
            ct).ConfigureAwait(false);
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
        var fileScope = StrArray(input, "fileScope");
        if (fileScope is not null)
            await workUnits.SetFileScopeAsync(workUnitId, fileScope, sessionId, ct).ConfigureAwait(false);
        var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        return ToJson(wu);
    }

    private async Task<string> WorkUnitListAsync(JsonElement input, CancellationToken ct)
    {
        var list = await workUnits.ListAsync(Str(input, "branchId"), ct).ConfigureAwait(false);
        return ToJson(list.Select(w => w.WorkUnitId));
    }

    private async Task<string> WorkUnitDependentsAsync(JsonElement input, CancellationToken ct)
    {
        var list = await workUnits.GetDependentsAsync(Str(input, "workUnitId")!, ct).ConfigureAwait(false);
        return ToJson(list.Select(w => w.WorkUnitId));
    }

    private async Task<string> RepositoryRegisterAsync(JsonElement input, CancellationToken ct)
    {
        var repository = await repositories.RegisterAsync(
            Str(input, "path")!, Str(input, "label"), ct).ConfigureAwait(false);
        return ToJson(new { repositoryId = repository.RepositoryId, path = repository.Path });
    }

    private async Task<string> RepositoryListAsync(CancellationToken ct)
    {
        var list = await repositories.ListAsync(ct).ConfigureAwait(false);
        return ToJson(list);
    }

    private async Task<string> RepositoryCreateAsync(JsonElement input, CancellationToken ct)
    {
        var repository = await repositories.CreateAsync(
            Str(input, "path")!, Str(input, "label"), ct).ConfigureAwait(false);
        return ToJson(new { repositoryId = repository.RepositoryId, path = repository.Path });
    }

    private async Task<string> RepositoryCloneAsync(JsonElement input, CancellationToken ct)
    {
        var repository = await repositories.CloneAsync(
            Str(input, "url")!, Str(input, "targetPath")!, Str(input, "label"), ct).ConfigureAwait(false);
        return ToJson(new { repositoryId = repository.RepositoryId, path = repository.Path });
    }

    private async Task<string> RepositoryReadFileAsync(JsonElement input, CancellationToken ct)
    {
        var repositoryId = Str(input, "repositoryId")!;
        var path = Str(input, "path")!;
        var content = await repositories.ReadFileAsync(repositoryId, path, ct).ConfigureAwait(false);
        return content is null
            ? ToError($"File '{path}' not found in repository '{repositoryId}'.")
            : ToJson(new { content });
    }

    private async Task<string> RepositoryListFilesAsync(JsonElement input, CancellationToken ct)
    {
        var files = await repositories.ListFilesAsync(
            Str(input, "repositoryId")!, Str(input, "subPath"), Str(input, "pattern"), ct).ConfigureAwait(false);
        return ToJson(new { files });
    }

    // ── Phase 12 — CAS-backed repository tools ───────────────────────────────
    // These tools operate against the snapshot's TreeEntries + IBlobStoreProvider directly,
    // with no filesystem materialization. They are additive alongside the existing
    // nm_v1_repository_read_file / nm_v1_repository_list_files (filesystem-backed) tools.

    private async Task<string> RepositoryBlobListAsync(JsonElement input, CancellationToken ct)
    {
        if (repoSnapshots is null) return ToError("Repository snapshot service is not configured.");
        var repositoryId = Str(input, "repositoryId")!;
        var scope = Str(input, "scope");
        var snapshot = await repoSnapshots.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
        if (snapshot?.TreeEntries is null)
            return ToError("No snapshot with tree entries found for this repository.");
        var paths = snapshot.TreeEntries.Keys
            .Where(p => scope is null || p.StartsWith(scope, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        return ToJson(new { paths, count = paths.Count, snapshotId = snapshot.SnapshotId });
    }

    private async Task<string> RepositoryBlobReadAsync(JsonElement input, CancellationToken ct)
    {
        if (repoSnapshots is null) return ToError("Repository snapshot service is not configured.");
        if (repoBlobStore is null) return ToError("Blob store is not configured.");
        var repositoryId = Str(input, "repositoryId")!;
        var path = Str(input, "path")!;
        var snapshot = await repoSnapshots.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
        if (snapshot?.TreeEntries is null)
            return ToError("No snapshot with tree entries found for this repository.");
        if (!snapshot.TreeEntries.TryGetValue(path, out var blobId))
            return ToError($"Path '{path}' not found in the latest snapshot.");
        var result = await repoBlobStore.TryGetBlobAsync(blobId, ct).ConfigureAwait(false);
        if (!result.Found || result.Bytes is null)
            return ToError($"Blob {blobId} is referenced in the snapshot but not found in CAS.");
        return ToJson(new
        {
            path, blobId,
            content = System.Text.Encoding.UTF8.GetString(result.Bytes),
            contentType = result.ContentType
        });
    }

    private async Task<string> RepositoryBlobWriteAsync(JsonElement input, CancellationToken ct)
    {
        if (repoSnapshots is null) return ToError("Repository snapshot service is not configured.");
        if (repoBlobStore is null) return ToError("Blob store is not configured.");
        if (repoOpEmitter is null) return ToError("Repository op service is not configured.");
        var repositoryId = Str(input, "repositoryId")!;
        var path = Str(input, "path")!;
        var content = Str(input, "content")!;
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var newBlobId = NodalMerge.Studio.Storage.BlobHasher.ComputeHash(bytes);
        await repoBlobStore.PutBlobAsync(newBlobId, bytes, "text/plain", ct).ConfigureAwait(false);
        var snapshot = await repoSnapshots.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
        var oldBlobId = snapshot?.TreeEntries?.GetValueOrDefault(path);
        var kind = oldBlobId is null ? OperationType.Add : OperationType.Replace;
        var op = new RepositoryOperation(
            OperationId:      $"op-{Guid.NewGuid():N}",
            RepositoryId:     repositoryId,
            ParentSnapshotId: snapshot?.SnapshotId,
            Kind:             kind,
            Path:             path,
            Timestamp:        DateTimeOffset.UtcNow,
            WorkUnitId:       Str(input, "workUnitId"),
            OldBlobId:        oldBlobId,
            NewBlobId:        newBlobId,
            Reason:           Str(input, "reason"));
        await repoOpEmitter.EmitAsync(op, ct).ConfigureAwait(false);
        return ToJson(new { path, blobId = newBlobId, kind = kind.ToString() });
    }

    private async Task<string> RepositoryBlobSearchAsync(JsonElement input, CancellationToken ct)
    {
        if (repoSnapshots is null) return ToError("Repository snapshot service is not configured.");
        if (repoBlobStore is null) return ToError("Blob store is not configured.");
        var repositoryId = Str(input, "repositoryId")!;
        var query = Str(input, "query")!;
        var scope = Str(input, "scope");
        var maxResults = Int(input, "maxResults") ?? 20;
        var snapshot = await repoSnapshots.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
        if (snapshot?.TreeEntries is null)
            return ToError("No snapshot with tree entries found for this repository.");
        var matches = new List<object>();
        foreach (var (path, blobId) in snapshot.TreeEntries)
        {
            if (matches.Count >= maxResults) break;
            if (scope is not null && !path.StartsWith(scope, StringComparison.Ordinal)) continue;
            var result = await repoBlobStore.TryGetBlobAsync(blobId, ct).ConfigureAwait(false);
            if (!result.Found || result.Bytes is null) continue;
            string text;
            try { text = System.Text.Encoding.UTF8.GetString(result.Bytes); }
            catch { continue; }
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length && matches.Count < maxResults; i++)
            {
                if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add(new { path, lineNumber = i + 1, snippet = lines[i].Trim() });
            }
        }
        return ToJson(new { query, matches, count = matches.Count });
    }

    private async Task<string> TaskCreateAsync(JsonElement input, CancellationToken ct)
    {
        var task = await taskCommands.CreateAsync(
            new TaskCreateCommand(
                Str(input, "workUnitId")!,
                Str(input, "title")!,
                Str(input, "description")!,
                Int(input, "priority") ?? 0),
            ct).ConfigureAwait(false);
        return ToJson(new { taskId = task.TaskId });
    }

    private async Task<string> TaskListAsync(JsonElement input, CancellationToken ct)
    {
        var list = await taskCommands.ListAsync(Str(input, "workUnitId"), ct).ConfigureAwait(false);
        return ToJson(list);
    }

    private async Task<string> TaskUpdateAsync(JsonElement input, CancellationToken ct)
    {
        var taskId = Str(input, "taskId")!;
        try
        {
            var result = await taskCommands.UpdateAsync(
                taskId, Str(input, "title"), Str(input, "description"),
                Str(input, "status"), Int(input, "priority"), ct).ConfigureAwait(false);
            return ToJson(result);
        }
        catch (KeyNotFoundException) { return ToError($"Task '{taskId}' not found."); }
        catch (InvalidOperationException ex) { return ToError(ex.Message); }
    }

    private async Task<string> TaskAssignAsync(JsonElement input, CancellationToken ct)
    {
        try
        {
            var assigned = await taskCommands.AssignAsync(
                Str(input, "taskId")!, Str(input, "agentId")!, ct).ConfigureAwait(false);
            return ToJson(assigned);
        }
        catch (KeyNotFoundException) { return ToError($"Task '{Str(input, "taskId")}' not found."); }
        catch (InvalidOperationException ex) { return ToError(ex.Message); }
    }

    private async Task<string> BranchCreateAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = await branches.CreateBranchAsync(
            Str(input, "name")!, Str(input, "fromBranch"), cancellationToken: ct).ConfigureAwait(false);
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
            Str(input, "provider"), Str(input, "profileId"), Str(input, "autoReviewProfileId"),
            enabledDomainAgents: StrArray(input, "enabledDomainAgents"),
            credentialRef: Str(input, "credentialRef"),
            cancellationToken: ct).ConfigureAwait(false);
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
        // Same class of bug as the workspace.* tools: trust WorkUnit.BranchId over whatever the
        // agent typed as sourceBranch when a workUnitId is given, so a misremembered branch string
        // can't produce a proposal that diffs an empty/wrong directory while reading as legitimate.
        var resolvedSourceBranch = await ResolveBranchIdAsync(
            Str(input, "workUnitId"), Str(input, "sourceBranch"), ct).ConfigureAwait(false) ?? Str(input, "sourceBranch")!;

        var created = await mergeCommands.ProposeAsync(
            resolvedSourceBranch,
            Str(input, "targetBranch")!,
            Str(input, "summary")!,
            Str(input, "goal"),
            Str(input, "changeDescription"),
            workUnitId: Str(input, "workUnitId"),
            agentId:    Str(input, "agentId"),
            model:      Str(input, "model"),
            provider:   Str(input, "provider"),
            sessionId:  sessionId,
            noFileChangesJustification: Str(input, "noFileChangesJustification"),
            cancellationToken: ct).ConfigureAwait(false);

        return ToJson(new {
            proposalId = created.ProposalId,
            status = created.Status.ToString(),
            reason = created.Status == MergeProposalStatus.Rejected ? created.ChangeDescription : null,
        });
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
                    .AutomatedReviewAsync(
                        proposalId, decision, verificationResults, Str(input, "reviewerAgentId"),
                        StrArray(input, "consideredArtifactIds"), ct)
                    .ConfigureAwait(false);
                return ToJson(proposal);
            }

            // Non-automated review through the MCP surface — the caller may still be an agent
            // (or an external MCP client acting for a person), so honor an explicit reviewedBy
            // and only fall back to ReviewAsync's own "user" default when none is given.
            //
            // Live-observed failure mode: the dedicated reviewer agent's own tool schema requires
            // verificationResults but not automated=true — an LLM that fills in real reasoning
            // ("missing tests") but forgets the automated flag falls straight into this branch,
            // which used to silently discard verificationResults entirely. The saved proposal ended
            // up with reviewNotes=null, so the retried worker had zero information about why it was
            // rejected and just guessed (in the observed case, guessing so wrong it re-did all 4
            // sibling tasks at once and got rejected again for scope). Falling back to
            // verificationResults as notes closes that hole regardless of which flag the caller
            // forgot; requiring some reason on Reject closes it for good.
            var notes = Str(input, "notes") ?? Str(input, "verificationResults");
            if (decision == MergeProposalStatus.Rejected && string.IsNullOrWhiteSpace(notes))
                return ToError("notes (or verificationResults) is required when rejecting a proposal — explain what needs to change so the retry has context.");

            var reviewed = await merge.ReviewAsync(
                proposalId, decision, notes, reviewedBy: Str(input, "reviewedBy"), cancellationToken: ct).ConfigureAwait(false);
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

    private async Task<string> WorkspaceStatusAsync(JsonElement input, CancellationToken ct)
    {
        var status = await workspace.GetStatusAsync(
            Str(input, "branchId"),
            Str(input, "workUnitId"),
            Int(input, "limit") ?? 50,
            Int(input, "offset") ?? 0,
            ct).ConfigureAwait(false);
        return ToJson(status);
    }

    private async Task<string> DocFetchAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var url = Str(input, "url");
        var reason = Str(input, "reason");
        var workUnitId = Str(input, "workUnitId");
        if (url is null || reason is null || workUnitId is null)
            return ToError("url, reason, and workUnitId are required.");

        try
        {
            var fetched = await docFetch.FetchAsync(url, reason, workUnitId, sessionId, ct).ConfigureAwait(false);
            return ToJson(fetched);
        }
        catch (ArgumentException ex)
        {
            return ToError(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToError(ex.Message);
        }
    }

    // Slice — workers/planners/reviewers sometimes pass workUnitId (or "work-" + workUnitId) as
    // branchId instead of the actual WorkUnit.BranchId, which silently writes/reads/diffs against
    // an orphan directory that's never attached to the real merge proposal. When the call also
    // carries a workUnitId, trust that and resolve the authoritative branch server-side — the
    // agent-supplied branchId is then only a fallback for calls with no workUnitId at all.
    private async Task<string?> ResolveBranchIdAsync(string? workUnitId, string? fallbackBranchId, CancellationToken ct)
    {
        if (workUnitId is not null)
        {
            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is not null) return wu.BranchId;
        }
        return fallbackBranchId;
    }

    private Task<string?> ResolveBranchIdAsync(JsonElement input, CancellationToken ct) =>
        ResolveBranchIdAsync(Str(input, "workUnitId"), Str(input, "branchId"), ct);

    private async Task<string> WorkspaceReadAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        var path     = Str(input, "path");
        if (branchId is null || path is null) return ToError("branchId (or workUnitId) and path are required.");
        try
        {
            var content = await fileWorkspace.ReadAsync(branchId, path, ct).ConfigureAwait(false);
            if (content is null)
            {
                // Phase 11 on-demand fetch: file may exist in the repository snapshot but wasn't
                // in the work unit's initial FileScope. Try to pull it from CAS before giving up.
                var materialized = await fileWorkspace.MaterializeFileAsync(branchId, path, ct).ConfigureAwait(false);
                if (!materialized)
                    return ToError($"File '{path}' does not exist in this branch or in the repository snapshot. If this is a new file, use nm_v1_workspace_write to create it.");
                content = await fileWorkspace.ReadAsync(branchId, path, ct).ConfigureAwait(false) ?? string.Empty;
            }
            _readPaths[ReadCacheKey(branchId, path)] = 0;
            await RecordWorkspaceReadAsync(sessionId, Str(input, "workUnitId"), [path], ct).ConfigureAwait(false);
            return ToJson(new { content });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceReadManyAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        var paths    = StrArray(input, "paths");
        if (branchId is null) return ToError("branchId (or workUnitId) is required.");
        if (paths is null or { Count: 0 } or { Count: > 50 })
            return ToError("paths must be a non-empty array of at most 50 entries.");

        try
        {
            var files = await fileWorkspace.ReadManyAsync(branchId, paths, ct).ConfigureAwait(false);

            // Phase 11 on-demand fetch: for any missing files, try CAS materialization then re-read.
            var missing = files.Where(f => !f.Found).Select(f => f.Path).ToList();
            if (missing.Count > 0)
            {
                var reMaterialize = new List<string>();
                foreach (var p in missing)
                    if (await fileWorkspace.MaterializeFileAsync(branchId, p, ct).ConfigureAwait(false))
                        reMaterialize.Add(p);

                if (reMaterialize.Count > 0)
                {
                    var reRead = await fileWorkspace.ReadManyAsync(branchId, reMaterialize, ct).ConfigureAwait(false);
                    var reReadMap = reRead.ToDictionary(f => f.Path, StringComparer.Ordinal);
                    files = files.Select(f => reReadMap.TryGetValue(f.Path, out var fresh) ? fresh : f).ToList();
                }
            }

            foreach (var file in files.Where(f => f.Found))
                _readPaths[ReadCacheKey(branchId, file.Path)] = 0;
            await RecordWorkspaceReadAsync(sessionId, Str(input, "workUnitId"), paths, ct).ConfigureAwait(false);
            return ToJson(new { files, branchId });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task RecordWorkspaceReadAsync(
        string? sessionId, string? workUnitId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (sessionId is null) return;
        await events.AppendAsync(
            sessionId, workUnitId, ExecutionEventKind.WorkspaceReadExecuted,
            new WorkspaceReadExecutedPayload(paths), ct: ct).ConfigureAwait(false);
    }

    private async Task<string> WorkspaceWriteAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        var path     = Str(input, "path");
        var content  = Str(input, "content");
        if (branchId is null || path is null || content is null) return ToError("branchId (or workUnitId), path, and content are required.");

        var leaseConflict = await CheckFileLeaseAsync(branchId, path, sessionId, ct).ConfigureAwait(false);
        if (leaseConflict is not null) return leaseConflict;

        try
        {
            // Phase 9g — a file that already exists must be read in this branch before it can be
            // overwritten. New files (don't yet exist) are unaffected — no read is required to
            // create something genuinely new.
            if (!_readPaths.ContainsKey(ReadCacheKey(branchId, path))
                && await fileWorkspace.ExistsAsync(branchId, path, ct).ConfigureAwait(false))
            {
                return ToError(
                    $"File '{path}' already exists with content you haven't read. " +
                    "Call nm_v1_workspace_read first, then write the updated content.");
            }

            await fileWorkspace.WriteAsync(branchId, path, content, ct).ConfigureAwait(false);
            _readPaths[ReadCacheKey(branchId, path)] = 0;
            return ToJson(new { written = true, path, branchId });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceReplaceAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        var path     = Str(input, "path");
        var oldText  = Str(input, "oldText");
        var newText  = Str(input, "newText");
        if (branchId is null || path is null || oldText is null || newText is null)
            return ToError("branchId (or workUnitId), path, oldText, and newText are required.");

        var leaseConflict = await CheckFileLeaseAsync(branchId, path, sessionId, ct).ConfigureAwait(false);
        if (leaseConflict is not null) return leaseConflict;

        var expectedMatches = Int(input, "expectedMatches") ?? 1;
        try
        {
            var result = await fileWorkspace
                .ReplaceAsync(branchId, path, oldText, newText, expectedMatches, ct)
                .ConfigureAwait(false);
            _readPaths[ReadCacheKey(branchId, path)] = 0;
            return ToJson(new
            {
                replaced = true,
                path,
                branchId,
                matches = result.Matches,
                oldLength = result.OldLength,
                newLength = result.NewLength,
                diff = result.Diff,
            });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceDeleteAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        var path     = Str(input, "path");
        if (branchId is null || path is null) return ToError("branchId (or workUnitId) and path are required.");

        var leaseConflict = await CheckFileLeaseAsync(branchId, path, sessionId, ct).ConfigureAwait(false);
        if (leaseConflict is not null) return leaseConflict;

        try
        {
            await fileWorkspace.DeleteAsync(branchId, path, ct).ConfigureAwait(false);
            return ToJson(new { deleted = true, path });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    // Phase 12 — replaces the old static FileScope hard block (CheckFileScopeAsync, removed).
    // A file's lease is held by whichever active sibling first successfully writes/deletes it;
    // anyone else wanting the same path while it's held is queued instead of rejected outright.
    // The branch's owning WorkUnitId (not necessarily the caller's own workUnitId — same
    // resolution CheckFileScopeAsync used) is the identity attributed to the lease, since a
    // branch is 1:1 with the WorkUnit that owns it.
    private async Task<string?> CheckFileLeaseAsync(string branchId, string path, string? sessionId, CancellationToken ct)
    {
        var owners = await workUnits.ListAsync(branchId, ct).ConfigureAwait(false);
        var owner = owners.FirstOrDefault();
        if (owner is null)
            return null;

        var (granted, holderWorkUnitId) = await fileLease
            .TryAcquireOrEnqueueAsync(owner.WorkUnitId, path, ct)
            .ConfigureAwait(false);

        if (granted)
            return null;

        // Phase 14 — records contention for WorkspaceUsageMetricsService's hot-spot query, the
        // evidence future Phase-12 leasing decisions (timeout, region-locking, etc.) hinge on.
        if (sessionId is not null && holderWorkUnitId is not null)
        {
            await events.AppendAsync(
                sessionId, owner.WorkUnitId, ExecutionEventKind.FileLeaseContended,
                new FileLeaseContendedPayload(path, owner.WorkUnitId, holderWorkUnitId),
                ct: ct).ConfigureAwait(false);
        }

        // Distinguishable from a plain ToError(...) string by the "awaitingFileLease" key —
        // WorkerAgentLoop checks for this exact shape to exit its loop on the same turn, without
        // sending the conflict back to the LLM for "please retry" prose first.
        return ToJson(new
        {
            awaitingFileLease = path,
            heldBy = holderWorkUnitId,
            message = $"File '{path}' is currently leased by work unit '{holderWorkUnitId}' until its " +
                "merge is applied. You've been queued and will be automatically resumed once it's " +
                "available — stop now, do not retry.",
        });
    }

    private async Task<string> WorkspaceListAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        if (branchId is null) return ToError("branchId (or workUnitId) is required.");
        try
        {
            var files = await fileWorkspace.ListAsync(branchId, Str(input, "path"), Str(input, "pattern"), ct).ConfigureAwait(false);
            return ToJson(new { files, branchId });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceSearchAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        var query    = Str(input, "query");
        if (branchId is null || query is null) return ToError("branchId (or workUnitId) and query are required.");
        try
        {
            var (matches, truncated) = await fileWorkspace.SearchAsync(
                branchId,
                query,
                subPath: Str(input, "path"),
                filePattern: Str(input, "filePattern"),
                regex: Bool(input, "regex") ?? false,
                caseSensitive: Bool(input, "caseSensitive") ?? false,
                contextLines: Int(input, "contextLines") ?? 3,
                maxResults: Int(input, "maxResults") ?? 200,
                ct: ct).ConfigureAwait(false);

            if (sessionId is not null)
            {
                await events.AppendAsync(
                    sessionId, Str(input, "workUnitId"), ExecutionEventKind.WorkspaceSearchExecuted,
                    new WorkspaceSearchExecutedPayload(
                        query, matches.Select(m => m.Path).Distinct().ToList(), matches.Count, truncated),
                    ct: ct).ConfigureAwait(false);
            }

            return ToJson(new { matches, truncated, branchId });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceDiffAsync(JsonElement input, CancellationToken ct)
    {
        var sourceBranchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false) ?? Str(input, "sourceBranchId");
        var targetBranchId = Str(input, "targetBranchId");
        if (sourceBranchId is null || targetBranchId is null) return ToError("branchId (or workUnitId) and targetBranchId are required.");
        try
        {
            var diff = await fileWorkspace.DiffAsync(sourceBranchId, targetBranchId, ct).ConfigureAwait(false);
            return ToJson(new { diff, sourceBranchId });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private WorkspaceSymbolQuery BuildSymbolQuery(JsonElement input) => new(
        Symbol: Str(input, "symbol"),
        Path: Str(input, "path"),
        Line: Int(input, "line"),
        Column: Int(input, "column"),
        MaxResults: Int(input, "maxResults") ?? 200);

    private async Task<string> WorkspaceSymbolDefinitionAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        if (branchId is null) return ToError("branchId (or workUnitId) is required.");

        var (locations, truncated) = await semanticNavigation
            .FindDefinitionsAsync(branchId, BuildSymbolQuery(input), ct)
            .ConfigureAwait(false);
        return ToJson(new { locations, truncated, branchId });
    }

    private async Task<string> WorkspaceSymbolReferencesAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        if (branchId is null) return ToError("branchId (or workUnitId) is required.");

        var (locations, truncated) = await semanticNavigation
            .FindReferencesAsync(branchId, BuildSymbolQuery(input), ct)
            .ConfigureAwait(false);
        return ToJson(new { locations, truncated, branchId });
    }

    private async Task<string> WorkspaceSymbolImplementationAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        if (branchId is null) return ToError("branchId (or workUnitId) is required.");

        var (locations, truncated) = await semanticNavigation
            .FindImplementationsAsync(branchId, BuildSymbolQuery(input), ct)
            .ConfigureAwait(false);
        return ToJson(new { locations, truncated, branchId });
    }

    private async Task<string> WorkspaceExistsAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        var path     = Str(input, "path");
        if (branchId is null || path is null) return ToError("branchId (or workUnitId) and path are required.");
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

    private async Task<string> ProjectionGetAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var typeStr  = Str(input, "projectionType") ?? Str(input, "type") ?? "WorkUnit";
        var levelStr = Str(input, "projectionLevel") ?? Str(input, "level") ?? "Normal";
        if (!Enum.TryParse<ProjectionType>(typeStr, ignoreCase: true, out var projType))
            return ToError($"Unknown projectionType '{typeStr}'.");
        if (!Enum.TryParse<ProjectionLevel>(levelStr, ignoreCase: true, out var projLevel))
            return ToError($"Unknown projectionLevel '{levelStr}'.");
        var workUnitId = Str(input, "workUnitId");
        var result = await projections.GetAsync(
            new ProjectionRequest(projType, projLevel, workUnitId, Str(input, "branchId"), Str(input, "agentId")),
            ct).ConfigureAwait(false);

        if (sessionId is not null && projType == ProjectionType.AgentWorkspace)
            await RecordSurfacedDomainAgentArtifactsAsync(result.DataJson, workUnitId, Str(input, "agentId"), sessionId, ct)
                .ConfigureAwait(false);

        return result.DataJson;
    }

    // Slice 23 — durable evidence that a domain-agent-authored Constraint/Research artifact (one
    // whose title starts with a DomainAgentRegistry prefix) was actually returned to an agent in a
    // projection it read, not just written into the DAG. Gated on sessionId like every other
    // execution-event append in this dispatcher (e.g. WorkspaceSearchExecuted above) — UI/REST
    // projection reads have no sessionId and intentionally don't count as "surfaced to an agent."
    private async Task RecordSurfacedDomainAgentArtifactsAsync(
        string dataJson, string? workUnitId, string? agentId, string sessionId, CancellationToken ct)
    {
        AgentWorkspaceProjectionPayload? payload;
        try
        {
            // ProjectionManager serializes with JsonSerializerOptions.Web (camelCase) — match that
            // here so deserialization actually binds the properties instead of silently leaving
            // them at their default values.
            payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(dataJson, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return;
        }
        if (payload is null) return;

        var seen = new HashSet<string>();
        foreach (var artifact in payload.Artifacts.Artifacts.Concat(payload.InheritedConstraints))
        {
            if (artifact.Title is not { } title) continue;
            if (!DomainAgentRegistry.All.Any(d => title.StartsWith(d.TitlePrefix, StringComparison.Ordinal))) continue;
            if (!seen.Add(artifact.ArtifactId)) continue;

            await events.AppendAsync(
                sessionId, workUnitId, ExecutionEventKind.ArtifactSurfaced,
                new ArtifactSurfacedPayload(artifact.ArtifactId, agentId, ProjectionType.AgentWorkspace.ToString()),
                ct: ct).ConfigureAwait(false);
        }
    }

    private async Task<string> ProjectionSnapshotCaptureAsync(JsonElement input, CancellationToken ct)
    {
        var snapshot = await projectionSnapshots.CaptureAsync(Str(input, "workUnitId")!, ct).ConfigureAwait(false);
        return ToJson(new { snapshotId = snapshot.SnapshotId, workUnitId = snapshot.WorkUnitId, createdAt = snapshot.CreatedAt });
    }

    private async Task<string> ProjectionSnapshotGetAsync(JsonElement input, CancellationToken ct)
    {
        var snapshot = await projectionSnapshots.GetAsync(Str(input, "snapshotId")!, ct).ConfigureAwait(false);
        return snapshot is null ? ToError("Projection snapshot was not found.") : ToJson(snapshot);
    }

    private async Task<string> ProjectionSnapshotListAsync(JsonElement input, CancellationToken ct)
    {
        var list = await projectionSnapshots.ListAsync(Str(input, "workUnitId"), ct).ConfigureAwait(false);
        return ToJson(list);
    }

    private async Task<string> ProjectionStaleCheckAsync(JsonElement input, CancellationToken ct)
    {
        try
        {
            var staleness = await projectionSnapshots.CheckStaleAsync(Str(input, "snapshotId")!, ct).ConfigureAwait(false);
            return ToJson(staleness);
        }
        catch (KeyNotFoundException ex) { return ToError(ex.Message); }
    }

    private async Task<string> ProjectionCompareAsync(JsonElement input, CancellationToken ct)
    {
        try
        {
            var comparison = await projectionSnapshots.CompareAsync(
                Str(input, "snapshotIdA")!, Str(input, "snapshotIdB")!, ct).ConfigureAwait(false);
            return ToJson(comparison);
        }
        catch (KeyNotFoundException ex) { return ToError(ex.Message); }
    }

    private async Task<string> ProjectionMaterializeAsync(JsonElement input, CancellationToken ct)
    {
        try
        {
            var request = new WorkspaceExecutionRequest(
                Build: Bool(input, "build") ?? true,
                Test: Bool(input, "test") ?? true,
                Lint: Bool(input, "lint") ?? false,
                BuildCommand: Str(input, "buildCommand"),
                TestCommand: Str(input, "testCommand"),
                LintCommand: Str(input, "lintCommand"),
                TimeoutSeconds: Int(input, "timeoutSeconds") ?? 300);
            var materialization = await projectionSnapshots.MaterializeAsync(
                Str(input, "workUnitId")!, request, ct).ConfigureAwait(false);
            return ToJson(materialization);
        }
        catch (KeyNotFoundException ex) { return ToError(ex.Message); }
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

    private async Task<string> ClarificationRequestAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var workUnitId = Str(input, "workUnitId");
        var question = Str(input, "question");
        if (workUnitId is null || question is null)
            return ToError("workUnitId and question are required.");

        var options = StrArray(input, "options");
        var result = await clarifications.RequestAsync(
            workUnitId,
            question,
            context: Str(input, "context"),
            blocking: Bool(input, "blocking") ?? true,
            options: options,
            requestedByAgentId: Str(input, "requestedByAgentId"),
            sessionId: sessionId,
            ct: ct).ConfigureAwait(false);

        return ToJson(new
        {
            requestId = result.RequestId,
            workUnitId = result.WorkUnitId,
            status = result.Status,
            awaitingClarification = result.ParkedAwaitingResponse,
            message = "Clarification requested and execution paused. Stop now and wait for human response."
        });
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

        try
        {
            var recorded = await artifactCommands.RecordAsync(
                unitId, typeStr, title, body, Str(input, "parentArtifactId"), ct).ConfigureAwait(false);
            return ToJson(new { artifactId = recorded.ArtifactId });
        }
        catch (ArgumentException ex)
        {
            return ToError(ex.Message);
        }
    }

    private async Task<string> ArtifactRecordPlanAsync(JsonElement input, CancellationToken ct)
    {
        var workUnitId = Str(input, "workUnitId");
        var planContent = Str(input, "planContent");
        if (workUnitId is null || planContent is null)
            return ToError("workUnitId and planContent are required.");

        var recorded = await artifactCommands.RecordPlanAsync(workUnitId, planContent, ct).ConfigureAwait(false);
        return ToJson(new { artifactId = recorded.ArtifactId, workUnitId });
    }

    private async Task<string> ArtifactQueryAsync(JsonElement input, CancellationToken ct)
    {
        var unitId = Str(input, "workUnitId");
        if (unitId is null)
            return ToError("workUnitId is required.");

        try
        {
            var filtered = await artifactCommands.QueryAsync(
                unitId, Str(input, "type"), Str(input, "keywords"), ct).ConfigureAwait(false);
            return ToJson(filtered);
        }
        catch (ArgumentException ex)
        {
            return ToError(ex.Message);
        }
    }

    private async Task<string> ArtifactListAsync(JsonElement input, CancellationToken ct)
    {
        var unitId = Str(input, "workUnitId");
        if (unitId is null)
            return ToError("workUnitId is required.");

        var includeAncestors = Bool(input, "includeAncestors") ?? true;
        var list = await artifactCommands.ListAsync(unitId, includeAncestors, ct).ConfigureAwait(false);
        return ToJson(list);
    }

    private async Task<string> ArtifactInvalidateAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var invalidated = await artifactCommands.InvalidateAsync(
            Str(input, "artifactId")!, Str(input, "reason")!, sessionId, ct).ConfigureAwait(false);
        return ToJson(invalidated);
    }

    // ── Slice 16d — workspace execution tool handlers ─────────────────────

    private async Task<string> WorkspaceBuildAsync(JsonElement input, CancellationToken ct)
    {
        var result = await executionCommands.BuildAsync(
            Str(input, "branchId")!,
            Str(input, "buildCommand"),
            Int(input, "timeoutSeconds") ?? 300,
            ct,
            Str(input, "rootPath")).ConfigureAwait(false);
        return ToJson(result);
    }

    private async Task<string> WorkspaceTestAsync(JsonElement input, CancellationToken ct)
    {
        var result = await executionCommands.TestAsync(
            Str(input, "branchId")!,
            Str(input, "testCommand"),
            Int(input, "timeoutSeconds") ?? 300,
            ct,
            Str(input, "rootPath")).ConfigureAwait(false);
        return ToJson(result);
    }

    private async Task<string> WorkspaceExecAsync(JsonElement input, CancellationToken ct)
    {
        var request = new WorkspaceExecutionRequest(
            Build: Bool(input, "build") ?? true,
            Test: Bool(input, "test") ?? true,
            Lint: Bool(input, "lint") ?? false,
            BuildCommand: Str(input, "buildCommand"),
            TestCommand: Str(input, "testCommand"),
            LintCommand: Str(input, "lintCommand"),
            TimeoutSeconds: Int(input, "timeoutSeconds") ?? 300);
        var result = await executionCommands.ExecAsync(Str(input, "branchId")!, request, ct).ConfigureAwait(false);
        return ToJson(result);
    }

    private async Task<string> WorkspaceRunAsync(JsonElement input, CancellationToken ct)
    {
        var result = await executionCommands.RunAsync(
            Str(input, "branchId")!,
            rootPath: Str(input, "rootPath"),
            runCommand: Str(input, "runCommand"),
            timeoutSeconds: Int(input, "timeoutSeconds") ?? 120,
            environmentVariables: null,
            ct: ct).ConfigureAwait(false);
        return ToJson(result);
    }

    private async Task<string> WorkspaceRunStopAsync(JsonElement input, CancellationToken ct)
    {
        var stopped = await executionCommands.StopAsync(
            Str(input, "branchId")!,
            pid: Int(input, "pid"),
            rootPath: Str(input, "rootPath"),
            ct: ct).ConfigureAwait(false);
        return ToJson(new { stopped });
    }

    private async Task<string> WorkspaceExecStatusAsync(JsonElement input, CancellationToken ct)
    {
        var result = await executionCommands.GetLatestAsync(Str(input, "branchId")!, ct).ConfigureAwait(false);
        return result is not null ? ToJson(result) : ToError("No execution result found for this branch.");
    }

    private async Task<string> WorkspacePathAsync(JsonElement input, CancellationToken ct)
    {
        var path = await executionCommands.GetBranchPathAsync(Str(input, "branchId")!, ct).ConfigureAwait(false);
        return ToJson(new { branchId = Str(input, "branchId"), workingDirectory = path, exists = path is not null });
    }

    private async Task<string> WorkspaceProfileGetAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        if (branchId is null) return ToError("branchId (or workUnitId) is required.");
        var profile = await workspaceProfiles.GetOrDetectAsync(branchId, ct).ConfigureAwait(false);
        return ToJson(profile);
    }

    private async Task<string> WorkspaceProfileRescanAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = await ResolveBranchIdAsync(input, ct).ConfigureAwait(false);
        if (branchId is null) return ToError("branchId (or workUnitId) is required.");
        var profile = await workspaceProfiles.RescanAsync(branchId, ct).ConfigureAwait(false);
        return ToJson(profile);
    }

    private async Task<string> WorkspaceSwitchAsync(JsonElement input, CancellationToken ct)
    {
        var repositoryId = Str(input, "repositoryId");
        var path = Str(input, "repositoryPath");
        if (repositoryId is not null)
        {
            var repository = await repositories.GetAsync(repositoryId, ct).ConfigureAwait(false);
            if (repository is null) return ToError($"Repository '{repositoryId}' was not found.");
            path = repository.Path;
        }
        if (string.IsNullOrWhiteSpace(path))
            return ToError("Either repositoryId or repositoryPath is required.");

        var branchId = Str(input, "branchId") ?? "main";
        var pending = await repositorySync.SyncBranchFromRepositoryAsync(
            branchId, path, SyncTrigger.ManualRefresh, ct).ConfigureAwait(false);
        workspaceOptions.SeedRepositoryPath = path;

        return ToJson(new { branchId, repositoryPath = path, sync = pending });
    }

    private string WorkspaceCapabilities() =>
        ToJson(NodalMerge.Studio.Storage.WorkspaceCapabilityResolver.Resolve(workspaceOptions));

    private static string? Str(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static IReadOnlyList<string>? StrArray(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : null;

    private static IReadOnlyList<FileReferenceV1>? FileReferenceArray(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(e => new FileReferenceV1(
                e.GetProperty("repositoryId").GetString()!,
                e.GetProperty("path").GetString()!,
                e.TryGetProperty("note", out var n) ? n.GetString() : null)).ToList()
            : null;

    private static int? Int(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && p.TryGetInt32(out var v) ? v : null;

    private static bool? Bool(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && (p.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? p.GetBoolean() : null;

    private static string ToJson(object? data) => JsonSerializer.Serialize(data);
    private static string ToError(string message) => JsonSerializer.Serialize(new { error = message });
}
