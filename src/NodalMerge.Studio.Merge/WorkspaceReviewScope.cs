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
// The RepositoryId test alone is NO LONGER sufficient to identify those independent units. CAS
// Slice 6.3a (InMemoryWorkUnitService.CreateWorkUnitAsync) makes EVERY fanned-out child inherit its
// parent's RepositoryId — a fan-out child created with repositoryId:null now resolves the goal's id
// so snapshot/CAS lookups work. Before 6.3a, "has a RepositoryId" implied "was explicitly linked to
// a repo" implied "independent real-repo unit"; after it, ordinary task children carry one too. Left
// as just `RepositoryId is not null`, this classified every fan-out child in a repo-linked goal as
// real-repo — routing it to WorkspaceReviewPolicy (which is never propagated to children, so it
// defaults to HumanRequired) instead of its inherited TaskReviewPolicy, stranding AgentApproval
// children at review; and, symmetrically, marking its own apply eligible to write to disk / target
// "main" instead of rolling up through merge/{parent}. Both are the same misclassification.
//
// The reliable discriminator is FanOutInfo.SliceId: it is set on (and only on) a fanned-out plan
// slice child — leaf or compound sub-planner. The genuinely-independent repo-linked units
// (Multi-Model comparison, ExperimentService/CounterfactualService/SteeringService forks) are
// created via CreateWorkUnitAsync/CreateAsync with a repositoryId but NEVER a sliceId, so they still
// qualify. So: a unit applies to the real repo iff it's top-level, OR it has its own RepositoryId
// AND is not a fan-out slice child.
//
// A null workUnit means the proposal isn't tracked against any WorkUnit at all (no WorkUnitId, or
// no IWorkUnitService registered) — the legacy/direct-spawn path predating work-unit tracking, not
// a fan-out child (those always resolve to a real WorkUnit with ParentWorkUnitId set). That's
// equivalent to a plain top-level goal, so it qualifies too.
public static class WorkspaceReviewScope
{
    public static bool AppliesToRealRepo(WorkUnit? workUnit) =>
        workUnit is null
        || workUnit.ParentWorkUnitId is null
        || (workUnit.RepositoryId is not null && workUnit.FanOutInfo?.SliceId is null);
}
