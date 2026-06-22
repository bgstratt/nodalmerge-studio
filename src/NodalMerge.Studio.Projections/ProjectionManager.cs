using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;
using TaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Projections;

public sealed class ProjectionManager : IProjectionManager
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly IWorkUnitService _workUnits;
    private readonly ITaskService _tasks;
    private readonly IMergeService _merges;
    private readonly IAgentRuntimeService _agentRuntime;
    private readonly IArtifactLineageService _artifactLineage;
    private readonly IWorkspaceExecutionCommandService? _executionCommands;
    private readonly IOrchestrationDecisionLogService? _decisionLog;
    private readonly IDecisionNodeService? _decisionNodes;
    private readonly IEvidenceNodeService? _evidenceNodes;
    private readonly IAgentProfileService? _agentProfiles;
    private readonly ISteeringDecisionService? _steeringDecisions;
    private readonly IFileWorkspaceService? _fileWorkspace;
    private readonly IWorkspaceProfileService? _workspaceProfiles;
    private readonly IExecutionSessionService? _sessions;
    private readonly IStudioNodeStore? _nodeStore;

    public ProjectionManager(
        IWorkUnitService workUnits,
        ITaskService tasks,
        IMergeService merges,
        IAgentRuntimeService agentRuntime,
        IArtifactLineageService artifactLineage,
        IWorkspaceExecutionCommandService? executionCommands = null,
        IOrchestrationDecisionLogService? decisionLog = null,
        IDecisionNodeService? decisionNodes = null,
        IEvidenceNodeService? evidenceNodes = null,
        IAgentProfileService? agentProfiles = null,
        ISteeringDecisionService? steeringDecisions = null,
        IFileWorkspaceService? fileWorkspace = null,
        IWorkspaceProfileService? workspaceProfiles = null,
        IExecutionSessionService? sessions = null,
        IStudioNodeStore? nodeStore = null)
    {
        _workUnits          = workUnits;
        _tasks              = tasks;
        _merges             = merges;
        _agentRuntime       = agentRuntime;
        _artifactLineage    = artifactLineage;
        _executionCommands  = executionCommands;
        _decisionLog        = decisionLog;
        _decisionNodes      = decisionNodes;
        _evidenceNodes      = evidenceNodes;
        _agentProfiles      = agentProfiles;
        _steeringDecisions  = steeringDecisions;
        _fileWorkspace      = fileWorkspace;
        _workspaceProfiles  = workspaceProfiles;
        _sessions           = sessions;
        _nodeStore          = nodeStore;
    }

    public async Task<ProjectionResult> GetAsync(ProjectionRequest request, CancellationToken cancellationToken = default)
    {
        var dataJson = request.Type switch
        {
            ProjectionType.WorkUnit => await BuildWorkUnitAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.Task => await BuildTaskAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.MergeProposal => await BuildMergeProposalAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.ExecutionSnapshot => await BuildExecutionSnapshotAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.AuthoritativeState => await BuildAuthoritativeStateAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.AgentWorkspace => await BuildAgentWorkspaceAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.GoalGraph => await BuildGoalGraphAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.EvidenceLedger => await BuildEvidenceLedgerAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.TrajectoryTimeline => await BuildTrajectoryTimelineAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.ModelDivergenceView => await BuildModelDivergenceViewAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.ReasoningCommitGraph => await BuildReasoningCommitGraphAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.DecisionContext => await BuildDecisionContextAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.CounterfactualComparison => await BuildCounterfactualComparisonAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.RunRetrospective => await BuildRunRetrospectiveAsync(request, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Type, "Unknown projection type.")
        };

        return new ProjectionResult(request.Type, request.Level, dataJson, DateTimeOffset.UtcNow);
    }

    public Task<ProjectionResult> CompactAsync(
        ProjectionType type,
        ProjectionLevel targetLevel,
        CancellationToken cancellationToken = default) =>
        GetAsync(new ProjectionRequest(type, targetLevel), cancellationToken);

    // Capped deliberately — this text goes straight into a real, paid LLM call per click, not free
    // heuristics. ~30 text samples and a fixed overall character ceiling keep token usage bounded
    // regardless of how much history has accumulated.
    private const int MaxScanTextSamples = 30;
    private const int MaxScanContextChars = 6_000;

    public async Task<string> BuildInsightScanContextAsync(CancellationToken cancellationToken = default)
    {
        var retroJson = await BuildRunRetrospectiveAsync(
            new ProjectionRequest(ProjectionType.RunRetrospective, ProjectionLevel.Normal), cancellationToken)
            .ConfigureAwait(false);

        var samples = new List<string>();

        if (_nodeStore is not null)
        {
            var decisionRecords = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.DecisionV1, cancellationToken).ConfigureAwait(false);
            samples.AddRange(decisionRecords
                .Select(r => JsonSerializer.Deserialize<DecisionNode>(r.PayloadJson))
                .Where(d => d is not null && d.Outcome == DecisionOutcome.Rejected && !string.IsNullOrWhiteSpace(d.Rationale))
                .OrderByDescending(d => d!.DecidedAt)
                .Take(MaxScanTextSamples)
                .Select(d => $"[rejection rationale] {d!.Rationale}"));

            var steeringRecords = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.SteeringDecisionV1, cancellationToken).ConfigureAwait(false);
            samples.AddRange(steeringRecords
                .Select(r => JsonSerializer.Deserialize<SteeringDecision>(r.PayloadJson))
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.InjectedConstraint))
                .OrderByDescending(s => s!.SteeredAt)
                .Take(MaxScanTextSamples)
                .Select(s => $"[human steering note] {s!.InjectedConstraint}"));
        }

        var proposals = await _merges.ListAsync(sourceBranch: null, cancellationToken).ConfigureAwait(false);
        samples.AddRange(proposals
            .Where(p => p.Status == MergeProposalStatus.Rejected && !string.IsNullOrWhiteSpace(p.ReviewNotes))
            .Take(MaxScanTextSamples)
            .Select(p => $"[review note] {p.ReviewNotes}"));

        var contextText = $"[Aggregate statistics]\n{retroJson}\n\n[Sampled history — up to {MaxScanTextSamples} entries per category]\n"
            + string.Join("\n", samples);

        return contextText.Length > MaxScanContextChars
            ? contextText[..MaxScanContextChars] + "\n...[truncated]"
            : contextText;
    }

    private async Task<string> BuildWorkUnitAsync(ProjectionRequest request, CancellationToken ct)
    {
        if (request.WorkUnitId is null)
        {
            var all = await _workUnits.ListAsync(request.BranchId, ct).ConfigureAwait(false);
            return Serialize(new { workUnitIds = all.Select(w => w.WorkUnitId).ToList(), count = all.Count });
        }

        var workUnit = await _workUnits.GetAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        if (workUnit is null)
            return Serialize(new { error = $"WorkUnit '{request.WorkUnitId}' not found." });

        var allTasks = await _tasks.ListAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        var activeTasks = allTasks
            .Where(t => t.Status is TaskStatus.Open or TaskStatus.InProgress)
            .Select(t => t.TaskId)
            .ToList();

        return request.Level switch
        {
            ProjectionLevel.Emergency => Serialize(new
            {
                workUnitId = workUnit.WorkUnitId,
                status = workUnit.Status.ToString(),
                activeTaskCount = activeTasks.Count
            }),
            ProjectionLevel.Compact => Serialize(new
            {
                workUnitId = workUnit.WorkUnitId,
                goal = workUnit.Goal,
                status = workUnit.Status.ToString(),
                activeTasks,
                assignedAgent = workUnit.AssignedAgent
            }),
            _ => Serialize(new WorkUnitProjectionPayload(
                workUnit.WorkUnitId,
                workUnit.Goal,
                workUnit.BranchId,
                workUnit.Status.ToString(),
                activeTasks,
                [],
                workUnit.SuccessCriteria,
                workUnit.AssignedAgent is not null ? [workUnit.AssignedAgent] : []))
        };
    }

    private async Task<string> BuildTaskAsync(ProjectionRequest request, CancellationToken ct)
    {
        var allTasks = await _tasks.ListAsync(request.WorkUnitId, ct).ConfigureAwait(false);

        var open = allTasks.Where(t => t.Status == TaskStatus.Open).Select(t => t.TaskId).ToList();
        var inProgress = allTasks.Where(t => t.Status == TaskStatus.InProgress).Select(t => t.TaskId).ToList();
        var blocked = allTasks.Where(t => t.Status == TaskStatus.Blocked).Select(t => t.TaskId).ToList();
        var completed = allTasks.Where(t => t.Status == TaskStatus.Completed).Select(t => t.TaskId).ToList();
        var assignments = allTasks
            .Where(t => t.Assignee is not null)
            .ToDictionary(t => t.TaskId, t => t.Assignee!);
        var active = open.Concat(inProgress).ToList();

        return request.Level switch
        {
            ProjectionLevel.Emergency => Serialize(new
            {
                activeCount = active.Count,
                nextTask = active.FirstOrDefault()
            }),
            ProjectionLevel.Compact => Serialize(new
            {
                active,
                blockedCount = blocked.Count,
                completedCount = completed.Count
            }),
            _ => Serialize(new TaskProjectionPayload(active, blocked, completed, assignments))
        };
    }

    private async Task<string> BuildMergeProposalAsync(ProjectionRequest request, CancellationToken ct)
    {
        var proposals = await _merges.ListAsync(request.BranchId, ct).ConfigureAwait(false);
        var pending = proposals
            .Where(p => p.Status is MergeProposalStatus.Draft or MergeProposalStatus.ReadyForReview)
            .ToList();
        var reviewStatus = proposals.ToDictionary(p => p.ProposalId, p => p.Status.ToString());
        var verificationResults = proposals
            .Where(p => p.VerificationResults is not null)
            .Select(p => p.VerificationResults!)
            .ToList();

        return request.Level switch
        {
            ProjectionLevel.Emergency => Serialize(new { pendingCount = pending.Count }),
            ProjectionLevel.Compact => Serialize(new
            {
                pending = pending.Select(p => p.ProposalId).ToList(),
                reviewStatus
            }),
            _ => Serialize(new MergeProposalProjectionPayload(
                pending.Select(p => p.ProposalId).ToList(),
                reviewStatus,
                verificationResults))
        };
    }

    private async Task<string> BuildExecutionSnapshotAsync(ProjectionRequest request, CancellationToken ct)
    {
        if (request.AgentId is null || request.WorkUnitId is null)
            return Serialize(new { error = "ExecutionSnapshot requires agentId and workUnitId." });

        var snapshot = await _agentRuntime.GetSnapshotAsync(request.AgentId, request.WorkUnitId, ct).ConfigureAwait(false);

        return request.Level switch
        {
            ProjectionLevel.Emergency => Serialize(new
            {
                agentId = snapshot.AgentId,
                failureCount = snapshot.FailureCount,
                nextSuggestedAction = snapshot.NextSuggestedAction
            }),
            ProjectionLevel.Compact => Serialize(new
            {
                agentId = snapshot.AgentId,
                currentGoal = snapshot.CurrentGoal,
                failureCount = snapshot.FailureCount,
                recentActions = snapshot.RecentActions.TakeLast(3).ToList()
            }),
            _ => Serialize(new ExecutionSnapshotProjectionPayload(
                snapshot.AgentId,
                snapshot.CurrentGoal,
                snapshot.RecentActions.ToList(),
                snapshot.Constraints.ToList()))
        };
    }

    private async Task<string> BuildAuthoritativeStateAsync(ProjectionRequest request, CancellationToken ct)
    {
        var branchId = request.BranchId ?? string.Empty;
        var workUnits = await _workUnits.ListAsync(branchId.Length > 0 ? branchId : null, ct).ConfigureAwait(false);
        var mergedState = workUnits
            .Where(w => w.Status == WorkUnitStatus.Completed)
            .ToDictionary(w => w.WorkUnitId, w => w.Goal);

        return request.Level switch
        {
            ProjectionLevel.Emergency => Serialize(new { branchId, mergedWorkUnitCount = mergedState.Count }),
            ProjectionLevel.Compact => Serialize(new { branchId, mergedWorkUnits = mergedState.Keys.ToList() }),
            _ => Serialize(new AuthoritativeStateProjectionPayload(branchId, mergedState))
        };
    }

    private async Task<string> BuildAgentWorkspaceAsync(ProjectionRequest request, CancellationToken ct)
    {
        var workUnitId = request.WorkUnitId;
        if (workUnitId is null)
            return Serialize(new { error = "AgentWorkspace projection requires workUnitId." });

        var refs = await _artifactLineage.GetChainAsync(workUnitId, ct).ConfigureAwait(false);

        // Enrich MergeProposal refs with current status from the merge service.
        var enriched = new List<ArtifactRef>(refs.Count);
        foreach (var r in refs)
        {
            if (r.Type == ArtifactType.MergeProposal)
            {
                var proposal = await _merges.GetAsync(r.ArtifactId, ct).ConfigureAwait(false);
                if (proposal is not null)
                {
                    var status = proposal.Status switch
                    {
                        MergeProposalStatus.Approved => ArtifactStatus.Approved,
                        MergeProposalStatus.Rejected => ArtifactStatus.Rejected,
                        MergeProposalStatus.Merged   => ArtifactStatus.Applied,
                        _                            => ArtifactStatus.Active,
                    };
                    enriched.Add(r with { Status = status });
                    continue;
                }
            }
            enriched.Add(r);
        }

        // Knowledge artifacts (10g) are inherited down the WorkUnit DAG: walk ParentWorkUnitId
        // root-first and fold in every ancestor's own chain ahead of this work unit's, de-duping
        // by ArtifactId (IDs are globally unique, so the only possible duplicates are across levels).
        var ancestorChain = await CollectAncestorChainAsync(workUnitId, ct).ConfigureAwait(false);
        var combined = new List<ArtifactRef>(ancestorChain.Count + enriched.Count);
        combined.AddRange(ancestorChain);
        combined.AddRange(enriched);

        // Global constraints (promoted Knowledge Findings, no owning work unit) apply to every
        // work unit regardless of lineage — folded in ahead of the ancestor-chain ones.
        var globalConstraints = await _artifactLineage.GetGlobalConstraintsAsync(ct).ConfigureAwait(false);
        var inheritedConstraints = globalConstraints
            .Concat(ancestorChain.Where(a => a.Type == ArtifactType.Constraint))
            .ToList();

        WorkUnit? wu = null;
        if (_executionCommands is not null || _workspaceProfiles is not null)
            wu = await _workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);

        // ── Slice 16l — attach latest execution result ───────────────────────
        WorkspaceExecutionSummary? execution = null;
        if (_executionCommands is not null && wu is not null)
        {
            try
            {
                var execResult = await _executionCommands.GetLatestAsync(wu.BranchId, ct).ConfigureAwait(false);
                if (execResult is not null)
                {
                    var buildSystems = execResult.Builds
                        .Select(b => b.BuildSystem)
                        .Concat(execResult.Tests.Select(t => t.BuildSystem))
                        .Where(bs => bs is not null)
                        .Distinct()
                        .ToList()!;
                    var testSummary = execResult.Tests.Count > 0
                        ? $"{execResult.Tests.Sum(t => t.Passed)} passed / {execResult.Tests.Sum(t => t.Failed)} failed"
                        : null;
                    execution = new WorkspaceExecutionSummary(
                        execResult.AllSucceeded,
                        buildSystems,
                        testSummary,
                        execResult.ExecutedAt);
                }
            }
            catch { /* best-effort — never fail a projection for execution data */ }
        }

        // ── Phase 9e — attach detected project roots, so agents know a repo can hold more
        // than one project before deciding where a file belongs.
        IReadOnlyList<ProjectRootSummary>? roots = null;
        if (_workspaceProfiles is not null && wu is not null)
        {
            try
            {
                var profile = await _workspaceProfiles.GetOrDetectAsync(wu.BranchId, ct).ConfigureAwait(false);
                roots = profile.Roots.Select(r => new ProjectRootSummary(r.RelativePath, r.Stack, r.RuleFileContent)).ToList();
            }
            catch { /* best-effort — never fail a projection for profile data */ }
        }

        var payload = new AgentWorkspaceProjectionPayload(
            request.AgentId,
            workUnitId,
            new ArtifactChain(combined),
            inheritedConstraints,
            execution,
            roots);

        return Serialize(payload);
    }

    private async Task<IReadOnlyList<ArtifactRef>> CollectAncestorChainAsync(string workUnitId, CancellationToken ct)
    {
        var ancestorWorkUnitIds = new List<string>();
        var unit = await _workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        var parentId = unit?.ParentWorkUnitId;
        while (parentId is not null)
        {
            ancestorWorkUnitIds.Add(parentId);
            var parent = await _workUnits.GetAsync(parentId, ct).ConfigureAwait(false);
            parentId = parent?.ParentWorkUnitId;
        }

        var seen = new HashSet<string>();
        var result = new List<ArtifactRef>();
        // Walk root-first so the oldest ancestor's context appears first.
        foreach (var ancestorId in Enumerable.Reverse(ancestorWorkUnitIds))
        {
            var chain = await _artifactLineage.GetChainAsync(ancestorId, ct).ConfigureAwait(false);
            foreach (var artifact in chain)
                if (seen.Add(artifact.ArtifactId))
                    result.Add(artifact);
        }

        return result;
    }

    private async Task<string> BuildGoalGraphAsync(ProjectionRequest request, CancellationToken ct)
    {
        var allWorkUnits = await _workUnits.ListAsync(request.BranchId, ct).ConfigureAwait(false);
        var nodes = allWorkUnits.Select(wu => new GoalGraphNode(
            GoalId: wu.WorkUnitId,
            Goal: wu.Goal,
            WorkUnitId: wu.WorkUnitId,
            BranchId: wu.BranchId,
            Status: wu.Status.ToString(),
            ParentGoalId: wu.ParentWorkUnitId,
            ChildGoalIds: allWorkUnits.Where(c => c.ParentWorkUnitId == wu.WorkUnitId).Select(c => c.WorkUnitId).ToList(),
            Owner: wu.Owner,
            AssignedAgent: wu.AssignedAgent,
            ProposalCount: 0,
            CreatedAt: wu.CreatedAt
        )).ToList();

        return Serialize(new GoalGraphProjectionPayload(nodes));
    }

    private async Task<string> BuildEvidenceLedgerAsync(ProjectionRequest request, CancellationToken ct)
    {
        if (request.WorkUnitId is null)
            return Serialize(new EvidenceLedgerProjectionPayload(string.Empty, []));

        var entries = new List<EvidenceEntry>();

        // Pull build/test evidence from workspace execution results.
        if (_executionCommands is not null)
        {
            try
            {
                var wu = await _workUnits.GetAsync(request.WorkUnitId, ct).ConfigureAwait(false);
                if (wu is not null)
                {
                    var execResult = await _executionCommands.GetLatestAsync(wu.BranchId, ct).ConfigureAwait(false);
                    if (execResult is not null)
                    {
                        foreach (var build in execResult.Builds)
                        {
                            var summary = build.Success
                                ? $"{build.BuildSystem ?? "build"}: passed (exit {build.ExitCode})"
                                : $"{build.BuildSystem ?? "build"}: failed (exit {build.ExitCode})";
                            entries.Add(new EvidenceEntry(
                                $"ev-bld-{Guid.NewGuid():N}",
                                EvidenceKind.Build,
                                summary,
                                null,
                                execResult.ExecutedAt));
                        }
                        foreach (var test in execResult.Tests)
                        {
                            var summary = test.Success
                                ? $"{test.BuildSystem ?? "test"}: passed ({test.Passed}/{test.TotalTests})"
                                : $"{test.BuildSystem ?? "test"}: failed ({test.Passed}/{test.TotalTests})";
                            entries.Add(new EvidenceEntry(
                                $"ev-tst-{Guid.NewGuid():N}",
                                EvidenceKind.Test,
                                summary,
                                null,
                                execResult.ExecutedAt));
                        }
                    }
                }
            }
            catch { /* best-effort */ }
        }

        return Serialize(new EvidenceLedgerProjectionPayload(request.WorkUnitId, entries));
    }

    private async Task<string> BuildTrajectoryTimelineAsync(ProjectionRequest request, CancellationToken ct)
    {
        var allWorkUnits = await _workUnits.ListAsync(request.BranchId, ct).ConfigureAwait(false);

        var nodes = allWorkUnits.Select(wu =>
        {
            var phase = wu.CurrentStage switch
            {
                PipelineStage.Orchestrate => "GoalDefined",
                PipelineStage.Plan        => "Decomposed",
                PipelineStage.Execute     => "Executing",
                PipelineStage.Review      => "Proposed",
                PipelineStage.Merge       => wu.Status is WorkUnitStatus.Completed or WorkUnitStatus.Merged ? "Converged" : "Converging",
                _ => wu.Status.ToString()
            };

            var children = allWorkUnits.Where(c => c.ParentWorkUnitId == wu.WorkUnitId).ToList();

            return new
            {
                trajectoryId = wu.WorkUnitId,
                workUnitId = wu.WorkUnitId,
                branchId = wu.BranchId,
                goal = wu.Goal,
                phase,
                parentTrajectoryId = wu.ParentWorkUnitId,
                childTrajectoryIds = children.Select(c => c.WorkUnitId).ToList(),
                agentId = wu.AssignedAgent,
                startedAt = wu.CreatedAt,
                completedAt = wu.Status is WorkUnitStatus.Completed or WorkUnitStatus.Merged ? (DateTimeOffset?)wu.UpdatedAt : null
            };
        }).ToList();

        return Serialize(new { nodes, count = nodes.Count });
    }

    private async Task<string> BuildModelDivergenceViewAsync(ProjectionRequest request, CancellationToken ct)
    {
        if (request.WorkUnitId is null)
            return Serialize(new { error = "ModelDivergenceView requires workUnitId to find associated proposals." });

        var wu = await _workUnits.GetAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        if (wu is null)
            return Serialize(new { error = $"WorkUnit '{request.WorkUnitId}' not found." });

        // Find proposals for this work unit, grouped by model
        var allProposals = await _merges.ListAsync(sourceBranch: null, ct).ConfigureAwait(false);
        var relevantProposals = allProposals.Where(p => p.WorkUnitId == request.WorkUnitId).ToList();

        var byModel = relevantProposals
            .GroupBy(p => p.Model ?? p.AgentId ?? "unknown")
            .ToList();

        var models = byModel.Select(g => new
        {
            model = g.Key,
            provider = g.First().Provider,
            proposalIds = g.Select(p => p.ProposalId).ToList(),
            proposalCount = g.Count(),
            latestStatus = g.OrderByDescending(p => p.Status).First().Status.ToString()
        }).ToList();

        return Serialize(new
        {
            workUnitId = request.WorkUnitId,
            goal = wu.Goal,
            models,
            comparedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task<string> BuildReasoningCommitGraphAsync(ProjectionRequest request, CancellationToken ct)
    {
        if (request.WorkUnitId is null)
            return Serialize(new ReasoningCommitGraphProjectionPayload(string.Empty, [], []));

        var rootWu = await _workUnits.GetAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        if (rootWu is null)
            return Serialize(new { error = $"WorkUnit '{request.WorkUnitId}' not found." });

        // 1. Collect the entire work unit tree rooted at WorkUnitId.
        var allWuIds = new HashSet<string> { request.WorkUnitId };
        await CollectDescendantWorkUnitIdsAsync(request.WorkUnitId, allWuIds, ct).ConfigureAwait(false);

        var nodes = new List<ReasoningCommitGraphNode>();
        var edges = new List<ReasoningCommitGraphEdge>();

        // Map workUnitId → last event's CommitId for chaining Refine/Decided edges.
        var lastEventIdByWu = new Dictionary<string, string>();

        // Map workUnitId → first event's CommitId for Fork edges from parent spawns to child roots.
        var firstEventIdByWu = new Dictionary<string, string>();

        // 2. Query orchestration events for every work unit in the tree.
        foreach (var wuId in allWuIds)
        {
            if (_decisionLog is null) continue;
            var events = await _decisionLog.GetEventsAsync(wuId, ct).ConfigureAwait(false);
            var ordered = events.OrderBy(e => e.OccurredAt).ToList();

            string? prevEventId = null;
            foreach (var evt in ordered)
            {
                var node = new ReasoningCommitGraphNode(
                    CommitId: evt.EventId,
                    WorkUnitId: evt.WorkUnitId,
                    AgentId: evt.OrchestratorAgentId,
                    Stage: evt.InputStage.ToString(),
                    Action: evt.Action.ToString(),
                    Reasoning: evt.Reason,
                    AgentModel: null,
                    AgentProvider: null,
                    OccurredAt: evt.OccurredAt);
                nodes.Add(node);

                // Track first event per work unit for Fork edge resolution.
                if (!firstEventIdByWu.ContainsKey(wuId))
                    firstEventIdByWu[wuId] = evt.EventId;

                // Refine edge: sequential events within the same work unit.
                if (prevEventId is not null)
                    edges.Add(new ReasoningCommitGraphEdge(prevEventId, evt.EventId, "Refine"));

                prevEventId = evt.EventId;

                // Fork edges: connect this spawn event to the first event of each spawned child.
                // SpawnedIds contains work unit IDs of children created by SpawnPlanner/SpawnWorker/Enqueue.
                foreach (var spawnedId in evt.SpawnedIds)
                {
                    if (allWuIds.Contains(spawnedId))
                    {
                        // The child's first event may not have been processed yet (if ordered by
                        // creation time but child created after parent). Record a deferred edge
                        // that will be resolved after all events are collected.
                        edges.Add(new ReasoningCommitGraphEdge(evt.EventId, spawnedId, "Fork"));
                    }
                }
            }

            if (prevEventId is not null)
                lastEventIdByWu[wuId] = prevEventId;
        }

        // 3. Query decision nodes for every work unit — add as decision commit nodes.
        if (_decisionNodes is not null)
        {
            foreach (var wuId in allWuIds)
            {
                var decisions = await _decisionNodes.ListByWorkUnitAsync(wuId, ct).ConfigureAwait(false);
                foreach (var dec in decisions)
                {
                    var decisionNode = new ReasoningCommitGraphNode(
                        CommitId: dec.DecisionId,
                        WorkUnitId: dec.WorkUnitId,
                        AgentId: dec.ReviewerAgentId,
                        Stage: "Review",
                        Action: dec.Outcome.ToString(),
                        Reasoning: dec.Rationale,
                        AgentModel: dec.ReviewerModel,
                        AgentProvider: dec.ReviewerProvider,
                        OccurredAt: dec.DecidedAt);
                    nodes.Add(decisionNode);

                    // Decided edge: last orchestration event → this decision.
                    if (lastEventIdByWu.TryGetValue(wuId, out var lastEventId))
                        edges.Add(new ReasoningCommitGraphEdge(lastEventId, dec.DecisionId, "Decided"));
                }
            }
        }

        // 4. Query evidence nodes and add EvidenceAttached edges to their associated decisions.
        if (_evidenceNodes is not null)
        {
            foreach (var wuId in allWuIds)
            {
                var evidenceList = await _evidenceNodes.ListByWorkUnitAsync(wuId, ct).ConfigureAwait(false);
                foreach (var ev in evidenceList)
                {
                    if (ev.ProposalId is not null)
                    {
                        // Find the decision node that references this proposal.
                        var matchingDecision = nodes.FirstOrDefault(n =>
                            n.Stage == "Review" &&
                            n.WorkUnitId == wuId);
                        if (matchingDecision is not null)
                            edges.Add(new ReasoningCommitGraphEdge(matchingDecision.CommitId, ev.EvidenceId, "EvidenceAttached"));
                    }
                }
            }
        }

        return Serialize(new ReasoningCommitGraphProjectionPayload(request.WorkUnitId, nodes, edges));
    }

    // ── Slice 24a — DecisionContext projection ─────────────────────────────────────────

    private async Task<string> BuildDecisionContextAsync(ProjectionRequest request, CancellationToken ct)
    {
        if (request.WorkUnitId is null)
            return Serialize(new { error = "DecisionContext projection requires workUnitId." });

        var wu = await _workUnits.GetAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        if (wu is null)
            return Serialize(new { error = $"WorkUnit '{request.WorkUnitId}' not found." });

        // 1. Plan — try to read plan.json from the work unit's branch.
        var plan = new List<DecisionContextPlanEntry>();
        if (_fileWorkspace is not null)
        {
            try
            {
                var planJson = await _fileWorkspace.ReadAsync(wu.BranchId, "plan.json", ct).ConfigureAwait(false);
                if (planJson is not null)
                {
                    var planDoc = JsonSerializer.Deserialize<PlanDocument>(planJson, JsonOptions);
                    if (planDoc?.Slices is not null)
                    {
                        plan = planDoc.Slices.Select(s => new DecisionContextPlanEntry(
                            s.SliceId,
                            s.Goal,
                            s.FileScope,
                            s.Steps)).ToList();
                    }
                }
            }
            catch { /* best-effort */ }
        }

        // 2. Assumptions — collect from orchestration events for this work unit.
        var assumptions = new List<string>();
        if (_decisionLog is not null)
        {
            try
            {
                var events = await _decisionLog.GetEventsAsync(request.WorkUnitId, ct).ConfigureAwait(false);
                foreach (var evt in events.OrderBy(e => e.OccurredAt))
                {
                    if (!string.IsNullOrWhiteSpace(evt.Reason))
                    {
                        var reason = evt.Reason.Trim();
                        if (!assumptions.Contains(reason))
                            assumptions.Add(reason);
                    }
                }
            }
            catch { /* best-effort */ }
        }

        // 3. Constraints — combine inherited constraint artifacts and steering decisions.
        var constraints = new List<string>();
        try
        {
            var chain = await _artifactLineage.GetChainAsync(request.WorkUnitId, ct).ConfigureAwait(false);
            foreach (var artifact in chain)
            {
                if (artifact.Type == ArtifactType.Constraint && !string.IsNullOrWhiteSpace(artifact.Title))
                {
                    if (!constraints.Contains(artifact.Title))
                        constraints.Add(artifact.Title);
                }
            }
        }
        catch { /* best-effort */ }

        string? steeredFromDecisionId = null;
        if (_steeringDecisions is not null)
        {
            try
            {
                var steeringRecords = await _steeringDecisions.ListByWorkUnitAsync(request.WorkUnitId, ct).ConfigureAwait(false);
                foreach (var sd in steeringRecords)
                {
                    if (!string.IsNullOrWhiteSpace(sd.InjectedConstraint) && !constraints.Contains(sd.InjectedConstraint))
                        constraints.Add(sd.InjectedConstraint);
                    if (sd.NewChildWorkUnitId == request.WorkUnitId)
                        steeredFromDecisionId = sd.SteeringDecisionId;
                }
            }
            catch { /* best-effort */ }
        }

        // 4. Evidence — pull build/test results.
        var evidence = new List<DecisionContextEvidenceEntry>();
        DecisionContextExecutionSummary? executionSummary = null;
        if (_executionCommands is not null)
        {
            try
            {
                var execResult = await _executionCommands.GetLatestAsync(wu.BranchId, ct).ConfigureAwait(false);
                if (execResult is not null)
                {
                    foreach (var build in execResult.Builds)
                    {
                        evidence.Add(new DecisionContextEvidenceEntry(
                            "Build",
                            build.Success
                                ? $"{build.BuildSystem ?? "build"}: passed (exit {build.ExitCode})"
                                : $"{build.BuildSystem ?? "build"}: failed (exit {build.ExitCode})",
                            build.Success));
                    }
                    foreach (var test in execResult.Tests)
                    {
                        evidence.Add(new DecisionContextEvidenceEntry(
                            "Test",
                            test.Success
                                ? $"{test.BuildSystem ?? "test"}: {test.Passed}/{test.TotalTests} passed"
                                : $"{test.BuildSystem ?? "test"}: {test.Failed} failed / {test.TotalTests} total",
                            test.Success));
                    }

                    var buildSystems = execResult.Builds
                        .Select(b => b.BuildSystem)
                        .Concat(execResult.Tests.Select(t => t.BuildSystem))
                        .Where(bs => bs is not null)
                        .Distinct()
                        .ToList()!;
                    var testSummary = execResult.Tests.Count > 0
                        ? $"{execResult.Tests.Sum(t => t.Passed)} passed / {execResult.Tests.Sum(t => t.Failed)} failed"
                        : null;
                    executionSummary = new DecisionContextExecutionSummary(
                        execResult.AllSucceeded,
                        buildSystems,
                        testSummary,
                        execResult.ExecutedAt);
                }
            }
            catch { /* best-effort */ }
        }

        // 5. Allowed tools — resolve from the agent's profile.
        var allowedTools = new List<string>();
        string? agentModel = null;
        string? agentProvider = null;
        if (_agentProfiles is not null)
        {
            try
            {
                var ownerProfile = wu.Owner;
                var profile = await _agentProfiles.GetAsync(ownerProfile, ct).ConfigureAwait(false);
                if (profile is not null)
                {
                    allowedTools = profile.AllowedTools.ToList();
                }
            }
            catch { /* best-effort */ }
        }

        var payload = new DecisionContextProjectionPayload(
            wu.WorkUnitId,
            wu.Goal,
            plan,
            assumptions,
            constraints,
            evidence,
            allowedTools,
            executionSummary,
            agentModel,
            agentProvider,
            steeredFromDecisionId);

        return Serialize(payload);
    }

    // ── Slice 25b — CounterfactualComparison projection ──────────────────────────────

    private async Task<string> BuildCounterfactualComparisonAsync(ProjectionRequest request, CancellationToken ct)
    {
        if (request.WorkUnitId is null)
            return Serialize(new { error = "CounterfactualComparison requires workUnitId to identify the original work unit." });

        var originalWu = await _workUnits.GetAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        if (originalWu is null)
            return Serialize(new { error = $"Original work unit '{request.WorkUnitId}' not found." });

        // Find counterfactual work units — siblings with different profiles that are forked from proposals
        var counterfactuals = new List<WorkUnit>();
        if (originalWu.ParentWorkUnitId is not null)
        {
            var siblings = await _workUnits.GetChildrenAsync(originalWu.ParentWorkUnitId, ct).ConfigureAwait(false);
            counterfactuals = siblings
                .Where(w => w.WorkUnitId != originalWu.WorkUnitId && w.ForkType == HypothesisForkType.Model)
                .ToList();
        }

        var counterfactualWu = counterfactuals.FirstOrDefault();
        if (counterfactualWu is null)
        {
            // No counterfactual found — return empty comparison
            return Serialize(new CounterfactualComparisonProjectionPayload(
                request.WorkUnitId,
                string.Empty,
                string.Empty,
                [],
                [],
                null, null, null, null,
                "No counterfactual available yet.",
                DateTimeOffset.UtcNow));
        }

        var allProposals = await _merges.ListAsync(sourceBranch: null, ct).ConfigureAwait(false);

        // Collect proposals for original work unit
        var originalProposals = allProposals
            .Where(p => p.WorkUnitId == originalWu.WorkUnitId)
            .Select(p => new CounterfactualComparisonProposal(
                p.ProposalId, p.Goal ?? originalWu.Goal, p.Status.ToString(),
                p.Model, p.Provider, p.Confidence,
                p.FilesTouched, p.ChangeDescription))
            .ToList();

        // Collect proposals for counterfactual work unit
        var counterfactualProposals = allProposals
            .Where(p => p.WorkUnitId == counterfactualWu.WorkUnitId)
            .Select(p => new CounterfactualComparisonProposal(
                p.ProposalId, p.Goal ?? counterfactualWu.Goal, p.Status.ToString(),
                p.Model, p.Provider, p.Confidence,
                p.FilesTouched, p.ChangeDescription))
            .ToList();

        // Determine original proposal ID from original work unit's proposals
        var originalProposalId = originalProposals.FirstOrDefault()?.ProposalId ?? string.Empty;

        // Determine model/provider from the proposals
        var origModel = originalProposals.FirstOrDefault()?.Model;
        var origProvider = originalProposals.FirstOrDefault()?.Provider;
        var cfModel = counterfactualProposals.FirstOrDefault()?.Model;
        var cfProvider = counterfactualProposals.FirstOrDefault()?.Provider;

        // Simple "Which was better?" heuristic — compare confidence scores
        string? whichWasBetter = null;
        var origMaxConf = originalProposals.Max(p => p.Confidence);
        var cfMaxConf = counterfactualProposals.Max(p => p.Confidence);
        if (origMaxConf.HasValue && cfMaxConf.HasValue)
        {
            if (origMaxConf > cfMaxConf)
                whichWasBetter = "Original";
            else if (cfMaxConf > origMaxConf)
                whichWasBetter = "Counterfactual";
            else
                whichWasBetter = "Equal";
        }

        var payload = new CounterfactualComparisonProjectionPayload(
            originalWu.WorkUnitId,
            counterfactualWu.WorkUnitId,
            originalProposalId,
            originalProposals,
            counterfactualProposals,
            origModel, origProvider,
            cfModel, cfProvider,
            whichWasBetter,
            DateTimeOffset.UtcNow);

        return Serialize(payload);
    }

    // Manual-only analytics dashboard for the Insights tab — computed fresh from the full DAG
    // history on every request, never on a schedule. Terminal WorkUnitStatus values (Merged,
    // Failed, DeadLettered, Cancelled) are treated as "decided"; everything else (Created, Active,
    // Waiting, Queued, Executing, Proposed, Reviewing, Retrying) is still in flight and excluded
    // from the success-rate denominator.
    private async Task<string> BuildRunRetrospectiveAsync(ProjectionRequest request, CancellationToken ct)
    {
        var allWorkUnits = await _workUnits.ListAsync(branchId: null, ct).ConfigureAwait(false);
        var allProposals = await _merges.ListAsync(sourceBranch: null, ct).ConfigureAwait(false);
        var allSessions = _sessions is not null
            ? await _sessions.ListAsync(ct).ConfigureAwait(false)
            : [];

        var workUnits = allWorkUnits
            .Where(w => InRange(w.CreatedAt, request.Since, request.Until))
            .ToList();
        var workUnitsById = workUnits.ToDictionary(w => w.WorkUnitId);

        // A proposal has no CreatedAt of its own — date-range filtering goes through its work unit.
        // Unfiltered (the common case) keeps every proposal, including ones with no WorkUnitId.
        var proposals = (request.Since is null && request.Until is null)
            ? allProposals
            : allProposals.Where(p => p.WorkUnitId is not null && workUnitsById.ContainsKey(p.WorkUnitId)).ToList();

        var sessions = allSessions
            .Where(s => InRange(s.StartedAt, request.Since, request.Until))
            .ToList();

        var workUnitsByStatus = workUnits
            .GroupBy(w => w.Status.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        var mergedCount = workUnitsByStatus.GetValueOrDefault(WorkUnitStatus.Merged.ToString());
        var failedCount = workUnitsByStatus.GetValueOrDefault(WorkUnitStatus.Failed.ToString());
        var deadLetteredCount = workUnitsByStatus.GetValueOrDefault(WorkUnitStatus.DeadLettered.ToString());
        var cancelledCount = workUnitsByStatus.GetValueOrDefault(WorkUnitStatus.Cancelled.ToString());
        var decidedCount = mergedCount + failedCount + deadLetteredCount + cancelledCount;
        var overallSuccessRate = decidedCount == 0 ? 0d : (double)mergedCount / decidedCount;

        var sessionsByStatus = sessions
            .GroupBy(s => s.Status.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        var modelPerformance = proposals
            .Where(p => p.Model is not null)
            .GroupBy(p => (p.Model!, p.Provider))
            .Select(g =>
            {
                var merged = g.Count(p => p.Status == MergeProposalStatus.Merged);
                var rejected = g.Count(p => p.Status == MergeProposalStatus.Rejected);
                var decided = merged + rejected;
                var confidences = g.Where(p => p.Confidence.HasValue).Select(p => p.Confidence!.Value).ToList();
                return new ModelPerformanceStat(
                    g.Key.Item1, g.Key.Item2,
                    g.Count(), merged, rejected,
                    decided == 0 ? 0d : (double)merged / decided,
                    confidences.Count == 0 ? null : confidences.Average());
            })
            .OrderByDescending(s => s.ProposalCount)
            .ToList();

        var forkWinRates = workUnits
            .Where(w => w.ForkType is not null)
            .GroupBy(w => w.ForkType!.Value)
            .Select(g =>
            {
                var wins = 0;
                var losses = 0;
                var pending = 0;
                foreach (var wu in g)
                {
                    var wuProposals = proposals.Where(p => p.WorkUnitId == wu.WorkUnitId).ToList();
                    if (wuProposals.Any(p => p.Status == MergeProposalStatus.Merged)) { wins++; }
                    else if (wuProposals.Any(p => p.Status is MergeProposalStatus.Rejected or MergeProposalStatus.Superseded)) { losses++; }
                    else { pending++; }
                }
                var decided = wins + losses;
                return new ForkWinRateStat(
                    g.Key.ToString(), g.Count(), wins, losses, pending,
                    decided == 0 ? 0d : (double)wins / decided);
            })
            .OrderByDescending(s => s.TotalForks)
            .ToList();

        var failureCauses = new List<FailureCauseStat>
        {
            new(
                "ExecutionFailure",
                workUnits.Sum(w => w.ExecutionInfo?.FailureAttemptCount ?? 0),
                workUnits.Count(w => (w.ExecutionInfo?.FailureAttemptCount ?? 0) > 0)),
            new(
                "AutomatedReviewRejection",
                workUnits.Sum(w => w.ExecutionInfo?.AutomatedReviewRejectionCount ?? 0),
                workUnits.Count(w => (w.ExecutionInfo?.AutomatedReviewRejectionCount ?? 0) > 0)),
            new(
                "HumanReviewRejection",
                workUnits.Sum(w => w.ExecutionInfo?.HumanReviewRejectionCount ?? 0),
                workUnits.Count(w => (w.ExecutionInfo?.HumanReviewRejectionCount ?? 0) > 0)),
        }.OrderByDescending(f => f.TotalCount).ToList();

        var reviewOutcomes = new List<ReviewOutcomeStat>();
        if (_nodeStore is not null)
        {
            var decisionRecords = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.DecisionV1, ct).ConfigureAwait(false);
            var decisions = decisionRecords
                .Select(r => JsonSerializer.Deserialize<DecisionNode>(r.PayloadJson))
                .Where(d => d is not null && InRange(d.DecidedAt, request.Since, request.Until))
                .Select(d => d!)
                .ToList();
            reviewOutcomes = decisions
                .GroupBy(d => d.Outcome.ToString())
                .Select(g =>
                {
                    var confidences = g.Where(d => d.Confidence.HasValue).Select(d => d.Confidence!.Value).ToList();
                    return new ReviewOutcomeStat(g.Key, g.Count(), confidences.Count == 0 ? null : confidences.Average());
                })
                .OrderByDescending(s => s.Count)
                .ToList();
        }

        // Model performance by pipeline stage — WorkUnit.Owner is the orchestrating AgentProfile's
        // id when created via a strategy/template (e.g. ArtifactExplorerPanel's "owner: template.
        // orchestrator"); rows whose Owner doesn't resolve to a known profile (e.g. "user") are
        // simply excluded rather than mislabeled.
        var modelPerformanceByStage = new List<ModelStagePerformanceStat>();
        if (_agentProfiles is not null)
        {
            var ownerIds = proposals
                .Where(p => p.Model is not null && p.WorkUnitId is not null && workUnitsById.ContainsKey(p.WorkUnitId))
                .Select(p => workUnitsById[p.WorkUnitId!].Owner)
                .Distinct()
                .ToList();
            var profilesByOwner = new Dictionary<string, AgentProfile>();
            foreach (var ownerId in ownerIds)
            {
                var profile = await _agentProfiles.GetAsync(ownerId, ct).ConfigureAwait(false);
                if (profile is not null) { profilesByOwner[ownerId] = profile; }
            }

            var stageRows = proposals
                .Where(p => p.Model is not null && p.WorkUnitId is not null && workUnitsById.ContainsKey(p.WorkUnitId))
                .Select(p => new { Proposal = p, Owner = workUnitsById[p.WorkUnitId!].Owner })
                .Where(x => profilesByOwner.ContainsKey(x.Owner))
                .Select(x => new { x.Proposal, Stage = profilesByOwner[x.Owner].Stage.ToString() })
                .ToList();

            modelPerformanceByStage = stageRows
                .GroupBy(x => (x.Proposal.Model!, x.Stage))
                .Select(g =>
                {
                    var merged = g.Count(x => x.Proposal.Status == MergeProposalStatus.Merged);
                    var rejected = g.Count(x => x.Proposal.Status == MergeProposalStatus.Rejected);
                    var decided = merged + rejected;
                    return new ModelStagePerformanceStat(
                        g.Key.Item1, g.Key.Item2, g.Count(), merged, rejected,
                        decided == 0 ? 0d : (double)merged / decided);
                })
                .OrderByDescending(s => s.ProposalCount)
                .ToList();
        }

        // Sub-bucket Architecture/Library/Product forks by the structured constraint metadata key
        // ExperimentService stores per fork type (architectureConstraint/libraryConstraint/
        // productStrategy) — exact grouping, not free-text keyword matching.
        var forkConstraintWinRates = workUnits
            .Where(w => w.ForkType is HypothesisForkType.Architecture or HypothesisForkType.Library or HypothesisForkType.Product)
            .Select(w => new { WorkUnit = w, Constraint = w.Metadata?.GetValueOrDefault(ForkConstraintMetadataKey(w.ForkType!.Value)) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Constraint))
            .GroupBy(x => (x.WorkUnit.ForkType!.Value.ToString(), x.Constraint!))
            .Select(g =>
            {
                var wins = 0;
                var losses = 0;
                var pending = 0;
                foreach (var x in g)
                {
                    var wuProposals = proposals.Where(p => p.WorkUnitId == x.WorkUnit.WorkUnitId).ToList();
                    if (wuProposals.Any(p => p.Status == MergeProposalStatus.Merged)) { wins++; }
                    else if (wuProposals.Any(p => p.Status is MergeProposalStatus.Rejected or MergeProposalStatus.Superseded)) { losses++; }
                    else { pending++; }
                }
                var decided = wins + losses;
                return new ForkConstraintWinRateStat(
                    g.Key.Item1, g.Key.Item2, g.Count(), wins, losses, pending,
                    decided == 0 ? 0d : (double)wins / decided);
            })
            .OrderByDescending(s => s.TotalForks)
            .ToList();

        const int minSampleSize = 5;
        var averageReworkCycles = workUnits.Count == 0
            ? 0d
            : workUnits.Average(w => (double)(w.ExecutionInfo?.FailureAttemptCount ?? 0));
        var topFailureCause = failureCauses.FirstOrDefault(f => f.TotalCount > 0);
        var mostSuccessfulModel = modelPerformance
            .Where(s => s.ProposalCount >= minSampleSize)
            .OrderByDescending(s => s.AcceptanceRate)
            .FirstOrDefault();
        var mostSuccessfulStrategy = forkWinRates
            .Where(s => s.Wins + s.Losses >= minSampleSize)
            .OrderByDescending(s => s.WinRate)
            .FirstOrDefault();

        var payload = new RunRetrospectiveProjectionPayload(
            request.Since,
            request.Until,
            sessions.Count,
            sessionsByStatus,
            workUnits.Count,
            workUnitsByStatus,
            overallSuccessRate,
            averageReworkCycles,
            topFailureCause,
            mostSuccessfulModel,
            mostSuccessfulStrategy,
            modelPerformance,
            modelPerformanceByStage,
            forkWinRates,
            forkConstraintWinRates,
            failureCauses,
            reviewOutcomes,
            DateTimeOffset.UtcNow);

        return Serialize(payload);
    }

    private static bool InRange(DateTimeOffset value, DateTimeOffset? since, DateTimeOffset? until) =>
        (since is null || value >= since) && (until is null || value <= until);

    private static string ForkConstraintMetadataKey(HypothesisForkType forkType) => forkType switch
    {
        HypothesisForkType.Architecture => "architectureConstraint",
        HypothesisForkType.Library => "libraryConstraint",
        HypothesisForkType.Product => "productStrategy",
        _ => "experimentConstraint",
    };

    /// <summary>
    /// Recursively collects all descendant work unit IDs starting from <paramref name="parentId"/>.
    /// Guards against cycles with the <paramref name="collected"/> set.
    /// </summary>
    private async Task CollectDescendantWorkUnitIdsAsync(
        string parentId, HashSet<string> collected, CancellationToken ct)
    {
        var children = await _workUnits.GetChildrenAsync(parentId, ct).ConfigureAwait(false);
        foreach (var child in children)
        {
            if (collected.Add(child.WorkUnitId))
                await CollectDescendantWorkUnitIdsAsync(child.WorkUnitId, collected, ct).ConfigureAwait(false);
        }
    }

    private static string Serialize(object data) => JsonSerializer.Serialize(data, JsonOptions);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioProjections(this IServiceCollection services)
    {
        services.AddSingleton<IProjectionManager, ProjectionManager>();
        return services;
    }
}
