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
