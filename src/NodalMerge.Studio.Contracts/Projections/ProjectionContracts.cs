using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Contracts.Projections;

/// <summary>
/// Frozen v1 projection type identifiers (MCP and Projection Manager).
/// </summary>
public enum ProjectionType
{
    WorkUnit,
    AuthoritativeState,
    Task,
    MergeProposal,
    ExecutionSnapshot,
    AgentWorkspace,
    GoalGraph,
    EvidenceLedger,
    TrajectoryTimeline,
    ModelDivergenceView,
    ReasoningCommitGraph,
    DecisionContext,
    CounterfactualComparison,
    RunRetrospective,
    HypothesisComparison,
}

public enum ProjectionLevel
{
    Full,
    Normal,
    Compact,
    Emergency
}

public sealed record ProjectionRequest(
    ProjectionType Type,
    ProjectionLevel Level,
    string? WorkUnitId = null,
    string? BranchId = null,
    string? AgentId = null,
    // RunRetrospective-only — every other handler ignores these. Null = all-time.
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null);

public sealed record ProjectionResult(
    ProjectionType Type,
    ProjectionLevel Level,
    string DataJson,
    DateTimeOffset GeneratedAt);

/// <summary>
/// WorkUnit projection payload shape at Normal level and above.
/// </summary>
public sealed record WorkUnitProjectionPayload(
    string WorkUnitId,
    string Goal,
    string BranchId,
    string Status,
    IReadOnlyList<string> ActiveTasks,
    IReadOnlyList<string> Dependencies,
    string? SuccessCriteria,
    IReadOnlyList<string> AssignedAgents);

public sealed record AuthoritativeStateProjectionPayload(
    string BranchId,
    IReadOnlyDictionary<string, string> MergedState);

public sealed record TaskProjectionPayload(
    IReadOnlyList<string> OpenTasks,
    IReadOnlyList<string> BlockedTasks,
    IReadOnlyList<string> CompletedTasks,
    IReadOnlyDictionary<string, string> Assignments);

public sealed record MergeProposalProjectionPayload(
    IReadOnlyList<string> PendingProposals,
    IReadOnlyDictionary<string, string> ReviewStatus,
    IReadOnlyList<string> VerificationResults);

public sealed record ExecutionSnapshotProjectionPayload(
    string AgentId,
    string? CurrentGoal,
    IReadOnlyList<string> FailureHistory,
    IReadOnlyList<string> RecoveryHints);

public sealed record ArtifactChain(
    IReadOnlyList<ArtifactRef> Artifacts);

/// <summary>Phase 9e — compact per-root summary (path + stack) for agent context; the full WorkspaceProfile (commands, run state) is available via nm_v1_workspace_profile_get. Phase 9h adds RuleFileContent so the projection delta carries a root's AGENTS.md-equivalent without a separate call.</summary>
public sealed record ProjectRootSummary(string RelativePath, string Stack, string? RuleFileContent = null);

// Phase 4.5 — recent op history per file, surfaced in the AgentWorkspace projection.
public sealed record FileOpSummary(
    string OperationId,
    string? WorkUnitId,
    string? WorkUnitGoal,
    string? AgentId,
    string OperationType,
    string? Reason,
    DateTimeOffset OccurredAt);

public sealed record FileOpHistory(
    string Path,
    IReadOnlyList<FileOpSummary> RecentOps);

// Phase 11.5 — slim co-modification hint included in agent workspace projections.
// PathA/PathB are the co-modified pair; Confidence is CoModCount/TotalWorkUnitsScanned.
public sealed record CoModHint(string PathA, string PathB, double Confidence);

public sealed record AgentWorkspaceProjectionPayload(
    string? AgentId,
    string? WorkUnitId,
    ArtifactChain Artifacts,
    IReadOnlyList<ArtifactRef> InheritedConstraints,
    WorkspaceExecutionSummary? Execution = null,
    IReadOnlyList<ProjectRootSummary>? Roots = null,
    IReadOnlyList<FileOpHistory>? RecentFileOps = null,
    IReadOnlyList<CoModHint>? CoModHints = null);

