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
    string? AgentId = null);

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

public sealed record AgentWorkspaceProjectionPayload(
    string? AgentId,
    string? WorkUnitId,
    ArtifactChain Artifacts,
    IReadOnlyList<ArtifactRef> InheritedConstraints,
    WorkspaceExecutionSummary? Execution = null);

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

public static class ProjectionCatalog
{
    public static IReadOnlyList<string> Types { get; } =
        Enum.GetNames<ProjectionType>();

    public static IReadOnlyList<string> Levels { get; } =
        Enum.GetNames<ProjectionLevel>();
}
