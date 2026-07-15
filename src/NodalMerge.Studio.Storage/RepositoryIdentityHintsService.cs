using System.Text.RegularExpressions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Storage;

// Slice 6.2 (plans/cas-distribution-and-storage.md Phase 6, D1/D2) — computes the local-folder
// side of docs/STUDIO_ROOM_SCHEMA.md (b)'s matching hints: the root-commit SHA set (equivalent to
// `git rev-list --max-parents=0 HEAD`) and normalized remote URLs. Never throws; every degraded
// case (no git repo, no commits yet, no remotes, a corrupted/partial clone) degrades to an empty
// set for the affected component, independently — a failure computing root SHAs must not also
// blank out remotes that resolved fine, and vice versa.
//
// Process-invocation choice: unlike RepositoryRegistryService.RunGitAsync (which shells `git
// clone`/`git init` — operations LibGit2Sharp doesn't perform) or GitAdapter.GitPushAsync (which
// shells `git push` specifically to reuse the host's credential store), hint computation is pure
// read-only repository *inspection* — exactly what GitAdapter's ImportAsync/ExportAsync/
// CreateGitBranchAsync already do via `new LibGit2Sharp.Repository(path)`. Using LibGit2Sharp here
// (already a NodalMerge.Studio.Storage package dependency) instead of shelling `git rev-list`/`git
// remote -v` avoids a second process-invocation style for what is conceptually the same kind of
// operation GitAdapter performs, and sidesteps requiring a `git` binary on PATH at all — the
// "no git binary" degraded case from the task brief is subsumed: LibGit2Sharp bundles its own
// native git implementation, so there is no external executable to be missing. The remaining
// degraded cases (not a git repo, shallow clone, empty repo/no HEAD, no remotes) are real
// LibGit2Sharp-level conditions handled explicitly below.
public interface IRepositoryIdentityHintsService
{
    Task<RepositoryIdentityHints> ComputeAsync(string repositoryPath, CancellationToken cancellationToken = default);

    // Exposed directly (not just as an implementation detail) so the frozen normalization
    // algorithm's worked-example vectors can be asserted against verbatim in unit tests without
    // opening a real repository.
    string NormalizeRemoteUrl(string rawRemoteUrl);
}

