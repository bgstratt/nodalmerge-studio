using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IRepositoryRegistryService
{
    Task<RepositoryV1> RegisterAsync(string path, string? label, CancellationToken ct = default);

    /// <summary>
    /// Slice 6.2 — resolves a pending workgroup-identity disambiguation
    /// (<see cref="RepositoryV1.PendingDisambiguation"/>) for an already-registered repository.
    /// <paramref name="chosenRepoId"/> must be one of the offered candidates' RepoId, or null/
    /// "register-new" to mint a fresh workgroup entry instead (see
    /// <see cref="IWorkgroupRepositoryDirectory.RegisterAsync"/>'s preferred-id continuity rule).
    /// Returns null if <paramref name="repositoryId"/> isn't registered; is a no-op (returns the
    /// repository unchanged) if it has no pending disambiguation.
    /// </summary>
    Task<RepositoryV1?> ResolveDisambiguationAsync(string repositoryId, string? chosenRepoId, CancellationToken ct = default);

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
    // Slice 6.2 — nullable/optional (not GetRequiredService-shaped): every real DI composition
    // registers both (see ServiceCollectionExtensions), but SnapshotRetentionPolicyTests constructs
    // this class directly with just the two required collaborators above, and that construction
    // must keep compiling/working exactly as before 6.2 — workgroup binding degrades to "not
    // attempted" (WorkgroupRepoId stays null) rather than becoming a hard dependency.
    private readonly IRepositoryIdentityHintsService? _identityHints;
    private readonly IWorkgroupRepositoryDirectory? _workgroupDirectory;
    private readonly ILogger<RepositoryRegistryService>? _logger;

    public RepositoryRegistryService(
        IStudioNodeStore nodeStore,
        IWorkspaceRegistryService workspaces,
        IRepositoryIdentityHintsService? identityHints = null,
        IWorkgroupRepositoryDirectory? workgroupDirectory = null,
        ILogger<RepositoryRegistryService>? logger = null)
    {
        _nodeStore = nodeStore;
        _workspaces = workspaces;
        _identityHints = identityHints;
        _workgroupDirectory = workgroupDirectory;
        _logger = logger;
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

        // Slice 6.2 (docs/STUDIO_ROOM_SCHEMA.md (b), D1/D2) — bind this local candidate to the
        // workgroup repositories map. RepositoryId above stays this repo's permanent local-candidate
        // identity regardless of outcome (see RepositoryV1's own comment); only WorkgroupRepoId/
        // PendingDisambiguation change here.
        repository = await BindToWorkgroupAsync(repository, ct).ConfigureAwait(false);

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

    // Slice 6.2 binding flow (docs/STUDIO_ROOM_SCHEMA.md (b) "Matching flow", D2): compute hints
    // for the local path, ask the workgroup directory to match, and record the outcome. Never
    // fails registration itself — a hint-computation or matching error degrades to "no workgroup
    // binding yet" (WorkgroupRepoId stays null, no PendingDisambiguation) rather than blocking
    // RegisterAsync, since pre-6.2 behavior (register always succeeds) must keep working when the
    // workgroup services aren't wired or something in the git/engine path throws.
    private async Task<RepositoryV1> BindToWorkgroupAsync(RepositoryV1 repository, CancellationToken ct)
    {
        if (_identityHints is null || _workgroupDirectory is null)
            return repository;

        try
        {
            var hints = await _identityHints.ComputeAsync(repository.Path, ct).ConfigureAwait(false);
            var match = await _workgroupDirectory.MatchAsync(hints, ct).ConfigureAwait(false);

            switch (match)
            {
                case RepositoryMatchResult.Matched matched:
                    return repository with { WorkgroupRepoId = matched.Entry.RepoId, PendingDisambiguation = null };

                case RepositoryMatchResult.NoMatch:
                {
                    var registered = await RegisterWorkgroupEntryAsync(repository, hints, ct).ConfigureAwait(false);
                    return repository with { WorkgroupRepoId = registered.RepoId, PendingDisambiguation = null };
                }

                // Degenerate sub-case of NeedsDisambiguation: zero candidates to disambiguate
                // against (the workgroup map has nothing registered yet, or nothing else shares
                // any signal). Per D2/6.2's preferred-id continuity rule this is the single-user/
                // standalone-first-run case — surfacing a prompt with nothing to choose between
                // would be pure friction, so this mints (reusing RepositoryId as the preferred
                // workgroup id) exactly like NoMatch.
                case RepositoryMatchResult.NeedsDisambiguation { Candidates.Count: 0 }:
                {
                    var registered = await RegisterWorkgroupEntryAsync(repository, hints, ct).ConfigureAwait(false);
                    return repository with { WorkgroupRepoId = registered.RepoId, PendingDisambiguation = null };
                }

                case RepositoryMatchResult.NeedsDisambiguation needsDisambiguation:
                    return repository with
                    {
                        WorkgroupRepoId = null,
                        PendingDisambiguation = new RepositoryDisambiguationPendingV1(
                            needsDisambiguation.Candidates
                                .Select(c => new RepositoryDisambiguationCandidateV1(c.RepoId, c.Label, c.Hints))
                                .ToList())
                    };

                default:
                    return repository;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "workgroup repository binding failed for '{RepositoryId}' at '{Path}' — registering without a workgroup binding",
                repository.RepositoryId, repository.Path);
            return repository;
        }
    }

    private Task<WorkgroupRepositoryEntry> RegisterWorkgroupEntryAsync(RepositoryV1 repository, RepositoryIdentityHints hints, CancellationToken ct) =>
        _workgroupDirectory!.RegisterAsync(repository.Label, hints, preferredRepoId: repository.RepositoryId, ct);

    public async Task<RepositoryV1?> ResolveDisambiguationAsync(string repositoryId, string? chosenRepoId, CancellationToken ct = default)
    {
        if (!_repositories.TryGetValue(repositoryId, out var repository))
            return null;

        if (repository.PendingDisambiguation is null)
            return repository;

        RepositoryV1 resolved;
        if (chosenRepoId is null || string.Equals(chosenRepoId, "register-new", StringComparison.OrdinalIgnoreCase))
        {
            if (_workgroupDirectory is null)
                throw new InvalidOperationException("No workgroup repository directory is configured — cannot resolve a disambiguation.");

            var hints = _identityHints is not null
                ? await _identityHints.ComputeAsync(repository.Path, ct).ConfigureAwait(false)
                : RepositoryIdentityHints.Empty;
            var registered = await RegisterWorkgroupEntryAsync(repository, hints, ct).ConfigureAwait(false);
            resolved = repository with { WorkgroupRepoId = registered.RepoId, PendingDisambiguation = null };
        }
        else
        {
            // Defends against binding to an arbitrary repoId a client hallucinated — must be one of
            // the candidates this repository was actually offered.
            var candidateIds = repository.PendingDisambiguation.Candidates
                .Select(c => c.RepoId)
                .ToHashSet(StringComparer.Ordinal);
            if (!candidateIds.Contains(chosenRepoId))
                throw new ArgumentException($"'{chosenRepoId}' was not one of the offered disambiguation candidates for '{repositoryId}'.");

            resolved = repository with { WorkgroupRepoId = chosenRepoId, PendingDisambiguation = null };
        }

        _repositories[repositoryId] = resolved;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.RepositoryV1, repositoryId, JsonSerializer.Serialize(resolved), ct).ConfigureAwait(false);
        return resolved;
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

    // Slice 6.5 Part 1 — deliberately a no-op, NOT the default "call RehydrateAsync again". Root
    // cause found while chasing a genuine regression this slice's own live-refresh wiring caused in
    // RoomPerRepoTests' two-repo scenario: "studio" is not actually a peer-private room the way
    // RepositoryV1's "peer-local candidate" convention assumes (see StudioNodeKind.RepoScopedKinds'
    // own comment: "RepositoryV1 itself... stays local by necessity"). A CONNECTED peer's "studio"
    // room *is* the literal same server-side room as the embedded host it connects to (RoomPeerClient
    // joins room `options.RoomId`, hardcoded "studio", by opening a WebSocket to that exact room name
    // on the remote host) — so once replication catches up, THIS peer's own engine-level view of
    // "studio" also contains every OTHER peer's own RepositoryV1 rows, not just this peer's. A
    // one-time startup RehydrateAsync (before any connection exists) never sees that — but re-running
    // it live, after packs have arrived, would silently absorb another peer's own local-candidate
    // registrations into this peer's _repositories cache as if they were this peer's own. That
    // corrupts BoundRepoRooms.GetBoundRepoRoomIdsAsync (used by RoomPeerClient's membership loop to
    // decide which repo rooms to actually JOIN), making a peer that only ever bound to one repo start
    // joining every repo the OTHER peer happens to have registered — confirmed by direct
    // instrumentation while building this slice: without this override, a peer bound only to repo R1
    // ended up also joining R2's and R3's rooms and genuinely receiving their real catch-up content.
    // No other RehydratedKinds-based partitioning fixes this (RepositoryV1 is correctly a "studio"-
    // room kind, not repo-scoped — the bug isn't about which room a pack arrived on, it's that this
    // service's own cache must never re-absorb replicated peer-local rows after startup). Safe to
    // leave a no-op: nothing else in this codebase needs this peer's registry cache to reflect
    // another peer's own bindings live — RepositoryRegistryService.RegisterAsync/
    // ResolveDisambiguationAsync already update _repositories directly for this peer's OWN writes.
    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