/// <summary>
/// What changed in a work unit's artifact chain since the last cycle. Lets an orchestrator
/// reason incrementally instead of re-reading the full projection every time, and lets the loop
/// detect stalls (no change for N consecutive cycles).
/// </summary>
public sealed record ProjectionDelta(
    string WorkUnitId,
    AgentWorkspaceProjectionPayload Previous,
    AgentWorkspaceProjectionPayload Current,
    IReadOnlyList<ArtifactRef> AddedArtifacts,
    IReadOnlyList<ArtifactRef> RemovedArtifacts,
    IReadOnlyList<ArtifactRef> StatusChangedArtifacts,
    IReadOnlyList<string> CompletedTaskIds,
    bool AnyChange)
{
    /// <summary>
    /// Diffs two AgentWorkspace snapshots on ArtifactId/Status. Artifacts are never deleted from
    /// the chain (it's append/replace lineage), so "removed" means a previously-active artifact
    /// transitioned to a terminal (non-Active) status, not that it disappeared.
    /// </summary>
    public static ProjectionDelta Compute(
        string workUnitId, AgentWorkspaceProjectionPayload previous, AgentWorkspaceProjectionPayload current)
    {
        var prevById = previous.Artifacts.Artifacts.ToDictionary(a => a.ArtifactId);
        var added = current.Artifacts.Artifacts.Where(a => !prevById.ContainsKey(a.ArtifactId)).ToList();
        var statusChanged = current.Artifacts.Artifacts
            .Where(a => prevById.TryGetValue(a.ArtifactId, out var p) && p.Status != a.Status)
            .ToList();
        var removed = statusChanged.Where(a => a.Status != ArtifactStatus.Active).ToList();
        var completedTaskIds = added.Concat(statusChanged)
            .Where(a => a.Type == ArtifactType.Task && a.Status == ArtifactStatus.Applied)
            .Select(a => a.ArtifactId)
            .ToList();

        return new ProjectionDelta(
            workUnitId, previous, current, added, removed, statusChanged, completedTaskIds,
            AnyChange: added.Count > 0 || removed.Count > 0 || statusChanged.Count > 0);
    }
}

/// <summary>
/// Capability-gap fix — a persisted, immutable capture of an AgentWorkspace projection at a point
/// in time. WorkUnitId stays the one identity (no parallel ProjectionId/WorkspaceId scheme);
/// SnapshotId only identifies this particular capture, the same way KnownGoodState.StateId
/// identifies a captured branch state without inventing a second "workspace" concept.
/// </summary>
public sealed record ProjectionSnapshot(
    string SnapshotId,
    string WorkUnitId,
    AgentWorkspaceProjectionPayload Payload,
    DateTimeOffset CreatedAt);

/// <summary>
/// Result of IProjectionSnapshotService.MaterializeAsync — runs a build/test/lint execution
/// against the work unit's branch, then immediately captures a snapshot, so the two are returned
/// together as one atomic "run it and freeze the result" operation.
/// </summary>
public sealed record ProjectionMaterializationResult(
    BranchExecutionResult Execution,
    ProjectionSnapshot Snapshot);

/// <summary>
/// Result of re-checking a snapshot's referenced artifacts against their current state. Reads the
/// InvalidatedByArtifactId/Status flags ArtifactLineageService.InvalidateAsync's cascade already
/// sets — no separate propagation logic, this just surfaces what already happened at the artifact
/// layer.
/// </summary>
public sealed record ProjectionStaleness(
    string SnapshotId,
    bool IsStale,
    IReadOnlyList<string> StaleArtifactIds);

public sealed record ArtifactStatusDivergence(string ArtifactId, ArtifactStatus StatusA, ArtifactStatus StatusB);

