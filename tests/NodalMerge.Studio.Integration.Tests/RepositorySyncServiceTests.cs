using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers IRepositorySyncService — the service that keeps "main" (and, conceptually, any branch
/// it's pointed at) synced with the real repository on disk, fixing the bug where "main" was
/// seeded once and then frozen forever (see plans/.. and FileSystemWorkspaceServiceSeedingTests).
/// Every mutation is preceded/followed by an immutable KnownGoodState snapshot and recorded as a
/// system-owned (OwnedByWorkUnitId == null) ExternalChangeset artifact.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class RepositorySyncServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-reposync-{Guid.NewGuid():N}");
    private readonly List<string> _externalDirs = [];

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
        foreach (var dir in _externalDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    private string NewExternalRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"studio-reposync-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _externalDirs.Add(dir);
        return dir;
    }

    private static void Write(string repoDir, string relativePath, string content)
    {
        var full = Path.Combine(repoDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private sealed record Harness(
        IRepositorySyncService Sync,
        IFileWorkspaceService FileWorkspace,
        IArtifactLineageService Artifacts,
        IKnownGoodStateService KnownGood,
        IServiceProvider Provider);

    private Harness Build(InMemoryStudioNodeStore? sharedStore = null, string? seedRepositoryPath = null)
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        if (sharedStore is not null)
            services.AddSingleton<IStudioNodeStore>(sharedStore);
        services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath, SeedRepositoryPath = seedRepositoryPath });
        var provider = services.BuildServiceProvider();
        return new Harness(
            provider.GetRequiredService<IRepositorySyncService>(),
            provider.GetRequiredService<IFileWorkspaceService>(),
            provider.GetRequiredService<IArtifactLineageService>(),
            provider.GetRequiredService<IKnownGoodStateService>(),
            provider);
    }

    [Fact]
    public async Task Sync_against_a_main_already_fully_seeded_by_InitBranchAsync_is_a_silent_no_op()
    {
        var repo = NewExternalRepo();
        Write(repo, "Program.cs", "// real source");

        // SeedRepositoryPath set up-front, exactly like InMemoryWorkUnitService's existing
        // one-time bootstrap — main is fully populated before RepositorySyncService ever runs.
        var h = Build(seedRepositoryPath: repo);

        var pending = await h.Sync.SyncBranchFromRepositoryAsync("main", repo, SyncTrigger.GoalCreation);

        Assert.Null(pending);
        Assert.Empty(await h.KnownGood.FindKnownGoodAsync("main"));

        var state = await h.Sync.GetStateAsync("main");
        Assert.NotNull(state);
        Assert.Equal(repo, state!.RepositoryPath);
        Assert.Equal(0, state.Generation);
        Assert.Null(state.LatestExternalChangesetId);
    }

    [Fact]
    public async Task Sync_against_a_genuinely_empty_main_records_a_real_InitialBootstrap_artifact()
    {
        var repo = NewExternalRepo();
        Write(repo, "Program.cs", "// real source");

        var h = Build(); // no SeedRepositoryPath — main starts genuinely empty

        var pending = await h.Sync.SyncBranchFromRepositoryAsync("main", repo, SyncTrigger.GoalCreation);

        Assert.NotNull(pending);
        Assert.Equal(SyncReason.InitialBootstrap, pending!.Reason);
        Assert.Equal(1, pending.WorkspaceGeneration);
        Assert.Equal(["Program.cs"], pending.Added);

        var artifact = await h.Artifacts.GetAsync(pending.ArtifactId);
        Assert.NotNull(artifact);
        Assert.Equal(ArtifactType.ExternalChangeset, artifact!.Type);
        Assert.Null(artifact.OwnedByWorkUnitId);
        Assert.Null(artifact.ParentArtifactId);

        Assert.Equal(2, (await h.KnownGood.FindKnownGoodAsync("main")).Count);

        var state = await h.Sync.GetStateAsync("main");
        Assert.Equal(1, state!.Generation);
        Assert.Equal(pending.ArtifactId, state.LatestExternalChangesetId);
    }

    [Fact]
    public async Task Drift_on_a_populated_main_chains_to_the_previous_sync_and_increments_generation()
    {
        var repo = NewExternalRepo();
        Write(repo, "Program.cs", "// v1");
        var h = Build();

        var first = await h.Sync.SyncBranchFromRepositoryAsync("main", repo, SyncTrigger.GoalCreation);
        Assert.NotNull(first);

        Write(repo, "Program.cs", "// v2");
        var second = await h.Sync.SyncBranchFromRepositoryAsync("main", repo, SyncTrigger.GoalCreation);

        Assert.NotNull(second);
        Assert.Equal(SyncReason.RepositoryDrift, second!.Reason);
        Assert.Equal(2, second.WorkspaceGeneration);
        Assert.Equal(["Program.cs"], second.Modified);

        var secondArtifact = await h.Artifacts.GetAsync(second.ArtifactId);
        Assert.Equal(first!.ArtifactId, secondArtifact!.ParentArtifactId);
    }

    [Fact]
    public async Task Sync_with_no_file_differences_is_a_silent_no_op()
    {
        var repo = NewExternalRepo();
        Write(repo, "Program.cs", "// v1");
        var h = Build();

        var first = await h.Sync.SyncBranchFromRepositoryAsync("main", repo, SyncTrigger.GoalCreation);
        Assert.NotNull(first);
        var snapshotsAfterFirst = (await h.KnownGood.FindKnownGoodAsync("main")).Count;

        var second = await h.Sync.SyncBranchFromRepositoryAsync("main", repo, SyncTrigger.GoalCreation);

        Assert.Null(second);
        Assert.Equal(snapshotsAfterFirst, (await h.KnownGood.FindKnownGoodAsync("main")).Count);

        var state = await h.Sync.GetStateAsync("main");
        Assert.Equal(1, state!.Generation);
        Assert.Equal(first!.ArtifactId, state.LatestExternalChangesetId);
    }

    [Fact]
    public async Task RepositorySwitch_resets_generation_and_deletes_the_old_repos_unique_files()
    {
        var repoA = NewExternalRepo();
        Write(repoA, "from-a.cs", "// a");
        var h = Build();

        await h.Sync.SyncBranchFromRepositoryAsync("main", repoA, SyncTrigger.GoalCreation);
        await h.Sync.SyncBranchFromRepositoryAsync("main", repoA, SyncTrigger.GoalCreation); // no-op, still gen 1
        Write(repoA, "from-a-2.cs", "// a2");
        var lastRepoASync = await h.Sync.SyncBranchFromRepositoryAsync("main", repoA, SyncTrigger.GoalCreation);
        Assert.Equal(2, lastRepoASync!.WorkspaceGeneration);

        var repoB = NewExternalRepo();
        Write(repoB, "from-b.cs", "// b");

        var switched = await h.Sync.SyncBranchFromRepositoryAsync("main", repoB, SyncTrigger.GoalCreation);

        Assert.NotNull(switched);
        Assert.Equal(SyncReason.RepositorySwitch, switched!.Reason);
        Assert.Equal(1, switched.WorkspaceGeneration);

        var switchedArtifact = await h.Artifacts.GetAsync(switched.ArtifactId);
        Assert.Null(switchedArtifact!.ParentArtifactId);

        var files = await h.FileWorkspace.ListAsync("main");
        Assert.Contains("from-b.cs", files);
        Assert.DoesNotContain("from-a.cs", files);
        Assert.DoesNotContain("from-a-2.cs", files);
    }

    [Fact]
    public async Task RepositorySwitch_with_a_coincidentally_empty_diff_still_resets_generation_with_no_artifact()
    {
        var repoA = NewExternalRepo();
        Write(repoA, "same-name.cs", "identical content");
        var h = Build();

        var firstSync = await h.Sync.SyncBranchFromRepositoryAsync("main", repoA, SyncTrigger.GoalCreation);
        Assert.Equal(1, firstSync!.WorkspaceGeneration);

        // RepoB has a different path identity but happens to produce byte-identical text content
        // for the same relative path — diff.IsEmpty is true even though this is a RepositorySwitch.
        var repoB = NewExternalRepo();
        Write(repoB, "same-name.cs", "identical content");

        var switched = await h.Sync.SyncBranchFromRepositoryAsync("main", repoB, SyncTrigger.GoalCreation);

        Assert.Null(switched);
        var state = await h.Sync.GetStateAsync("main");
        Assert.Equal(repoB, state!.RepositoryPath);
        Assert.Equal(0, state.Generation);
        Assert.Null(state.LatestExternalChangesetId);
    }

    [Fact]
    public async Task Switch_then_drift_keeps_separate_chains_with_no_cross_repository_linkage()
    {
        var repoA = NewExternalRepo();
        Write(repoA, "a.cs", "// a1");
        var h = Build();

        var a1 = await h.Sync.SyncBranchFromRepositoryAsync("main", repoA, SyncTrigger.GoalCreation);
        Write(repoA, "a.cs", "// a2");
        var a2 = await h.Sync.SyncBranchFromRepositoryAsync("main", repoA, SyncTrigger.GoalCreation);
        Write(repoA, "a.cs", "// a3");
        var a3 = await h.Sync.SyncBranchFromRepositoryAsync("main", repoA, SyncTrigger.GoalCreation);

        var repoB = NewExternalRepo();
        Write(repoB, "b.cs", "// b1");
        var b1 = await h.Sync.SyncBranchFromRepositoryAsync("main", repoB, SyncTrigger.GoalCreation);
        Write(repoB, "b.cs", "// b2");
        var b2 = await h.Sync.SyncBranchFromRepositoryAsync("main", repoB, SyncTrigger.GoalCreation);

        var repoAIds = new[] { a1!.ArtifactId, a2!.ArtifactId, a3!.ArtifactId };

        var b1Artifact = await h.Artifacts.GetAsync(b1!.ArtifactId);
        Assert.Null(b1Artifact!.ParentArtifactId);

        var b2Artifact = await h.Artifacts.GetAsync(b2!.ArtifactId);
        Assert.Equal(b1.ArtifactId, b2Artifact!.ParentArtifactId);
        Assert.DoesNotContain(b2Artifact.ParentArtifactId, repoAIds);
        Assert.DoesNotContain(b1.ArtifactId, repoAIds);
    }

    [Fact]
    public async Task Concurrent_syncs_against_the_same_branch_do_not_double_record()
    {
        var repo = NewExternalRepo();
        Write(repo, "Program.cs", "// v1");
        var h = Build();

        var results = await Task.WhenAll(
            h.Sync.SyncBranchFromRepositoryAsync("main", repo, SyncTrigger.GoalCreation),
            h.Sync.SyncBranchFromRepositoryAsync("main", repo, SyncTrigger.GoalCreation));

        Assert.Single(results, r => r is not null);

        var state = await h.Sync.GetStateAsync("main");
        Assert.Equal(1, state!.Generation);
        Assert.Equal(2, (await h.KnownGood.FindKnownGoodAsync("main")).Count);
    }

    [Fact]
    public async Task State_survives_rehydration_from_the_same_node_store()
    {
        var repo = NewExternalRepo();
        Write(repo, "Program.cs", "// v1");

        // Full production composition root (same pattern as WorkSchedulerRehydrationTests) so
        // every IRehydratable's other dependencies (e.g. InMemoryDeadLetterService's
        // IWorkUnitService) are actually registered — a minimal AddInMemoryStorage()-only
        // provider can't resolve the full IRehydratable set.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"studio-reposync-rehydrate-{Guid.NewGuid():N}");
        try
        {
            Microsoft.AspNetCore.Builder.WebApplication BuildApp() => NodalMerge.Studio.Host.StudioWebApplication.Build(
                [],
                configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NodalMerge:Storage:Sqlite:DbPath"] = Path.Combine(tempRoot, "nodes.db"),
                    ["NodalMerge:Storage:FileBlobs:RootPath"] = Path.Combine(tempRoot, "blobs"),
                    ["Workspace:RootPath"] = Path.Combine(tempRoot, "workspace"),
                }));

            var app1 = BuildApp();
            var sync1 = app1.Services.GetRequiredService<IRepositorySyncService>();
            var pending = await sync1.SyncBranchFromRepositoryAsync("main", repo, SyncTrigger.GoalCreation);
            Assert.NotNull(pending);

            var app2 = BuildApp();
            foreach (var rehydratable in app2.Services.GetServices<IRehydratable>())
                await rehydratable.RehydrateAsync();

            var state = await app2.Services.GetRequiredService<IRepositorySyncService>().GetStateAsync("main");
            Assert.NotNull(state);
            Assert.Equal(1, state!.Generation);
            Assert.Equal(pending!.ArtifactId, state.LatestExternalChangesetId);
            Assert.Equal(repo, state.RepositoryPath);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
