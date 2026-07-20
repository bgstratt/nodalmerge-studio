using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 6.3 (plans/cas-distribution-and-storage.md Phase 6, D1/D3) — room-per-repository:
///
///  1. Kind→room routing: a repo-scoped StudioNodeKind write (WorkUnitV1 with a bound
///     RepositoryId) lands in repo/{repoId}'s engine room, never "studio"; a workspace-global
///     kind stays in "studio"; a repo-scoped write whose RepositoryId has no workgroup binding
///     falls back to "studio". ReadAllNodesAsync aggregates across two bound repo rooms.
///  2. Migration: pre-6.3 repo-scoped state written into the "studio" engine map is copied into
///     the right repo room on that room's first touch — one-shot (marker-guarded, restart-proof),
///     old "studio" rows left intact (AP-5).
///  3. Two hosts, two rooms (extends RoomReplicationTests' 6.1b scenario): peer B, bound to R1
///     only, receives R1's room traffic but never R2's; a goal spanning R1+R2 is two work units
///     in two repo rooms plus one workgroup goal node; and a repositories-map registration made
///     on host A appears in peer B's directory listing while both are connected (closing 6.2's
///     silently-dropped-workgroup-writes gap end-to-end).
///  4. Pinned cross-repo reference (D3, STUDIO_ROOM_SCHEMA.md (c)): resolves via repo room →
///     generation node → TreeHash → CAS walk → blob on a peer whose registered repo path does
///     not even exist on disk (no clone, no materialization), both via IPinnedReferenceResolver
///     directly and over POST /studio/references/resolve.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class RoomPerRepoTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-room-per-repo-{Guid.NewGuid():N}");

    public void Dispose()
    {
        // See NodalMergeStudioNodeStoreEngineTests' Dispose for why ClearAllPools is required on
        // Windows before deleting the temp directory.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private WebApplication BuildApp(string name, Action<IWebHostBuilder>? configureWebHost = null)
    {
        var root = Path.Combine(_tempRoot, name);

        return StudioWebApplication.Build(
            [],
            configureWebHost: configureWebHost,
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = Path.Combine(root, "nodes.db"),
                ["NodalMerge:Storage:FileBlobs:RootPath"] = Path.Combine(root, "blobs"),
                ["Workspace:RootPath"] = Path.Combine(root, "workspace"),
            }));
    }

    // Registers a local path and guarantees a workgroup binding: the first registration in a fresh
    // workspace auto-mints (degraded hints, zero candidates — the preferred-id continuity path);
    // later ones come back PendingDisambiguation (degraded hints, >=1 candidate) and are resolved
    // "register-new", the same one-time-user-prompt flow D2 specifies.
    private static async Task<RepositoryV1> RegisterBoundRepoAsync(IServiceProvider services, string path, string label)
    {
        var registry = services.GetRequiredService<IRepositoryRegistryService>();
        var repo = await registry.RegisterAsync(path, label);
        if (repo.WorkgroupRepoId is null)
        {
            repo = await registry.ResolveDisambiguationAsync(repo.RepositoryId, chosenRepoId: null)
                ?? throw new InvalidOperationException("disambiguation resolution returned null");
        }

        Assert.NotNull(repo.WorkgroupRepoId);
        return repo;
    }

    [Fact]
    public async Task Repo_scoped_write_lands_in_the_repo_room_and_global_write_stays_in_studio()
    {
        await using var app = BuildApp("routing");
        var store = app.Services.GetRequiredService<IStudioNodeStore>();
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();

        var repo = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "routing-repoA"), "repoA");
        var repoRoomId = $"repo/{repo.WorkgroupRepoId}";

        // Repo-scoped write via the 6.3 routing overload.
        var wuPayload = JsonSerializer.Serialize(new { WorkUnitId = "WU-route", RepositoryId = repo.RepositoryId, Status = "Draft" });
        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-route", wuPayload, repo.RepositoryId);

        var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-route");
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", key));
        Assert.Null(TryReadEngineMapValue(bridge, "studio", "studio", key));

        // Room-agnostic read still finds it (bound-room fan-out).
        var readBack = await store.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-route");
        Assert.NotNull(readBack);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(wuPayload), JsonNode.Parse(readBack)));

        // Workspace-global kind stays in "studio".
        await store.WriteNodeAsync(StudioNodeKind.RuntimeSettingsV1, "settings", "{\"theme\":\"dark\"}");
        var settingsKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.RuntimeSettingsV1, "settings");
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", settingsKey));
        Assert.Null(TryReadEngineMapValue(bridge, repoRoomId, "studio", settingsKey));

        // A repo-scoped write whose RepositoryId never resolves to a binding falls back to "studio"
        // — a write is never blocked or lost on an absent workgroup binding.
        await store.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1, "WU-unbound",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-unbound", RepositoryId = "repo-never-registered" }),
            "repo-never-registered");
        var unboundKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-unbound");
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", unboundKey));
    }

    /// <summary>
    /// #1 goal replication (plans/repo-identity-convergence.md) — a GoalV1 carrying a bound
    /// RepositoryId must route into repo/{repoId}'s engine room (so it replicates to another peer on
    /// the same repo), never the peer-private "studio" room. Before #1, GoalV1 was workspace-global
    /// and stayed in "studio" (non-replicating post-7.3) — so two peers on one repo each saw only
    /// their own goals. Mirrors Repo_scoped_write_lands_in_the_repo_room for the work-unit case.
    /// </summary>
    [Fact]
    public async Task Goal_write_with_a_bound_repository_lands_in_the_repo_room_not_studio()
    {
        await using var app = BuildApp("goal-routing");
        var repo = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "goal-repoA"), "repoA");
        var repoRoomId = $"repo/{repo.WorkgroupRepoId}";
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();
        var goalNodes = app.Services.GetRequiredService<IGoalNodeService>();

        // A goal bound to the repo, exactly as POST /studio/goals sets RepositoryId from the goal's
        // top-level work unit.
        var goal = new GoalNode(
            GoalId: "GOAL-route", Goal: "g", WorkUnitId: "GOAL-route", BranchId: "b",
            Status: GoalStatus.Exploring, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow,
            Owner: "tester", RepositoryId: repo.RepositoryId);
        await goalNodes.RecordAsync(goal);

        var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.GoalV1, "GOAL-route");
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", key));
        Assert.Null(TryReadEngineMapValue(bridge, "studio", "studio", key));

        // Room-agnostic read still finds it (bound-room fan-out) — the goal list stays correct.
        var readBack = await goalNodes.GetAsync("GOAL-route");
        Assert.NotNull(readBack);
        Assert.Equal(repo.RepositoryId, readBack!.RepositoryId);

        // A goal with no repository still routes to "studio" (unchanged pre-#1 fallback).
        var globalGoal = new GoalNode(
            GoalId: "GOAL-global", Goal: "g", WorkUnitId: "GOAL-global", BranchId: "b",
            Status: GoalStatus.Exploring, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow,
            Owner: "tester");
        await goalNodes.RecordAsync(globalGoal);
        var globalKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.GoalV1, "GOAL-global");
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", globalKey));
        Assert.Null(TryReadEngineMapValue(bridge, repoRoomId, "studio", globalKey));
    }

    /// <summary>
    /// Slice 6.3a (plans/cas-distribution-and-storage.md Phase 6, D1) — denormalize RepositoryId
    /// onto the repo-scoped-indirect kinds (MergeProposalV1, TaskV1, BranchV1, KnownGoodStateV1,
    /// DecisionV1, ArtifactRefV1) and route them like 6.3 already routes the 5 direct kinds. Every
    /// record below is created through its REAL production service (IOrchestratorService,
    /// ITaskService, IMergeCommandService, IKnownGoodStateService, IDecisionNodeService,
    /// IArtifactLineageService) — not a synthetic WriteNodeAsync call — so this proves the
    /// denormalization actually happens on the write paths agents/UI/REST exercise, not just that
    /// the mechanism *could* work.
    /// </summary>
    [Fact]
    public async Task Indirect_kinds_denormalize_RepositoryId_through_real_services_and_route_to_repo_room()
    {
        await using var app = BuildApp("indirect-routing");
        var repo = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "indirect-repoA"), "repoA");
        var repoRoomId = $"repo/{repo.WorkgroupRepoId}";
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var parent = await orchestrator.CreateWorkUnitAsync(
            goal: "parent goal", owner: "tester", repositoryId: repo.RepositoryId);
        Assert.Equal(repo.RepositoryId, parent.RepositoryId);

        // BranchV1 — previously a bare {id, parentId, createdAt} with no repo linkage at all.
        var branchKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.BranchV1, parent.BranchId);
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", branchKey));
        Assert.Null(TryReadEngineMapValue(bridge, "studio", "studio", branchKey));

        // Child work unit: single-hop parent-chain inheritance — no repositoryId passed explicitly.
        var child = await orchestrator.CreateWorkUnitAsync(
            goal: "child goal", owner: "tester", parentWorkUnitId: parent.WorkUnitId);
        Assert.Equal(repo.RepositoryId, child.RepositoryId);
        var childKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, child.WorkUnitId);
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", childKey));

        // TaskV1 — denormalized from its (required) WorkUnitId.
        var tasks = app.Services.GetRequiredService<ITaskService>();
        var task = await tasks.CreateAsync(new StudioTask(
            $"TASK-{Guid.NewGuid():N}", parent.WorkUnitId, "title", "desc",
            NodalMerge.Studio.Contracts.Domain.TaskStatus.Open, null, 0));
        Assert.Equal(repo.RepositoryId, task.RepositoryId);
        var taskKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.TaskV1, task.TaskId);
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", taskKey));

        // MergeProposalV1 — via the real command service, which resolves RepositoryId from the
        // proposing work unit exactly like the MCP/REST/agent-loop paths do.
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: parent.BranchId, targetBranch: "main", summary: "test proposal",
            workUnitId: parent.WorkUnitId);
        Assert.Equal(repo.RepositoryId, proposal.RepositoryId);
        var proposalKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.MergeProposalV1, proposal.ProposalId);
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", proposalKey));

        // KnownGoodStateV1 — has no WorkUnitId at all; resolved via its BranchId's own BranchV1.
        var knownGood = app.Services.GetRequiredService<IKnownGoodStateService>();
        var state = await knownGood.MarkKnownGoodAsync(new KnownGoodState(
            $"KGS-{Guid.NewGuid():N}", parent.BranchId, "checkpoint", null, DateTimeOffset.UtcNow, "tester"));
        Assert.Equal(repo.RepositoryId, state.RepositoryId);
        var kgsKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.KnownGoodStateV1, state.StateId);
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", kgsKey));

        // DecisionV1 — denormalized from WorkUnitId.
        var decisions = app.Services.GetRequiredService<IDecisionNodeService>();
        var decision = await decisions.RecordAsync(new DecisionNode(
            $"DEC-{Guid.NewGuid():N}", parent.WorkUnitId, null, DecisionOutcome.Accepted,
            null, null, null, null, null, DateTimeOffset.UtcNow));
        Assert.Equal(repo.RepositoryId, decision.RepositoryId);
        var decisionKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.DecisionV1, decision.DecisionId);
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", decisionKey));

        // ArtifactRefV1 — CreateWorkUnitAsync already recorded parent's own Goal artifact; confirm
        // it denormalized and routed too (OwnedByWorkUnitId -> owning work unit's RepositoryId).
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var goalArtifact = await artifacts.GetAsync(parent.WorkUnitId);
        Assert.NotNull(goalArtifact);
        Assert.Equal(repo.RepositoryId, goalArtifact!.RepositoryId);
        var artifactKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.ArtifactRefV1, goalArtifact.ArtifactId);
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", artifactKey));

        // Broken chain: a work unit with no RepositoryId and no parent — everything hanging off it
        // stays in "studio", never blocked.
        var orphan = await orchestrator.CreateWorkUnitAsync(goal: "orphan goal", owner: "tester");
        Assert.Null(orphan.RepositoryId);
        var orphanTask = await tasks.CreateAsync(new StudioTask(
            $"TASK-{Guid.NewGuid():N}", orphan.WorkUnitId, "t", "d",
            NodalMerge.Studio.Contracts.Domain.TaskStatus.Open, null, 0));
        Assert.Null(orphanTask.RepositoryId);
        var orphanTaskKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.TaskV1, orphanTask.TaskId);
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", orphanTaskKey));
        Assert.Null(TryReadEngineMapValue(bridge, repoRoomId, "studio", orphanTaskKey));
    }

    /// <summary>
    /// Migration: pre-6.3a rows of the newly-routed kinds carry NO "RepositoryId" property at all
    /// (the field didn't exist yet) — the second-pass migration must resolve ownership via the FK
    /// chain (proposal -> work unit -> repo), leave broken chains in "studio", run exactly once
    /// (marker-guarded, restart-proof), and leave old rows intact (AP-5). Also covers rehydration
    /// completeness: after restart, IMergeService's own rehydrated in-memory dictionary must see
    /// the repo-room-resident proposal — the highest-risk regression this slice calls out.
    /// </summary>
    [Fact]
    public async Task Pre63a_indirect_kind_rows_migrate_via_FK_chain_exactly_once_and_rehydrate_after_restart()
    {
        var repoPath = Path.Combine(_tempRoot, "premig-repoA");
        string workgroupRepoId;
        string localRepoId;

        await using (var app1 = BuildApp("premig"))
        {
            var store1 = app1.Services.GetRequiredService<IStudioNodeStore>();
            var bridge1 = app1.Services.GetRequiredService<IRuntimeCommandBridge>();

            var repo = await RegisterBoundRepoAsync(app1.Services, repoPath, "repoA");
            workgroupRepoId = repo.WorkgroupRepoId!;
            localRepoId = repo.RepositoryId;
            var repoRoomId = $"repo/{workgroupRepoId}";

            // Simulate a pre-6.3a writer: real domain records, serialized exactly like production
            // code does (JsonSerializer.Serialize(record), default options — enums as numbers).
            // WorkUnitV1 already carries RepositoryId (that field existed since 6.3); MergeProposal's
            // RepositoryId defaults to null, which round-trips identically to "property absent" for
            // TryExtractRepositoryId's purposes — the exact shape a genuinely pre-6.3a row has. Both
            // land in "studio" via the room-agnostic 3-arg overload, exactly where every pre-6.3a
            // row lived.
            var workUnit = new WorkUnit(
                "WU-premig", "premig goal", "branch-premig", WorkUnitStatus.Active,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "tester", null, null, null, null, [], [],
                RepositoryId: localRepoId);
            await store1.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-premig", JsonSerializer.Serialize(workUnit));

            var proposal = new MergeProposal(
                "MP-premig", "branch-premig", "main", "premig goal", "summary", "change",
                null, null, null, MergeProposalStatus.Draft, WorkUnitId: "WU-premig");
            await store1.WriteNodeAsync(StudioNodeKind.MergeProposalV1, "MP-premig", JsonSerializer.Serialize(proposal));

            // Broken-chain row: WorkUnitId points at nothing — must stay in "studio" forever, not
            // block the migration or get silently dropped.
            var ghostProposal = new MergeProposal(
                "MP-ghost", "branch-ghost", "main", "ghost goal", "summary", "change",
                null, null, null, MergeProposalStatus.Draft, WorkUnitId: "WU-does-not-exist");
            await store1.WriteNodeAsync(StudioNodeKind.MergeProposalV1, "MP-ghost", JsonSerializer.Serialize(ghostProposal));

            var proposalKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.MergeProposalV1, "MP-premig");
            var ghostKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.MergeProposalV1, "MP-ghost");
            Assert.NotNull(TryReadEngineMapValue(bridge1, "studio", "studio", proposalKey));

            // First touch of the repo room (any repo-scoped read triggers GetOrCreateRepoRoomMapAsync,
            // which now runs BOTH the 6.3 and 6.3a migration passes).
            var readBack = await store1.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-premig");
            Assert.NotNull(readBack);

            // The proposal resolved via WU-premig's own RepositoryId and migrated into the repo room.
            Assert.NotNull(TryReadEngineMapValue(bridge1, repoRoomId, "studio", proposalKey));
            Assert.NotNull(TryReadEngineMapValue(bridge1, "studio", "studio", proposalKey)); // old row intact (AP-5)

            // The ghost (broken-chain) proposal never resolves — stays in "studio" only.
            Assert.Null(TryReadEngineMapValue(bridge1, repoRoomId, "studio", ghostKey));
            Assert.NotNull(TryReadEngineMapValue(bridge1, "studio", "studio", ghostKey));

            var proposalReadBack = await store1.ReadNodeAsync(StudioNodeKind.MergeProposalV1, "MP-premig");
            Assert.NotNull(proposalReadBack);
            using var doc = JsonDocument.Parse(proposalReadBack!);
            Assert.Equal("MP-premig", doc.RootElement.GetProperty("ProposalId").GetString());
        }

        // Restart: migration must not re-run (v2 marker is durable), must not duplicate/clobber
        // anything, and rehydration must see the repo-room-resident proposal through the real
        // IMergeService — the highest-risk regression path (in-memory service rehydration).
        await using (var app2 = BuildApp("premig"))
        {
            // StudioStateRehydrationService is an IHostedService — only StartAsync actually runs
            // every IRehydratable's RehydrateAsync (including InMemoryMergeService's own).
            await app2.StartAsync();

            var store2 = app2.Services.GetRequiredService<IStudioNodeStore>();
            var merge2 = app2.Services.GetRequiredService<IMergeService>();

            var all = await store2.ReadAllNodesAsync(StudioNodeKind.MergeProposalV1);
            Assert.Single(all, e => e.EntityId == "MP-premig");
            Assert.Single(all, e => e.EntityId == "MP-ghost");

            var rehydrated = await merge2.GetAsync("MP-premig");
            Assert.NotNull(rehydrated);
            Assert.Equal("WU-premig", rehydrated!.WorkUnitId);

            var rehydratedGhost = await merge2.GetAsync("MP-ghost");
            Assert.NotNull(rehydratedGhost);
        }
    }

    [Fact]
    public async Task ReadAllNodesAsync_aggregates_across_two_bound_repo_rooms_and_studio()
    {
        await using var app = BuildApp("aggregate");
        var store = app.Services.GetRequiredService<IStudioNodeStore>();

        var repoA = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "agg-repoA"), "repoA");
        var repoB = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "agg-repoB"), "repoB");
        Assert.NotEqual(repoA.WorkgroupRepoId, repoB.WorkgroupRepoId);

        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-a",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-a", RepositoryId = repoA.RepositoryId }), repoA.RepositoryId);
        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-b",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-b", RepositoryId = repoB.RepositoryId }), repoB.RepositoryId);
        // No repositoryId — stays in "studio" (e.g. a workspace-only work unit with no repo).
        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-local",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-local" }));

        var all = await store.ReadAllNodesAsync(StudioNodeKind.WorkUnitV1);
        var ids = all.Select(e => e.EntityId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("WU-a", ids);
        Assert.Contains("WU-b", ids);
        Assert.Contains("WU-local", ids);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task Pre63_studio_room_state_migrates_into_the_repo_room_once_with_old_rows_intact()
    {
        var repoPath = Path.Combine(_tempRoot, "mig-repoA");
        string workgroupRepoId;
        string localRepoId;

        await using (var app1 = BuildApp("migration"))
        {
            var store1 = app1.Services.GetRequiredService<IStudioNodeStore>();
            var bridge1 = app1.Services.GetRequiredService<IRuntimeCommandBridge>();

            var repo = await RegisterBoundRepoAsync(app1.Services, repoPath, "repoA");
            workgroupRepoId = repo.WorkgroupRepoId!;
            localRepoId = repo.RepositoryId;

            // Simulate a pre-6.3 writer: the 3-arg overload always writes to the "studio" room —
            // exactly where every repo-scoped entry lived before this slice.
            var payload = JsonSerializer.Serialize(new { WorkUnitId = "WU-mig", RepositoryId = localRepoId, Status = "Draft" });
            await store1.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-mig", payload);

            var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-mig");
            var repoRoomId = $"repo/{workgroupRepoId}";
            Assert.NotNull(TryReadEngineMapValue(bridge1, "studio", "studio", key));

            // First touch of the repo room (a room-agnostic read is enough) triggers the one-shot
            // per-room migration.
            var readBack = await store1.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-mig");
            Assert.NotNull(readBack);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), JsonNode.Parse(readBack)));

            Assert.NotNull(TryReadEngineMapValue(bridge1, repoRoomId, "studio", key)); // migrated in
            Assert.NotNull(TryReadEngineMapValue(bridge1, "studio", "studio", key));   // old row intact (AP-5)

            // Post-migration writes route to the repo room and win over the frozen studio copy.
            var updated = JsonSerializer.Serialize(new { WorkUnitId = "WU-mig", RepositoryId = localRepoId, Status = "Completed" });
            await store1.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-mig", updated, localRepoId);
            var readUpdated = await store1.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-mig");
            Assert.NotNull(readUpdated);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(updated), JsonNode.Parse(readUpdated)));
        }

        // Restart: migration must not re-run (marker is durable) and must not clobber the
        // post-migration update with the stale studio copy; exactly one WU-mig entity survives.
        await using (var app2 = BuildApp("migration"))
        {
            var store2 = app2.Services.GetRequiredService<IStudioNodeStore>();

            var readBack = await store2.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-mig");
            Assert.NotNull(readBack);
            using var doc = JsonDocument.Parse(readBack);
            Assert.Equal("Completed", doc.RootElement.GetProperty("Status").GetString());

            var all = await store2.ReadAllNodesAsync(StudioNodeKind.WorkUnitV1);
            Assert.Single(all, e => e.EntityId == "WU-mig");
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Two_hosts_two_rooms_goal_spanning_two_repos_and_workgroup_replication()
    {
        await using var hostA = BuildApp("bidir-hostA", configureWebHost: wh => wh.UseUrls("http://127.0.0.1:0"));
        await hostA.StartAsync();

        var boundAddress = hostA.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var wsUriA = boundAddress.Replace("http://", "ws://", StringComparison.Ordinal);

        // Two bound repos on A, then the D3 shape: one goal, two work units in two repo rooms, one
        // workgroup goal node.
        var repoR1 = await RegisterBoundRepoAsync(hostA.Services, Path.Combine(_tempRoot, "bidir-r1"), "r1");
        var repoR2 = await RegisterBoundRepoAsync(hostA.Services, Path.Combine(_tempRoot, "bidir-r2"), "r2");

        var storeA = hostA.Services.GetRequiredService<IStudioNodeStore>();
        await storeA.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-span-r1",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-span-r1", RepositoryId = repoR1.RepositoryId, Goal = "cross-repo goal, R1 half" }),
            repoR1.RepositoryId);
        await storeA.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-span-r2",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-span-r2", RepositoryId = repoR2.RepositoryId, Goal = "cross-repo goal, R2 half" }),
            repoR2.RepositoryId);
        await hostA.Services.GetRequiredService<IWorkgroupGoalDirectory>().RecordAsync("GOAL-span",
        [
            new WorkgroupGoalRepoWorkUnit(repoR1.WorkgroupRepoId!, "WU-span-r1"),
            new WorkgroupGoalRepoWorkUnit(repoR2.WorkgroupRepoId!, "WU-span-r2"),
        ]);

        // On A: the two work units live in two different repo rooms, neither in "studio".
        var bridgeA = hostA.Services.GetRequiredService<IRuntimeCommandBridge>();
        var keyR1 = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-span-r1");
        var keyR2 = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-span-r2");
        Assert.NotNull(TryReadEngineMapValue(bridgeA, $"repo/{repoR1.WorkgroupRepoId}", "studio", keyR1));
        Assert.NotNull(TryReadEngineMapValue(bridgeA, $"repo/{repoR2.WorkgroupRepoId}", "studio", keyR2));
        Assert.Null(TryReadEngineMapValue(bridgeA, "studio", "studio", keyR1));
        Assert.Null(TryReadEngineMapValue(bridgeA, "studio", "studio", keyR2));
        Assert.NotNull(TryReadEngineMapValue(bridgeA, "workgroup", "goals", "GOAL-span"));

        // Peer B connects to A.
        var rootB = Path.Combine(_tempRoot, "bidir-hostB");
        var hostB = StudioWebApplication.BuildPeer(
            [],
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = Path.Combine(rootB, "nodes.db"),
                ["NodalMerge:Storage:FileBlobs:RootPath"] = Path.Combine(rootB, "blobs"),
                ["Workspace:RootPath"] = Path.Combine(rootB, "workspace"),
                ["Peer:RoomId"] = "studio",
                ["Peer:HostUri"] = wsUriA,
                ["Peer:PeerId"] = "peer-b",
            }));

        try
        {
            await hostB.StartAsync();

            // (Acceptance: workgroup replication end-to-end) — registrations made on A appear in
            // B's directory listing while both are connected. R1/R2 were registered before B
            // connected (covered by catch-up); a third registration made *while* connected covers
            // the live-broadcast half below.
            var directoryB = hostB.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
            await PollUntilAsync(async () =>
            {
                var entries = await directoryB.ListAsync();
                return entries.Any(e => e.RepoId == repoR1.WorkgroupRepoId)
                    && entries.Any(e => e.RepoId == repoR2.WorkgroupRepoId);
            });

            var repoR3 = await RegisterBoundRepoAsync(hostA.Services, Path.Combine(_tempRoot, "bidir-r3"), "r3");
            await PollUntilAsync(async () =>
                (await directoryB.ListAsync()).Any(e => e.RepoId == repoR3.WorkgroupRepoId));

            // B binds to R1 ONLY (D2's one-time disambiguation flow: degraded local hints against
            // the replicated candidates, human picks R1).
            var registryB = hostB.Services.GetRequiredService<IRepositoryRegistryService>();
            var localB = await registryB.RegisterAsync(Path.Combine(rootB, "clone-of-r1"), "r1-on-b");
            Assert.Null(localB.WorkgroupRepoId);
            Assert.NotNull(localB.PendingDisambiguation);
            localB = await registryB.ResolveDisambiguationAsync(localB.RepositoryId, repoR1.WorkgroupRepoId);
            Assert.Equal(repoR1.WorkgroupRepoId, localB!.WorkgroupRepoId);

            // B's membership loop joins repo/R1 within its reconcile interval; hello/catch-up then
            // delivers WU-span-r1. B is NOT bound to R2, never joins its room, and must never see
            // WU-span-r2.
            var storeB = hostB.Services.GetRequiredService<IStudioNodeStore>();
            await PollUntilAsync(async () =>
                await storeB.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-span-r1") is not null);

            Assert.Null(await storeB.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-span-r2"));
            var bridgeB = hostB.Services.GetRequiredService<IRuntimeCommandBridge>();
            Assert.Null(TryReadEngineMapValue(bridgeB, $"repo/{repoR2.WorkgroupRepoId}", "studio", keyR2));

            // The workgroup goal node replicated too — B sees the cross-repo coordination record.
            var goalOnB = await hostB.Services.GetRequiredService<IWorkgroupGoalDirectory>().GetAsync("GOAL-span");
            Assert.NotNull(goalOnB);
            Assert.Equal(2, goalOnB!.RepoWorkUnits.Count);
            Assert.Contains(goalOnB.RepoWorkUnits, r => r.RepoId == repoR1.WorkgroupRepoId && r.WorkUnitId == "WU-span-r1");
            Assert.Contains(goalOnB.RepoWorkUnits, r => r.RepoId == repoR2.WorkgroupRepoId && r.WorkUnitId == "WU-span-r2");

            // Slice 6.3a's whole point — 5.3's precondition: a MergeProposal created on peer A for a
            // bound repo (R1) rides the exact same repo-room replication WU-span-r1 used, and becomes
            // visible on peer B without restart on either side.
            var proposalPayload = JsonSerializer.Serialize(new
            {
                ProposalId = "MP-span-r1",
                SourceBranch = "WU-span-r1-branch",
                TargetBranch = "main",
                Goal = "cross-repo goal, R1 half",
                Summary = "test proposal",
                ChangeDescription = "test",
                Status = "Draft",
                WorkUnitId = "WU-span-r1",
                RepositoryId = repoR1.RepositoryId,
            });
            await storeA.WriteNodeAsync(
                StudioNodeKind.MergeProposalV1, "MP-span-r1", proposalPayload, repoR1.RepositoryId);

            await PollUntilAsync(async () =>
                await storeB.ReadNodeAsync(StudioNodeKind.MergeProposalV1, "MP-span-r1") is not null);
            var proposalOnB = await storeB.ReadNodeAsync(StudioNodeKind.MergeProposalV1, "MP-span-r1");
            Assert.NotNull(proposalOnB);
            using (var proposalDoc = JsonDocument.Parse(proposalOnB!))
                Assert.Equal("WU-span-r1", proposalDoc.RootElement.GetProperty("WorkUnitId").GetString());
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Pinned_reference_resolves_with_no_local_clone_of_the_referenced_repo()
    {
        await using var app = BuildApp("pinned", configureWebHost: wh => wh.UseUrls("http://127.0.0.1:0"));
        await app.StartAsync();

        // The registered path deliberately does not exist — no clone, no materialization. The repo
        // room + CAS are the only local state the resolution is allowed to touch.
        var phantomPath = Path.Combine(_tempRoot, "pinned-phantom-repo");
        Assert.False(Directory.Exists(phantomPath));
        var repo = await RegisterBoundRepoAsync(app.Services, phantomPath, "phantom");
        Assert.False(Directory.Exists(phantomPath));

        // Seed CAS: file blob + tree object.
        var content = "export const answer = 42;\n";
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var fileHash = BlobHasher.ComputeHash(contentBytes);
        var blobStore = app.Services.GetRequiredService<IBlobStoreProvider>();
        await blobStore.PutBlobAsync(fileHash, contentBytes, "text/plain");

        var treeResolver = app.Services.GetRequiredService<ISnapshotTreeResolver>();
        var treeHash = await treeResolver.WriteTreeAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/lib/util.ts"] = fileHash,
        });

        // Seed the generation node into the repo room (what replication would have delivered).
        var snapshot = new RepositorySnapshot(
            SnapshotId: "gen-pin-1",
            RepositoryId: repo.RepositoryId,
            TreeHash: treeHash,
            Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            TreeEntries: null,
            TreeFormat: "cas-tree");
        var store = app.Services.GetRequiredService<IStudioNodeStore>();
        await store.WriteNodeAsync(
            StudioNodeKind.RepositorySnapshotV1, snapshot.SnapshotId,
            JsonSerializer.Serialize(snapshot), repo.RepositoryId);

        // Resolve through the service — full frozen chain: repo room -> generation -> TreeHash ->
        // CAS walk -> blob.
        var resolver = app.Services.GetRequiredService<IPinnedReferenceResolver>();
        var bytes = await resolver.ResolveAsync(new PinnedReference(repo.WorkgroupRepoId!, "gen-pin-1", "src/lib/util.ts"));
        Assert.NotNull(bytes);
        Assert.Equal(content, Encoding.UTF8.GetString(bytes!));

        // Unknown generation / path miss both come back null, never throw.
        Assert.Null(await resolver.ResolveAsync(new PinnedReference(repo.WorkgroupRepoId!, "gen-nope", "src/lib/util.ts")));
        Assert.Null(await resolver.ResolveAsync(new PinnedReference(repo.WorkgroupRepoId!, "gen-pin-1", "src/lib/missing.ts")));

        // And over the REST surface.
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };

        var ok = await http.PostAsJsonAsync("/studio/references/resolve",
            new { repoId = repo.WorkgroupRepoId, generationId = "gen-pin-1", path = "src/lib/util.ts" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var okDoc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal(content, Encoding.UTF8.GetString(
            Convert.FromBase64String(okDoc.RootElement.GetProperty("contentBase64").GetString()!)));
        Assert.Equal(contentBytes.Length, okDoc.RootElement.GetProperty("sizeBytes").GetInt32());

        var notFound = await http.PostAsJsonAsync("/studio/references/resolve",
            new { repoId = repo.WorkgroupRepoId, generationId = "gen-nope", path = "src/lib/util.ts" });
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        var badRequest = await http.PostAsJsonAsync("/studio/references/resolve",
            new { repoId = "", generationId = "", path = "" });
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
    }

    /// <summary>
    /// Phase 1 (plans/organizational-knowledge-and-workgroup-scope.md) — promoting a KnowledgeGuideline
    /// finding now creates a REPLICATED constraint (Reach=Workgroup), not one stranded in the local
    /// room (the motivating bug). Default is shared + repo-specific: the finding's own RepositoryId
    /// (Phase 0) becomes the constraint's application scope, so it lands in that repo's room; a finding
    /// with no repo promotes to the shared "workgroup" room (all repos).
    /// </summary>
    [Fact]
    public async Task Promoting_a_finding_creates_a_replicated_constraint()
    {
        await using var app = BuildApp("promote");
        var repo = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "promote-repoA"), "repoA");
        var repoRoomId = $"repo/{repo.WorkgroupRepoId}";
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();
        var findings = app.Services.GetRequiredService<IFindingService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();

        // A finding attributed to the repo → promotes to a Workgroup + repo-specific constraint.
        await findings.ProposeAsync(new Finding(
            FindingId: "finding-repo", Kind: FindingKind.KnowledgeGuideline,
            Source: FindingSource.Deterministic, Title: "run migrations first", Summary: "do X before Y",
            SupportingDataJson: null, Status: FindingStatus.Open, CreatedAt: DateTimeOffset.UtcNow,
            RepositoryId: repo.RepositoryId));
        var reviewedRepo = await findings.ReviewAsync("finding-repo", FindingStatus.Promoted);
        Assert.NotNull(reviewedRepo.PromotedArtifactId);

        var repoConstraint = await artifacts.GetAsync(reviewedRepo.PromotedArtifactId!);
        Assert.Equal(ArtifactReach.Workgroup, repoConstraint!.Reach);
        Assert.Equal(repo.RepositoryId, repoConstraint.RepositoryId);
        var repoKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.ArtifactRefV1, repoConstraint.ArtifactId);
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", repoKey)); // replicated to repo peers
        Assert.Null(TryReadEngineMapValue(bridge, "studio", "studio", repoKey));       // NOT stranded local

        // A finding with no repo → promotes to a workgroup-wide (all repos) constraint.
        await findings.ProposeAsync(new Finding(
            FindingId: "finding-global", Kind: FindingKind.KnowledgeGuideline,
            Source: FindingSource.Deterministic, Title: "global rule", Summary: "everywhere",
            SupportingDataJson: null, Status: FindingStatus.Open, CreatedAt: DateTimeOffset.UtcNow));
        var reviewedGlobal = await findings.ReviewAsync("finding-global", FindingStatus.Promoted);

        var globalConstraint = await artifacts.GetAsync(reviewedGlobal.PromotedArtifactId!);
        Assert.Equal(ArtifactReach.Workgroup, globalConstraint!.Reach);
        Assert.Null(globalConstraint.RepositoryId);
        var globalKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.ArtifactRefV1, globalConstraint.ArtifactId);
        Assert.NotNull(TryReadEngineMapValue(bridge, "workgroup", "studio", globalKey)); // shared workgroup room
        Assert.Null(TryReadEngineMapValue(bridge, "studio", "studio", globalKey));
    }

    /// <summary>
    /// Phase 1 (plans/organizational-knowledge-and-workgroup-scope.md) — progressive promotion:
    /// elevating a Workgroup + repo-specific constraint widens its application to all repos
    /// (RepositoryId → null) and re-routes it from the repo room into the shared "workgroup" room.
    /// </summary>
    [Fact]
    public async Task Elevating_a_repo_constraint_moves_it_to_the_workgroup_room()
    {
        await using var app = BuildApp("elevate");
        var repo = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "elevate-repoA"), "repoA");
        var repoRoomId = $"repo/{repo.WorkgroupRepoId}";
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();

        await artifacts.RecordAsync(new ArtifactRef(
            ArtifactId: "c-elevate", Type: ArtifactType.Constraint, ParentArtifactId: null,
            Status: ArtifactStatus.Approved, CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: "c", Body: "b",
            RepositoryId: repo.RepositoryId, Reach: ArtifactReach.Workgroup));

        var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.ArtifactRefV1, "c-elevate");
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", key));   // starts repo-scoped
        Assert.Null(TryReadEngineMapValue(bridge, "workgroup", "studio", key));

        var elevated = await artifacts.ElevateToWorkgroupAsync("c-elevate");
        Assert.Equal(ArtifactReach.Workgroup, elevated!.Reach);
        Assert.Null(elevated.RepositoryId);                                          // now all-repos
        Assert.NotNull(TryReadEngineMapValue(bridge, "workgroup", "studio", key));   // now in the workgroup room

        // Idempotent: a second elevate is a no-op.
        var again = await artifacts.ElevateToWorkgroupAsync("c-elevate");
        Assert.Null(again!.RepositoryId);
    }

    /// <summary>
    /// Items 1+2 (plans/vision-punchlist-remediation.md) — the orphan-impact report. Re-linking never
    /// migrates content: whatever is already in the room being left stays there and drops out of the
    /// read fan-out. A preview must therefore be able to say how much that is, BEFORE anything is
    /// committed, so the user makes an informed choice instead of silently losing sight of work.
    ///
    /// Uses "register-new" deliberately: splitting an already-bound repository into its own room is
    /// the case where a dry run has nothing to mint, so it was the one path that reported no impact
    /// at all until the preview learned to treat a split as a move.
    /// </summary>
    [Fact]
    public async Task Relink_preview_reports_what_the_repository_would_leave_behind()
    {
        await using var app = BuildApp("relink-impact");
        var repo = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "impact-repo"), "impact");
        var store = app.Services.GetRequiredService<IStudioNodeStore>();
        var registry = app.Services.GetRequiredService<IRepositoryRegistryService>();

        // Put some repo-scoped state in the room this repository currently occupies.
        await store.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1, "WU-impact-1",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-impact-1", RepositoryId = repo.RepositoryId }),
            repo.RepositoryId);
        await store.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1, "WU-impact-2",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-impact-2", RepositoryId = repo.RepositoryId }),
            repo.RepositoryId);

        var preview = await registry.RelinkAsync(
            repo.RepositoryId, RepositoryRelinkMode.Manual, "register-new", commit: false);

        Assert.NotNull(preview);
        Assert.False(preview!.Committed);                       // a preview writes nothing...
        Assert.NotNull(preview.Impact);                          // ...but still knows the cost
        Assert.Equal($"repo/{repo.WorkgroupRepoId}", preview.Impact!.RoomId);
        Assert.True(preview.Impact.TotalNodes >= 2,
            $"expected the two work units to be counted, got {preview.Impact.TotalNodes}");
        Assert.Contains(StudioNodeKind.WorkUnitV1, preview.Impact.CountsByKind.Keys);

        // And the binding really is untouched by the preview.
        Assert.Equal(repo.WorkgroupRepoId, (await registry.GetAsync(repo.RepositoryId))!.WorkgroupRepoId);
    }

    /// <summary>
    /// Item 3 (plans/vision-punchlist-remediation.md) — re-scoping re-routes an artifact into a new
    /// room but cannot delete the copy left in the old one (EngineRoomMap has no per-entry eviction),
    /// so the same ArtifactId lives in two rooms with divergent payloads. The read fan-out used to
    /// resolve that purely by room precedence (workgroup &gt; repo &gt; studio), which meant every
    /// *narrowing* move resolved to the STALE broader copy: a Workgroup+repo → Private constraint
    /// read back as Workgroup on every peer and after any rehydrate, silently reverting the change.
    /// ScopeVersion (bumped by every scope mutator) now decides the winner instead.
    /// </summary>
    [Fact]
    public async Task Narrowing_a_constraints_scope_is_not_reverted_by_the_stale_room_copy()
    {
        await using var app = BuildApp("rescope-narrow");
        var repo = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "rescope-repoA"), "repoA");
        var repoRoomId = $"repo/{repo.WorkgroupRepoId}";
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var store = app.Services.GetRequiredService<IStudioNodeStore>();

        await artifacts.RecordAsync(new ArtifactRef(
            ArtifactId: "c-narrow", Type: ArtifactType.Constraint, ParentArtifactId: null,
            Status: ArtifactStatus.Approved, CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: "c", Body: "b",
            RepositoryId: repo.RepositoryId, Reach: ArtifactReach.Workgroup));

        var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.ArtifactRefV1, "c-narrow");
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", key));   // starts repo-scoped

        // The narrowing move: Workgroup + repo → Private (into the peer-private "studio" room).
        var updated = await artifacts.SetScopeAsync("c-narrow", ArtifactReach.Private, null);
        Assert.Equal(ArtifactReach.Private, updated!.Reach);
        Assert.Equal(1, updated.ScopeVersion);

        // Both copies genuinely exist now — this is what makes the collision real rather than
        // hypothetical, and why read-side resolution (not eviction) is the fix.
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", key));   // stale, still there
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", key));     // the live one

        // The fan-out must collapse to the NEW copy. Under room precedence the repo-room copy wins
        // and this reads Workgroup.
        var all = await store.ReadAllNodesAsync(StudioNodeKind.ArtifactRefV1);
        var payload = all.Single(e => e.EntityId == "c-narrow").PayloadJson;
        var resolved = JsonSerializer.Deserialize<ArtifactRef>(payload);
        Assert.Equal(ArtifactReach.Private, resolved!.Reach);

        // ...and the user-visible path: a live refresh (what an inbound pack or a restart drives)
        // repopulates from that same fan-out, so it must not revert either.
        await ((IRehydratable)artifacts).RefreshAsync();
        var afterRefresh = await artifacts.GetAsync("c-narrow");
        Assert.Equal(ArtifactReach.Private, afterRefresh!.Reach);
    }

    /// <summary>
    /// Item 3 — the repo→repo case, which room precedence resolved *nondeterministically* (both
    /// copies sit in equal-precedence repo rooms, so the winner fell out of dictionary iteration
    /// order). Highest ScopeVersion makes it deterministic.
    /// </summary>
    [Fact]
    public async Task Re_scoping_a_constraint_between_two_repos_resolves_to_the_newer_room()
    {
        await using var app = BuildApp("rescope-repo-to-repo");
        var repoA = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "r2r-repoA"), "repoA");
        var repoB = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "r2r-repoB"), "repoB");
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var store = app.Services.GetRequiredService<IStudioNodeStore>();

        await artifacts.RecordAsync(new ArtifactRef(
            ArtifactId: "c-move", Type: ArtifactType.Constraint, ParentArtifactId: null,
            Status: ArtifactStatus.Approved, CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: "c", Body: "b",
            RepositoryId: repoA.RepositoryId, Reach: ArtifactReach.Workgroup));

        await artifacts.SetScopeAsync("c-move", ArtifactReach.Workgroup, repoB.RepositoryId);

        var all = await store.ReadAllNodesAsync(StudioNodeKind.ArtifactRefV1);
        var resolved = JsonSerializer.Deserialize<ArtifactRef>(
            all.Single(e => e.EntityId == "c-move").PayloadJson);
        Assert.Equal(repoB.RepositoryId, resolved!.RepositoryId);

        await ((IRehydratable)artifacts).RefreshAsync();
        var afterRefresh = await artifacts.GetAsync("c-move");
        Assert.Equal(repoB.RepositoryId, afterRefresh!.RepositoryId);
    }

    /// <summary>
    /// Phase 1 (plans/organizational-knowledge-and-workgroup-scope.md) — ArtifactRef routes by the
    /// 2×2 (Reach × Application): Private → peer-private "studio" room; Workgroup + RepositoryId →
    /// that repo's room; Workgroup + no RepositoryId → the shared "workgroup" room (the old "global",
    /// now actually replicated instead of stranded locally). Legacy artifacts (Reach null) keep the
    /// pre-Phase-1 repo-room-or-"studio" routing. The read fan-out consults the workgroup room too, so
    /// a workgroup-only artifact is still found.
    /// </summary>
    [Fact]
    public async Task Artifact_reach_routes_to_studio_repo_or_workgroup_room()
    {
        await using var app = BuildApp("artifact-reach");
        var repo = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "reach-repoA"), "repoA");
        var repoRoomId = $"repo/{repo.WorkgroupRepoId}";
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var store = app.Services.GetRequiredService<IStudioNodeStore>();

        ArtifactRef Make(string id, ArtifactReach? reach, string? repositoryId) => new(
            ArtifactId: id, Type: ArtifactType.Constraint, ParentArtifactId: null,
            Status: ArtifactStatus.Approved, CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: id, Body: "b",
            RepositoryId: repositoryId, Reach: reach);

        static string Key(string id) => NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.ArtifactRefV1, id);

        // 1. Workgroup + no repo → the shared "workgroup" room (the old "global", now replicated).
        await artifacts.RecordAsync(Make("wg-global", ArtifactReach.Workgroup, null));
        Assert.NotNull(TryReadEngineMapValue(bridge, "workgroup", "studio", Key("wg-global")));
        Assert.Null(TryReadEngineMapValue(bridge, "studio", "studio", Key("wg-global")));
        Assert.Null(TryReadEngineMapValue(bridge, repoRoomId, "studio", Key("wg-global")));

        // 2. Workgroup + repo → that repo's room.
        await artifacts.RecordAsync(Make("wg-repo", ArtifactReach.Workgroup, repo.RepositoryId));
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", Key("wg-repo")));
        Assert.Null(TryReadEngineMapValue(bridge, "workgroup", "studio", Key("wg-repo")));
        Assert.Null(TryReadEngineMapValue(bridge, "studio", "studio", Key("wg-repo")));

        // 3. Private → the peer-private "studio" room, never replicated.
        await artifacts.RecordAsync(Make("priv", ArtifactReach.Private, null));
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", Key("priv")));
        Assert.Null(TryReadEngineMapValue(bridge, "workgroup", "studio", Key("priv")));

        // 4. Private + repo (local override — the 4th 2×2 cell) → studio, NOT the repo room.
        await artifacts.RecordAsync(Make("priv-repo", ArtifactReach.Private, repo.RepositoryId));
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", Key("priv-repo")));
        Assert.Null(TryReadEngineMapValue(bridge, repoRoomId, "studio", Key("priv-repo")));

        // 5. Legacy (Reach null) unchanged: no repo → "studio", not the workgroup room.
        await artifacts.RecordAsync(Make("legacy-local", null, null));
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", Key("legacy-local")));
        Assert.Null(TryReadEngineMapValue(bridge, "workgroup", "studio", Key("legacy-local")));

        // Read fan-out: wg-global lives ONLY in the workgroup room, so a successful read proves
        // ReadNodeAsync/ReadAllNodesAsync now consult that room.
        Assert.NotNull(await store.ReadNodeAsync(StudioNodeKind.ArtifactRefV1, "wg-global"));
        var all = await store.ReadAllNodesAsync(StudioNodeKind.ArtifactRefV1);
        var ids = all.Select(e => e.EntityId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("wg-global", ids);
        Assert.Contains("wg-repo", ids);
        Assert.Contains("priv", ids);
    }

    /// <summary>
    /// Phase 0 (plans/organizational-knowledge-and-workgroup-scope.md) — a Finding proposed in a
    /// workspace bound to a repo is attributed to that repo (FindingService stamps RepositoryId from
    /// the workspace seed repo) and, because FindingV1 is now in RepoScopedKinds, lands in the
    /// repo/{repoId} room so the Insights Findings queue is shared with same-repo peers — never the
    /// peer-private "studio" room, where findings used to be stranded.
    /// </summary>
    [Fact]
    public async Task Finding_is_attributed_to_the_workspace_repo_and_lands_in_the_repo_room()
    {
        var seedRepoPath = Path.Combine(_tempRoot, "findings-seed-repo");
        Directory.CreateDirectory(seedRepoPath);
        var root = Path.Combine(_tempRoot, "findings-attr");

        await using var app = StudioWebApplication.Build(
            [],
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = Path.Combine(root, "nodes.db"),
                ["NodalMerge:Storage:FileBlobs:RootPath"] = Path.Combine(root, "blobs"),
                ["Workspace:RootPath"] = Path.Combine(root, "workspace"),
                ["Workspace:SeedRepositoryPath"] = seedRepoPath,
            }));

        var repo = await RegisterBoundRepoAsync(app.Services, seedRepoPath, "seed");
        var repoRoomId = $"repo/{repo.WorkgroupRepoId}";
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();
        var findings = app.Services.GetRequiredService<IFindingService>();

        // No RepositoryId supplied — ProposeAsync must stamp it from the workspace seed repo.
        var finding = await findings.ProposeAsync(new Finding(
            FindingId: "finding-attr",
            Kind: FindingKind.KnowledgeGuideline,
            Source: FindingSource.Deterministic,
            Title: "t", Summary: "s",
            SupportingDataJson: null,
            Status: FindingStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow));

        Assert.Equal(repo.RepositoryId, finding.RepositoryId);

        var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.FindingV1, "finding-attr");
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", key)); // shared repo room
        Assert.Null(TryReadEngineMapValue(bridge, "studio", "studio", key));       // not stranded locally

        // A review re-persists the finding; it must stay in the same repo room, not fall back to studio.
        await findings.ReviewAsync("finding-attr", FindingStatus.Dismissed, "nope");
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", key));
        Assert.Null(TryReadEngineMapValue(bridge, "studio", "studio", key));
    }

    /// <summary>
    /// Phase 0 — safe degradation: with no workspace seed repo to attribute to, a finding keeps a null
    /// RepositoryId and stays in the local "studio" room (the 3-arg write), never blocked or lost.
    /// </summary>
    [Fact]
    public async Task Finding_with_no_workspace_repo_stays_local()
    {
        await using var app = BuildApp("findings-local");
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();
        var findings = app.Services.GetRequiredService<IFindingService>();

        var finding = await findings.ProposeAsync(new Finding(
            FindingId: "finding-local",
            Kind: FindingKind.KnowledgeGuideline,
            Source: FindingSource.Deterministic,
            Title: "t", Summary: "s",
            SupportingDataJson: null,
            Status: FindingStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow));

        Assert.Null(finding.RepositoryId);
        var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.FindingV1, "finding-local");
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", key));
    }

    private static JsonNode? TryReadEngineMapValue(IRuntimeCommandBridge bridge, string roomId, string @namespace, string key)
    {
        var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = roomId,
            command = new { MapGet = new { @namespace, key } }
        }));

        // Non-Ok includes "room doesn't exist on this peer" — exactly the "never joined that room"
        // case some assertions here rely on, so it maps to null rather than an assert failure.
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

            return JsonNode.Parse(value.GetRawText());
        }

        return null;
    }

    private static async Task PollUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
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
