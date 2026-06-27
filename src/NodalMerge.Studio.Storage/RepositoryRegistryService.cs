using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IRepositoryRegistryService
{
    Task<RepositoryV1> RegisterAsync(string path, string? label, CancellationToken ct = default);

    /// <summary>
    /// Creates a fresh repository at <paramref name="path"/> (creating the directory if needed),
    /// runs `git init` if it isn't already a git repository, then registers it. Idempotent: safe
    /// to call again for a path that's already a git repo and/or already registered.
    /// </summary>
    Task<RepositoryV1> CreateAsync(string path, string? label, CancellationToken ct = default);

    /// <summary>
    /// Clones <paramref name="url"/> into <paramref name="targetPath"/> via `git clone`, then
    /// registers the resulting directory.
    /// </summary>
    Task<RepositoryV1> CloneAsync(string url, string targetPath, string? label, CancellationToken ct = default);

    /// <summary>
    /// Reads a file directly off disk from a registered repository — read-only, no branch/CRDT
    /// involvement, for cross-repo file reference (WorkUnit.ReferenceFiles). Returns null if the
    /// repository or file doesn't exist, or if relativePath escapes the repository root.
    /// </summary>
    Task<string?> ReadFileAsync(string repositoryId, string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Lists files in a registered repository (relative paths), optionally narrowed to a
    /// sub-directory and/or a wildcard pattern. Returns an empty list if the repository doesn't
    /// exist, or if subPath escapes the repository root.
    /// </summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string? subPath = null, string? pattern = null, CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryV1>> ListAsync(CancellationToken ct = default);
    Task<RepositoryV1?> GetAsync(string repositoryId, CancellationToken ct = default);

    /// <summary>
    /// Of the given candidate paths, returns the ones that are NOT already registered (by
    /// normalized path). Lets a client ask "which of these are unregistered" without
    /// re-implementing the registry's own path-identity matching.
    /// </summary>
    Task<IReadOnlyList<string>> FilterUnregisteredAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
}

public sealed class RepositoryRegistryService : IRepositoryRegistryService, IRehydratable
{
    private readonly ConcurrentDictionary<string, RepositoryV1> _repositories = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IWorkspaceRegistryService _workspaces;

    public RepositoryRegistryService(IStudioNodeStore nodeStore, IWorkspaceRegistryService workspaces)
    {
        _nodeStore = nodeStore;
        _workspaces = workspaces;
    }

    public async Task<RepositoryV1> RegisterAsync(string path, string? label, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path);

        // Idempotent by normalized path — re-registering the same repository returns the existing
        // entry rather than duplicating it.
        var existing = _repositories.Values.FirstOrDefault(r => NormalizePath(r.Path) == normalized);
        if (existing is not null)
            return existing;

        var repository = new RepositoryV1(
            RepositoryId: $"repo-{Guid.NewGuid():N}",
            Path: path,
            Label: label,
            RegisteredAt: DateTimeOffset.UtcNow);

        _repositories[repository.RepositoryId] = repository;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.RepositoryV1, repository.RepositoryId,
            JsonSerializer.Serialize(repository), ct).ConfigureAwait(false);

        // Every registered repository belongs to the (currently singleton) workspace — see
        // plans/phase-16-workspace-aggregate.md. Repository is a resource the workspace owns, not
        // an identity in its own right.
        await _workspaces.AttachRepositoryAsync(repository.RepositoryId, ct).ConfigureAwait(false);

        return repository;
    }

    public async Task<RepositoryV1> CreateAsync(string path, string? label, CancellationToken ct = default)
    {
        Directory.CreateDirectory(path);

        if (!Directory.Exists(Path.Combine(path, ".git")))
            await RunGitAsync("init", path, ct).ConfigureAwait(false);

        return await RegisterAsync(path, label, ct).ConfigureAwait(false);
    }

    public async Task<RepositoryV1> CloneAsync(string url, string targetPath, string? label, CancellationToken ct = default)
    {
        // Working directory doesn't matter here — the destination is an explicit argument — so a
        // stable, always-existing directory (rather than targetPath's not-yet-created parent) keeps
        // this simple.
        await RunGitAsync($"clone \"{url}\" \"{targetPath}\"", Path.GetTempPath(), ct).ConfigureAwait(false);
        return await RegisterAsync(targetPath, label, ct).ConfigureAwait(false);
    }

    // Mirrors the ProcessStartInfo idiom in WorkspaceExecutionService.CreateProcessStartInfo —
    // cmd.exe wrapping on Windows so PATH-resolved shims work, redirected stdio, short timeout.
    // Not shared with that class directly: it's coupled to WorkspaceOptions.BuildCommand/TestCommand
    // config, this is a one-off git invocation with nothing to configure.
    private static async Task RunGitAsync(string arguments, string workingDirectory, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c git {arguments}";
        }
        else
        {
            psi.FileName = "git";
            psi.Arguments = arguments;
        }

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not run 'git {arguments}' in '{workingDirectory}' — is git installed and on PATH?", ex);
        }

        using (process)
        {
            string stderr;
            try
            {
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw new InvalidOperationException($"'git {arguments}' timed out after 15s.");
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"'git {arguments}' failed (exit {process.ExitCode}): {stderr}");
        }
    }

    public Task<IReadOnlyList<RepositoryV1>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RepositoryV1>>(
            _repositories.Values.OrderBy(r => r.RegisteredAt).ToList());

    // Path-identity matching (NormalizePath) is registry logic — a client (e.g. the VS Code
    // extension's cross-repo reference picker, deciding which open folders to offer as "not yet
    // registered") asks the registry rather than re-implementing normalization itself.
    public Task<IReadOnlyList<string>> FilterUnregisteredAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var registered = _repositories.Values.Select(r => NormalizePath(r.Path)).ToHashSet();
        IReadOnlyList<string> unregistered = paths.Where(p => !registered.Contains(NormalizePath(p))).ToList();
        return Task.FromResult(unregistered);
    }

    public Task<RepositoryV1?> GetAsync(string repositoryId, CancellationToken ct = default)
    {
        _repositories.TryGetValue(repositoryId, out var repository);
        return Task.FromResult(repository);
    }

    public async Task<string?> ReadFileAsync(string repositoryId, string relativePath, CancellationToken ct = default)
    {
        var repository = await GetAsync(repositoryId, ct).ConfigureAwait(false);
        if (repository is null) return null;

        var fullPath = ResolveWithinRepository(repository.Path, relativePath);
        if (fullPath is null || !File.Exists(fullPath)) return null;

        return await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string? subPath = null, string? pattern = null, CancellationToken ct = default)
    {
        var repository = await GetAsync(repositoryId, ct).ConfigureAwait(false);
        if (repository is null) return [];

        var root = ResolveWithinRepository(repository.Path, subPath ?? string.Empty);
        if (root is null || !Directory.Exists(root)) return [];

        var repoRoot = Path.GetFullPath(repository.Path);
        return Directory.EnumerateFiles(root, pattern ?? "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(repoRoot, f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Resolves relativePath against repoPath, rejecting any result that escapes the repository
    // root (e.g. "../../etc/passwd") — the only thing standing between a foreign repo's disk and
    // arbitrary filesystem access via a cross-repo file reference.
    private static string? ResolveWithinRepository(string repoPath, string relativePath)
    {
        var repoRoot = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(repoRoot, relativePath));
        var withinRoot = combined.Equals(repoRoot, StringComparison.OrdinalIgnoreCase) ||
            combined.StartsWith(repoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return withinRoot ? combined : null;
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore
            .ReadAllNodesAsync(StudioNodeKind.RepositoryV1, cancellationToken).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var repository = JsonSerializer.Deserialize<RepositoryV1>(payloadJson);
            if (repository is not null)
                _repositories[repository.RepositoryId] = repository;
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
