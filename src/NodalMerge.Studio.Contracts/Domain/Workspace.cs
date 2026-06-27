namespace NodalMerge.Studio.Contracts.Domain;

// The persistent engineering context that owns Repositories and (transitively, via
// WorkUnit.WorkspaceId) every Goal/WorkUnit/Artifact/Experiment. Cardinality is 1 per Studio
// instance today — WorkspaceId is always "workspace-default" — but every owned entity already
// carries an explicit WorkspaceId so a second workspace is additive later, not a rewrite. See
// plans/phase-16-workspace-aggregate.md.
public sealed record WorkspaceV1(
    string WorkspaceId,
    string Name,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> RepositoryIds);
