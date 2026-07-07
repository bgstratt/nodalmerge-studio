using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Orchestrator;

public sealed class FanOutService : IFanOutService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Slice 13g — two concurrent fan-out calls for the same parent (an explicit caller racing
    // the orchestrator loop's own post-turn fan-out, see LlmProfileSelectionTests.cs's comment on
    // the workaround this removes the need for) could each read the same pre-creation snapshot of
    // existing children and independently decide a plan slice has no child yet, creating two. One
    // semaphore per parent work unit serializes the read-children/create-children/enqueue section
    // below so the second caller always sees the first caller's children once it gets its turn.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _parentGates = new();

    private readonly IWorkUnitService _workUnits;
    private readonly IOrchestratorService _orchestrator;
    private readonly IArtifactLineageService _artifacts;
    private readonly IFileWorkspaceService _fileWorkspace;
    private readonly IWorkScheduler _scheduler;
    private readonly ITaskService _tasks;
    private readonly IAgentControlService _agentControl;
    private readonly IProfileSelectionService _profileSelection;
    private readonly IOrchestrationDecisionLogService _decisionLog;
    private readonly IPolicyGateService _policyGate;
    private readonly IAgentProfileService _agentProfiles;
    private readonly IMergeService _merge;

    public FanOutService(
        IWorkUnitService workUnits,
        IOrchestratorService orchestrator,
        IArtifactLineageService artifacts,
        IFileWorkspaceService fileWorkspace,
        IWorkScheduler scheduler,
        ITaskService tasks,
        IAgentControlService agentControl,
        IProfileSelectionService profileSelection,
        IOrchestrationDecisionLogService decisionLog,
        IPolicyGateService policyGate,
        IAgentProfileService agentProfiles,
        IMergeService merge)
    {
        _workUnits         = workUnits;
        _orchestrator      = orchestrator;
        _artifacts         = artifacts;
        _fileWorkspace     = fileWorkspace;
        _scheduler         = scheduler;
        _tasks             = tasks;
        _agentControl      = agentControl;
        _profileSelection  = profileSelection;
        _decisionLog       = decisionLog;
        _agentProfiles     = agentProfiles;
        _policyGate        = policyGate;
        _merge             = merge;
    }

    public Task<FanOutResult> TryFanOutFromPlanAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken ct = default) =>
        ProcessAsync(parentWorkUnitId, createChildren: true, sessionId, ct);

    public Task<FanOutResult> TryEnqueueReadyDependentsAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken ct = default) =>
        ProcessAsync(parentWorkUnitId, createChildren: false, sessionId, ct);

    private async Task<FanOutResult> ProcessAsync(
        string parentWorkUnitId,
        bool createChildren,
        string? sessionId,
        CancellationToken ct)
    {
        var actions = new List<FanOutAction>();
        var enqueued = new List<string>();

        var parent = await _workUnits.GetAsync(parentWorkUnitId, ct).ConfigureAwait(false);
        if (parent is null)
            return new FanOutResult(actions, enqueued);

        var planContent = await ReadPlanFromArtifactAsync(parent.WorkUnitId, ct).ConfigureAwait(false);
        if (planContent is null)
            return new FanOutResult(actions, enqueued);

        PlanDocument? plan;
        try
        {
            plan = JsonSerializer.Deserialize<PlanDocument>(planContent, JsonOpts);
        }
        catch (JsonException)
        {
            return new FanOutResult(actions, enqueued);
        }

        if (plan is null || plan.Slices.Count == 0)
            return new FanOutResult(actions, enqueued);

        var gate = _parentGates.GetOrAdd(parentWorkUnitId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Built inside the gate so a caller that had to wait sees every child the previous
            // holder just created, not a snapshot taken before either acquired it.
            var sliceIdToWorkUnitId = await BuildSliceMapAsync(parent.WorkUnitId, ct).ConfigureAwait(false);

            if (createChildren)
            {
                var created = await EnsureChildWorkUnitsAsync(parent, plan, sliceIdToWorkUnitId, ct).ConfigureAwait(false);
                if (created)
                    actions.Add(FanOutAction.ChildrenCreated);
            }

            var creds = _agentControl.GetCredentialsForStage(parentWorkUnitId, PipelineStage.Execute)
                ?? _agentControl.GetOrchestratorCredentials(parentWorkUnitId);
            var children = await _workUnits.GetChildrenAsync(parentWorkUnitId, ct).ConfigureAwait(false);
            foreach (var child in children)
            {
                if (!await IsReadyToEnqueueAsync(child, ct).ConfigureAwait(false))
                    continue;

                await RefreshBranchFromDependenciesAsync(child, ct).ConfigureAwait(false);

                if (await EnqueueChildWorkerAsync(child, parentWorkUnitId, creds, sessionId, ct).ConfigureAwait(false))
                {
                    actions.Add(FanOutAction.ChildEnqueued);
                    enqueued.Add(child.WorkUnitId);
                }
            }
        }
        finally
        {
            gate.Release();
        }

        return new FanOutResult(actions, enqueued);
    }

    private async Task<string?> ReadPlanFromArtifactAsync(string workUnitId, CancellationToken ct)
    {
        var chain = await _artifacts.GetChainAsync(workUnitId, ct).ConfigureAwait(false);
        var planArtifact = chain.LastOrDefault(a => a.Type == ArtifactType.Plan);
        if (planArtifact?.Body is not null)
            return planArtifact.Body;

        // Fallback: the planner may have written plan.json directly to the workspace
        // branch (e.g. if its AllowedTools was missing ArtifactRecordPlan). Read it
        // from the orchestrator's own branch so fan-out can still proceed.
        var parent = await _workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        if (parent is not null)
        {
            try
            {
                var fileContent = await _fileWorkspace.ReadAsync(parent.BranchId, "plan.json", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fileContent))
                    return fileContent;
            }
            catch (FileNotFoundException) { }
        }

        return null;
    }

    private async Task<Dictionary<string, string>> BuildSliceMapAsync(string parentWorkUnitId, CancellationToken ct)
    {
        var children = await _workUnits.GetChildrenAsync(parentWorkUnitId, ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in children)
        {
            var sliceId = child.FanOutInfo?.SliceId;
            if (!string.IsNullOrEmpty(sliceId))
            {
                map[sliceId] = child.WorkUnitId;
            }
        }

        return map;
    }

    private async Task<bool> EnsureChildWorkUnitsAsync(
        WorkUnit parent,
        PlanDocument plan,
        Dictionary<string, string> sliceIdToWorkUnitId,
        CancellationToken ct)
    {
        var created = false;
        var remaining = new HashSet<string>(plan.Slices.Select(s => s.SliceId), StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            var progressed = false;
            foreach (var slice in plan.Slices.Where(s => remaining.Contains(s.SliceId)))
            {
                if (sliceIdToWorkUnitId.ContainsKey(slice.SliceId))
                {
                    remaining.Remove(slice.SliceId);
                    progressed = true;
                    continue;
                }

                if (slice.DependsOn.Any(dep => !sliceIdToWorkUnitId.ContainsKey(dep)))
                    continue;

                var resolvedDeps = slice.DependsOn
                    .Select(dep => sliceIdToWorkUnitId[dep])
                    .ToList();

                // Bug fix — a fanned-out child previously always got CreateWorkUnitAsync's own
                // default (ReviewPolicy.HumanRequired), regardless of what the parent goal's
                // ReviewPolicy/BypassPromotionBranch were actually set to (e.g. via the Goal
                // Workspace "Task Review" radio button) — reviewPolicy/bypassPromotionBranch
                // were simply never passed here. A child slice should inherit its parent's chosen
                // task review policy/merge target, not silently revert to the human-required
                // default. Children never need WorkspaceReviewPolicy — that field only ever gates
                // the top-level goal's own apply into the real on-disk repo, and a child is never
                // top-level.
                var child = await _orchestrator.CreateWorkUnitAsync(
                    slice.Goal,
                    parent.Owner,
                    parentWorkUnitId: parent.WorkUnitId,
                    dependsOn: resolvedDeps,
                    fileScope: slice.FileScope,
                    seedFromBranchId: parent.BranchId,
                    sliceId: slice.SliceId,
                    taskReviewPolicy: parent.TaskReviewPolicy,
                    taskReviewHybridTimeoutMinutes: parent.TaskReviewHybridTimeoutMinutes,
                    bypassPromotionBranch: parent.BypassPromotionBranch,
                    cancellationToken: ct).ConfigureAwait(false);

                sliceIdToWorkUnitId[slice.SliceId] = child.WorkUnitId;
                remaining.Remove(slice.SliceId);
                created = true;
                progressed = true;
            }

            if (!progressed)
                break;
        }

        return created;
    }

    private async Task<bool> IsReadyToEnqueueAsync(WorkUnit child, CancellationToken ct)
    {
        if (child.Status is not WorkUnitStatus.Created)
            return false;

        foreach (var depId in child.DependsOn)
        {
            var dep = await _workUnits.GetAsync(depId, ct).ConfigureAwait(false);
            if (dep is null)
                return false;

            // Phase 12 — Proposed only means a proposal exists and is awaiting review; its
            // content isn't real yet. A dependent must not start until its dependency's output
            // has actually landed, same reasoning as the file-lease queue's merge-gated release.
            if (dep.Status is not WorkUnitStatus.Merged)
                return false;
        }

        return true;
    }

    // Phase 12 — every child's branch is seeded once from parent.BranchId at fan-out time
    // (EnsureChildWorkUnitsAsync above) and never refreshed. That's correct for an independent
    // slice, but a dependent slice declared via dependsOn needs its dependency's actual merged
    // output — not just file-lease-conflict overlap, the narrower case the lease queue handles —
    // since a semantic dependency (a class, schema, contract another slice introduced) may touch
    // files the dependent never declared an interest in.
    //
    // Deliberately CopyFilesAsync (additive) over each dependency's own FilesTouched, not
    // ApplyBranchAsync (the merge-apply primitive): ApplyBranchAsync does a destructive full
    // mirror — it deletes every file in the target that isn't present in the source
    // (FileSystemWorkspaceService.ApplyBranchAsync, "Delete files in target that are absent in
    // source"), which is correct for landing ONE proposal into a target branch but wrong here —
    // with two-or-more dependencies, applying dep2's whole branch after dep1's would wipe out
    // every one of dep1's files that dep2's branch doesn't also happen to contain. Copying only
    // each dependency's own declared FilesTouched is purely additive: dependency order can only
    // affect which dependency's content wins on a genuine overlap, never delete an unrelated
    // dependency's contribution.
    private async Task RefreshBranchFromDependenciesAsync(WorkUnit child, CancellationToken ct)
    {
        if (child.DependsOn.Count == 0)
            return;

        foreach (var depId in child.DependsOn)
        {
            var dep = await _workUnits.GetAsync(depId, ct).ConfigureAwait(false);
            if (dep is null)
                continue;

            var chain = await _artifacts.GetChainAsync(dep.WorkUnitId, ct).ConfigureAwait(false);
            var proposalRef = chain.LastOrDefault(a => a.Type == ArtifactType.MergeProposal);
            if (proposalRef is null)
                continue;

            var proposal = await _merge.GetAsync(proposalRef.ArtifactId, ct).ConfigureAwait(false);
            if (proposal is null)
                continue;

            var files = proposal.FilesTouched.Count > 0
                ? proposal.FilesTouched
                : await _fileWorkspace.ListAsync(dep.BranchId, ct: ct).ConfigureAwait(false);
            if (files.Count == 0)
                continue;

            await _fileWorkspace.CopyFilesAsync(dep.BranchId, child.BranchId, files, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> EnqueueChildWorkerAsync(
        WorkUnit child,
        string parentWorkUnitId,
        OrchestratorCredentials? creds,
        string? sessionId,
        CancellationToken ct)
    {
        var existingTasks = await _tasks.ListAsync(child.WorkUnitId, ct).ConfigureAwait(false);
        var task = existingTasks.FirstOrDefault();
        if (task is null)
        {
            task = await _tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"),
                child.WorkUnitId,
                child.Goal,
                $"Execute slice for {child.Goal}",
                StudioTaskStatus.Open,
                null,
                0), ct).ConfigureAwait(false);

            await _artifacts.RecordAsync(new ArtifactRef(
                task.TaskId,
                ArtifactType.Task,
                child.WorkUnitId,
                ArtifactStatus.Active,
                DateTimeOffset.UtcNow,
                child.WorkUnitId,
                null), ct).ConfigureAwait(false);
        }

        // Slice 14c — deterministic, free, instant routing tier checked before the
        // LLM/heuristic IProfileSelectionService path. Falls through completely unchanged
        // (same heuristic-or-LLM behavior as today) on zero or multiple matches.
        var matchedProfile = await TryMatchFileScopeProfileAsync(child, ct).ConfigureAwait(false);
        var matchedPattern = matchedProfile is not null;
        var selection = matchedProfile is not null
            ? new ProfileSelectionResult(
                matchedProfile.AgentProfileId,
                $"fileScope matched profile '{matchedProfile.AgentProfileId}' declared FileScopePatterns",
                UsedLlm: false)
            : await _profileSelection.SelectProfileAsync(child, creds, ct).ConfigureAwait(false);

        // Slice 14b — built here (rather than inside a rule) so IPolicyRule implementations like
        // NonOverlappingFileScopeRule don't need their own IWorkUnitService dependency.
        var siblings = await _workUnits.GetChildrenAsync(parentWorkUnitId, ct).ConfigureAwait(false);
        var activeSiblings = siblings
            .Where(s => s.WorkUnitId != child.WorkUnitId)
            .Select(s => new FileScopeSibling(s.WorkUnitId, s.FanOutInfo?.SliceId, s.Status, s.FileScope))
            .ToList();

        var policyContext = new Dictionary<string, object?>
        {
            ["workUnitId"] = child.WorkUnitId,
            ["parentWorkUnitId"] = parentWorkUnitId,
            ["goal"] = child.Goal,
            ["fileScope"] = child.FileScope,
            ["activeSiblings"] = activeSiblings,
        };
        var policyResult = await _policyGate
            .EvaluateAsync(PolicyCheckpoint.BeforeEnqueue, policyContext, ct)
            .ConfigureAwait(false);
        if (!policyResult.Allowed)
        {
            var reason = string.Join("; ", policyResult.Violations.Select(v => $"{v.RuleId}: {v.Message}"));

            await _decisionLog.RecordAsync(
                parentWorkUnitId,
                "fanout",
                PipelineStage.Plan,
                JsonSerializer.Serialize(new
                {
                    childWorkUnitId = child.WorkUnitId,
                    childGoal = child.Goal,
                }),
                OrchestrationAction.PolicyBlocked,
                [child.WorkUnitId],
                reason,
                sessionId,
                ct).ConfigureAwait(false);

            await _workUnits.SetFanOutBlockedReasonAsync(child.WorkUnitId, $"blocked — {reason}", ct)
                .ConfigureAwait(false);

            return false;
        }

        if (child.FanOutInfo?.BlockedReason is not null)
        {
            await _workUnits.SetFanOutBlockedReasonAsync(child.WorkUnitId, null, ct).ConfigureAwait(false);
        }

        await _scheduler.EnqueueAsync(
            child.WorkUnitId,
            selection.ProfileId,
            task.TaskId,
            creds?.Model,
            creds?.BaseUrl,
            creds?.ApiKey,
            creds?.Provider,
            sessionId,
            ct).ConfigureAwait(false);

        // Slice 12d — fan-out child enqueue previously had no decision-log entry at all (it
        // happens outside any LLM tool call, so OrchestratorAgentLoop's RecordToolDecisionAsync
        // never saw it). Recording one here for every child, not just the LLM-selection path,
        // makes the chosen profile (and why) auditable from the Artifact Explorer regardless of
        // whether the toggle is on.
        await _decisionLog.RecordAsync(
            parentWorkUnitId,
            "fanout",
            PipelineStage.Plan,
            JsonSerializer.Serialize(new
            {
                childWorkUnitId = child.WorkUnitId,
                childGoal = child.Goal,
                selectedProfileId = selection.ProfileId,
                usedLlm = selection.UsedLlm,
                matchedPattern,
            }),
            OrchestrationAction.Enqueue,
            [child.WorkUnitId],
            selection.Reason,
            sessionId,
            ct).ConfigureAwait(false);

        return true;
    }

    // Slice 14c — "matches every path" means every entry in the child's fileScope is covered by
    // at least one of the profile's declared patterns; exactly one such profile routes
    // deterministically, zero or multiple fall through to IProfileSelectionService unchanged.
    private async Task<AgentProfile?> TryMatchFileScopeProfileAsync(WorkUnit child, CancellationToken ct)
    {
        if (child.FileScope.Count == 0)
            return null;

        var profiles = await _agentProfiles.ListAsync(ct).ConfigureAwait(false);
        var matches = profiles
            .Where(p => p.Stage == PipelineStage.Execute && p.FileScopePatterns.Count > 0)
            .Where(p => child.FileScope.All(path => p.FileScopePatterns.Any(pattern =>
                AgentWorkspaceService.MatchesGlob(pattern.Replace('\\', '/'), path.Replace('\\', '/')))))
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }
}
