namespace NodalMerge.Studio.Contracts.Domain;

public enum SyncReason { InitialBootstrap, RepositoryDrift, RepositorySwitch }

public enum SyncTrigger { GoalCreation, ManualRefresh, StartupRecovery, PostMergeWriteBack }

// Persisted, one per synced branch (today only ever "main"). Generation/LatestExternalChangesetId/
// RepositoryIdentityEstablishedAt are scoped to RepositoryPath's *current* identity — a
// RepositorySwitch resets all three, even when the switch itself diffs empty.
public sealed record RepositorySyncState(
    string BranchId,
    string? RepositoryPath,
    string? RepositoryFingerprint,
    int Generation,
    string? LatestExternalChangesetId,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? RepositoryIdentityEstablishedAt);

public sealed record PendingExternalSync(
    string ArtifactId,
    SyncReason Reason,
    int WorkspaceGeneration,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Modified,
    IReadOnlyList<string> Deleted);
