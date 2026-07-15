using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Slice 6.2 (plans/cas-distribution-and-storage.md Phase 6, D1/D2) — RepositoryRegistryService's
// binding flow (compute hints -> MatchAsync -> bind/register/needs-disambiguation) and the
// GET/POST /studio/repositories/{id}/identity[/resolve] REST disambiguation surface. Uses a fake
// IRepositoryIdentityHintsService (no real git repos on disk needed) so hint values are fully
// controlled per test.
[Trait("Category", "Integration")]
public class RepositoryRegistryWorkgroupBindingTests
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

    private static (IRepositoryRegistryService Registry, IWorkgroupRepositoryDirectory Directory, FakeIdentityHintsService Hints, IServiceProvider Services)
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
            fake,
            app.Services);
    }

    [Fact]
    public async Task RegisterAsync_with_empty_workgroup_map_binds_workgroup_id_to_the_local_candidate_id()
    {
        var (registry, directory, hints, _) = BuildServices();
        hints.SetHints(@"D:\Repos\Foo", new RepositoryIdentityHints(["root-sha-1"], []));

        var repo = await registry.RegisterAsync(@"D:\Repos\Foo", "Foo");

        // Single-user/standalone continuity (D2/6.2's preferred-id rule): with nothing else
        // registered, the workgroup id equals the already-minted local RepositoryId — no dual
        // identity for a workspace that's never seen another peer.
        Assert.Equal(repo.RepositoryId, repo.WorkgroupRepoId);
        Assert.Null(repo.PendingDisambiguation);

        var all = await directory.ListAsync();
        Assert.Single(all, e => e.RepoId == repo.RepositoryId);
    }

    [Fact]
    public async Task RegisterAsync_binds_to_an_existing_workgroup_entry_when_hints_match()
    {
        var (registry, directory, hints, _) = BuildServices();
        var seeded = await directory.RegisterAsync("upstream", new RepositoryIdentityHints(["shared-root"], []));

        hints.SetHints(@"D:\Repos\Bar", new RepositoryIdentityHints(["shared-root"], []));
        var repo = await registry.RegisterAsync(@"D:\Repos\Bar", "Bar");

        Assert.Equal(seeded.RepoId, repo.WorkgroupRepoId);
        Assert.NotEqual(repo.RepositoryId, repo.WorkgroupRepoId);
        Assert.Null(repo.PendingDisambiguation);
    }

    [Fact]
    public async Task RegisterAsync_records_pending_disambiguation_on_fork_ambiguity_and_ResolveDisambiguationAsync_binds_the_chosen_candidate()
    {
        var (registry, directory, hints, _) = BuildServices();
        var upstream = await directory.RegisterAsync("upstream", new RepositoryIdentityHints(["shared-root"], ["github.com/acme/repo"]));
        var fork = await directory.RegisterAsync("fork", new RepositoryIdentityHints(["shared-root"], ["github.com/someone/repo"]));

        hints.SetHints(@"D:\Repos\Ambiguous", new RepositoryIdentityHints(["shared-root"], ["github.com/unknown/repo"]));
        var repo = await registry.RegisterAsync(@"D:\Repos\Ambiguous", "Ambiguous");

        Assert.Null(repo.WorkgroupRepoId);
        Assert.NotNull(repo.PendingDisambiguation);
        Assert.Equal(2, repo.PendingDisambiguation!.Candidates.Count);
        Assert.Contains(repo.PendingDisambiguation.Candidates, c => c.RepoId == upstream.RepoId);
        Assert.Contains(repo.PendingDisambiguation.Candidates, c => c.RepoId == fork.RepoId);

        var resolved = await registry.ResolveDisambiguationAsync(repo.RepositoryId, fork.RepoId);

        Assert.NotNull(resolved);
        Assert.Equal(fork.RepoId, resolved!.WorkgroupRepoId);
        Assert.Null(resolved.PendingDisambiguation);
    }

    [Fact]
    public async Task ResolveDisambiguationAsync_register_new_mints_a_fresh_workgroup_entry()
    {
        var (registry, directory, hints, _) = BuildServices();
        await directory.RegisterAsync("upstream", new RepositoryIdentityHints(["shared-root"], ["github.com/acme/repo"]));
        await directory.RegisterAsync("fork", new RepositoryIdentityHints(["shared-root"], ["github.com/someone/repo"]));

        hints.SetHints(@"D:\Repos\NewOne", new RepositoryIdentityHints(["shared-root"], ["github.com/unknown/repo"]));
        var repo = await registry.RegisterAsync(@"D:\Repos\NewOne", "NewOne");
        Assert.NotNull(repo.PendingDisambiguation);

        var resolved = await registry.ResolveDisambiguationAsync(repo.RepositoryId, "register-new");

        Assert.NotNull(resolved!.WorkgroupRepoId);
        Assert.Null(resolved.PendingDisambiguation);
        var all = await directory.ListAsync();
        Assert.Equal(3, all.Count); // upstream + fork + the freshly minted entry
    }

    [Fact]
    public async Task ResolveDisambiguationAsync_rejects_a_repoId_that_was_not_offered()
    {
        var (registry, directory, hints, _) = BuildServices();
        await directory.RegisterAsync("upstream", new RepositoryIdentityHints(["shared-root"], ["github.com/acme/repo"]));
        await directory.RegisterAsync("fork", new RepositoryIdentityHints(["shared-root"], ["github.com/someone/repo"]));

        hints.SetHints(@"D:\Repos\Ambiguous2", new RepositoryIdentityHints(["shared-root"], ["github.com/unknown/repo"]));
        var repo = await registry.RegisterAsync(@"D:\Repos\Ambiguous2", "Ambiguous2");

        await Assert.ThrowsAsync<ArgumentException>(() => registry.ResolveDisambiguationAsync(repo.RepositoryId, "repo-not-a-real-candidate"));
    }

    // Hints consulted once (D2): a binding, once made, is never re-derived even if the underlying
    // repository's hints change (rebase, remote rename). RegisterAsync's idempotent-by-path
    // short-circuit is exactly the mechanism that guarantees this — proven here by mutating the
    // fake's hints between two RegisterAsync calls for the same path and confirming both the
    // binding AND the hint-computation call count are unaffected by the second call.
    [Fact]
    public async Task A_cached_binding_is_never_reidentified_after_hints_change()
    {
        var (registry, _, hints, _) = BuildServices();
        var path = @"D:\Repos\Stable";
        hints.SetHints(path, new RepositoryIdentityHints(["root-a"], ["github.com/acme/repo"]));

        var first = await registry.RegisterAsync(path, "Stable");
        Assert.Equal(1, hints.ComputeCallCount);

        // Simulate a rebase (different root) and a remote rename post-binding.
        hints.SetHints(path, new RepositoryIdentityHints(["root-b-totally-different"], ["github.com/acme/renamed"]));

        var second = await registry.RegisterAsync(path, "Stable");

        Assert.Equal(first.RepositoryId, second.RepositoryId);
        Assert.Equal(first.WorkgroupRepoId, second.WorkgroupRepoId);
        Assert.Equal(1, hints.ComputeCallCount); // never re-consulted for an already-registered path
    }

    [Fact]
    public async Task REST_identity_endpoint_reports_pending_disambiguation_and_resolve_binds_it()
    {
        var fake = new FakeIdentityHintsService();
        await using var webApp = StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton<IRepositoryIdentityHintsService>(fake);
            });
        await webApp.StartAsync();
        var client = webApp.GetTestClient();

        var directory = webApp.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
        var upstream = await directory.RegisterAsync("upstream", new RepositoryIdentityHints(["shared-root"], ["github.com/acme/repo"]));
        var fork = await directory.RegisterAsync("fork", new RepositoryIdentityHints(["shared-root"], ["github.com/someone/repo"]));

        var repositories = webApp.Services.GetRequiredService<IRepositoryRegistryService>();
        fake.SetHints(@"D:\Repos\RestAmbiguous", new RepositoryIdentityHints(["shared-root"], ["github.com/unknown/repo"]));
        var repo = await repositories.RegisterAsync(@"D:\Repos\RestAmbiguous", "RestAmbiguous");

        var identityResponse = await client.GetAsync($"/studio/repositories/{repo.RepositoryId}/identity");
        identityResponse.EnsureSuccessStatusCode();
        var identityDoc = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.True(identityDoc.GetProperty("workgroupRepoId").ValueKind == JsonValueKind.Null);
        var candidates = identityDoc.GetProperty("pendingDisambiguation").GetProperty("candidates").EnumerateArray()
            .Select(c => c.GetProperty("repoId").GetString()).ToList();
        Assert.Contains(upstream.RepoId, candidates);
        Assert.Contains(fork.RepoId, candidates);

        var resolveResponse = await client.PostAsJsonAsync(
            $"/studio/repositories/{repo.RepositoryId}/identity/resolve", new { chosenRepoId = fork.RepoId });
        resolveResponse.EnsureSuccessStatusCode();
        var resolveDoc = JsonDocument.Parse(await resolveResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(fork.RepoId, resolveDoc.GetProperty("workgroupRepoId").GetString());

        var followUp = await client.GetAsync($"/studio/repositories/{repo.RepositoryId}/identity");
        followUp.EnsureSuccessStatusCode();
        var followUpDoc = JsonDocument.Parse(await followUp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(fork.RepoId, followUpDoc.GetProperty("workgroupRepoId").GetString());
        Assert.True(followUpDoc.GetProperty("pendingDisambiguation").ValueKind == JsonValueKind.Null);
    }
}
