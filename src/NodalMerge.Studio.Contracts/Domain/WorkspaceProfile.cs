namespace NodalMerge.Studio.Contracts.Domain;

// Phase 9a — describes the sub-project roots detected inside a branch's working directory, so
// build/test/run and agent context can be scoped per-root instead of assuming a single project at
// the branch root.
public sealed record ProjectRoot(
    string RelativePath,      // "" for the branch root, "frontend", "backend/Host", etc. ('/' separators)
    string Stack,             // "dotnet", "npm", "cargo", "go", "python", "make", "cmake", "none" (rule-file-only)
    string? BuildCommand,     // null = stack has no build step (e.g. python) or none could be resolved
    string? TestCommand,
    string? RunCommand,       // null = no unambiguous entry point detected
    bool IsLongRunning,       // true = RunCommand starts a server/long-lived process, not a one-shot command
    string? RuleFileContent = null); // Phase 9h — first of AGENTS.md/CLAUDE.md/.clinerules/.cursorrules found in this root's directory, capped at 4000 chars

public sealed record WorkspaceProfile(
    string BranchId,
    IReadOnlyList<ProjectRoot> Roots,
    DateTimeOffset DetectedAt);
