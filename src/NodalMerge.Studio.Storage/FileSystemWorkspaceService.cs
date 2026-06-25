using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class FileSystemWorkspaceService(WorkspaceOptions options) : IFileWorkspaceService
{
    public Task InitBranchAsync(string branchId, string? seedFromBranchId = null, CancellationToken ct = default)
    {
        var branchDir = BranchDir(branchId);
        // Directory.Exists alone isn't enough: a branch dir can be created empty (e.g. main,
        // before any SeedRepositoryPath was ever supplied) and would otherwise never become
        // eligible for seeding again, since this method no-ops on every subsequent call.
        if (Directory.Exists(branchDir) && Directory.EnumerateFileSystemEntries(branchDir).Any())
            return Task.CompletedTask;

        Directory.CreateDirectory(branchDir);

        if (seedFromBranchId is not null)
        {
            var seedDir = BranchDir(seedFromBranchId);
            if (Directory.Exists(seedDir) && Directory.EnumerateFileSystemEntries(seedDir).Any())
            {
                CopyDirectory(seedDir, branchDir);
                return Task.CompletedTask;
            }
        }

        if (string.Equals(branchId, "main", StringComparison.OrdinalIgnoreCase)
            && options.SeedRepositoryPath is { Length: > 0 } seed
            && Directory.Exists(seed))
        {
            CopyDirectory(seed, branchDir);
        }

        return Task.CompletedTask;
    }

    public async Task<string?> ReadAsync(string branchId, string relativePath, CancellationToken ct = default)
    {
        var fullPath = SafePath(branchId, relativePath);
        if (!File.Exists(fullPath))
            return null;

        var info = new FileInfo(fullPath);
        if (info.Length > options.MaxReadBytes)
            throw new InvalidOperationException(
                $"File '{relativePath}' is {info.Length:N0} bytes, which exceeds the read limit of {options.MaxReadBytes:N0} bytes.");

        return await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkspaceFileRead>> ReadManyAsync(
        string branchId, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var results = new List<WorkspaceFileRead>(paths.Count);
        foreach (var path in paths)
        {
            var content = await ReadAsync(branchId, path, ct).ConfigureAwait(false);
            results.Add(new WorkspaceFileRead(path, content, Found: content is not null));
        }
        return results;
    }

    public async Task WriteAsync(string branchId, string relativePath, string content, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > options.MaxWriteBytes)
            throw new InvalidOperationException(
                $"Content is {bytes:N0} bytes, which exceeds the write limit of {options.MaxWriteBytes:N0} bytes.");

        var fullPath = SafePath(branchId, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(fullPath, content, ct).ConfigureAwait(false);
    }

    public async Task<WorkspaceReplaceResult> ReplaceAsync(
        string branchId, string relativePath, string oldText, string newText, int expectedMatches = 1,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(oldText))
            throw new ArgumentException("oldText must not be empty.");

        var content = await ReadAsync(branchId, relativePath, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"File '{relativePath}' not found in branch '{branchId}'.");

        var actualMatches = CountOccurrences(content, oldText);
        if (actualMatches != expectedMatches)
            throw new InvalidOperationException(
                $"Expected {expectedMatches} occurrence(s) of oldText in '{relativePath}' but found {actualMatches}.");

        var diff = BuildReplaceDiff(content, oldText, newText);
        var updated = content.Replace(oldText, newText, StringComparison.Ordinal);

        await WriteAsync(branchId, relativePath, updated, ct).ConfigureAwait(false);

        return new WorkspaceReplaceResult(actualMatches, content.Length, updated.Length, diff);
    }

    private static int CountOccurrences(string content, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // Bounded by construction: one "@@ line {n} @@ / - / +" block per occurrence, and occurrence
    // count was already validated against expectedMatches before this runs.
    private static string BuildReplaceDiff(string content, string oldText, string newText)
    {
        var sb = new StringBuilder();
        var searchIndex = 0;
        int occurrence;
        while ((occurrence = content.IndexOf(oldText, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            var line = content[..occurrence].Count(c => c == '\n') + 1;
            sb.AppendLine($"@@ line {line} @@");
            foreach (var l in oldText.Split('\n'))
                sb.AppendLine($"- {l.TrimEnd('\r')}");
            foreach (var l in newText.Split('\n'))
                sb.AppendLine($"+ {l.TrimEnd('\r')}");
            searchIndex = occurrence + oldText.Length;
        }
        return sb.ToString().TrimEnd();
    }

    public Task DeleteAsync(string branchId, string relativePath, CancellationToken ct = default)
    {
        var fullPath = SafePath(branchId, relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string branchId, string relativePath, CancellationToken ct = default)
    {
        var fullPath = SafePath(branchId, relativePath);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task<IReadOnlyList<string>> ListAsync(string branchId, string? subPath = null, string? pattern = null, CancellationToken ct = default)
    {
        var branchDir = BranchDir(branchId);
        var searchRoot = subPath is { Length: > 0 }
            ? SafePath(branchId, subPath)
            : branchDir;

        if (!Directory.Exists(searchRoot))
            return Task.FromResult<IReadOnlyList<string>>([]);

        var matcher = PatternMatcher(pattern);
        var files = Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
            .Where(f => !IsHidden(f))
            .Select(f => Path.GetRelativePath(branchDir, f).Replace('\\', '/'))
            .Where(matcher)
            .OrderBy(f => f)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    // Translates a plain filename ("Foo.cs") into a substring match and a wildcard pattern
    // ("*Foo*"/"Foo?.cs") into a regex — null/empty pattern matches everything.
    private static Func<string, bool> PatternMatcher(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return _ => true;

        var regexPattern = "^.*" + string.Concat(pattern.Select(c => c switch
        {
            '*' => ".*",
            '?' => ".",
            _   => System.Text.RegularExpressions.Regex.Escape(c.ToString())
        })) + ".*$";
        var regex = new System.Text.RegularExpressions.Regex(
            regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return regex.IsMatch;
    }

    public async Task<(IReadOnlyList<WorkspaceSearchMatch> Matches, bool Truncated)> SearchAsync(
        string branchId, string query, string? subPath = null, string? filePattern = null,
        bool regex = false, bool caseSensitive = false, int contextLines = 3, int maxResults = 200,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("query must not be empty.");

        contextLines = Math.Clamp(contextLines, 0, 20);
        maxResults = Math.Clamp(maxResults, 1, 1000);

        var branchDir = BranchDir(branchId);
        var searchRoot = subPath is { Length: > 0 } ? SafePath(branchId, subPath) : branchDir;
        if (!Directory.Exists(searchRoot))
            return ([], false);

        var queryPattern = regex ? query : Regex.Escape(query);
        var queryRegexOptions = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        var queryRegex = new Regex(queryPattern, queryRegexOptions);

        var fileMatcher = PatternMatcher(filePattern);
        var files = Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
            .Where(f => !IsHidden(f))
            .Select(f => Path.GetRelativePath(branchDir, f).Replace('\\', '/'))
            .Where(fileMatcher)
            .OrderBy(f => f, StringComparer.Ordinal);

        var matches = new List<WorkspaceSearchMatch>();
        foreach (var relative in files)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(branchDir, relative);
            var info = new FileInfo(fullPath);
            if (info.Length > options.MaxReadBytes)
                continue;
            if (await IsBinaryAsync(fullPath, ct).ConfigureAwait(false))
                continue;

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(fullPath, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (!queryRegex.IsMatch(lines[i]))
                    continue;

                var start = Math.Max(0, i - contextLines);
                var end = Math.Min(lines.Length - 1, i + contextLines);
                var snippet = string.Join('\n', lines[start..(end + 1)]);
                matches.Add(new WorkspaceSearchMatch(relative, i + 1, start + 1, end + 1, snippet));

                if (matches.Count >= maxResults)
                    return (matches, true);
            }
        }

        return (matches, false);
    }

    // Cheap binary-detection heuristic (same as most grep tools): a null byte anywhere in the first
    // chunk of a file means it isn't text. Without this, scanning a repo containing node_modules
    // (past IsHidden), images, DLLs, or PDFs produces garbage matches or decode exceptions.
    private static async Task<bool> IsBinaryAsync(string fullPath, CancellationToken ct)
    {
        const int SampleSize = 8192;
        var buffer = new byte[SampleSize];
        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var read = await stream.ReadAsync(buffer.AsMemory(0, SampleSize), ct).ConfigureAwait(false);
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
                return true;
        }
        return false;
    }

    public async Task<string> DiffAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default)
    {
        var sourceDir = BranchDir(sourceBranchId);
        var targetDir = BranchDir(targetBranchId);

        var sourceFiles = Directory.Exists(sourceDir)
            ? Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                .Where(f => !IsHidden(f))
                .Select(f => Path.GetRelativePath(sourceDir, f).Replace('\\', '/'))
                .ToHashSet()
            : new HashSet<string>();

        var targetFiles = Directory.Exists(targetDir)
            ? Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories)
                .Where(f => !IsHidden(f))
                .Select(f => Path.GetRelativePath(targetDir, f).Replace('\\', '/'))
                .ToHashSet()
            : new HashSet<string>();

        var added    = sourceFiles.Except(targetFiles).OrderBy(f => f).ToList();
        var deleted  = targetFiles.Except(sourceFiles).OrderBy(f => f).ToList();
        var modified = new List<string>();

        foreach (var file in sourceFiles.Intersect(targetFiles).OrderBy(f => f))
        {
            var sourceText = await File.ReadAllTextAsync(Path.Combine(sourceDir, file), ct).ConfigureAwait(false);
            var targetText = await File.ReadAllTextAsync(Path.Combine(targetDir, file), ct).ConfigureAwait(false);
            if (sourceText != targetText)
                modified.Add(file);
        }

        if (added.Count == 0 && deleted.Count == 0 && modified.Count == 0)
            return $"No differences between {sourceBranchId} and {targetBranchId}.";

        var sb = new StringBuilder();
        sb.AppendLine($"Diff: {sourceBranchId} → {targetBranchId}");
        sb.AppendLine($"Added: {added.Count}  Modified: {modified.Count}  Deleted: {deleted.Count}");
        sb.AppendLine();

        foreach (var file in added)
        {
            sb.AppendLine($"+++ ADDED: {file}");
            var content = await TryReadLimitedAsync(Path.Combine(sourceDir, file), ct).ConfigureAwait(false);
            foreach (var line in content)
                sb.AppendLine($"+ {line}");
            sb.AppendLine();
        }

        foreach (var file in modified)
        {
            sb.AppendLine($"~~~ MODIFIED: {file}");
            var newContent = await TryReadLimitedAsync(Path.Combine(sourceDir, file), ct).ConfigureAwait(false);
            foreach (var line in newContent)
                sb.AppendLine($"+ {line}");
            sb.AppendLine();
        }

        foreach (var file in deleted)
        {
            sb.AppendLine($"--- DELETED: {file}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task CopyFilesAsync(
        string sourceBranchId,
        string targetBranchId,
        IReadOnlyList<string> relativePaths,
        CancellationToken ct = default)
    {
        foreach (var path in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            var content = await ReadAsync(sourceBranchId, path, ct).ConfigureAwait(false);
            if (content is not null)
                await WriteAsync(targetBranchId, path, content, ct).ConfigureAwait(false);
        }
    }

    public async Task ApplyBranchAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default)
    {
        var sourceDir = BranchDir(sourceBranchId);
        var targetDir = BranchDir(targetBranchId);

        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        var sourceRelative = Directory.Exists(sourceDir)
            ? Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                .Where(f => !IsHidden(f))
                .Select(f => Path.GetRelativePath(sourceDir, f).Replace('\\', '/'))
                .Where(rel => !IsPlanArtifact(rel))
                .ToHashSet()
            : new HashSet<string>();

        // Delete files in target that are absent in source (approved diff == merged result). The
        // target's own plan.json (if any) is skipped entirely here, not just left out of deletion —
        // it belongs to the target work unit's own planning, not to this merge's diff, regardless of
        // whether the source happens to have one too.
        if (Directory.Exists(targetDir))
        {
            foreach (var targetFile in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories).Where(f => !IsHidden(f)))
            {
                var rel = Path.GetRelativePath(targetDir, targetFile).Replace('\\', '/');
                if (IsPlanArtifact(rel)) continue;
                if (!sourceRelative.Contains(rel))
                    File.Delete(targetFile);
            }
        }

        // Copy all source files to target
        foreach (var rel in sourceRelative)
        {
            ct.ThrowIfCancellationRequested();
            var srcFull = Path.Combine(sourceDir, rel);
            var dstFull = Path.Combine(targetDir, rel);
            var dstDirPath = Path.GetDirectoryName(dstFull)!;
            if (!Directory.Exists(dstDirPath))
                Directory.CreateDirectory(dstDirPath);
            File.Copy(srcFull, dstFull, overwrite: true);
        }
    }

    public Task<string?> GetWorkingDirectoryAsync(string branchId, CancellationToken ct = default)
    {
        var dir = BranchDir(branchId);
        return Task.FromResult<string?>(Directory.Exists(dir) ? dir : null);
    }

    public async Task<WorkspaceDiff> DiffExternalPathAsync(string branchId, string externalPath, CancellationToken ct = default)
    {
        var branchDir = BranchDir(branchId);

        var branchFiles = Directory.Exists(branchDir)
            ? Directory.EnumerateFiles(branchDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(branchDir, f).Replace('\\', '/'))
                .Where(rel => !IsIgnoredDirSegment(rel) && !IsPlanArtifact(rel))
                .ToHashSet()
            : new HashSet<string>();

        var externalEntries = EnumerateExternalEntries(externalPath);
        var externalFiles = externalEntries.Keys.ToHashSet();

        var added   = externalFiles.Except(branchFiles).OrderBy(f => f).ToList();
        var deleted = branchFiles.Except(externalFiles).OrderBy(f => f).ToList();
        var modified = new List<string>();

        foreach (var file in externalFiles.Intersect(branchFiles).OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();
            var branchText   = await File.ReadAllTextAsync(Path.Combine(branchDir, file), ct).ConfigureAwait(false);
            var externalText = await File.ReadAllTextAsync(Path.Combine(externalPath, file), ct).ConfigureAwait(false);
            if (branchText != externalText)
                modified.Add(file);
        }

        return new WorkspaceDiff(added, modified, deleted, ComputeFingerprint(externalEntries));
    }

    public Task ApplyExternalPathAsync(string branchId, string externalPath, CancellationToken ct = default)
    {
        var branchDir = BranchDir(branchId);
        if (!Directory.Exists(branchDir))
            Directory.CreateDirectory(branchDir);

        var sourceRelative = EnumerateExternalEntries(externalPath).Keys.ToHashSet();

        // Delete files in branchDir absent from externalPath (always a full destructive mirror —
        // see the interface doc-comment; this is correct for ordinary drift, not just a switch).
        foreach (var targetFile in Directory.EnumerateFiles(branchDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(branchDir, targetFile).Replace('\\', '/');
            if (IsIgnoredDirSegment(rel) || IsPlanArtifact(rel)) continue;
            if (!sourceRelative.Contains(rel))
                File.Delete(targetFile);
        }

        foreach (var rel in sourceRelative)
        {
            ct.ThrowIfCancellationRequested();
            var srcFull = Path.Combine(externalPath, rel);
            var dstFull = Path.Combine(branchDir, rel);
            var dstDirPath = Path.GetDirectoryName(dstFull)!;
            if (!Directory.Exists(dstDirPath))
                Directory.CreateDirectory(dstDirPath);
            File.Copy(srcFull, dstFull, overwrite: true);
        }

        return Task.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string BranchDir(string branchId) =>
        Path.Combine(options.RootPath, SanitizeBranchId(branchId));

    private string SafePath(string branchId, string relativePath)
    {
        if (relativePath.Contains("..") || Path.IsPathRooted(relativePath))
            throw new ArgumentException($"Unsafe path: '{relativePath}'");

        // Normalize to absolute so the StartsWith check works even if RootPath was relative.
        var branchDir = Path.GetFullPath(BranchDir(branchId));
        var combined  = Path.GetFullPath(Path.Combine(branchDir, relativePath));

        // Trailing separator prevents work-abc matching as prefix of work-abcdef.
        var prefix = branchDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path traversal detected: '{relativePath}'");

        return combined;
    }

    private static string SanitizeBranchId(string branchId) =>
        string.Concat(branchId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // Checking the raw absolute path for "any separator followed by a dot" looks right until
    // RootPath itself sits under a dot-folder (e.g. .nodalmerge/workspace, our own default-ish
    // layout for repo-local workspaces) — every file under every branch would then contain that
    // ancestor segment and get treated as hidden, silently emptying every diff/list/copy. Only the
    // portion of the path *inside* the branch (i.e. relative to RootPath) should count.
    // Mirrors WorkspaceProfileService.IgnoredDirNames — dependency/build directories are
    // reinstallable or regenerable, not actual merge content. Without this, ApplyBranchAsync
    // copies every file under e.g. node_modules one at a time (tens of thousands of File.Copy
    // calls for a typical npm project), which is both pointless and slow enough to blow past
    // the webview's apply request timeout.
    private static readonly string[] IgnoredDirNames =
        ["node_modules", "bin", "obj", "dist", "build", "target", "__pycache__", "venv", ".git"];

    private bool IsHidden(string path)
    {
        var relative = Path.GetRelativePath(options.RootPath, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Length > 0 &&
                (segment[0] == '.' || IgnoredDirNames.Contains(segment, StringComparer.OrdinalIgnoreCase)));
    }

    // Dotfiles (e.g. .env) are deliberately NOT excluded here, unlike IsHidden above — seeding a
    // new branch is the one place a dotfile genuinely needs to come along (a project's .env is
    // often required for it to run at all), whereas IsHidden's callers are list/diff/apply, where
    // dotfiles are conventionally treated as not-part-of-the-tracked-diff.
    private static bool IsIgnoredDirSegment(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => IgnoredDirNames.Contains(segment, StringComparer.OrdinalIgnoreCase));

    // plan.json is an internal planning artifact (PlanDocumentPaths.FileName) written explicitly by
    // the Planner via WriteAsync and read explicitly by FanOutService — never meant to travel via a
    // bulk copy. Every caller below passes a path already relative to a single branch/external root
    // (not RootPath), so a plain equality check is unambiguous: it only matches the planner's own
    // root-level file, never a same-named file nested in real project content (e.g. "docs/plan.json").
    // Without this, seeding a new branch from a parent that has its own plan.json — or merging a
    // child branch up to its parent/main — leaks a stale, unrelated plan onto the target, and
    // FanOutService.ProcessAsync (which reads whatever plan.json sits on the target, regardless of
    // which goal wrote it) blindly fans out from it instead of the target's own current goal.
    private static bool IsPlanArtifact(string relativeToRoot) =>
        relativeToRoot.Replace('\\', '/') == PlanDocumentPaths.FileName;

    // Used by DiffExternalPathAsync/ApplyExternalPathAsync — externalPath has no relationship to
    // RootPath at all, so (unlike IsHidden) this enumerates and excludes purely relative to its own
    // root, via the same IsIgnoredDirSegment rule CopyDirectory already uses for seeding (keeps
    // dotfiles like .env, drops node_modules/bin/obj/.../.git).
    private static Dictionary<string, (long Length, long LastWriteUtcTicks)> EnumerateExternalEntries(string externalPath)
    {
        var result = new Dictionary<string, (long Length, long LastWriteUtcTicks)>(StringComparer.Ordinal);
        if (!Directory.Exists(externalPath))
            return result;

        foreach (var file in Directory.EnumerateFiles(externalPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(externalPath, file).Replace('\\', '/');
            if (IsIgnoredDirSegment(relative) || IsPlanArtifact(relative)) continue;
            var info = new FileInfo(file);
            result[relative] = (info.Length, info.LastWriteTimeUtc.Ticks);
        }

        return result;
    }

    // Diagnostic structural fingerprint (relative path + size + last-write time, no content reads)
    // — see WorkspaceDiff.ExternalFingerprint's doc-comment for what this is and isn't used for.
    private static string ComputeFingerprint(Dictionary<string, (long Length, long LastWriteUtcTicks)> entries)
    {
        var lines = entries
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}:{kv.Value.Length}:{kv.Value.LastWriteUtcTicks}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        return Convert.ToHexString(hash)[..16];
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            if (IsIgnoredDirSegment(relative)) continue;
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (IsIgnoredDirSegment(relative) || IsPlanArtifact(relative)) continue;
            File.Copy(file, Path.Combine(destination, relative), overwrite: true);
        }
    }

    private static async Task<IReadOnlyList<string>> TryReadLimitedAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(fullPath, ct).ConfigureAwait(false);
            return lines.Length <= 100 ? lines : [..lines.Take(100), $"... ({lines.Length - 100} more lines)"];
        }
        catch
        {
            return ["(could not read file)"];
        }
    }
}
