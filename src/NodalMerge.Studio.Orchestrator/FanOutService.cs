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
    private readonly WorkspaceOptions _options;

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
        IMergeService merge,
        WorkspaceOptions options)
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
        _options           = options;
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

            var credRootId = await ResolveCredentialRootAsync(parentWorkUnitId, ct).ConfigureAwait(false);
            var creds = _agentControl.GetCredentialsForStage(credRootId, PipelineStage.Execute)
                ?? _agentControl.GetGoalDefaultCredentials(credRootId);
            // plans/recursive-planning-spike.md — review wiring the root goal carries. GetAutoReviewProfileId
            // is a direct per-workUnitId lookup with NO walk to root, so a Compound child spawned as an
            // orchestrator must be given the root's autoReviewProfileId / enabledDomainAgents explicitly —
            // otherwise its own registration shadows the resolution with null and its subtree's automated
            // review silently falls back. Resolved from credRootId (the goal root, which has the registration).
            var rootAutoReviewProfileId = _agentControl.GetAutoReviewProfileId(credRootId);
            var rootEnabledDomainAgents = _agentControl.GetEnabledDomainAgents(credRootId);

            // plans/recursive-planning-spike.md S2/S3 — the route decision reads the child's stored
            // slice Kind (WorkUnitFanOutInfo.Kind) and compares each child's depth (parentDepth + 1)
            // against the effective cap: the goal's per-goal override (propagated onto every unit)
            // when set, else the global WorkspaceOptions.MaxPlanDepth.
            var parentDepth = await ComputePlanDepthAsync(parentWorkUnitId, ct).ConfigureAwait(false);
            var effectiveMaxDepth = parent.MaxPlanDepthOverride ?? _options.MaxPlanDepth;

            var children = await _workUnits.GetChildrenAsync(parentWorkUnitId, ct).ConfigureAwait(false);
            foreach (var child in children)
            {
                // Collision avoidance — two sibling slices with overlapping fileScope and no
                // dependsOn between them is a planning gap, not something to catch at runtime via
                // the file lease. Sequencing it here (before it's ever enqueued) means the newer
                // sibling simply never starts until the older one has merged, instead of both
                // starting, one burning iterations, then discovering the conflict at write time.
                var sequenced = await AutoSequenceOverlappingSiblingsAsync(child, children, ct).ConfigureAwait(false);

                if (!await IsReadyToEnqueueAsync(sequenced, ct).ConfigureAwait(false))
                    continue;

                await RefreshBranchFromDependenciesAsync(sequenced, parent.BranchId, ct).ConfigureAwait(false);

                var isCompound = sequenced.FanOutInfo?.Kind == PlanSliceKind.Compound;
                var underCap = (parentDepth + 1) < effectiveMaxDepth;

                if (isCompound && underCap)
                {
                    // Route a Compound slice under the cap to a *sub-planner* instead of a worker by
                    // spawning it as an orchestrator — exactly what ReconciliationAgentService does for
                    // a reconciliation unit. This runs StartGoalAsync → ConvergeAsync(ensurePlanner:
                    // true) → EnsurePlannerAsync on the child, reusing the full Plan-stage credential
                    // ladder and SpawnPlanner logging. The child then plans itself, fans out its own
                    // grandchildren (via the Plan-stage release branch), reconciles them, and proposes
                    // up to this parent — recursion and the bottom-up roll-up close with no new
                    // machinery. The Execute-stage `creds` passed here only satisfy the spawn's
                    // canStartLoop gate; EnsurePlannerAsync re-resolves Plan-stage creds keyed by the
                    // goal root. Idempotent: EnsurePlannerAsync's enqueue transitions the child off
                    // Created → Queued, so IsReadyToEnqueueAsync skips it on every later sweep.
                    await _agentControl.SpawnAsync(
                        "orchestrator",
                        sequenced.WorkUnitId,
                        model: creds?.Model,
                        baseUrl: creds?.BaseUrl,
                        apiKey: creds?.ApiKey,
                        provider: creds?.Provider,
                        profileId: creds?.ProfileId,
                        autoReviewProfileId: rootAutoReviewProfileId,
                        enabledDomainAgents: rootEnabledDomainAgents,
                        credentialRef: creds?.CredentialRef,
                        cancellationToken: ct).ConfigureAwait(false);

                    actions.Add(FanOutAction.ChildEnqueued);
                    enqueued.Add(sequenced.WorkUnitId);
                    continue;
                }

                // A Compound slice that fails the cap (child depth at/over MaxPlanDepth) is forced to
                // a worker. That is the signature of a planner overshooting its remaining depth budget
                // — recorded as `demotedFromCompound` so the plan-quality eval can count it.
                var demotedFromCompound = isCompound && !underCap;

                if (await EnqueueChildWorkerAsync(sequenced, parentWorkUnitId, credRootId, creds, demotedFromCompound, sessionId, ct).ConfigureAwait(false))
                {
                    actions.Add(FanOutAction.ChildEnqueued);
                    enqueued.Add(sequenced.WorkUnitId);
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
                    sliceKind: slice.Kind,
                    // Propagate the per-goal depth override down so every unit in the tree reads its own
                    // effective ceiling (mirrors BypassPromotionBranch's propagation above).
                    maxPlanDepthOverride: parent.MaxPlanDepthOverride,
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

    // Statuses where a sibling's file-scope claim no longer matters — either its content has
    // already landed (Merged, so a fresh dependsOn edge onto it is what a NEW overlapping sibling
    // needs anyway) or it never will (Cancelled). Everything else (Created, Queued, Executing,
    // Proposed, Reviewing, DeadLettered, Retrying) is still "in flight" from a collision-avoidance
    // point of view — DeadLettered in particular may yet be retried/continued and land later, so
    // it still counts as a live claim on its files.
    private static readonly HashSet<WorkUnitStatus> DoesNotNeedSequencing =
        [WorkUnitStatus.Merged, WorkUnitStatus.Cancelled];

    // Auto-inserts a dependsOn edge instead of the old NonOverlappingFileScopeRule's flat reject —
    // a planning gap (two sibling slices sharing a file, no dependsOn declared between them) is
    // cheap to fix here, before either one is ever enqueued, versus catching it at the file lease
    // after one side has already burned iterations getting to the conflicting write. Only ever
    // makes the NEWER of an overlapping pair depend on the OLDER one (a strict, deterministic
    // total order by CreatedAt, tie-broken by WorkUnitId) — that alone rules out two siblings both
    // trying to depend on each other in the same pass. The cycle check below additionally guards
    // the cross-generation case (e.g. a re-planned sibling whose own dependency chain already
    // reaches back to this one) — if that ever fires, this leaves the pair alone and lets the file
    // lease serialize them at runtime instead, rather than forcing an edge that would deadlock.
    private async Task<WorkUnit> AutoSequenceOverlappingSiblingsAsync(
        WorkUnit child, IReadOnlyList<WorkUnit> siblings, CancellationToken ct)
    {
        if (child.FileScope.Count == 0)
            return child;

        var childScope = new HashSet<string>(child.FileScope.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        var current = child;

        foreach (var sibling in siblings)
        {
            if (sibling.WorkUnitId == current.WorkUnitId) continue;
            if (DoesNotNeedSequencing.Contains(sibling.Status)) continue;
            if (sibling.FileScope.Count == 0) continue;
            if (current.DependsOn.Contains(sibling.WorkUnitId)) continue;

            var overlaps = sibling.FileScope.Select(NormalizePath).Any(childScope.Contains);
            if (!overlaps) continue;

            var siblingIsOlder = sibling.CreatedAt < current.CreatedAt
                || (sibling.CreatedAt == current.CreatedAt
                    && string.CompareOrdinal(sibling.WorkUnitId, current.WorkUnitId) < 0);
            if (!siblingIsOlder) continue;

            if (await WouldCreateCycleAsync(sibling.WorkUnitId, current.WorkUnitId, ct).ConfigureAwait(false))
                continue;

            current = await _workUnits.AddDependencyAsync(current.WorkUnitId, sibling.WorkUnitId, ct)
                .ConfigureAwait(false);
        }

        return current;
    }

    // True if candidateDependencyId's own DependsOn chain already reaches targetWorkUnitId —
    // meaning "targetWorkUnitId depends on candidateDependencyId" would close a cycle.
    private async Task<bool> WouldCreateCycleAsync(
        string candidateDependencyId, string targetWorkUnitId, CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(candidateDependencyId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (current == targetWorkUnitId) return true;

            var unit = await _workUnits.GetAsync(current, ct).ConfigureAwait(false);
            if (unit is null) continue;
            foreach (var dep in unit.DependsOn)
                queue.Enqueue(dep);
        }

        return false;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private async Task<bool> IsReadyToEnqueueAsync(WorkUnit child, CancellationToken ct)
    {
        if (child.Status is not WorkUnitStatus.Created)
            return false;

        foreach (var depId in child.DependsOn)
        {
            var dep = await _workUnits.GetAsync(depId, ct).ConfigureAwait(false);
            if (dep is null)
                return false;

            if (!await IsDependencyContentReadyAsync(dep, ct).ConfigureAwait(false))
                return false;
        }

        return true;
    }

    // A dependent must not start until its dependency's output is real — but "real" only ever
    // meant "review has signed off," not "already merged into the target branch." Requiring
    // WorkUnitStatus.Merged made a dependent wait for the same all-siblings-simultaneous merge
    // batch that MergeReconciliationService's staggered-arrival design deliberately doesn't
    // require (see StaggeredChildCompletionTests) — Agent Approval mode existed specifically to
    // let a dependent begin as soon as review passed, and a human clicking Approve is the same
    // signal. RefreshBranchFromDependenciesAsync below already seeds the dependent's branch by
    // copying the dependency's own branch content (not the merged target), so an Approved-but-
    // not-yet-merged proposal is already safe to build on.
    private async Task<bool> IsDependencyContentReadyAsync(WorkUnit dep, CancellationToken ct)
    {
        if (dep.Status is WorkUnitStatus.Merged)
            return true;

        var chain = await _artifacts.GetChainAsync(dep.WorkUnitId, ct).ConfigureAwait(false);
        var proposalRef = chain.LastOrDefault(a => a.Type == ArtifactType.MergeProposal);
        if (proposalRef is null)
            return false;

        var proposal = await _merge.GetAsync(proposalRef.ArtifactId, ct).ConfigureAwait(false);
        return proposal is not null
            && proposal.Status is MergeProposalStatus.Approved or MergeProposalStatus.Merged;
    }

    // Phase 12 — every child's branch is seeded once from parent.BranchId at fan-out time
    // (EnsureChildWorkUnitsAsync above) and never refreshed. That's correct for an independent
    // slice, but a dependent slice declared via dependsOn needs its dependency's actual merged
    // output — not just file-lease-conflict overlap, the narrower case the lease queue handles —
    // since a semantic dependency (a class, schema, contract another slice introduced) may touch
    // files the dependent never declared an interest in.
    //
    // Deliberately CopyFilesAsync (additive) over the dependency's changed-file set, not
    // ApplyBranchAsync (the merge-apply primitive): ApplyBranchAsync does a destructive full
    // mirror — it deletes every file in the target that isn't present in the source
    // (FileSystemWorkspaceService.ApplyBranchAsync, "Delete files in target that are absent in
    // source"), which is correct for landing ONE proposal into a target branch but wrong here —
    // with two-or-more dependencies, applying dep2's whole branch after dep1's would wipe out
    // every one of dep1's files that dep2's branch doesn't also happen to contain. Copying only
    // the dependency's changed files is purely additive: dependency order can only affect which
    // dependency's content wins on a genuine overlap, never delete an unrelated dependency's
    // contribution.
    //
    // That changed-file set is computed by diffing dep.BranchId against parentBranchId — the
    // common ancestor every sibling was seeded from (EnsureChildWorkUnitsAsync) — rather than
    // reading the dependency's own MergeProposal.FilesTouched. FilesTouched only lists files the
    // dependency's OWN task wrote; in a multi-hop chain (Task1 -> Task2 -> Task3) a file Task1
    // changed but Task2 never itself touched still differs between Task2's branch and the common
    // ancestor (Task2 inherited it via this same method when IT became ready), so the diff
    // catches it — FilesTouched alone would silently drop it, leaving Task3 on the pre-Task1
    // value even though Task2's branch already has the fix.
    private async Task RefreshBranchFromDependenciesAsync(WorkUnit child, string parentBranchId, CancellationToken ct)
    {
        if (child.DependsOn.Count == 0)
            return;

        foreach (var depId in child.DependsOn)
        {
            var dep = await _workUnits.GetAsync(depId, ct).ConfigureAwait(false);
            if (dep is null)
                continue;

            var diff = await _fileWorkspace.DiffAsync(dep.BranchId, parentBranchId, ct).ConfigureAwait(false);
            var files = ParseChangedFiles(diff);
            if (files.Count == 0)
                continue;

            await _fileWorkspace.CopyFilesAsync(dep.BranchId, child.BranchId, files, ct).ConfigureAwait(false);
        }
    }

    // Mirrors MergeCommandService.ParseFilesTouched's marker convention over DiffAsync's output —
    // kept local rather than shared since deletions are intentionally excluded here (this method
    // is additive-only, see RefreshBranchFromDependenciesAsync's own comment) where the merge-side
    // parser also tracks deletions.
    private static IReadOnlyList<string> ParseChangedFiles(string diff)
    {
        var files = new List<string>();
        foreach (var rawLine in diff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var file =
                line.StartsWith("+++ ADDED: ", StringComparison.Ordinal)    ? line["+++ ADDED: ".Length..] :
                line.StartsWith("~~~ MODIFIED: ", StringComparison.Ordinal) ? line["~~~ MODIFIED: ".Length..] :
                null;
            if (file is not null)
                files.Add(file);
        }

        return files;
    }

    private async Task<bool> EnqueueChildWorkerAsync(
        WorkUnit child,
        string parentWorkUnitId,
        string credRootId,
        GoalDefaultCredentials? creds,
        bool demotedFromCompound,
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

        // A file-scope-matched profile that bound its own Model Profile (AgentProfile.ModelProfileId)
        // runs on *that* LLM instead of the Execute-stage default. Null when the profile declared no
        // binding (or the selection came from the heuristic/LLM path, which doesn't carry a profile
        // credential), so this cleanly falls back to the stage `creds` — exactly today's behavior.
        // See plans/scoped-execute-workers-per-profile-models.md.
        var effectiveCreds = matchedProfile is not null
            ? _agentControl.GetCredentialsForProfile(credRootId, matchedProfile.AgentProfileId) ?? creds
            : creds;

        var policyContext = new Dictionary<string, object?>
        {
            ["workUnitId"] = child.WorkUnitId,
            ["parentWorkUnitId"] = parentWorkUnitId,
            ["goal"] = child.Goal,
            ["fileScope"] = child.FileScope,
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
            effectiveCreds?.Model,
            effectiveCreds?.BaseUrl,
            effectiveCreds?.ApiKey,
            effectiveCreds?.Provider,
            sessionId,
            effectiveCreds?.CredentialRef,
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
                // plans/recursive-planning-spike.md S3 — true when this child's slice was marked
                // Compound but the depth cap forced it to a worker. The free depth-overshoot counter.
                demotedFromCompound,
            }),
            OrchestrationAction.Enqueue,
            [child.WorkUnitId],
            selection.Reason,
            sessionId,
            ct).ConfigureAwait(false);

        return true;
    }

    // Credential registrations — stage, goal-default, and the per-profile bindings behind
    // GetCredentialsForProfile — are all written once at orchestrator spawn, keyed by the GOAL ROOT
    // work unit. Resolving them by the immediate parent only ever worked for a root's direct
    // children: a deeper fan-out (an orchestrator-type child, typically a reconciliation or sub-plan
    // unit, fanning out its own children via GoalCoordinator's rescue sweep) has an intermediate
    // unit as its parent, found no registration, and silently enqueued with *no* credentials at all
    // — not even the stage default, since that lookup is keyed the same way. Walking to the root
    // makes every depth resolve the one registration that exists. For a root's direct children this
    // returns parentWorkUnitId unchanged, so depth-1 behavior is identical to before.
    // Bounded like ContinueService.ResolveGoalSessionIdAsync's walk; a missing parent mid-walk stops
    // and uses the last id resolved.
    private async Task<string> ResolveCredentialRootAsync(string workUnitId, CancellationToken ct)
    {
        var rootId = workUnitId;
        for (var depth = 0; depth < 32; depth++)
        {
            var unit = await _workUnits.GetAsync(rootId, ct).ConfigureAwait(false);
            if (unit?.ParentWorkUnitId is not { } parentId)
                break;
            rootId = parentId;
        }
        return rootId;
    }

    // plans/recursive-planning-spike.md S2 — planning depth = the number of ancestor work units.
    // The root goal is depth 0, a root slice's unit is depth 1, a grandchild is depth 2. Same
    // bounded parent-walk as ResolveCredentialRootAsync (never write a second unbounded walk); a
    // missing parent mid-walk stops and returns the count so far. Used by the S3 route decision:
    // a Compound slice's child (whose depth = parentDepth + 1) may sub-plan iff it is < MaxPlanDepth.
    private async Task<int> ComputePlanDepthAsync(string workUnitId, CancellationToken ct)
    {
        var depth = 0;
        var currentId = workUnitId;
        for (var hop = 0; hop < 32; hop++)
        {
            var unit = await _workUnits.GetAsync(currentId, ct).ConfigureAwait(false);
            if (unit?.ParentWorkUnitId is not { } parentId)
                break;
            depth++;
            currentId = parentId;
        }
        return depth;
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
