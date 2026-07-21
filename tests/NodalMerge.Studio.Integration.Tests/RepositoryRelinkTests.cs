using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// plans/vision-punchlist-remediation.md (Items 1+2) — user-initiated repository re-link.
//
// Deterministic ids converge two clones that both have a root-SHA signal. Two populations were still
// stranded with no automatic escape: a degraded checkout (shallow/empty/no-HEAD) that minted a guid
// and kept it forever, and installs that already diverged before deterministic ids existed. Re-link
// is the human-driven way out. It is the only path allowed to re-read git and re-match an
// already-bound repository — and it must never fire on its own (docs/STUDIO_ROOM_SCHEMA.md, "User-
// initiated re-link").
//
// Uses the same fake IRepositoryIdentityHintsService pattern as
// RepositoryRegistryWorkgroupBindingTests so hints can be *changed between calls* — which is what
// makes the un-shallowed-clone case testable at all.
[Trait("Category", "Integration")]
public class RepositoryRelinkTests
{
    private sealed class FakeIdentityHintsService : IRepositoryIdentityHintsService
    {
        private readonly Dictionary<string, RepositoryIdentityHints> _hintsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly GitRepositoryIdentityHintsService _real = new();

        public int ComputeCallCount { get; private set; }

        public void SetHints(string path, RepositoryIdentityHints hints) => _hintsByPath[path] = hints;

        public Task<RepositoryIdentityHints> ComputeAsync(string repositoryPath, CancellationToken cancellationToken = default)
        {
            ComputeCallCount++;
            return Task.FromResult(_hintsByPath.TryGetValue(repositoryPath, out var hints) ? hints : RepositoryIdentityHints.Empty);
        }

        public string NormalizeRemoteUrl(string rawRemoteUrl) => _real.NormalizeRemoteUrl(rawRemoteUrl);
    }

