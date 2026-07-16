using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 6.5 (plans/cas-distribution-and-storage.md Phase 6) — the multi-user milestone. Two full
/// studio hosts (A = embedded host with a real registered/bootstrapped repo — stands in for "the
/// server" role too, same simplification RoomPerRepoTests/RoomReplicationTests already make for the
/// in-CI .NET-only topology; B = a headless peer, i.e. the second laptop) exercising, against real
/// production services (no synthetic WriteNodeAsync calls):
///
///  1. Work created on A (a goal/work unit bound to a real, bootstrapped repository) becomes visible
///     in B's *service layer* — IWorkUnitService.GetAsync, not just IStudioNodeStore — proving Part
///     1's inbound cache-refresh coordinator (RehydratableRefreshCoordinator), not merely that the
///     node store replicates (RoomPerRepoTests/RoomReplicationTests already cover that half).
///  2. B — which never bootstrapped this repository and starts with a completely empty local blob
///     cache — cold-materializes the work unit's *scoped* branch directly off the replicated
///     RepositorySnapshot (IStudioNodeStore -> IRepositorySnapshotService, itself only populated via
///     Part 1's refresh) through a ChainedRemote IBlobStoreProvider pointed at A's own /blobs HTTP
///     endpoint (Phase 2/4's chained provider) — asserted by counting B's local blob cache directory
///     before (must be empty of anything but the provider's own init marker) and after (must be
///     larger) the materialize call, so the test fails if anything pre-seeds the cache and hides a
///     broken remote path.
///  3. B edits the materialized file and harvests -> proposes (MergeProposal) against "main". The
///     proposal correctly denormalizes+routes into the SAME shared repo room the work unit itself
///     lives in (verified directly against the engine map, below) — proving both 6.3a's
///     denormalization AND this slice's own cross-peer routing fallback (NodalMergeStudioNodeStore.
///     TryResolveRepoRoomIdCoreAsync) work for an entity whose FK (WorkUnitId) was minted by a
///     DIFFERENT peer than the one now writing.
///  4. Concurrently, A lands its OWN edit to a different file — a genuinely independent, divergent
///     change proposed/reviewed/applied through the ordinary command surface — advancing the
///     repository's real RepositorySnapshot via the same PostMergeWriteBack auto-resync
///     (ForceSyncAsync/BaseSnapshotId) PostMergeResyncIntegrationTests already proves in isolation.
///     Both edits are now durably recorded: A's in the real on-disk repository (write-back always
///     runs on whichever peer owns the physical clone), B's in the shared repo room's CRDT state
///     (durable, and would materialize correctly for any peer that runs a fresh catch-up).
///
/// ── A genuine defect this milestone surfaced, NOT worked around (per this slice's own
/// instructions: report precisely, don't patch around another repo) ──────────────────────────────
/// Step 3's proposal, though correctly routed into the shared repo room's CRDT state (verified
/// directly against the engine map below), never becomes visible through peer A's OWN
/// IMergeService — the last hop the milestone's "proposal replicates back to A" bullet wants. Root
/// cause, traced precisely while building this test: A plays the ROOM-SERVER role for this room (B
/// connects to A's own /ws/{room} endpoint); an inbound "pack" arriving at a room *server*'s own
/// WebSocket endpoint is handled by RuntimeWebSocketLoopRunner (nodalmerge repo,
/// hosts/dotnet/src/NodalMerge.DotNetHost/Runtime/RuntimeWebSocketLoopRunner.cs) — engine-level
/// relay only (ImportPack + persist + rebroadcast to other connected peers). The Studio-domain
/// bridge (replay canonical resolution into the engine's live "room_maps" state, then refresh every
/// IRehydratable's in-memory cache — this slice's own Part 1) exists ONLY in RoomPeerClient's
/// client-role receive loop (ApplyInboundPackForRoomAsync), which A never runs for its OWN room
/// (A has no Room:HostUri — it never connects outward to itself). So a peer-authored write pushed
/// UPSTREAM to a host that is simultaneously the room's server never reaches that host's own Studio
/// state or in-memory caches, live or on next read — every existing 6.1b/6.3 test only ever
/// exercises the opposite direction (the embedded host writes, a connected peer receives), which is
/// why this never surfaced before. This is a nodalmerge-repo (NodalMerge.DotNetHost) defect, out of
/// this slice's edit scope by the harness's own instructions — flagged here and in the slice's final
/// report, not patched around. Consequently this test does NOT assert A ever sees or applies B's
/// proposal; it stops at proving the proposal is correctly formed and durably shared.
///
/// ── Another documented cross-peer accommodation (test-only, not a claim this generalizes) ───────
/// RepositorySnapshot.RepositoryId (and every lookup keyed by it — InMemoryRepositorySnapshotService's
/// cache, FileSystemWorkspaceService's scoped-materialization fallback) is the *peer-local absolute
/// filesystem path* of the physical repository (RepositorySyncService.SyncCoreAsync:
/// `Path.GetFullPath(repositoryPath)`), not the portable WorkgroupRepoId the room-binding itself
/// uses. Two real laptops with the repo cloned to different paths would never converge on the same
/// key even though the underlying rows replicate byte-for-byte. This test's single-process topology
/// can't help but share a filesystem, so it gives B's own `Workspace:SeedRepositoryPath` the
/// identical literal path A registered — which is what makes the lookup succeed here, but is not
/// something a real two-laptop deployment can rely on. See this slice's final report for the
/// recommended follow-up (portable snapshot keying via WorkgroupRepoId).
///
/// Work units are deliberately created on A throughout (never on B): WorkUnit.RepositoryId is a
/// peer-local registry id (RoomPerRepoTests proves A and B mint *different* local ids for the same
/// workgroup-bound repo), so a work unit B itself created would carry a RepositoryId meaningless to
/// A's own registry.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class MultiUserMilestoneTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-multiuser-milestone-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact(Timeout = 120_000)]
    public async Task Two_peers_one_repo_goal_appears_cold_materializes_and_edits_land()
    {
        // ── Fixture: a real repository directory with two independent files. ───────────────────
        var repoPath = Path.Combine(_tempRoot, "shared-repo");
        Directory.CreateDirectory(Path.Combine(repoPath, "src"));
        await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "FileA.cs"), "// A v1\n");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "src", "FileB.cs"), "// B v1\n");

        // ── Host A: embedded studio host, real HTTP server (doubles as the room host AND the
        // blob origin B's chained provider will fetch from). ────────────────────────────────────
        var rootA = Path.Combine(_tempRoot, "hostA");
        await using var hostA = StudioWebApplication.Build(
            [],
            configureWebHost: wh => wh.UseUrls("http://127.0.0.1:0"),
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = Path.Combine(rootA, "nodes.db"),
                ["NodalMerge:Storage:FileBlobs:RootPath"] = Path.Combine(rootA, "blobs"),
                ["Workspace:RootPath"] = Path.Combine(rootA, "workspace"),
            }));
        await hostA.StartAsync();

        var hostAAddress = hostA.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var wsUriA = hostAAddress.Replace("http://", "ws://", StringComparison.Ordinal);

        // ── Seed on A: register + bootstrap the real repo through the production goal-creation
        // path (WorkUnitCommandService.CreateAsync with RepositoryPath) — the exact call PostMerge
        // ResyncIntegrationTests already proves advances a real RepositorySnapshot, not a synthetic
        // WriteNodeAsync. First-ever registration in a fresh workspace auto-mints a WorkgroupRepoId
        // (RoomPerRepoTests' RegisterBoundRepoAsync helper's own documented behavior). ────────────
        var workUnitCommandsA = hostA.Services.GetRequiredService<IWorkUnitCommandService>();
        var seedWorkUnit = await workUnitCommandsA.CreateAsync(
            new WorkUnitCreateCommand("seed repository", "test", RepositoryPath: repoPath));

        var registryA = hostA.Services.GetRequiredService<IRepositoryRegistryService>();
        var repoA = await registryA.GetAsync(seedWorkUnit.RepositoryId!);
        Assert.NotNull(repoA);
        Assert.NotNull(repoA!.WorkgroupRepoId); // auto-minted, no disambiguation needed for the first registration
        var repoRoomId = $"repo/{repoA.WorkgroupRepoId}";

        var physicalRepositoryId = Path.GetFullPath(repoPath); // RepositorySnapshot's own keying, per the class doc comment

        var snapshotServiceA = hostA.Services.GetRequiredService<IRepositorySnapshotService>();
        var genesisSnapshot = await snapshotServiceA.GetLatestAsync(physicalRepositoryId);
        Assert.NotNull(genesisSnapshot); // gen 0, the bootstrap

        // ── Peer B: a headless peer (the second laptop), pointed at A's room AND at A's own HTTP
        // endpoint as its remote blob origin. B's local blob cache starts as a brand-new empty
        // directory — nothing pre-seeded. B's Workspace:SeedRepositoryPath is set to the SAME
        // literal path A registered — the documented cross-peer accommodation from the class
        // comment above, not a claim this generalizes past a single shared filesystem. ───────────
        var rootB = Path.Combine(_tempRoot, "hostB");
        var blobsB = Path.Combine(rootB, "blobs");
        var hostB = StudioWebApplication.BuildPeer(
            [],
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = Path.Combine(rootB, "nodes.db"),
                ["NodalMerge:Providers:BlobStorage"] = "ChainedRemote",
                ["NodalMerge:Storage:FileBlobs:RootPath"] = blobsB,
                ["NodalMerge:Storage:RemoteOrigin:BaseUrl"] = hostAAddress,
                ["Workspace:RootPath"] = Path.Combine(rootB, "workspace"),
                ["Workspace:SeedRepositoryPath"] = repoPath,
                // StudioWebApplication.ApplyCasRootPath derives FileBlobs:RootPath from
                // Workspace:SeedRepositoryPath (Combine(seedPath, ".nodalmerge", "cas")) whenever
                // Workspace:CasRootPath is unset — and since it's applied via a LATER
                // AddInMemoryCollection than this dictionary's own FileBlobs:RootPath, it would
                // silently win and redirect B's "local" blob cache into a folder inside the SHARED
                // repoPath directory instead of blobsB. Pinning CasRootPath explicitly here keeps
                // B's local cache the empty, host-B-only directory the cold-fetch assertion below
                // actually inspects.
                ["Workspace:CasRootPath"] = blobsB,
                ["Room:HostUri"] = wsUriA,
                ["Peer:PeerId"] = "peer-b",
            }));

        try
        {
            await hostB.StartAsync();
            // FileBlobStoreProvider writes a one-time ".layout-v2" marker file on init — not a
            // cached blob — so the cold-fetch proof below compares against this baseline count
            // rather than assuming a literal zero.
            var blobCacheBaselineCount = CountFilesRecursive(blobsB);

            // B binds its own local candidate to A's already-minted workgroup repo (D2's one-time
            // disambiguation flow — identical to RoomPerRepoTests' pattern). The placeholder path
            // never needs to exist on disk; only the workgroup binding matters for room membership.
            // Must wait for the workgroup repositories map to actually catch up to B first (the
            // same wait RoomPerRepoTests performs) — registering before that lands would see zero
            // candidates and auto-mint a brand-new (wrong) repo instead of PendingDisambiguation.
            var directoryB = hostB.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
            await PollUntilTrueAsync(async () =>
                (await directoryB.ListAsync()).Any(e => e.RepoId == repoA.WorkgroupRepoId));

            var registryB = hostB.Services.GetRequiredService<IRepositoryRegistryService>();
            var localB = await registryB.RegisterAsync(Path.Combine(rootB, "clone-placeholder"), "shared-repo-on-b");
            Assert.Null(localB.WorkgroupRepoId);
            Assert.NotNull(localB.PendingDisambiguation);
            localB = await registryB.ResolveDisambiguationAsync(localB.RepositoryId, repoA.WorkgroupRepoId);
            Assert.Equal(repoA.WorkgroupRepoId, localB!.WorkgroupRepoId);

            // ── (1) Work created on A appears in B's SERVICE LAYER — not just the store. Created
            // AFTER B is connected+bound, so this exercises live inbound-pack cache refresh, not
            // startup catch-up. ──────────────────────────────────────────────────────────────────
            var orchestratorA = hostA.Services.GetRequiredService<IOrchestratorService>();
            var goalWorkUnit = await orchestratorA.CreateWorkUnitAsync(
                goal: "edit FileA (assigned to peer B)", owner: "test",
                repositoryId: repoA.RepositoryId, fileScope: ["src/FileA.cs"]);

            var workUnitsB = hostB.Services.GetRequiredService<IWorkUnitService>();
            var goalOnB = await PollUntilNotNullAsync(
                () => workUnitsB.GetAsync(goalWorkUnit.WorkUnitId));
            Assert.Equal(goalWorkUnit.Goal, goalOnB!.Goal);
            Assert.Equal(goalWorkUnit.BranchId, goalOnB.BranchId);

            // The replicated RepositorySnapshot must also be visible through B's own in-memory
            // IRepositorySnapshotService — the cold materialize below depends on it (again, Part
            // 1's refresh, not a fresh read).
            var snapshotServiceB = hostB.Services.GetRequiredService<IRepositorySnapshotService>();
            var snapshotOnB = await PollUntilNotNullAsync(
                () => snapshotServiceB.GetLatestAsync(physicalRepositoryId));
            Assert.Equal(genesisSnapshot!.SnapshotId, snapshotOnB!.SnapshotId);

            // ── (2) Cold-peer scoped branch materialization via the chained provider. This is the
            // exact call CreateBranchAsync itself would have made had the branch been created on B
            // (FileSystemWorkspaceService.InitBranchAsync's scoped-materialization path) — B never
            // ran it, since the branch was created on A, so calling it here for the first time on B
            // is genuinely cold. ─────────────────────────────────────────────────────────────────
            var fileWorkspaceB = hostB.Services.GetRequiredService<IFileWorkspaceService>();
            await fileWorkspaceB.InitBranchAsync(goalWorkUnit.BranchId, null, goalWorkUnit.FileScope);

            var branchDirB = await fileWorkspaceB.GetWorkingDirectoryAsync(goalWorkUnit.BranchId);
            Assert.NotNull(branchDirB);
            var materializedFileA = Path.Combine(branchDirB!, "src", "FileA.cs");
            Assert.True(File.Exists(materializedFileA), "Expected FileA.cs to be cold-materialized into B's branch directory.");
            Assert.Equal("// A v1\n", await File.ReadAllTextAsync(materializedFileA));

            // The cold fetch really did traverse the remote link: B's local blob cache, which held
            // nothing but the provider's own init marker a moment ago, now holds the write-through-
            // cached bytes too (ChainedBlobStoreProvider's own behavior — see
            // NodalMerge.Host.Composition.ChainedBlobStoreProvider).
            Assert.True(CountFilesRecursive(blobsB) > blobCacheBaselineCount,
                "Expected B's local blob cache to be populated by the cold remote fetch.");

            // ── (3) Edit on B, harvest -> propose. The proposal must land in the SHARED repo room
            // (not B's own private "studio" room) — proving both 6.3a's RepositoryId denormalization
            // (from goalWorkUnit's own field, minted by A) and this slice's cross-peer routing
            // fallback (NodalMergeStudioNodeStore.TryResolveRepoRoomIdCoreAsync) work correctly for
            // an entity whose FK was minted by a different peer than the one now writing it. ───────
            await fileWorkspaceB.WriteAsync(goalWorkUnit.BranchId, "src/FileA.cs", "// A v2 — edited on peer B\n");

            var mergeCommandsB = hostB.Services.GetRequiredService<IMergeCommandService>();
            var proposalFromB = await mergeCommandsB.ProposeAsync(
                sourceBranch: goalWorkUnit.BranchId, targetBranch: "main",
                summary: "Update FileA from peer B", workUnitId: goalWorkUnit.WorkUnitId);
            Assert.Equal(repoA.RepositoryId, proposalFromB.RepositoryId);

            var bridgeB = hostB.Services.GetRequiredService<IRuntimeCommandBridge>();
            var proposalKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.MergeProposalV1, proposalFromB.ProposalId);
            Assert.NotNull(TryReadEngineMapValue(bridgeB, repoRoomId, "studio", proposalKey));
            Assert.Null(TryReadEngineMapValue(bridgeB, "studio", "studio", proposalKey));

            // NOTE: this is as far as B's proposal can be followed today — see the class-level doc
            // comment's "genuine defect" section for exactly why peer A's own IMergeService never
            // observes it, and why that is out of this slice's scope to fix (a nodalmerge-repo
            // defect in the room-server's own inbound WS handling, not this repo's).

            // ── (4) A's own divergent, concurrent edit to a DIFFERENT file — proposed, reviewed,
            // and applied through the ordinary command surface. Advances the repository's real
            // RepositorySnapshot via the same PostMergeWriteBack auto-resync
            // (ForceSyncAsync/BaseSnapshotId) PostMergeResyncIntegrationTests exercises in isolation,
            // proving the "recorded 3-way base" lineage this milestone's acceptance bar asks for
            // still advances correctly with a second peer concurrently active against the same
            // repository room. ───────────────────────────────────────────────────────────────────
            var workUnitOnA2 = await orchestratorA.CreateWorkUnitAsync(
                goal: "edit FileB (on A, concurrently)", owner: "test",
                repositoryId: repoA.RepositoryId, fileScope: ["src/FileB.cs"]);

            var fileWorkspaceA = hostA.Services.GetRequiredService<IFileWorkspaceService>();
            await fileWorkspaceA.WriteAsync(workUnitOnA2.BranchId, "src/FileB.cs", "// B v2 — edited on A\n");

            var mergeCommandsA = hostA.Services.GetRequiredService<IMergeCommandService>();
            var proposalFromA = await mergeCommandsA.ProposeAsync(
                sourceBranch: workUnitOnA2.BranchId, targetBranch: "main",
                summary: "Update FileB from A", workUnitId: workUnitOnA2.WorkUnitId);
            await mergeCommandsA.ValidateAsync(proposalFromA.ProposalId);
            await mergeCommandsA.ReviewAsync(proposalFromA.ProposalId, "Approved");
            await mergeCommandsA.ApplyAsync(proposalFromA.ProposalId);

            // ── A's edit landed on the real physical repository, and the recorded lineage
            // (BaseSnapshotId) chains correctly off the bootstrap generation. ───────────────────────
            Assert.Equal("// B v2 — edited on A\n", await File.ReadAllTextAsync(Path.Combine(repoPath, "src", "FileB.cs")));

            var snapshotAfterA = await snapshotServiceA.GetLatestAsync(physicalRepositoryId);
            Assert.NotNull(snapshotAfterA);
            Assert.True(snapshotAfterA!.Generation > genesisSnapshot.Generation);
            Assert.Equal(genesisSnapshot.SnapshotId, snapshotAfterA.BaseSnapshotId); // gen1's base is gen0
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }

    private static int CountFilesRecursive(string root) =>
        Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() : 0;

    // Mirrors RoomPerRepoTests' identical helper — direct engine-map read, bypassing every
    // Studio-domain cache, so the "which room did this actually land in" assertions above can't be
    // fooled by a stale in-memory dictionary.
    private static System.Text.Json.Nodes.JsonNode? TryReadEngineMapValue(
        IRuntimeCommandBridge bridge, string roomId, string @namespace, string key)
    {
        var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = roomId,
            command = new { MapGet = new { @namespace, key } }
        }));

        if (response.Status != AsStatus.Ok)
            return null;

        using var doc = JsonDocument.Parse(response.EventsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var evt in doc.RootElement.EnumerateArray())
        {
            if (!evt.TryGetProperty("MapValueRead", out var read))
                continue;
            if (!read.TryGetProperty("value", out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            return System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText());
        }

        return null;
    }

    private static async Task<T?> PollUntilNotNullAsync<T>(Func<Task<T?>> read, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = await read();
            if (value is not null)
                return value;
            await Task.Delay(200);
        }

        throw new TimeoutException("Condition never satisfied (value stayed null) within the timeout.");
    }

    private static async Task PollUntilTrueAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(200);
        }

        throw new TimeoutException("Condition never satisfied within the timeout.");
    }
}