/// <summary>
/// Symmetric diff between two sibling projection snapshots (e.g. a React fork vs a Vue fork) —
/// deliberately distinct from ProjectionDelta.Compute above, whose "added/removed" semantics are
/// temporal (cycle N vs N+1 of the *same* work unit, where artifacts are never reported missing,
/// only status-changed). Comparing two different work units' snapshots needs real set difference:
/// an artifact only present in one side is OnlyInA/OnlyInB, not "added since previous."
/// </summary>
public sealed record ProjectionComparison(
    string SnapshotIdA,
    string SnapshotIdB,
    IReadOnlyList<ArtifactRef> OnlyInA,
    IReadOnlyList<ArtifactRef> OnlyInB,
    IReadOnlyList<ArtifactStatusDivergence> DifferingStatus)
{
    public static ProjectionComparison Compute(
        string snapshotIdA, string snapshotIdB,
        AgentWorkspaceProjectionPayload a, AgentWorkspaceProjectionPayload b)
    {
        var allA = a.Artifacts.Artifacts.Concat(a.InheritedConstraints).ToDictionary(x => x.ArtifactId);
        var allB = b.Artifacts.Artifacts.Concat(b.InheritedConstraints).ToDictionary(x => x.ArtifactId);

        var onlyInA = allA.Where(kv => !allB.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var onlyInB = allB.Where(kv => !allA.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var differingStatus = allA
            .Where(kv => allB.TryGetValue(kv.Key, out var other) && other.Status != kv.Value.Status)
            .Select(kv => new ArtifactStatusDivergence(kv.Key, kv.Value.Status, allB[kv.Key].Status))
            .ToList();

        return new ProjectionComparison(snapshotIdA, snapshotIdB, onlyInA, onlyInB, differingStatus);
    }
}

/// <summary>
/// GoalGraph projection — DAG of goal nodes for the decision tree.
/// </summary>
public sealed record GoalGraphProjectionPayload(
    IReadOnlyList<GoalGraphNode> Nodes);

public sealed record GoalGraphNode(
    string GoalId,
    string Goal,
    string WorkUnitId,
    string BranchId,
    string Status,
    string? ParentGoalId,
    IReadOnlyList<string> ChildGoalIds,
    string Owner,
    string? AssignedAgent,
    int ProposalCount,
    DateTimeOffset CreatedAt);

/// <summary>
/// EvidenceLedger projection — evidentiary basis for a work unit or proposal.
/// </summary>
public sealed record EvidenceLedgerProjectionPayload(
    string WorkUnitId,
    IReadOnlyList<EvidenceEntry> Entries);

public sealed record EvidenceEntry(
    string EvidenceId,
    EvidenceKind Kind,
    string Summary,
    string? DetailJson,
    DateTimeOffset AttachedAt);

/// <summary>
/// ModelDivergenceView projection — side-by-side diff of outputs from two models.
/// </summary>
public sealed record ModelDivergenceProjectionPayload(
    string ModelA,
    string ModelB,
    IReadOnlyList<ModelDivergenceFile> DivergedFiles,
    DateTimeOffset ComparedAt);

public sealed record ModelDivergenceFile(
    string Path,
    string DiffAText,
    string DiffBText,
    IReadOnlyList<string> OverlappingLines);

/// <summary>
/// ReasoningCommitGraph projection — reasoning→model→execution→convergence graph
/// built from orchestration decision log events, decision records, and execution evidence.
/// </summary>
public sealed record ReasoningCommitGraphProjectionPayload(
    string RootWorkUnitId,
    IReadOnlyList<ReasoningCommitGraphNode> Nodes,
    IReadOnlyList<ReasoningCommitGraphEdge> Edges);

/// <summary>
/// A single commit in the reasoning graph. Each node corresponds to either an
/// orchestration decision log event (reasoning step) or a convergence decision record.
/// </summary>
public sealed record ReasoningCommitGraphNode(
    string CommitId,
    string WorkUnitId,
    string? AgentId,
    string Stage,
    string Action,
    string? Reasoning,
    string? AgentModel,
    string? AgentProvider,
    DateTimeOffset OccurredAt);

/// <summary>
/// Typed edge connecting two reasoning commits.
/// EdgeType values: Refine, Fork, Replace, Merge, Invalidate, EvidenceAttached, Decided.
/// </summary>
public sealed record ReasoningCommitGraphEdge(
    string FromCommitId,
    string ToCommitId,
    string EdgeType);

/// <summary>
/// DecisionContext projection — structured decision audit for a work unit.
/// Assembles goal, plan, assumptions, constraints, evidence, allowed tools, and
/// execution results without exposing raw prompt text.
/// </summary>
public sealed record DecisionContextProjectionPayload(
    string WorkUnitId,
    string Goal,
    IReadOnlyList<DecisionContextPlanEntry> Plan,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<DecisionContextEvidenceEntry> Evidence,
    IReadOnlyList<string> AllowedTools,
    DecisionContextExecutionSummary? Execution,
    string? AgentModel,
    string? AgentProvider,
    string? SteeredFromDecisionId);

public sealed record DecisionContextPlanEntry(
    string SliceId,
    string Goal,
    IReadOnlyList<string> FileScope,
    IReadOnlyList<string> Steps);

public sealed record DecisionContextEvidenceEntry(
    string Kind,
    string Summary,
    bool Success);

public sealed record DecisionContextExecutionSummary(
    bool AllSucceeded,
    IReadOnlyList<string> BuildSystems,
    string? TestSummary,
    DateTimeOffset ExecutedAt);

/// <summary>
/// CounterfactualComparison projection — side-by-side comparison of an original work unit
/// and a counterfactual (different model/profile) work unit that branched from the same proposal.
/// </summary>
public sealed record CounterfactualComparisonProjectionPayload(
    string OriginalWorkUnitId,
    string CounterfactualWorkUnitId,
    string OriginalProposalId,
    IReadOnlyList<CounterfactualComparisonProposal> Originals,
    IReadOnlyList<CounterfactualComparisonProposal> Counterfactuals,
    string? OriginalModel,
    string? OriginalProvider,
    string? CounterfactualModel,
    string? CounterfactualProvider,
    string? WhichWasBetter,
    DateTimeOffset ComparedAt);

public sealed record CounterfactualComparisonProposal(
    string ProposalId,
    string Goal,
    string Status,
    string? Model,
    string? Provider,
    double? Confidence,
    IReadOnlyList<string> FilesTouched,
    string? DiffSummary);

/// <summary>
/// HypothesisComparison projection — deterministic, evidence-based aggregation across N sibling
/// forks of an experiment (HypothesisForkType.Model/Architecture/Library/Product). Unlike
/// ModelDivergenceView/CounterfactualComparison (which compare exactly 2 proposals' raw fields),
/// this aggregates EvidenceNode/proposal data into a score per sibling and a recommendation. It
/// recommends; it does not decide — convergence still requires an explicit ConvergeAsync call.
/// </summary>
public sealed record HypothesisComparisonProjectionPayload(
    string ParentWorkUnitId,
    IReadOnlyList<HypothesisComparisonSibling> Siblings,
    string? RecommendedWorkUnitId,
    DateTimeOffset ComparedAt);

public sealed record HypothesisComparisonSibling(
    string WorkUnitId,
    string? HypothesisId,
    string ForkType,
    string HypothesisStatus,
    string? LatestProposalId,
    string? ProposalStatus,
    double? Confidence,
    int EvidenceCount,
    IReadOnlyList<string> EvidenceSummaries,
    double Score);

/// <summary>
/// RunRetrospective projection — analytics dashboard for the Insights tab. Aggregates outcomes
/// across the entire DAG history (all work units/proposals/decisions/sessions), computed fresh on
/// every request — there is no scheduled or background trigger, only the user-initiated "Run
/// Analysis" action. Each stat list below is deliberately a flat row shape so a future phase could
/// promote an individual row into a reviewable "Insight" finding without reshaping this payload.
/// </summary>
public sealed record RunRetrospectiveProjectionPayload(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    int TotalSessions,
    IReadOnlyDictionary<string, int> SessionsByStatus,
    int TotalWorkUnits,
    IReadOnlyDictionary<string, int> WorkUnitsByStatus,
    double OverallSuccessRate,
    double AverageReworkCycles,
    FailureCauseStat? TopFailureCause,
    ModelPerformanceStat? MostSuccessfulModel,
    ForkWinRateStat? MostSuccessfulStrategy,
    IReadOnlyList<ModelPerformanceStat> ModelPerformance,
    IReadOnlyList<ModelStagePerformanceStat> ModelPerformanceByStage,
    IReadOnlyList<ForkWinRateStat> ForkWinRates,
    IReadOnlyList<ForkConstraintWinRateStat> ForkConstraintWinRates,
    IReadOnlyList<FailureCauseStat> FailureCauses,
    IReadOnlyList<ReviewOutcomeStat> ReviewOutcomes,
    DateTimeOffset GeneratedAt);

public sealed record ModelPerformanceStat(
    string Model,
    string? Provider,
    int ProposalCount,
    int MergedCount,
    int RejectedCount,
    double AcceptanceRate,
    double? AvgConfidence);

public sealed record ForkWinRateStat(
    string ForkType,
    int TotalForks,
    int Wins,
    int Losses,
    int Pending,
    double WinRate);

/// <summary>Stage is resolved heuristically from WorkUnit.Owner — when a work unit was created via
/// a strategy/template, Owner is the orchestrating AgentProfile's id; rows where Owner doesn't match
/// a known profile (e.g. "user") are excluded rather than mislabeled.</summary>
public sealed record ModelStagePerformanceStat(
    string Model,
    string Stage,
    int ProposalCount,
    int MergedCount,
    int RejectedCount,
    double AcceptanceRate);

/// <summary>Sub-bucket of an Architecture/Library/Product fork by the specific constraint text the
/// fork was created with (e.g. "Mapster" vs "AutoMapper") — sourced from the structured
/// WorkUnit.Metadata key ExperimentService stores per fork type, not free-text parsing.</summary>
public sealed record ForkConstraintWinRateStat(
    string ForkType,
    string Constraint,
    int TotalForks,
    int Wins,
    int Losses,
    int Pending,
    double WinRate);

/// <summary>Category is one of: ExecutionFailure, AutomatedReviewRejection, HumanReviewRejection —
/// sourced from WorkUnit.ExecutionInfo's typed counters rather than free-text evidence parsing.</summary>
public sealed record FailureCauseStat(
    string Category,
    int TotalCount,
    int WorkUnitsAffected);

public sealed record ReviewOutcomeStat(
    string Outcome,
    int Count,
    double? AvgConfidence);

public static class ProjectionCatalog
{
    public static IReadOnlyList<string> Types { get; } =
        Enum.GetNames<ProjectionType>();

    public static IReadOnlyList<string> Levels { get; } =
        Enum.GetNames<ProjectionLevel>();
}
