namespace NodalMerge.Studio.Storage;

// Shared path-filtering logic used by FileSystemWorkspaceService (branch diffs/copies) and
// RepositoryImportService (CAS bootstrap). Kept internal — callers outside this assembly don't
// need to reason about which filesystem paths are importable.
internal static class WorkspacePathFilter
{
    // Dependency/build directories that are regenerable and should never be treated as source
    // content. Mirrors WorkspaceProfileService.IgnoredDirNames.
    // .workspace (plans/harness-hosting-architecture.md) is the harness contract directory, not
    // source content — unlike other dotfiles (e.g. .env), it must be excluded here explicitly:
    // FileSystemWorkspaceService.IsHidden (diff/list) already treats every dot-prefixed segment as
    // hidden, but this list is also consulted by RepositoryImportService's CAS snapshot walk, which
    // deliberately does NOT treat dotfiles as hidden (so .env seeds correctly) — .workspace would
    // otherwise leak into RepositorySnapshot and future branch seeds.
    internal static readonly string[] IgnoredDirNames =
        ["node_modules", "bin", "obj", "dist", "build", "target", "__pycache__", "venv", ".git", ".nodalmerge", ".workspace"];

    // Returns true when any path segment matches an ignored directory name (case-insensitive).
    // Used for both branch workspace diffs and repo-root file walks.
    internal static bool IsIgnoredDirSegment(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => IgnoredDirNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
