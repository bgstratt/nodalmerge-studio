using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 5 slice 5.2 (plans/cas-distribution-and-storage.md) — retention-aware live set + staged
/// local GC + run ledger, exercised against a REAL file-layout CAS (the production File blob
/// provider, selected by config) so "bytes reclaimed" means actual files deleted from disk, not an
/// in-memory dictionary mutation. The snapshot/work-unit DAG is seeded directly into the
/// (in-memory) node store WITHOUT RepositoryOps — sync-emitted ops carry NewBlobId and rule (b)
/// protects op-referenced blobs unconditionally, so an import-driven DAG could never demonstrate
/// reclamation at all (see the dedicated op-protection test below for that rule's own coverage).
/// </summary>
[Trait("Category", "Integration")]
public class BlobGcServiceTests : IAsyncLifetime
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"studio-blobgc-{Guid.NewGuid():N}");
    private string CasRoot => Path.Combine(_tempRoot, "cas");

    private static readonly string RepositoryId = Path.GetFullPath(@"C:\fake\gc-repo");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_tempRoot);

    private Microsoft.AspNetCore.Builder.WebApplication BuildApp(
        BlobGcOptions? gcOptions = null, int retainIntermediateDays = 0)
    {
        return StudioWebApplication.Build(
            [],
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // InMemory node storage so no Sqlite db file is created; File blob storage so the
                // materializer/tree-resolver and the GC coordinator share one real on-disk CAS.
                ["NodalMerge:Providers:NodeStorage"] = "InMemory",
                ["NodalMerge:Providers:BlobStorage"] = "File",
                ["NodalMerge:Storage:FileBlobs:RootPath"] = CasRoot,
            }),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                // AddInMemoryStorage re-registers WorkspaceOptions with only RootPath set (its
                // test-isolation default), which would drop CasRootPath — re-register once more
                // with both (last AddSingleton registration wins).
                services.AddSingleton(new WorkspaceOptions
                {
                    RootPath = Path.Combine(_tempRoot, "workspace"),
                    CasRootPath = CasRoot,
                });
                services.AddSingleton(new RetentionPolicyOptions { RetainIntermediateDays = retainIntermediateDays });
                services.AddSingleton(gcOptions ?? new BlobGcOptions());
            });
    }

    private static async Task<string> PutBlobAsync(IBlobStoreProvider blobStore, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = BlobHasher.ComputeHash(bytes);
        await blobStore.PutBlobAsync(hash, bytes, "text/plain");
        return hash;
    }

    private static Task WriteSnapshotAsync(IStudioNodeStore store, RepositorySnapshot snapshot) =>
        store.WriteNodeAsync(StudioNodeKind.RepositorySnapshotV1, snapshot.SnapshotId, JsonSerializer.Serialize(snapshot));

    private string BlobPath(string hash) => Path.Combine(CasRoot, "blake3", hash);
    private string TombstonePath(string hash) => Path.Combine(CasRoot, ".tombstones", "blake3", hash);

    /// <summary>
    /// Seeds the canonical three-generation DAG every test below reuses:
    ///   gen0 — Source="Bootstrap" → Pinned forever;         tree: { a.txt: shared }
    ///   gen1 — retired intermediate, CreatedAt 10 d ago →   tree: { a.txt: shared, b.txt: UNIQUE }
    ///          expired under RetainIntermediateDays=0
    ///   gen2 — current head → Active regardless of age;     tree: { a.txt: shared, c.txt: head }
    /// so the ONLY sweep candidate is gen1's unique blob.
    /// </summary>
    private async Task<(string Shared, string Unique, string Head)> SeedAsync(IServiceProvider services)
    {
        var blobStore = services.GetRequiredService<IBlobStoreProvider>();
        var nodeStore = services.GetRequiredService<IStudioNodeStore>();
        var now = DateTimeOffset.UtcNow;

        var shared = await PutBlobAsync(blobStore, "shared content, referenced by every generation");
        var unique = await PutBlobAsync(blobStore, "unique to the retired intermediate generation");
        var head   = await PutBlobAsync(blobStore, "unique to the current head generation");

        await WriteSnapshotAsync(nodeStore, new RepositorySnapshot(
            SnapshotId: "snap-gen0-bootstrap", RepositoryId: RepositoryId, TreeHash: "", Generation: 0,
            CreatedAt: now - TimeSpan.FromDays(30), Source: "Bootstrap",
            TreeEntries: new Dictionary<string, string> { ["a.txt"] = shared }));

        await WriteSnapshotAsync(nodeStore, new RepositorySnapshot(
            SnapshotId: "snap-gen1-retired", RepositoryId: RepositoryId, TreeHash: "", Generation: 1,
            CreatedAt: now - TimeSpan.FromDays(10),
            TreeEntries: new Dictionary<string, string> { ["a.txt"] = shared, ["b.txt"] = unique }));

        await WriteSnapshotAsync(nodeStore, new RepositorySnapshot(
            SnapshotId: "snap-gen2-head", RepositoryId: RepositoryId, TreeHash: "", Generation: 2,
            CreatedAt: now - TimeSpan.FromDays(1),
            TreeEntries: new Dictionary<string, string> { ["a.txt"] = shared, ["c.txt"] = head }));

        return (shared, unique, head);
    }

    [Fact]
    public async Task SweepHard_reclaims_a_retired_generations_unique_blob_and_pinned_history_still_materializes()
    {
        await using var app = BuildApp(new BlobGcOptions { Mode = BlobGcMode.SweepHard });
        var (shared, unique, head) = await SeedAsync(app.Services);
        var gc = app.Services.GetRequiredService<IBlobGcService>();

        // First SweepHard call marks (tombstones) the candidate; second deletes it — the
        // coordinator's two-phase mark-then-sweep is preserved even under zero grace (see
        // BlobGcService's mode-mapping comment for why "hard" doesn't elide it).
        var run1 = await gc.RunAsync();
        Assert.Equal(BlobGcMode.SweepHard, run1.Mode);
        Assert.Equal(1, run1.Marked);
        Assert.Equal(0, run1.Deleted);
        Assert.True(File.Exists(TombstonePath(unique)), "first pass must tombstone the retired blob");
        Assert.True(File.Exists(BlobPath(unique)), "first pass must not delete yet");

        var run2 = await gc.RunAsync();
        Assert.Equal(1, run2.Deleted);
        Assert.False(File.Exists(BlobPath(unique)), "the retired generation's unique blob must be reclaimed");

        // Shared bytes (also referenced by retained generations) and the head's bytes survive.
        Assert.True(File.Exists(BlobPath(shared)));
        Assert.True(File.Exists(BlobPath(head)));

        // Pinned (promoted) history is still fully materializable after the aggressive GC.
        var nodeStore = app.Services.GetRequiredService<IStudioNodeStore>();
        var pinnedJson = await nodeStore.ReadNodeAsync(StudioNodeKind.RepositorySnapshotV1, "snap-gen0-bootstrap");
        var pinned = JsonSerializer.Deserialize<RepositorySnapshot>(pinnedJson!)!;
        var materializer = app.Services.GetRequiredService<IMaterializationEngine>();
        var target = Path.Combine(_tempRoot, "materialized-pinned");
        var written = await materializer.MaterializeAsync(pinned, target);
        Assert.Equal(1, written);
        Assert.Equal(
            "shared content, referenced by every generation",
            await File.ReadAllTextAsync(Path.Combine(target, "a.txt")));

        // Ledger: both runs recorded, newest first, with retention context attached.
        var runs = await gc.GetRecentRunsAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal(run2.RunId, runs[0].RunId);
        Assert.All(runs, r => Assert.True(r.Success));
        // Retained = gen0 (Pinned, bootstrap) + gen2 (Active, head); gen1 expired out.
        Assert.All(runs, r => Assert.Equal(2, r.RetainedSnapshotCount));
        // Live = shared (gen0+gen2) + head (gen2); gen1's unique blob is exactly what fell out.
        Assert.All(runs, r => Assert.Equal(2, r.LiveSetSize));
    }

    [Fact]
    public async Task DryRun_mutates_nothing_and_reports_the_same_candidate_set_the_live_run_acts_on()
    {
        await using var app = BuildApp(new BlobGcOptions { Mode = BlobGcMode.DryRun });
        var (_, unique, _) = await SeedAsync(app.Services);
        var gc = app.Services.GetRequiredService<IBlobGcService>();

        var dryRun = await gc.RunAsync();
        Assert.Equal(BlobGcMode.DryRun, dryRun.Mode);
        Assert.Equal(1, dryRun.Marked);
        Assert.Equal(0, dryRun.Deleted);
        Assert.True(File.Exists(BlobPath(unique)), "DryRun must not delete");
        Assert.False(Directory.Exists(Path.Combine(CasRoot, ".tombstones")), "DryRun must not write tombstones");

        // The live run acts on exactly the candidate DryRun reported: same Marked count, and the
        // one tombstone it writes is for the same hash (the only candidate in this DAG).
        var liveRun = await gc.RunAsync(BlobGcMode.SweepHard);
        Assert.Equal(dryRun.Marked, liveRun.Marked);
        Assert.True(File.Exists(TombstonePath(unique)));
    }

    [Fact]
    public async Task MarkOnly_tombstones_but_never_deletes_no_matter_how_often_it_runs()
    {
        await using var app = BuildApp(new BlobGcOptions { Mode = BlobGcMode.MarkOnly });
        var (_, unique, _) = await SeedAsync(app.Services);
        var gc = app.Services.GetRequiredService<IBlobGcService>();

        var run1 = await gc.RunAsync();
        var run2 = await gc.RunAsync();

        Assert.Equal(1, run1.Marked);
        Assert.True(File.Exists(TombstonePath(unique)), "MarkOnly writes a real, persisted tombstone");
        Assert.Equal(0, run1.Deleted);
        Assert.Equal(0, run2.Deleted);
        Assert.Equal(0, run2.DeleteCandidates); // infinite grace — never even a candidate
        Assert.True(File.Exists(BlobPath(unique)), "MarkOnly must never delete bytes");
    }

    [Fact]
    public async Task SweepSoft_honors_the_grace_window_then_SweepHard_reclaims_immediately()
    {
        await using var app = BuildApp(new BlobGcOptions { Mode = BlobGcMode.SweepSoft, GraceHours = 24 });
        var (_, unique, _) = await SeedAsync(app.Services);
        var gc = app.Services.GetRequiredService<IBlobGcService>();

        var run1 = await gc.RunAsync(); // marks
        var run2 = await gc.RunAsync(); // tombstone is seconds old — inside the 24 h grace window
        Assert.Equal(1, run1.Marked);
        Assert.Equal(0, run2.Deleted);
        Assert.True(File.Exists(BlobPath(unique)), "inside the grace window nothing is deleted");

        // Operator escalates: an explicit SweepHard override (zero grace) reclaims right now.
        var run3 = await gc.RunAsync(BlobGcMode.SweepHard);
        Assert.Equal(1, run3.Deleted);
        Assert.False(File.Exists(BlobPath(unique)));
    }

    [Fact]
    public async Task Op_referenced_blobs_stay_protected_even_when_no_retained_snapshot_references_them()
    {
        await using var app = BuildApp(new BlobGcOptions { Mode = BlobGcMode.SweepHard });
        var (_, _, _) = await SeedAsync(app.Services);
        var blobStore = app.Services.GetRequiredService<IBlobStoreProvider>();
        var nodeStore = app.Services.GetRequiredService<IStudioNodeStore>();

        // A blob referenced ONLY by an uncompacted RepositoryOp — rule (b), unchanged from v1.
        var opBlob = await PutBlobAsync(blobStore, "op-referenced content, in no retained tree");
        var op = new RepositoryOperation(
            OperationId: "op-1", RepositoryId: RepositoryId, ParentSnapshotId: "snap-gen2-head",
            Kind: OperationType.Replace, Path: "d.txt", Timestamp: DateTimeOffset.UtcNow,
            NewBlobId: opBlob);
        await nodeStore.WriteNodeAsync(StudioNodeKind.RepositoryOpV1, op.OperationId, JsonSerializer.Serialize(op));

        var gc = app.Services.GetRequiredService<IBlobGcService>();
        await gc.RunAsync();
        await gc.RunAsync();

        Assert.True(File.Exists(BlobPath(opBlob)), "op-referenced blobs are live regardless of retention");
    }

    [Fact]
    public async Task A_retained_snapshot_with_an_unresolvable_tree_aborts_the_run_with_no_deletes()
    {
        await using var app = BuildApp(new BlobGcOptions { Mode = BlobGcMode.SweepHard });
        var (_, unique, _) = await SeedAsync(app.Services);
        var nodeStore = app.Services.GetRequiredService<IStudioNodeStore>();

        // A NEW head (gen3, cas-tree) whose tree blob does not exist — the head is Active, i.e.
        // retained, so GetLiveBlobHashesAsync must fail closed rather than under-report.
        await WriteSnapshotAsync(nodeStore, new RepositorySnapshot(
            SnapshotId: "snap-gen3-broken-head", RepositoryId: RepositoryId,
            TreeHash: new string('f', 64), Generation: 3,
            CreatedAt: DateTimeOffset.UtcNow, TreeFormat: "cas-tree"));

        var gc = app.Services.GetRequiredService<IBlobGcService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gc.RunAsync());

        // Zero mutations: the coordinator never ran at all.
        Assert.True(File.Exists(BlobPath(unique)));
        Assert.False(Directory.Exists(Path.Combine(CasRoot, ".tombstones")));

        // ...but the aborted attempt is still on the ledger, as a failure.
        var runs = await gc.GetRecentRunsAsync();
        var failed = Assert.Single(runs);
        Assert.False(failed.Success);
        Assert.NotNull(failed.Error);
        Assert.Equal(0, failed.Deleted);
    }

    [Fact]
    public async Task RunAsync_throws_BlobGcNotConfiguredException_when_no_cas_root_is_configured()
    {
        await using var app = StudioWebApplication.Build([], configureServices: services =>
        {
            services.AddInMemoryStorage(); // its WorkspaceOptions has no CasRootPath
        });

        var gc = app.Services.GetRequiredService<IBlobGcService>();
        await Assert.ThrowsAsync<BlobGcNotConfiguredException>(() => gc.RunAsync());
    }
}
