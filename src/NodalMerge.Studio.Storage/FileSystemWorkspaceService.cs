using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class FileSystemWorkspaceService(
    WorkspaceOptions options,
    IBlobStoreProvider? blobStore = null,
    IRepositoryOpService? repoOpService = null,
    IMaterializationEngine? materializer = null,
    IRepositorySnapshotService? snapshotService = null,
    ILogger<FileSystemWorkspaceService>? logger = null) : IFileWorkspaceService
{
    // CAS being unconfigured is a legitimate, common, intentional deployment choice — not a
    // misconfiguration — so this is logged once per instance (this class is a singleton, so
    // effectively once per process) at Information, not per-call at Warning. WriteAsync/DeleteAsync
    // fire on every file op; a per-call log would be spam regardless of level.
    private bool _loggedMissingCasConfig;
    public async Task InitBranchAsync(string branchId, string? seedFromBranchId = null,
        IReadOnlyList<string>? fileScope = null, CancellationToken ct = default)
    {
        var branchDir = BranchDir(branchId);
        // Directory.Exists alone isn't enough: a branch dir can be created empty (e.g. main,
        // before any SeedRepositoryPath was ever supplied) and would otherwise never become
        // eligible for seeding again, since this method no-ops on every subsequent call.
        if (Directory.Exists(branchDir) && Directory.EnumerateFileSystemEntries(branchDir).Any())
            return;

        Directory.CreateDirectory(branchDir);

        // Phase 11 — scoped materialization: when a work unit declares FileScope and CAS is
        // available, materialize only the matching paths from the latest snapshot instead of
        // copying the full seed branch directory. This keeps work unit branch dirs small.
        if (fileScope is { Count: > 0 } && materializer is not null && snapshotService is not null
            && options.SeedRepositoryPath is { Length: > 0 } seedForScope)
        {
            var repositoryId = Path.GetFullPath(seedForScope);
            var snapshot = await snapshotService.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
            if (snapshot?.TreeEntries is not null)
            {
                // Expand work unit glob patterns to materializer prefix paths, and always include
                // project structure files so WorkspaceProfileService can detect project roots.
                var materializationScope = ExpandScopeForMaterializer(fileScope);
                await materializer.MaterializeAsync(snapshot, branchDir, materializationScope, ct)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (seedFromBranchId is not null)
        {
            var seedDir = BranchDir(seedFromBranchId);
            if (Directory.Exists(seedDir) && Directory.EnumerateFileSystemEntries(seedDir).Any())
            {
                CopyDirectory(seedDir, branchDir);
                return;
            }
        }

        if (string.Equals(branchId, "main", StringComparison.OrdinalIgnoreCase)
            && options.SeedRepositoryPath is { Length: > 0 } seed)
        {
            // Phase 7 — prefer CAS reconstruction over directory copy so that "main" can always
            // be rebuilt even when SeedRepositoryPath is absent or has been modified.
            if (materializer is not null && snapshotService is not null)
            {
                var repositoryId = Path.GetFullPath(seed);
                var snapshot = await snapshotService.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
                if (snapshot?.TreeEntries is not null)
                {
                    await materializer.MaterializeAsync(snapshot, branchDir, ct: ct).ConfigureAwait(false);
                    return;
                }
            }

            // Fallback: direct copy from seed repo (pre-Phase-7 behavior, or when CAS is absent).
            if (Directory.Exists(seed))
                CopyDirectory(seed, branchDir);
        }
    }

    // Converts WorkUnit.FileScope glob patterns (e.g. "src/Auth/**") to prefix paths the
    // materializer's IsInScope understands (e.g. "src/Auth"). Also injects project structure
    // file patterns so WorkspaceProfileService can always detect project roots.
    private static readonly string[] ProjectStructureFiles =
    [
        ".csproj", ".sln", ".slnx", "package.json", "Cargo.toml",
        "go.mod", "pyproject.toml", "Makefile", "CMakeLists.txt",
    ];

    private static IReadOnlyList<string> ExpandScopeForMaterializer(IReadOnlyList<string> fileScope)
    {
        var expanded = new List<string>();
        foreach (var pattern in fileScope)
        {
            // Strip trailing glob segments to get a directory prefix.
            // "src/Auth/**" → "src/Auth"
            // "src/Auth/*.cs" → "src/Auth"
            // "src/Auth/UserService.cs" → kept as-is (exact file match)
            var trimmed = pattern.TrimEnd('/').TrimEnd('*').TrimEnd('/');
            if (trimmed.Length > 0)
                expanded.Add(trimmed);
        }
        return expanded;
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
        var contentBytes = Encoding.UTF8.GetBytes(content);
        if (contentBytes.Length > options.MaxWriteBytes)
            throw new InvalidOperationException(
                $"Content is {contentBytes.Length:N0} bytes, which exceeds the write limit of {options.MaxWriteBytes:N0} bytes.");

        var fullPath = SafePath(branchId, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await EmitWriteOpAsync(fullPath, relativePath, contentBytes, ct).ConfigureAwait(false);
        // Concurrent actors legitimately touch the same branch file (a background reconciliation
        // agent refreshing/copying while a human's apply lands, sibling merges into candidate),
        // and Windows write handles are exclusive — a moment of overlap surfaces as a transient
        // IOException sharing violation, not a real error. Bounded retry; last attempt rethrows.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await File.WriteAllBytesAsync(fullPath, contentBytes, ct).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(25 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
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

    public async Task DeleteAsync(string branchId, string relativePath, CancellationToken ct = default)
    {
        var fullPath = SafePath(branchId, relativePath);
        await EmitDeleteOpAsync(fullPath, relativePath, ct).ConfigureAwait(false);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
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

    // ── Repository op emission (Phase 4 dual-write) ───────────────────────────

    // Guard: skip if CAS or op service is unwired, or no repository is configured. Logs once
    // (not silently) so a future debugging session doesn't have to infer this from stale/missing
    // CAS state — see the class-level _loggedMissingCasConfig comment for why this is Information,
    // logged once, not a per-call Warning.
    private bool CanEmitOps([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? repositoryId)
    {
        repositoryId = null;
        if (blobStore is not null && repoOpService is not null && options.SeedRepositoryPath is { Length: > 0 })
        {
            repositoryId = Path.GetFullPath(options.SeedRepositoryPath);
            return true;
        }

        if (!_loggedMissingCasConfig)
        {
            _loggedMissingCasConfig = true;
            logger?.LogInformation(
                "CAS dual-write disabled (blobStore={HasBlobStore}, repoOpService={HasRepoOpService}, " +
                "seedRepositoryPath={HasSeedPath}). This is expected for deployments that don't need " +
                "the audit trail; file writes/deletes proceed normally on disk.",
                blobStore is not null, repoOpService is not null, options.SeedRepositoryPath is { Length: > 0 });
        }
        return false;
    }

    private async Task EmitWriteOpAsync(string fullPath, string relativePath, byte[] newBytes, CancellationToken ct)
    {
        if (!CanEmitOps(out var repositoryId) || IsStudioInternalPath(relativePath))
            return;

        var newBlobId = BlobId(newBytes);
        byte[]? oldBytes = File.Exists(fullPath) ? await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false) : null;
        var oldBlobId = oldBytes is not null ? BlobId(oldBytes) : null;

        await blobStore!.PutBlobAsync(newBlobId, newBytes, "text/plain", ct).ConfigureAwait(false);
        await repoOpService!.EmitAsync(new RepositoryOperation(
            OperationId: Guid.NewGuid().ToString("N"),
            RepositoryId: repositoryId,
            ParentSnapshotId: null,
            Kind: oldBlobId is null ? OperationType.Add : OperationType.Replace,
            Path: relativePath.Replace('\\', '/'),
            Timestamp: DateTimeOffset.UtcNow,
            OldBlobId: oldBlobId,
            NewBlobId: newBlobId), ct).ConfigureAwait(false);
    }

    private async Task EmitDeleteOpAsync(string fullPath, string relativePath, CancellationToken ct)
    {
        if (!CanEmitOps(out var repositoryId) || IsStudioInternalPath(relativePath) || !File.Exists(fullPath))
            return;

        var oldBytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        var oldBlobId = BlobId(oldBytes);

        await repoOpService!.EmitAsync(new RepositoryOperation(
            OperationId: Guid.NewGuid().ToString("N"),
            RepositoryId: repositoryId,
            ParentSnapshotId: null,
            Kind: OperationType.Delete,
            Path: relativePath.Replace('\\', '/'),
            Timestamp: DateTimeOffset.UtcNow,
            OldBlobId: oldBlobId,
            NewBlobId: null), ct).ConfigureAwait(false);
    }

    // Phase 11.75 — Blake3 to match the host engine's CAS blob ID format.
    private static string BlobId(byte[] bytes) => BlobHasher.ComputeHash(bytes);

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
    private bool IsHidden(string path)
    {
        var relative = Path.GetRelativePath(options.RootPath, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Length > 0 &&
                (segment[0] == '.' || WorkspacePathFilter.IgnoredDirNames.Contains(segment, StringComparer.OrdinalIgnoreCase)));
    }

    // Dotfiles (e.g. .env) are deliberately NOT excluded here, unlike IsHidden above — seeding a
    // new branch is the one place a dotfile genuinely needs to come along (a project's .env is
    // often required for it to run at all), whereas IsHidden's callers are list/diff/apply, where
    // dotfiles are conventionally treated as not-part-of-the-tracked-diff.
    private static bool IsIgnoredDirSegment(string relative) =>
        WorkspacePathFilter.IsIgnoredDirSegment(relative);

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

    // Paths that flow through WriteAsync but must not produce RepositoryOperation nodes because
    // they are Studio-internal artifacts, not source files in the tracked repository.
    private static bool IsStudioInternalPath(string relativePath) =>
        IsPlanArtifact(relativePath);

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

    // Phase 11 on-demand fetch: materializes a single path from the latest snapshot into the branch
    // dir so agents can access files that weren't in their initial FileScope. Returns true when the
    // file was found in the snapshot and written to disk, false when it genuinely doesn't exist.
    public async Task<bool> MaterializeFileAsync(string branchId, string path, CancellationToken ct = default)
    {
        if (materializer is null || snapshotService is null
            || options.SeedRepositoryPath is not { Length: > 0 } seed)
            return false;

        var repositoryId = Path.GetFullPath(seed);
        var snapshot = await snapshotService.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
        if (snapshot?.TreeEntries is null || !snapshot.TreeEntries.ContainsKey(path))
            return false;

        var branchDir = BranchDir(branchId);
        Directory.CreateDirectory(branchDir);
        await materializer.MaterializeAsync(snapshot, branchDir, [path], ct).ConfigureAwait(false);
        return true;
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
