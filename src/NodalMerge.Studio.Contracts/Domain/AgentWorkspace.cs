namespace NodalMerge.Studio.Contracts.Domain;

// v1 backing: wraps the work unit's existing branch/file-copy isolation (see InMemoryWorkUnitService
// + IFileWorkspaceService). Real git worktrees are a future, pluggable backing model — not implemented
// yet — so this enum intentionally does not claim worktree semantics.
public enum WorkspaceBackingModel
{
    LogicalBranchWorkspace,
}

public sealed record AgentWorkspace(
    string WorkspaceId,
    string WorkUnitId,
    WorkspaceBackingModel BackingModel,
    string BranchName,
    string BaseBranch,
    string? Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DestroyedAt);
