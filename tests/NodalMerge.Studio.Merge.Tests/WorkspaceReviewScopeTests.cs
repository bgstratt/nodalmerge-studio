using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Merge.Tests;

// Regression guard for the CAS-6.3a collision: InMemoryWorkUnitService now makes every fanned-out
// child inherit its parent goal's RepositoryId (for snapshot/CAS lookups), so "has a RepositoryId"
// can no longer stand in for "is an independent real-repo unit." AppliesToRealRepo must key the
// distinction on FanOutInfo.SliceId — set on (and only on) a fanned-out plan slice child — or a
// repo-linked goal's fan-out children get routed to WorkspaceReviewPolicy (never propagated to
// children, so defaulted HumanRequired) instead of their inherited TaskReviewPolicy, and their own
// applies become eligible to write to disk / target "main" instead of rolling up through the parent.
public class WorkspaceReviewScopeTests
{
    [Fact]
    public void Null_work_unit_applies_to_real_repo()
        => Assert.True(WorkspaceReviewScope.AppliesToRealRepo(null));

    [Fact]
    public void Top_level_goal_applies_to_real_repo()
        => Assert.True(WorkspaceReviewScope.AppliesToRealRepo(
            MakeWorkUnit(parentWorkUnitId: null, repositoryId: "repo-1", fanOut: null)));

    [Fact]
    public void Independent_repo_linked_child_without_a_slice_applies_to_real_repo()
        // Multi-Model comparison / experiment / steering forks: a grouping parent + their OWN repoId,
        // but never a SliceId. They must stay real-repo (gated by WorkspaceReviewPolicy).
        => Assert.True(WorkspaceReviewScope.AppliesToRealRepo(
            MakeWorkUnit(parentWorkUnitId: "WU-parent", repositoryId: "repo-1", fanOut: null)));

    [Fact]
    public void Fan_out_leaf_child_with_inherited_repo_is_not_real_repo()
        // THE FIX: parent set + inherited repoId + a SliceId => task-scoped, gated by TaskReviewPolicy.
        => Assert.False(WorkspaceReviewScope.AppliesToRealRepo(
            MakeWorkUnit(
                parentWorkUnitId: "WU-parent",
                repositoryId: "repo-1",
                fanOut: new WorkUnitFanOutInfo("s1", SeedFromBranchId: null))));

    [Fact]
    public void Fan_out_compound_child_with_inherited_repo_is_not_real_repo()
        // A compound sub-planner is an interior node — its reconciled proposal rolls up into the root,
        // it never writes to disk itself, so it too must be task-scoped despite the inherited repoId.
        => Assert.False(WorkspaceReviewScope.AppliesToRealRepo(
            MakeWorkUnit(
                parentWorkUnitId: "WU-parent",
                repositoryId: "repo-1",
                fanOut: new WorkUnitFanOutInfo("s1", SeedFromBranchId: null, Kind: PlanSliceKind.Compound))));

    [Fact]
    public void Plain_child_without_repo_or_slice_is_not_real_repo()
        // Unchanged legacy behavior: a child with neither a repoId nor a SliceId (e.g. the in-memory
        // no-repo test path) is task-scoped via the ParentWorkUnitId branch.
        => Assert.False(WorkspaceReviewScope.AppliesToRealRepo(
            MakeWorkUnit(parentWorkUnitId: "WU-parent", repositoryId: null, fanOut: null)));

    private static WorkUnit MakeWorkUnit(
        string? parentWorkUnitId, string? repositoryId, WorkUnitFanOutInfo? fanOut) =>
        new(
            "WU-1",
            "goal",
            "main",
            WorkUnitStatus.Proposed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "owner",
            null,
            null,
            null,
            parentWorkUnitId,
            [],
            [],
            FanOutInfo: fanOut,
            RepositoryId: repositoryId);
}