    private static (IRepositoryRegistryService Registry, IWorkgroupRepositoryDirectory Directory, FakeIdentityHintsService Hints)
        BuildServices()
    {
        var fake = new FakeIdentityHintsService();
        var app = StudioWebApplication.Build(
            [],
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton<IRepositoryIdentityHintsService>(fake);
            });
        return (
            app.Services.GetRequiredService<IRepositoryRegistryService>(),
            app.Services.GetRequiredService<IWorkgroupRepositoryDirectory>(),
            fake);
    }

    /// <summary>
    /// THE anchor case, and the one no cached-hints design could ever solve: a repository registered
    /// from a shallow clone has no root SHA, so it mints a guid and is stranded. Once the clone has
    /// its history, re-link re-reads git, finds the real root SHA, and converges onto the workgroup's
    /// canonical entry. This is precisely why re-link is allowed to re-read.
    /// </summary>
    [Fact]
    public async Task Auto_relink_re_reads_git_so_an_un_shallowed_clone_converges()
    {
        var (registry, directory, hints) = BuildServices();
        const string path = @"D:\Repos\Shallow";

        // Registered while shallow — no root SHA, no remotes.
        hints.SetHints(path, RepositoryIdentityHints.Empty);
        var repo = await registry.RegisterAsync(path, "Shallow");
        Assert.Equal(RepositoryBindingProvenance.ProvisionalMint, repo.Provenance);

        // The canonical repository, registered by a peer that had full history.
        var canonical = await directory.RegisterAsync("upstream", new RepositoryIdentityHints(["shared-root"], []));
        Assert.NotEqual(canonical.RepoId, repo.WorkgroupRepoId);

        // The clone gains its history (a `git fetch --unshallow`).
        hints.SetHints(path, new RepositoryIdentityHints(["shared-root"], []));

        var result = await registry.RelinkAsync(repo.RepositoryId, RepositoryRelinkMode.Auto);

        Assert.NotNull(result);
        Assert.True(result!.Committed);
        Assert.Equal(canonical.RepoId, result.ProposedWorkgroupRepoId);

        var after = await registry.GetAsync(repo.RepositoryId);
        Assert.Equal(canonical.RepoId, after!.WorkgroupRepoId);
        // A re-link is a human decision however it was reached, so nothing later treats it as
        // provisional and moves it again.
        Assert.Equal(RepositoryBindingProvenance.HumanResolved, after.Provenance);
    }

    /// <summary>
    /// The real two-machine scenario: this peer is already bound to a room (a guid it minted alone),
    /// and the canonical repository exists elsewhere in the workgroup. ResolveDisambiguationAsync
    /// cannot fix this — it no-ops unless a disambiguation is pending — which is the gap re-link
    /// closes.
    /// </summary>
    [Fact]
    public async Task Auto_relink_repoints_an_already_settled_divergent_binding()
    {
        var (registry, directory, hints) = BuildServices();
        const string path = @"D:\Repos\Diverged";

        // Bound, settled, and wrong: minted alone with no signal.
        hints.SetHints(path, RepositoryIdentityHints.Empty);
        var repo = await registry.RegisterAsync(path, "Diverged");
        var mintedRoom = repo.WorkgroupRepoId;
        Assert.NotNull(mintedRoom);

        var canonical = await directory.RegisterAsync("canonical", new RepositoryIdentityHints(["real-root"], []));
        hints.SetHints(path, new RepositoryIdentityHints(["real-root"], []));

        var result = await registry.RelinkAsync(repo.RepositoryId, RepositoryRelinkMode.Auto);

        Assert.True(result!.Committed);
        Assert.Equal(mintedRoom, result.CurrentWorkgroupRepoId);
        Assert.Equal(canonical.RepoId, result.ProposedWorkgroupRepoId);
        Assert.Equal(canonical.RepoId, (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId);
    }

    /// <summary>
    /// A preview must evaluate exactly like the real thing but write nothing — that is what lets the
    /// UI state what will change, and what will stop appearing, before the user commits.
    /// </summary>
    [Fact]
    public async Task Relink_preview_reports_the_change_without_committing_it()
    {
        var (registry, directory, hints) = BuildServices();
        const string path = @"D:\Repos\Preview";

        hints.SetHints(path, RepositoryIdentityHints.Empty);
        var repo = await registry.RegisterAsync(path, "Preview");
        var before = (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId;

        var canonical = await directory.RegisterAsync("canonical", new RepositoryIdentityHints(["r"], []));
        hints.SetHints(path, new RepositoryIdentityHints(["r"], []));

        var preview = await registry.RelinkAsync(repo.RepositoryId, RepositoryRelinkMode.Auto, commit: false);

        Assert.False(preview!.Committed);
        Assert.Equal(canonical.RepoId, preview.ProposedWorkgroupRepoId);   // it knows what it would do
        Assert.Equal(before, (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId); // ...and did not do it
    }

    /// <summary>
    /// A genuine fork shares a root commit but has disjoint remotes. Auto mode must never silently
    /// fold one into the other's room — it commits nothing and hands back the candidates.
    /// </summary>
    [Fact]
    public async Task Auto_relink_never_collapses_a_fork_and_returns_candidates_instead()
    {
        var (registry, directory, hints) = BuildServices();
        const string path = @"D:\Repos\Fork";

        await directory.RegisterAsync("upstream", new RepositoryIdentityHints(["shared-root"], ["github.com/acme/repo"]));
        await directory.RegisterAsync("otherfork", new RepositoryIdentityHints(["shared-root"], ["github.com/someone/repo"]));

        hints.SetHints(path, RepositoryIdentityHints.Empty);
        var repo = await registry.RegisterAsync(path, "Fork");
        var before = (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId;

        // Shares the root commit with both, but its own remote matches neither.
        hints.SetHints(path, new RepositoryIdentityHints(["shared-root"], ["github.com/mine/repo"]));

        var result = await registry.RelinkAsync(repo.RepositoryId, RepositoryRelinkMode.Auto);

        Assert.False(result!.Committed);
        Assert.Null(result.ProposedWorkgroupRepoId);
        Assert.NotEmpty(result.Candidates);
        Assert.Equal(before, (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId);
    }

    /// <summary>
    /// D2's actual concern: a binding a person chose must never be moved by an automatic pass, even
    /// one they triggered on a different repository.
    /// </summary>
    [Fact]
    public async Task Auto_relink_leaves_a_human_resolved_binding_alone()
    {
        var (registry, directory, hints) = BuildServices();
        const string path = @"D:\Repos\Chosen";

        hints.SetHints(path, RepositoryIdentityHints.Empty);
        var repo = await registry.RegisterAsync(path, "Chosen");

        var target = await directory.RegisterAsync("target", new RepositoryIdentityHints(["t-root"], []));
        await registry.RelinkAsync(repo.RepositoryId, RepositoryRelinkMode.Manual, target.RepoId);
        Assert.Equal(RepositoryBindingProvenance.HumanResolved,
            (await registry.GetAsync(repo.RepositoryId))!.Provenance);

        // A different repository now matches this path's history — Auto must still not move it.
        var other = await directory.RegisterAsync("other", new RepositoryIdentityHints(["other-root"], []));
        hints.SetHints(path, new RepositoryIdentityHints(["other-root"], []));

        var result = await registry.RelinkAsync(repo.RepositoryId, RepositoryRelinkMode.Auto);

        Assert.False(result!.Committed);
        Assert.NotEqual(other.RepoId, (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId);
        Assert.Equal(target.RepoId, (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId);
    }

    /// <summary>
    /// Manual mode is the escape hatch for everything Auto refuses to guess at, including
    /// "register-new" for a repository that only *looks* like another one.
    /// </summary>
    [Fact]
    public async Task Manual_relink_register_new_splits_the_repository_into_its_own_room()
    {
        var (registry, directory, hints) = BuildServices();
        const string path = @"D:\Repos\Split";

        var shared = await directory.RegisterAsync("shared", new RepositoryIdentityHints(["same-root"], []));
        hints.SetHints(path, new RepositoryIdentityHints(["same-root"], []));

        var repo = await registry.RegisterAsync(path, "Split");
        Assert.Equal(shared.RepoId, repo.WorkgroupRepoId); // auto-bound to the shared room on register

        var result = await registry.RelinkAsync(repo.RepositoryId, RepositoryRelinkMode.Manual, "register-new");

        Assert.True(result!.Committed);
        Assert.NotEqual(shared.RepoId, result.ProposedWorkgroupRepoId);
        Assert.Equal(result.ProposedWorkgroupRepoId, (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId);
    }

    /// <summary>
    /// Re-link is USER-INITIATED ONLY. Nothing in the runtime may call it — no timer, no startup
    /// sweep, no inbound-pack handler. This guards the schema rule by asserting that merely making a
    /// better-matching entry visible changes no binding until someone actually asks.
    /// </summary>
    [Fact]
    public async Task A_better_match_appearing_changes_nothing_until_a_relink_is_requested()
    {
        var (registry, directory, hints) = BuildServices();
        const string path = @"D:\Repos\Passive";

        hints.SetHints(path, RepositoryIdentityHints.Empty);
        var repo = await registry.RegisterAsync(path, "Passive");
        var before = (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId;

        // A canonical entry lands in the workgroup map, and the local checkout gains matching
        // history — everything an automatic re-resolver would have needed.
        await directory.RegisterAsync("canonical", new RepositoryIdentityHints(["now-matching"], []));
        hints.SetHints(path, new RepositoryIdentityHints(["now-matching"], []));

        // Re-read the registry the way the runtime does. Nothing here re-matches.
        await ((IRehydratable)registry).RefreshAsync();

        Assert.Equal(before, (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId);

        // ...and only now, on an explicit request, does it converge.
        var result = await registry.RelinkAsync(repo.RepositoryId, RepositoryRelinkMode.Auto);
        Assert.True(result!.Committed);
        Assert.NotEqual(before, (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId);
    }
}