public sealed class GitRepositoryIdentityHintsService(ILogger<GitRepositoryIdentityHintsService>? logger = null)
    : IRepositoryIdentityHintsService
{
    public Task<RepositoryIdentityHints> ComputeAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Repository repo;
        try
        {
            repo = new Repository(repositoryPath);
        }
        catch (Exception ex)
        {
            // Not a git repo, path doesn't exist, corrupted .git, or (in principle) a LibGit2Sharp
            // native-load failure — all degrade to "no signal", never a throw (D2: hints are best
            // effort; the caller must be able to proceed with an honest "can't tell" rather than a
            // registration failure).
            logger?.LogDebug(ex, "repository identity hints: could not open '{Path}' as a git repository — degraded (empty) hints", repositoryPath);
            return Task.FromResult(RepositoryIdentityHints.Empty);
        }

        using (repo)
        {
            var rootShas = TryComputeRootShas(repo, repositoryPath);
            var remotes = TryComputeRemotes(repo, repositoryPath);
            return Task.FromResult(new RepositoryIdentityHints(rootShas, remotes));
        }
    }

    // Equivalent to `git rev-list --max-parents=0 HEAD`: every commit reachable from HEAD with zero
    // parents. A set (order-insensitive, deduplicated) because merged-unrelated-histories repos
    // have more than one root. Width-agnostic per the frozen doc — SHA-1 (40 hex) today, SHA-256
    // (64 hex) under `extensions.objectFormat = sha256`; nothing here assumes a fixed length.
    private IReadOnlyList<string> TryComputeRootShas(Repository repo, string repositoryPath)
    {
        try
        {
            // Empty repo (no commits yet — "git init" with nothing committed) has an unborn HEAD;
            // no commits are reachable, so the root set is legitimately empty, not an error.
            if (repo.Info.IsHeadUnborn)
                return [];

            var filter = new CommitFilter { IncludeReachableFrom = repo.Head };
            return repo.Commits.QueryBy(filter)
                .Where(c => !c.Parents.Any())
                .Select(c => c.Sha)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            // Shallow clones (LibGit2Sharp honors the same .git/shallow grafting git itself does,
            // so this ordinarily just works and returns the shallow boundary commit(s) — still only
            // a hint, per D2), or any other unexpected repository-state failure: degrade to empty
            // rather than fail the whole hint computation.
            logger?.LogDebug(ex, "repository identity hints: root-SHA computation failed for '{Path}' — degraded (empty) root SHA set", repositoryPath);
            return [];
        }
    }

    private IReadOnlyList<string> TryComputeRemotes(Repository repo, string repositoryPath)
    {
        try
        {
            return repo.Network.Remotes
                .Select(r => r.Url)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(NormalizeRemoteUrl)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "repository identity hints: remote enumeration failed for '{Path}' — degraded (empty) remotes set", repositoryPath);
            return [];
        }
    }

    // docs/STUDIO_ROOM_SCHEMA.md (b) "Remote-URL normalization" — implemented to the letter,
    // 6 ordered steps. See the worked-example table there; RepositoryIdentityHintsNormalizationTests
    // pins every row plus the extra vectors the 6.2 task brief calls for (scp-form with no user,
    // trailing-slash+".git" combined).
    public string NormalizeRemoteUrl(string rawRemoteUrl)
    {
        var raw = rawRemoteUrl.Trim();

        string hostAndPort;
        string path;

        var schemeMatch = UriSchemeRegex.Match(raw);
        if (schemeMatch.Success)
        {
            // Step 1 (URI form): scheme://[user[:pass]@]host[:port]/path.
            var scheme = schemeMatch.Groups[1].Value.ToLowerInvariant();
            var rest = schemeMatch.Groups[2].Value;

            var slashIdx = rest.IndexOf('/');
            var authority = slashIdx >= 0 ? rest[..slashIdx] : rest;
            path = slashIdx >= 0 ? rest[(slashIdx + 1)..] : "";

            // Discard user[:pass]@ entirely.
            var atIdx = authority.LastIndexOf('@');
            if (atIdx >= 0)
                authority = authority[(atIdx + 1)..];

            var colonIdx = authority.IndexOf(':');
            string host;
            int? port = null;
            if (colonIdx >= 0 && int.TryParse(authority[(colonIdx + 1)..], out var parsedPort))
            {
                host = authority[..colonIdx];
                port = parsedPort;
            }
            else
            {
                host = authority;
            }

            // Step 2: lowercase host only, path keeps its case.
            host = host.ToLowerInvariant();

            // Step 3: drop the port if it's the scheme's default.
            var isDefaultPort =
                (port == 80 && scheme == "http") ||
                (port == 443 && scheme == "https") ||
                (port == 22 && (scheme == "ssh" || scheme == "git"));
            hostAndPort = port is not null && !isDefaultPort ? $"{host}:{port}" : host;
        }
        else if (TryParseScpForm(raw, out var scpHost, out var scpPath))
        {
            // Step 1 (SCP-like form): [user@]host:path, no "://" anywhere and no port expressible.
            hostAndPort = scpHost.ToLowerInvariant();
            path = scpPath;
        }
        else
        {
            // Step 1 (bare form): already host/path (e.g. feeding a prior normalization output back
            // in) — host = first path segment (may itself already contain ":port" from a bare
            // re-normalization; lowercasing the whole segment is safe either way), path = the rest.
            var slashIdx = raw.IndexOf('/');
            hostAndPort = (slashIdx >= 0 ? raw[..slashIdx] : raw).ToLowerInvariant();
            path = slashIdx >= 0 ? raw[(slashIdx + 1)..] : "";
        }

        // Step 4: strip exactly one trailing ".git" suffix, if present — applied once, in this
        // order, before step 5. A path ending in ".git/" (trailing slash *after* the suffix) is
        // therefore NOT stripped of ".git" by this step (it doesn't end in ".git", it ends in "/")
        // — only the trailing slash is removed by step 5 below. This is the literal, frozen-order
        // consequence exercised by the "trailing-slash+.git combined" test vector; it is not a bug,
        // it's what "steps applied in order, once each" specifies.
        if (path.EndsWith(".git", StringComparison.Ordinal))
            path = path[..^4];

        // Step 5: strip one or more trailing '/' characters.
        path = path.TrimEnd('/');

        // Step 6: join.
        return path.Length == 0 ? hostAndPort : $"{hostAndPort}/{path}";
    }

    private static readonly Regex UriSchemeRegex = new(@"^([A-Za-z][A-Za-z0-9+.\-]*)://(.*)$", RegexOptions.Singleline | RegexOptions.Compiled);

    // SCP-like form per the frozen doc: "[user@]host:path", identified by the *absence* of
    // "://" anywhere in the string (already ruled out by the caller not matching UriSchemeRegex)
    // plus the presence of a ':' splitting host from path. Guards against misreading a Windows
    // drive-letter local path ("C:\repos\foo") as scp form — a drive letter is exactly one
    // character, immediately followed by '\' (or end of string).
    private static bool TryParseScpForm(string raw, out string host, out string path)
    {
        host = "";
        path = "";

        var colonIdx = raw.IndexOf(':');
        if (colonIdx <= 0)
            return false;

        if (colonIdx == 1 && (raw.Length == 2 || raw[2] == '\\'))
            return false;

        var hostPart = raw[..colonIdx];
        var pathPart = raw[(colonIdx + 1)..];

        var atIdx = hostPart.IndexOf('@');
        host = atIdx >= 0 ? hostPart[(atIdx + 1)..] : hostPart;
        path = pathPart;

        return host.Length > 0;
    }
}
