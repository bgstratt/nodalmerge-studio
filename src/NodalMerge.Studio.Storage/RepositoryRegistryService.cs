using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// plans/vision-punchlist-remediation.md (Items 1+2).
public enum RepositoryRelinkMode
{
    // Converge only on an unambiguous match. Commits nothing when the answer is genuinely unknown —
    // a fork, a degraded signal, or several candidates — and hands back candidates for the human.
    Auto,

    // Bind the caller's explicit choice, or "register-new" to mint a fresh room (a deliberate split).
    Manual,
}

// What will stop appearing if this re-link commits. Re-pointing a repository changes which room is
// read and written from here on, but nothing migrates the content already in the old room — it stays
// on disk and simply drops out of the read fan-out. Surfacing this is the difference between an
// informed choice and a silent orphaning.
public sealed record RepositoryRelinkImpact(
    string RoomId,
    int TotalNodes,
    IReadOnlyDictionary<string, int> CountsByKind);

public sealed record RepositoryRelinkResult(
    string RepositoryId,
    string? CurrentWorkgroupRepoId,
    // What this re-link would bind (or did bind). Null when there is nothing to change, or when the
    // answer needs a human — check Candidates.
    string? ProposedWorkgroupRepoId,
    bool Committed,
    RepositoryBindingProvenance? Provenance,
    IReadOnlyList<RepositoryDisambiguationCandidateV1> Candidates,
    // Null when the binding would not actually move (nothing to lose).
    RepositoryRelinkImpact? Impact,
    // Plain-language reason, shown directly to the user — this flow is only ever driven by a human.
    string Explanation);

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
    /// plans/vision-punchlist-remediation.md (Items 1+2) — USER-INITIATED re-link of a repository's
    /// workgroup binding. Nothing calls this on its own: re-resolution never runs in the background,
    /// on an inbound pack, or on a timer, so a binding that has settled stays settled unless a human
    /// asks. That restriction is what makes re-reading git here safe, and it is the whole reason this
    /// is separate from <see cref="ResolveDisambiguationAsync"/>.
    ///
    /// Unlike ResolveDisambiguationAsync — which no-ops unless a disambiguation is pending — this
    /// works on an already-settled binding, which is the case that matters: two clones that diverged
    /// before deterministic ids existed, or a clone that was shallow at register time and has since
    /// gained its root history. Hints are re-read from git here (that fresh read is the point; a
    /// cached degraded hint set could never converge).
    ///
    /// <paramref name="mode"/> Auto commits only an unambiguous match, and never moves a binding a
    /// human chose. Manual commits <paramref name="chosenRepoId"/> (or "register-new" to mint a fresh
    /// room, deliberately splitting this repo off). Pass <paramref name="commit"/> false for a dry run
    /// — same evaluation, no writes — which is how a caller previews the impact before asking.
    /// Returns null if the repository isn't registered.
    /// </summary>
    Task<RepositoryRelinkResult?> RelinkAsync(
        string repositoryId,
        RepositoryRelinkMode mode,
        string? chosenRepoId = null,
        bool commit = true,
        CancellationToken ct = default);

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

    /// <summary>
    /// Slice 7.2 (plans/cas-distribution-and-storage.md Phase 7) — resolves the workgroup-portable
    /// CAS/snapshot-store identity for a repository, replacing the pre-7.2 convention where every
    /// CAS read/write (RepositorySnapshot.RepositoryId, RepositoryOperation.RepositoryId, ...) used
    /// Path.GetFullPath(local disk path) directly — a value with no meaning across peers whose
    /// physical clone directories differ (the exact gap the 6.5 multi-user smoke's forced-matching-
    /// paths accommodation papered over; see docs/guides/multi-user-smoke.md).
    ///
    /// At least one of <paramref name="repositoryId"/> (a local-candidate RepositoryV1.RepositoryId
    /// — as carried by WorkUnit.RepositoryId etc., possibly FOREIGN: minted by a different peer) and
    /// <paramref name="repositoryPath"/> (a known local disk path) should be supplied. Sticky-
    /// continuity resolution order (never orphans a repository's existing snapshot chain):
    ///   1. <paramref name="repositoryPath"/> (or, if omitted, this peer's own registered
    ///      repository's Path for <paramref name="repositoryId"/>) — if a snapshot chain already
    ///      exists under that path today, that IS this repository's key, unchanged. Covers every
    ///      pre-7.2 workspace and the ordinary single-peer/default-repo case.
    ///   2. The repository's WorkgroupRepoId — resolved via this peer's own registry entry, or (a
    ///      candidate this peer never itself registered) via the shared workgroup directory when
    ///      <paramref name="repositoryId"/> already equals a registered WorkgroupRepoId (true
    ///      whenever preferred-id continuity applied at that repo's first registration — see
    ///      IWorkgroupRepositoryDirectory.RegisterAsync), falling back to a direct "studio"
    ///      node-store read (pre-7.3 this was the only foreign-candidate path, since "studio" was
    ///      shared upstream then; post-7.3 it only ever finds THIS peer's own prior rows — see
    ///      ResolveWorkgroupRepoIdAsync's own comment for the full 7.3 story, including the
    ///      disambiguated-fork case this does NOT yet cover). This is what makes a brand-new
    ///      repository's identity resolvable by a peer with a different (or no) physical clone
    ///      path, in the common (non-forked) case.
    ///   3. The bare local disk path (Path.GetFullPath) — degraded fallback for ad hoc/ungoverned
    ///      repositories the workgroup directory was never wired to bind.
    /// Returns null only when nothing above resolves — the caller's cue to surface
    /// <see cref="RepositoryIdentityUnresolvedException"/> instead of a bare "not found".
    /// </summary>
    Task<string?> ResolveCasIdentityAsync(string? repositoryId, string? repositoryPath = null, CancellationToken ct = default);
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
    // Slice 7.2 — optional/nullable like every other collaborator here: SnapshotRetentionPolicyTests
    // (and any other direct 2-arg construction) must keep compiling/working exactly as before.
    // Confirmed no circular DI risk: InMemoryRepositorySnapshotService depends only on
    // IStudioNodeStore/IServiceProvider?/ISnapshotTreeResolver?, never on IRepositoryRegistryService.
    private readonly IRepositorySnapshotService? _snapshotService;

    public RepositoryRegistryService(
        IStudioNodeStore nodeStore,
        IWorkspaceRegistryService workspaces,
        IRepositoryIdentityHintsService? identityHints = null,
        IWorkgroupRepositoryDirectory? workgroupDirectory = null,
        ILogger<RepositoryRegistryService>? logger = null,
        IRepositorySnapshotService? snapshotService = null)
    {
        _nodeStore = nodeStore;
        _workspaces = workspaces;
        _identityHints = identityHints;
        _workgroupDirectory = workgroupDirectory;
        _logger = logger;
        _snapshotService = snapshotService;
    }

    public async Task<RepositoryV1> RegisterAsync(string path, string? label, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path);

        // Idempotent by normalized path — re-registering the same repository returns the existing
        // entry rather than duplicating it.
        var existing = _repositories.Values.FirstOrDefault(r => NormalizePath(r.Path) == normalized);
        if (existing is not null)
            return existing;

        // Phase 1 (plans/repo-identity-convergence.md) — compute identity hints ONCE, here, so they
        // seed both the local RepositoryId and the workgroup binding below. Computed after the
        // idempotent-by-path short-circuit above so a re-register of a known path never re-consults
        // git (preserves the "hints consulted only at first contact" contract). Never throws —
        // degrades to Empty (which yields a guid id + no binding), matching pre-Phase-1 behavior
        // when the hints service is absent.
        RepositoryIdentityHints hints;
        try
        {
            hints = _identityHints is not null
                ? await _identityHints.ComputeAsync(path, ct).ConfigureAwait(false)
                : RepositoryIdentityHints.Empty;
        }
        catch
        {
            hints = RepositoryIdentityHints.Empty;
        }

        // Deterministic local-candidate id from the root-SHA set (strong signal) so two clones of one
        // repo mint the SAME RepositoryId — which the preferred-id continuity in BindToWorkgroupAsync
        // then propagates to WorkgroupRepoId, converging both peers on one repo room with zero
        // replication dependency. Falls back to a guid for a degraded signal, OR when the deterministic
        // id already names a different local registration (two forks sharing a root SHA cloned on THIS
        // peer — the local registry is keyed by RepositoryId and must stay collision-free; the second
        // fork then disambiguates to its own workgroup id below).
        var deterministicId = RepositoryIdentityMatcher.DeterministicRepoId(hints.RootShas);
        var repositoryId = deterministicId is not null && !_repositories.ContainsKey(deterministicId)
            ? deterministicId
            : $"repo-{Guid.NewGuid():N}";

        var repository = new RepositoryV1(
            RepositoryId: repositoryId,
            Path: path,
            Label: label,
            RegisteredAt: DateTimeOffset.UtcNow,
            Hints: hints,
            // A deterministic id means the root-SHA signal was strong AND uncontested locally; the
            // guid fallback above is exactly the provisional case a later re-link can converge.
            Provenance: repositoryId == deterministicId
                ? RepositoryBindingProvenance.Deterministic
                : RepositoryBindingProvenance.ProvisionalMint);

        // Slice 6.2 (docs/STUDIO_ROOM_SCHEMA.md (b), D1/D2) — bind this local candidate to the
        // workgroup repositories map. RepositoryId above stays this repo's permanent local-candidate
        // identity regardless of outcome (see RepositoryV1's own comment); only WorkgroupRepoId/
        // PendingDisambiguation change here.
        repository = await BindToWorkgroupAsync(repository, hints, ct).ConfigureAwait(false);

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
    private async Task<RepositoryV1> BindToWorkgroupAsync(RepositoryV1 repository, RepositoryIdentityHints hints, CancellationToken ct)
    {
        if (_identityHints is null || _workgroupDirectory is null)
            return repository;

        try
        {
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

        // A human made this call — mark it so Auto re-link never second-guesses it later.
        resolved = resolved with { Provenance = RepositoryBindingProvenance.HumanResolved };

        _repositories[repositoryId] = resolved;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.RepositoryV1, repositoryId, JsonSerializer.Serialize(resolved), ct).ConfigureAwait(false);
        return resolved;
    }

    // See IRepositoryRegistryService.RelinkAsync. User-initiated only — nothing in the runtime calls
    // this; it exists solely behind an explicit human action.
    public async Task<RepositoryRelinkResult?> RelinkAsync(
        string repositoryId,
        RepositoryRelinkMode mode,
        string? chosenRepoId = null,
        bool commit = true,
        CancellationToken ct = default)
    {
        if (!_repositories.TryGetValue(repositoryId, out var repository))
            return null;

        if (_workgroupDirectory is null)
            throw new InvalidOperationException("No workgroup repository directory is configured — cannot re-link.");

        var current = repository.WorkgroupRepoId;

        // Re-read git NOW rather than trusting the cached hints. This is the one place that is
        // allowed to, and it is the only thing that can rescue the stuck population: a clone that was
        // shallow (or had no HEAD) at register time produced no root SHA and fell back to a guid; once
        // it has real history, a fresh read yields the deterministic id and converges.
        RepositoryIdentityHints hints;
        try
        {
            hints = _identityHints is not null
                ? await _identityHints.ComputeAsync(repository.Path, ct).ConfigureAwait(false)
                : RepositoryIdentityHints.Empty;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "re-link: could not read identity hints for '{RepositoryId}'", repositoryId);
            hints = repository.Hints ?? RepositoryIdentityHints.Empty;
        }

        string? proposed = null;
        IReadOnlyList<RepositoryDisambiguationCandidateV1> candidates = [];
        string explanation;

        if (mode == RepositoryRelinkMode.Manual)
        {
            if (string.IsNullOrWhiteSpace(chosenRepoId))
                throw new ArgumentException("A manual re-link requires chosenRepoId (or \"register-new\").", nameof(chosenRepoId));

            if (string.Equals(chosenRepoId, "register-new", StringComparison.OrdinalIgnoreCase))
            {
                if (commit)
                {
                    var minted = await _workgroupDirectory
                        .RegisterAsync(repository.Label, hints, preferredRepoId: null, ct).ConfigureAwait(false);
                    proposed = minted.RepoId;
                }
                explanation = "Registers this repository as its own new room, splitting it from any repository it currently shares one with.";
            }
            else
            {
                proposed = chosenRepoId;
                explanation = $"Binds this repository to '{chosenRepoId}'.";
            }
        }
        else
        {
            // Auto. Never move a binding a human deliberately chose — that is precisely the settled
            // decision the schema's no-re-derivation rule exists to protect.
            if (repository.Provenance == RepositoryBindingProvenance.HumanResolved)
            {
                explanation = "This binding was set explicitly by a person, so automatic re-link leaves it alone. Use manual re-link to change it.";
            }
            else
            {
                var match = await _workgroupDirectory.MatchAsync(hints, ct).ConfigureAwait(false);
                switch (match)
                {
                    case RepositoryMatchResult.Matched matched when matched.Entry.RepoId != current:
                        proposed = matched.Entry.RepoId;
                        explanation = $"Found a repository in this workgroup with the same history — converges onto '{matched.Entry.RepoId}'.";
                        break;

                    case RepositoryMatchResult.Matched:
                        explanation = "Already bound to the matching workgroup repository — nothing to change.";
                        break;

                    case RepositoryMatchResult.NoMatch:
                        explanation = hints.IsDegraded
                            ? "No usable identity signal from this checkout (a shallow or empty clone has no root commit), and nothing in the workgroup matches. Fetch full history, or pick a repository manually."
                            : "No other repository in this workgroup shares this history — the current binding is already the right one.";
                        break;

                    // Ambiguous: a fork sharing a root commit, or a degraded signal with several
                    // registered candidates. Deliberately commits nothing.
                    case RepositoryMatchResult.NeedsDisambiguation needs:
                        candidates = needs.Candidates
                            .Select(c => new RepositoryDisambiguationCandidateV1(c.RepoId, c.Label, c.Hints))
                            // Stable, and the same order on every peer.
                            .OrderBy(c => c.RepoId, StringComparer.Ordinal)
                            .ToList();
                        explanation = candidates.Count == 0
                            ? "Nothing in the workgroup to match against yet."
                            : "More than one repository could be the right match — choose one, or register this as its own.";
                        break;

                    default:
                        explanation = "Nothing to change.";
                        break;
                }
            }
        }

        var willMove = proposed is not null && !string.Equals(proposed, current, StringComparison.Ordinal);

        // Only meaningful when the binding actually moves — otherwise nothing leaves the fan-out.
        RepositoryRelinkImpact? impact = null;
        if (willMove && !string.IsNullOrWhiteSpace(current))
        {
            try
            {
                var counts = await _nodeStore.CountRepoRoomNodesByKindAsync(current!, ct).ConfigureAwait(false);
                var total = counts.Values.Sum();
                if (total > 0)
                    impact = new RepositoryRelinkImpact($"repo/{current}", total, counts);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "re-link: could not count nodes in the current room for '{RepositoryId}'", repositoryId);
            }
        }

        var provenance = repository.Provenance;
        if (commit && willMove)
        {
            // A re-link is always a human decision, whichever mode produced it — mark it so nothing
            // later treats the result as provisional.
            provenance = RepositoryBindingProvenance.HumanResolved;
            var updated = repository with
            {
                WorkgroupRepoId = proposed,
                PendingDisambiguation = null,
                Hints = hints,
                Provenance = provenance,
            };

            _repositories[repositoryId] = updated;
            await _nodeStore.WriteNodeAsync(
                StudioNodeKind.RepositoryV1, repositoryId, JsonSerializer.Serialize(updated), ct).ConfigureAwait(false);

            _logger?.LogInformation(
                "re-link: '{RepositoryId}' re-pointed from '{Old}' to '{New}' ({Mode}, user-initiated)",
                repositoryId, current ?? "(unbound)", proposed, mode);
        }
        else if (commit && !willMove && !hints.IsDegraded && !Equals(repository.Hints, hints))
        {
            // Binding unchanged, but the fresh read produced better hints than the ones on file —
            // persist them so the next re-link (and the identity view) reflects reality.
            var refreshed = repository with { Hints = hints };
            _repositories[repositoryId] = refreshed;
            await _nodeStore.WriteNodeAsync(
                StudioNodeKind.RepositoryV1, repositoryId, JsonSerializer.Serialize(refreshed), ct).ConfigureAwait(false);
        }

        return new RepositoryRelinkResult(
            RepositoryId: repositoryId,
            CurrentWorkgroupRepoId: current,
            ProposedWorkgroupRepoId: willMove ? proposed : null,
            Committed: commit && willMove,
            Provenance: provenance,
            Candidates: candidates,
            Impact: impact,
            Explanation: explanation);
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

    // See the interface member's own doc comment for the full resolution-order rationale.
    public async Task<string?> ResolveCasIdentityAsync(string? repositoryId, string? repositoryPath = null, CancellationToken ct = default)
    {
        var localPath = repositoryPath;
        RepositoryV1? own = null;
        if (localPath is null && repositoryId is not null)
        {
            _repositories.TryGetValue(repositoryId, out own);
            localPath = own?.Path;
        }

        // 1. Sticky continuity.
        if (localPath is { Length: > 0 } && _snapshotService is not null)
        {
            var pathKey = Path.GetFullPath(localPath);
            var existing = await _snapshotService.GetLatestAsync(pathKey, ct).ConfigureAwait(false);
            if (existing is not null)
                return pathKey;
        }

        // 2. Workgroup-portable key.
        if (repositoryId is not null)
        {
            var workgroupRepoId = await ResolveWorkgroupRepoIdAsync(repositoryId, own, ct).ConfigureAwait(false);
            if (workgroupRepoId is not null)
                return workgroupRepoId;
        }

        // 3. Degraded local-path fallback.
        if (localPath is { Length: > 0 })
            return Path.GetFullPath(localPath);

        return null;
    }

    // Resolves WorkgroupRepoId for repositoryId — this peer's own registry cache first (the common
    // case, own is already looked up by the caller when available), then two FOREIGN-candidate
    // fallbacks (a replicated peer's own local-candidate id this peer never itself registered —
    // e.g. WorkUnit.RepositoryId minted by a different peer, exactly MultiUserMilestoneTests'
    // "work units are deliberately created on A throughout" scenario):
    //
    //   Slice 7.3 (plans/cas-distribution-and-storage.md Phase 7) note: pre-7.3 the ONLY fallback
    //   here was a direct node-store read of StudioNodeKind.RepositoryV1 keyed by repositoryId —
    //   safe only because "studio" (RepositoryV1's home room) was, pre-7.3, replicated upstream to
    //   every connected peer, so a foreign peer's own private row was visible here as a side effect.
    //   7.3 severs that (the whole point of the slice) — that node-store read now ALWAYS misses for
    //   a genuinely foreign id, since no peer's "studio" room ever receives another peer's rows
    //   anymore. Discovered as a real regression while building 7.3 (MultiUserMilestoneTests'
    //   cold-materialize step, which resolves CAS identity from a WorkUnit.RepositoryId minted by
    //   the OTHER peer, started failing) — fixed forward here rather than left broken, per this
    //   slice's own "confirm nothing breaks when studio stops syncing" instruction. The new first
    //   fallback below covers the common case (preferred-id continuity: a repo's first-ever,
    //   uncontested registration reuses its own RepositoryId as its WorkgroupRepoId — see
    //   IWorkgroupRepositoryDirectory.RegisterAsync's own doc comment — so a very common real
    //   WorkUnit.RepositoryId already IS a valid, workgroup-shared WorkgroupRepoId with no foreign
    //   row needed at all). The disambiguated-fork case (a repo whose WorkgroupRepoId differs from
    //   every peer's own local-candidate id) has no shared reverse-index today and is NOT covered —
    //   flagged as a follow-up (a real cross-peer "which local id maps to which workgroup id" index
    //   would need new replicated state, out of this slice's scope); the stale node-store read is
    //   kept only as a harmless same-peer/restart-migration safety net, not a cross-peer mechanism
    //   anymore.
    private async Task<string?> ResolveWorkgroupRepoIdAsync(string repositoryId, RepositoryV1? own, CancellationToken ct)
    {
        if (own is not null)
            return own.WorkgroupRepoId;

        if (_repositories.TryGetValue(repositoryId, out var local))
            return local.WorkgroupRepoId;

        if (_workgroupDirectory is not null)
        {
            var entries = await _workgroupDirectory.ListAsync(ct).ConfigureAwait(false);
            if (entries.Any(e => string.Equals(e.RepoId, repositoryId, StringComparison.Ordinal)))
                return repositoryId;
        }

        var payloadJson = await _nodeStore.ReadNodeAsync(StudioNodeKind.RepositoryV1, repositoryId, ct).ConfigureAwait(false);
        if (payloadJson is null)
            return null;

        try
        {
            var foreign = JsonSerializer.Deserialize<RepositoryV1>(payloadJson);
            return foreign?.WorkgroupRepoId;
        }
        catch (JsonException)
        {
            return null;
        }
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

    // Slice 6.5 Part 1 made this a no-op instead of the default "call RehydrateAsync again". Root
    // cause at the time: "studio" was not actually a peer-private room the way RepositoryV1's
    // "peer-local candidate" convention assumes (see StudioNodeKind.RepoScopedKinds' own comment:
    // "RepositoryV1 itself... stays local by necessity"). A CONNECTED peer's "studio" room *was*
    // (pre-7.3) the literal same server-side room as the embedded host it connects to (RoomPeerClient
    // joined room `options.RoomId`, hardcoded "studio", by opening a WebSocket to that exact room name
    // on the remote host) — so once replication caught up, THIS peer's own engine-level view of
    // "studio" also contained every OTHER peer's own RepositoryV1 rows, not just this peer's. A
    // one-time startup RehydrateAsync (before any connection exists) never saw that — but re-running
    // it live, after packs had arrived, silently absorbed another peer's own local-candidate
    // registrations into this peer's _repositories cache as if they were this peer's own. That
    // corrupted BoundRepoRooms.GetBoundRepoRoomIdsAsync (used by RoomPeerClient's membership loop to
    // decide which repo rooms to actually JOIN), making a peer that only ever bound to one repo start
    // joining every repo the OTHER peer happened to have registered — confirmed by direct
    // instrumentation while building 6.5 Part 1: without the no-op, a peer bound only to repo R1
    // ended up also joining R2's and R3's rooms and genuinely receiving their real catch-up content.
    //
    // Slice 7.3 (plans/cas-distribution-and-storage.md Phase 7) restores the default live-refresh
    // behavior: RoomPeerClient no longer joins or pushes "studio" upstream AT ALL (see that class's
    // own 7.3 comments) — the room is now genuinely peer-private in the way this convention always
    // assumed. The root cause above is therefore structurally gone, not merely papered over: no
    // pack for "studio" can ever arrive on this peer from anywhere but this peer's own writes, so
    // RehydrateAsync's ReadAllNodesAsync(RepositoryV1) can only ever re-absorb rows THIS peer itself
    // already wrote — safe to run live, exactly like every other IRehydratable's default. See
    // RoomReplicationTests' 7.3 no-cross-peer-absorption test for the proof (two peers, each with
    // their own unrelated local repository registration, connected via a SHARED repo room so a live
    // refresh actually fires — neither peer's registry cache ever contains the other's row).
    public Task RefreshAsync(CancellationToken cancellationToken = default) => RehydrateAsync(cancellationToken);

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
