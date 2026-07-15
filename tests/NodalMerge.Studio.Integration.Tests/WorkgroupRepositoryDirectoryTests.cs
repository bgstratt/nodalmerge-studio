using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Slice 6.2 (plans/cas-distribution-and-storage.md Phase 6, D1/D2) — the workgroup repositories
// map's matching flow (docs/STUDIO_ROOM_SCHEMA.md (b)). Uses InMemoryWorkgroupRepositoryDirectory
// throughout: the plan's own 6.2 acceptance row calls "in-memory store level" sufficient for
// proving two-peer convergence, and RepositoryIdentityMatcher's logic is storage-agnostic by
// design (see its own class comment), so these tests exercise it directly without a real engine
// room.
[Trait("Category", "Integration")]
public class WorkgroupRepositoryDirectoryTests
{
    private static RepositoryIdentityHints Hints(string[]? roots = null, string[]? remotes = null) =>
        new(roots ?? [], remotes ?? []);

    [Fact]
    public async Task Two_peers_with_independent_clones_of_the_same_repo_converge_on_one_repoId()
    {
        // "Two peers" sharing one seeded workgroup map state, per the plan's own acceptance
        // wording — one shared backing store, two directory instances.
        var sharedStore = new WorkgroupRepositoryStore();
        var peerA = new InMemoryWorkgroupRepositoryDirectory(sharedStore);
        var peerB = new InMemoryWorkgroupRepositoryDirectory(sharedStore);

        var hints = Hints(roots: ["4b825dc642cb6eb9a060e54bf8d69288fbee4904"], remotes: ["github.com/acme/repo"]);

        // Peer A registers first (its clone is new to the workgroup).
        var matchOnA = await peerA.MatchAsync(hints);
        Assert.IsType<RepositoryMatchResult.NoMatch>(matchOnA);
        var registered = await peerA.RegisterAsync("acme/repo", hints);

        // Peer B independently clones the same repo (same root SHA set) and matches — it must see
        // peer A's registration (shared workgroup map state) and bind to the SAME repoId, not mint
        // a second one.
        var matchOnB = await peerB.MatchAsync(hints);
        var matched = Assert.IsType<RepositoryMatchResult.Matched>(matchOnB);
        Assert.Equal(registered.RepoId, matched.Entry.RepoId);

        // Both peers' view of the directory agrees on exactly one entry.
        Assert.Single(await peerA.ListAsync());
        Assert.Single(await peerB.ListAsync());
    }

    [Fact]
    public async Task Fork_sharing_root_shas_is_not_silently_unified_remote_tiebreak_binds_correctly()
    {
        var store = new WorkgroupRepositoryStore();
        var directory = new InMemoryWorkgroupRepositoryDirectory(store);

        var sharedRoot = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        var upstream = await directory.RegisterAsync(
            "upstream", Hints(roots: [sharedRoot], remotes: ["github.com/acme/repo"]));
        var fork = await directory.RegisterAsync(
            "fork", Hints(roots: [sharedRoot], remotes: ["github.com/someone/repo"]));

        // A local clone of the fork (same root SHA, fork's remote) must bind to the fork, not
        // upstream, and not silently unify the two.
        var forkMatch = await directory.MatchAsync(Hints(roots: [sharedRoot], remotes: ["github.com/someone/repo"]));
        var forkMatched = Assert.IsType<RepositoryMatchResult.Matched>(forkMatch);
        Assert.Equal(fork.RepoId, forkMatched.Entry.RepoId);

        var upstreamMatch = await directory.MatchAsync(Hints(roots: [sharedRoot], remotes: ["github.com/acme/repo"]));
        var upstreamMatched = Assert.IsType<RepositoryMatchResult.Matched>(upstreamMatch);
        Assert.Equal(upstream.RepoId, upstreamMatched.Entry.RepoId);

        // A local clone with an unrecognized remote (root SHA ambiguous between the two, remote
        // doesn't narrow it) must NOT silently pick one — NeedsDisambiguation, offering both.
        var unknownRemoteMatch = await directory.MatchAsync(Hints(roots: [sharedRoot], remotes: ["github.com/someone-else/repo"]));
        var needsDisambiguation = Assert.IsType<RepositoryMatchResult.NeedsDisambiguation>(unknownRemoteMatch);
        Assert.Equal(2, needsDisambiguation.Candidates.Count);
        Assert.Contains(needsDisambiguation.Candidates, c => c.RepoId == upstream.RepoId);
        Assert.Contains(needsDisambiguation.Candidates, c => c.RepoId == fork.RepoId);
    }

    [Fact]
    public async Task Fork_ambiguity_with_no_local_remote_at_all_needs_disambiguation()
    {
        var store = new WorkgroupRepositoryStore();
        var directory = new InMemoryWorkgroupRepositoryDirectory(store);
        var sharedRoot = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        await directory.RegisterAsync("upstream", Hints(roots: [sharedRoot], remotes: ["github.com/acme/repo"]));
        await directory.RegisterAsync("fork", Hints(roots: [sharedRoot], remotes: ["github.com/someone/repo"]));

        // Root SHA matches both, but the local clone has no remote configured at all to tiebreak
        // with — still an honest NeedsDisambiguation, never a silent pick.
        var result = await directory.MatchAsync(Hints(roots: [sharedRoot]));
        Assert.IsType<RepositoryMatchResult.NeedsDisambiguation>(result);
    }

    [Theory]
    [MemberData(nameof(DegradedHintScenarios))]
    public async Task Degraded_hints_never_silently_mint_or_misfile(RepositoryIdentityHints degradedHints)
    {
        var store = new WorkgroupRepositoryStore();
        var directory = new InMemoryWorkgroupRepositoryDirectory(store);
        await directory.RegisterAsync("some-repo", Hints(roots: ["deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"]));

        var result = await directory.MatchAsync(degradedHints);

        // Never Matched, never a mint — always an honest "can't tell".
        Assert.IsType<RepositoryMatchResult.NeedsDisambiguation>(result);
    }

    public static TheoryData<RepositoryIdentityHints> DegradedHintScenarios() => new()
    {
        RepositoryIdentityHints.Empty, // shallow-with-no-remote / empty-repo-with-no-remote
    };

    [Fact]
    public async Task RegisterAsync_reuses_preferred_id_when_not_already_taken()
    {
        var directory = new InMemoryWorkgroupRepositoryDirectory();
        var entry = await directory.RegisterAsync(
            "my-repo", Hints(roots: ["a"]), preferredRepoId: "repo-existing-local-candidate");

        Assert.Equal("repo-existing-local-candidate", entry.RepoId);
        Assert.Equal("repo/repo-existing-local-candidate", entry.RepoRoomId);
    }

    [Fact]
    public async Task RegisterAsync_mints_a_fresh_id_when_preferred_id_is_already_taken()
    {
        var store = new WorkgroupRepositoryStore();
        var directory = new InMemoryWorkgroupRepositoryDirectory(store);
        await directory.RegisterAsync("first", Hints(roots: ["a"]), preferredRepoId: "repo-collide");

        var second = await directory.RegisterAsync("second", Hints(roots: ["b"]), preferredRepoId: "repo-collide");

        Assert.NotEqual("repo-collide", second.RepoId);
        Assert.StartsWith("repo-", second.RepoId);
    }
}
