using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Merge;

// Shared definition of "this work unit's own apply is allowed to reach the real on-disk repo,"
// used identically by AutoReviewRule/MergeCommandService (to pick WorkspaceReviewPolicy vs
// TaskReviewPolicy) and InMemoryMergeService (to gate the disk write-back itself). Keeping one
// definition avoids the two ever drifting apart — a work unit governed by WorkspaceReviewPolicy
// but blocked from writing to disk (or vice versa) would be a real correctness bug.
//
// A plain top-level goal (ParentWorkUnitId is null) always qualifies. So does any work unit that
// was explicitly given its own RepositoryId at creation — this covers Multi-Model Comparison's
// per-model children (clients/vscode-extension ArtifactExplorerPanel.ts posts a repositoryPath for
// each child directly, and WorkUnitCommandService.CreateAsync auto-registers a RepositoryId for
// any work unit given one, regardless of ParentWorkUnitId), which are deliberately independent,
// repo-linked orchestrator runs sharing a comparison parent only for grouping in the UI.
//
// FanOutService's fanned-out task children and ExperimentService's generic comparison forks never
// receive their own RepositoryId (both bypass WorkUnitCommandService and call
// IOrchestratorService.CreateWorkUnitAsync directly with no repositoryId) — so they correctly stay
// excluded and remain gated by TaskReviewPolicy, never touching disk.
//
// A null workUnit means the proposal isn't tracked against any WorkUnit at all (no WorkUnitId, or
// no IWorkUnitService registered) — the legacy/direct-spawn path predating work-unit tracking, not
// a fan-out child (those always resolve to a real WorkUnit with ParentWorkUnitId set). That's
// equivalent to a plain top-level goal, so it qualifies too.
public static class WorkspaceReviewScope
{
    public static bool AppliesToRealRepo(WorkUnit? workUnit) =>
        workUnit is null || workUnit.ParentWorkUnitId is null || workUnit.RepositoryId is not null;
}
