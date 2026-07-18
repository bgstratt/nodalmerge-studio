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

    // Phase 1 (plans/repo-identity-convergence.md) — the real production race: two peers register
    // the same repo WITHOUT a shared/replicated workgroup map (separate stores), and with
    // DIFFERENT remote sets (the eval repo had {local-path,github} on one machine, {github} on the
    // other). They must still converge on the same repoId, computed from the root-SHA set alone,
    // with zero replication dependency. RED before Phase 1 (each mints its own guid).
    [Fact]
    public async Task Two_peers_registering_independently_without_a_shared_map_converge_on_the_same_id()
    {
        var peerA = new InMemoryWorkgroupRepositoryDirectory(new WorkgroupRepositoryStore());
        var peerB = new InMemoryWorkgroupRepositoryDirectory(new WorkgroupRepositoryStore());

        var sharedRoot = "76dbfae5adf4ae2130fba0f88990ac5cc86ddbd0";
        // Neither peer's map contains the other's entry yet (the startup race).
        Assert.IsType<RepositoryMatchResult.NoMatch>(
            await peerA.MatchAsync(Hints(roots: [sharedRoot], remotes: ["local-path/eval", "github.com/acme/eval"])));
        Assert.IsType<RepositoryMatchResult.NoMatch>(
            await peerB.MatchAsync(Hints(roots: [sharedRoot], remotes: ["github.com/acme/eval"])));

        var a = await peerA.RegisterAsync("eval", Hints(roots: [sharedRoot], remotes: ["local-path/eval", "github.com/acme/eval"]));
        var b = await peerB.RegisterAsync("eval", Hints(roots: [sharedRoot], remotes: ["github.com/acme/eval"]));

        Assert.Equal(a.RepoId, b.RepoId);
        Assert.Equal(a.RepoRoomId, b.RepoRoomId);
    }

    // Phase 1 — the deterministic id function is a pure function of the root-SHA set only: stable
    // across remote differences and clone order, distinct for distinct roots, null when degraded.
    [Fact]
    public void DeterministicRepoId_depends_only_on_the_root_sha_set()
    {
        var id1 = RepositoryIdentityMatcher.DeterministicRepoId(["r1", "r2"]);
        var id2 = RepositoryIdentityMatcher.DeterministicRepoId(["r2", "r1"]); // order-insensitive
        var id3 = RepositoryIdentityMatcher.DeterministicRepoId(["r1", "r2", "r2"]); // dup-insensitive
        var other = RepositoryIdentityMatcher.DeterministicRepoId(["r3"]);

        Assert.NotNull(id1);
        Assert.StartsWith("repo-", id1);
        Assert.Equal(id1, id2);
        Assert.Equal(id1, id3);
        Assert.NotEqual(id1, other);
        Assert.Null(RepositoryIdentityMatcher.DeterministicRepoId([])); // degraded -> no deterministic id
    }

    // Phase 1 fork-split (D2's real concern preserved): a lone root-matching entry whose remotes
    // are non-empty and DISJOINT from the local remotes is a possible fork sharing the deterministic
    // id — must NOT silently unify. RED before Phase 1's matcher change (single root match -> Matched).
    [Fact]
    public async Task Single_root_matching_entry_with_disjoint_remotes_needs_disambiguation()
    {
        var directory = new InMemoryWorkgroupRepositoryDirectory(new WorkgroupRepositoryStore());
        var sharedRoot = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        await directory.RegisterAsync("upstream", Hints(roots: [sharedRoot], remotes: ["github.com/acme/repo"]));

        // Same root SHA, a wholly different remote, and it's the ONLY entry — a fork, not this repo.
        var result = await directory.MatchAsync(Hints(roots: [sharedRoot], remotes: ["github.com/someone/repo"]));
        Assert.IsType<RepositoryMatchResult.NeedsDisambiguation>(result);

        // But an overlapping remote (or no local remote) is the same repo -> Matched, not split.
        Assert.IsType<RepositoryMatchResult.Matched>(
            await directory.MatchAsync(Hints(roots: [sharedRoot], remotes: ["github.com/acme/repo"])));
        Assert.IsType<RepositoryMatchResult.Matched>(
            await directory.MatchAsync(Hints(roots: [sharedRoot])));
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
