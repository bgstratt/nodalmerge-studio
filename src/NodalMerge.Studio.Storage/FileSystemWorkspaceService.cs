using System.Text;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class FileSystemWorkspaceService(WorkspaceOptions options) : IFileWorkspaceService
{
    public Task InitBranchAsync(string branchId, string? seedFromBranchId = null, CancellationToken ct = default)
    {
        var branchDir = BranchDir(branchId);
        if (Directory.Exists(branchDir))
            return Task.CompletedTask;

        Directory.CreateDirectory(branchDir);

        if (seedFromBranchId is not null)
        {
            var seedDir = BranchDir(seedFromBranchId);
            if (Directory.Exists(seedDir))
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

    public Task<IReadOnlyList<string>> ListAsync(string branchId, string? subPath = null, CancellationToken ct = default)
    {
        var branchDir = BranchDir(branchId);
        var searchRoot = subPath is { Length: > 0 }
            ? SafePath(branchId, subPath)
            : branchDir;

        if (!Directory.Exists(searchRoot))
            return Task.FromResult<IReadOnlyList<string>>([]);

        var files = Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
            .Where(f => !IsHidden(f))
            .Select(f => Path.GetRelativePath(branchDir, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(files);
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
                .ToHashSet()
            : new HashSet<string>();

        // Delete files in target that are absent in source (approved diff == merged result)
        if (Directory.Exists(targetDir))
        {
            foreach (var targetFile in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories).Where(f => !IsHidden(f)))
            {
                var rel = Path.GetRelativePath(targetDir, targetFile).Replace('\\', '/');
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

    private static bool IsHidden(string path) =>
        Path.GetFileName(path).StartsWith('.') ||
        path.Contains(Path.DirectorySeparatorChar + ".") ||
        path.Contains(Path.AltDirectorySeparatorChar + ".");

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
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
