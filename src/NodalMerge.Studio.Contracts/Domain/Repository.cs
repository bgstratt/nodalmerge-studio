namespace NodalMerge.Studio.Contracts.Domain;

// Capability-gap fix — informational registry of repository paths agents have referenced, so a
// "create a repository elsewhere" request can be discovered/registered instead of guessed at. Does
// not change which repository is actually seeded (WorkspaceOptions.SeedRepositoryPath) — Studio
// still manages exactly one active repository per instance until real multi-repository support
// exists.
public sealed record RepositoryV1(
    string RepositoryId,
    string Path,
    string? Label,
    DateTimeOffset RegisteredAt);

// Cross-repo file reference — a pointer to a file in a *different* registered repository than the
// one a work unit is actually seeded from, used for read-only context (style/examples) during a
// run. Distinct from WorkUnit.FileScope, which gates which files an agent may write to.
public sealed record FileReferenceV1(string RepositoryId, string Path, string? Note = null);
