namespace NodalMerge.Studio.Storage;

// Shared path-filtering logic used by FileSystemWorkspaceService (branch diffs/copies) and
// RepositoryImportService (CAS bootstrap). Kept internal — callers outside this assembly don't
// need to reason about which filesystem paths are importable.
internal static class WorkspacePathFilter
{
    // Dependency/build directories that are regenerable and should never be treated as source
    // content. Mirrors WorkspaceProfileService.IgnoredDirNames.
    internal static readonly string[] IgnoredDirNames =
        ["node_modules", "bin", "obj", "dist", "build", "target", "__pycache__", "venv", ".git", ".nodalmerge"];

    // Returns true when any path segment matches an ignored directory name (case-insensitive).
    // Used for both branch workspace diffs and repo-root file walks.
    internal static bool IsIgnoredDirSegment(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => IgnoredDirNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
