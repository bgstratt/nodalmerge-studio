using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 5 slice 5.1 (plans/cas-distribution-and-storage.md) — ISnapshotRetentionPolicy is pure
/// policy: given a seeded node-store DAG and a fixed clock, classification must match a
/// hand-computed expectation for all three classes (the slice's acceptance bar). Nothing consumes
/// the policy yet, so these tests exercise SnapshotRetentionPolicy directly against
/// InMemoryStudioNodeStore rather than through the Host/DI pipeline.
/// </summary>
[Trait("Category", "Integration")]
public class SnapshotRetentionPolicyTests
{
    private const int RetainIntermediateDays = 30;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<(InMemoryStudioNodeStore NodeStore, RepositoryRegistryService Registry)> NewStoreAsync()
    {
        var nodeStore = new InMemoryStudioNodeStore();
        var workspaces = new WorkspaceRegistryService(nodeStore);
        var registry = new RepositoryRegistryService(nodeStore, workspaces);
        return (nodeStore, registry);
    }

    private static Task WriteSnapshotAsync(InMemoryStudioNodeStore store, RepositorySnapshot snapshot) =>
        store.WriteNodeAsync(StudioNodeKind.RepositorySnapshotV1, snapshot.SnapshotId, JsonSerializer.Serialize(snapshot));

    private static Task WriteWorkUnitAsync(InMemoryStudioNodeStore store, WorkUnit workUnit) =>
        store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, workUnit.WorkUnitId, JsonSerializer.Serialize(workUnit));

    /// <summary>
    /// The full acceptance matrix in one DAG: bootstrap -> Pinned; applied-proposal -> Pinned; a
    /// non-terminal work unit's >30-day-old branch seed -> Active (the plan's explicit acceptance
    /// case); the repository's current head -> Active; a retired branch's intermediate generation
    /// inside the retention window -> Intermediate+retained; the same shape outside the window ->
    /// Intermediate+expired.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_matches_hand_computed_expectation_for_all_three_classes()
    {
        var (nodeStore, registry) = await NewStoreAsync();

        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);

        // A literal rooted path — NOT Path.Combine("C:", ...), which (Windows only appends a
        // separator when the first segment already ends in one) would produce a drive-relative
        // "C:fake\repoA" that Path.GetFullPath then resolves against the current directory,
        // making the test non-deterministic.
        var repoAPath = @"C:\fake\repoA";
        var repoARegistration = await registry.RegisterAsync(repoAPath, "Repo A");
        var repoAId = Path.GetFullPath(repoAPath);

        // Gen 0 — bootstrap generation. Pinned regardless of age.
        var snapBootstrap = new RepositorySnapshot(
            SnapshotId: "snap-bootstrap-a", RepositoryId: repoAId, TreeHash: "t0", Generation: 0,
            CreatedAt: now - TimeSpan.FromDays(200), Source: "Bootstrap");
        await WriteSnapshotAsync(nodeStore, snapBootstrap);

        // Gen 1 — captured by an applied merge proposal's write-back resync. Pinned via the
        // applying work unit's Metadata["appliedSnapshotId"] stamp, not via the snapshot itself.
        var snapApplied = new RepositorySnapshot(
            SnapshotId: "snap-applied-a", RepositoryId: repoAId, TreeHash: "t1", Generation: 1,
            CreatedAt: now - TimeSpan.FromDays(150), Source: "ImportedFromFilesystem");
        await WriteSnapshotAsync(nodeStore, snapApplied);
        var wuApplied = new WorkUnit(
            WorkUnitId: "wu-applied", Goal: "test", BranchId: "branch-applied",
            Status: WorkUnitStatus.Merged,
            CreatedAt: now - TimeSpan.FromDays(151), UpdatedAt: now - TimeSpan.FromDays(149),
            Owner: "test", AssignedAgent: null, SuccessCriteria: null,
            Metadata: new Dictionary<string, string> { ["appliedSnapshotId"] = "snap-applied-a" },
            ParentWorkUnitId: null, DependsOn: [], FileScope: [],
            RepositoryId: repoARegistration.RepositoryId);
        await WriteWorkUnitAsync(nodeStore, wuApplied);

        // Gen 2 — the generation that was live when a still-in-flight work unit's branch was
        // created, 50 days ago. Must be Active even though it's well past RetainIntermediateDays —
        // the plan's explicit acceptance case ("an in-flight work unit's seed is Active even when
        // >30 d old").
        var snapSeed = new RepositorySnapshot(
            SnapshotId: "snap-seed-old", RepositoryId: repoAId, TreeHash: "t2", Generation: 2,
            CreatedAt: now - TimeSpan.FromDays(50));
        await WriteSnapshotAsync(nodeStore, snapSeed);
        var wuInFlight = new WorkUnit(
            WorkUnitId: "wu-inflight", Goal: "test", BranchId: "branch-inflight",
            Status: WorkUnitStatus.Executing,
            CreatedAt: now - TimeSpan.FromDays(49), UpdatedAt: now - TimeSpan.FromDays(49),
            Owner: "test", AssignedAgent: null, SuccessCriteria: null, Metadata: null,
            ParentWorkUnitId: null, DependsOn: [], FileScope: [],
            RepositoryId: repoARegistration.RepositoryId);
        await WriteWorkUnitAsync(nodeStore, wuInFlight);

        // Gen 3 — a retired (terminal) branch's intermediate generation, still inside the
        // retention window: its work unit went terminal 5 days ago, well under 30.
        var snapRetiredInside = new RepositorySnapshot(
            SnapshotId: "snap-retired-inside", RepositoryId: repoAId, TreeHash: "t3", Generation: 3,
            CreatedAt: now - TimeSpan.FromDays(10), WorkUnitId: "wu-retired-inside");
        await WriteSnapshotAsync(nodeStore, snapRetiredInside);
        var wuRetiredInside = new WorkUnit(
            WorkUnitId: "wu-retired-inside", Goal: "test", BranchId: "branch-retired-inside",
            Status: WorkUnitStatus.Completed,
            CreatedAt: now - TimeSpan.FromDays(11), UpdatedAt: now - TimeSpan.FromDays(5),
            Owner: "test", AssignedAgent: null, SuccessCriteria: null, Metadata: null,
            ParentWorkUnitId: null, DependsOn: [], FileScope: [],
            RepositoryId: repoARegistration.RepositoryId);
        await WriteWorkUnitAsync(nodeStore, wuRetiredInside);

        // Gen 4 — same shape, but its work unit went terminal 40 days ago: past the 30-day window.
        var snapRetiredOutside = new RepositorySnapshot(
            SnapshotId: "snap-retired-outside", RepositoryId: repoAId, TreeHash: "t4", Generation: 4,
            CreatedAt: now - TimeSpan.FromDays(100), WorkUnitId: "wu-retired-outside");
        await WriteSnapshotAsync(nodeStore, snapRetiredOutside);
        var wuRetiredOutside = new WorkUnit(
            WorkUnitId: "wu-retired-outside", Goal: "test", BranchId: "branch-retired-outside",
            Status: WorkUnitStatus.Completed,
            CreatedAt: now - TimeSpan.FromDays(101), UpdatedAt: now - TimeSpan.FromDays(40),
            Owner: "test", AssignedAgent: null, SuccessCriteria: null, Metadata: null,
            ParentWorkUnitId: null, DependsOn: [], FileScope: [],
            RepositoryId: repoARegistration.RepositoryId);
        await WriteWorkUnitAsync(nodeStore, wuRetiredOutside);

        // Gen 5 — the repository's current head (highest Generation). Active regardless of the
        // fact that, taken alone, it would also be a perfectly ordinary un-expired Intermediate.
        var snapHead = new RepositorySnapshot(
            SnapshotId: "snap-head-a", RepositoryId: repoAId, TreeHash: "t5", Generation: 5,
            CreatedAt: now - TimeSpan.FromDays(1));
        await WriteSnapshotAsync(nodeStore, snapHead);

        var policy = new SnapshotRetentionPolicy(
            nodeStore, registry, options: null,
            retentionOptions: new RetentionPolicyOptions { RetainIntermediateDays = RetainIntermediateDays },
            clock: clock);

        var report = await policy.ClassifyAsync();
        var byId = report.Snapshots.ToDictionary(e => e.SnapshotId);

        Assert.Equal(SnapshotRetentionClass.Pinned, byId["snap-bootstrap-a"].Class);
        Assert.True(byId["snap-bootstrap-a"].Retained);
        Assert.Contains("Bootstrap", byId["snap-bootstrap-a"].Reason);

        Assert.Equal(SnapshotRetentionClass.Pinned, byId["snap-applied-a"].Class);
        Assert.True(byId["snap-applied-a"].Retained);
        Assert.Contains("appliedSnapshotId", byId["snap-applied-a"].Reason);

        Assert.Equal(SnapshotRetentionClass.Active, byId["snap-seed-old"].Class);
        Assert.True(byId["snap-seed-old"].Retained);
        Assert.Contains("wu-inflight", byId["snap-seed-old"].Reason);

        Assert.Equal(SnapshotRetentionClass.Active, byId["snap-head-a"].Class);
        Assert.True(byId["snap-head-a"].Retained);
        Assert.Contains("Current head", byId["snap-head-a"].Reason);

        Assert.Equal(SnapshotRetentionClass.Intermediate, byId["snap-retired-inside"].Class);
        Assert.True(byId["snap-retired-inside"].Retained);
        Assert.NotNull(byId["snap-retired-inside"].ExpiresAt);
        Assert.True(byId["snap-retired-inside"].ExpiresAt > now);

        Assert.Equal(SnapshotRetentionClass.Intermediate, byId["snap-retired-outside"].Class);
        Assert.False(byId["snap-retired-outside"].Retained);
        Assert.NotNull(byId["snap-retired-outside"].ExpiresAt);
        Assert.True(byId["snap-retired-outside"].ExpiresAt < now);

        // Retained set is exactly Pinned + Active + not-yet-expired Intermediate.
        var expectedRetained = new[]
        {
            "snap-bootstrap-a", "snap-applied-a", "snap-seed-old", "snap-head-a", "snap-retired-inside",
        };
        Assert.Equal(expectedRetained.OrderBy(x => x), report.RetainedSnapshotIds.OrderBy(x => x));
        Assert.DoesNotContain("snap-retired-outside", report.RetainedSnapshotIds);

        Assert.Equal(2, report.PinnedCount);
        Assert.Equal(2, report.ActiveCount);
        Assert.Equal(1, report.IntermediateRetainedCount);
        Assert.Equal(1, report.IntermediateExpiredCount);
        Assert.Empty(report.Anomalies);
    }

    /// <summary>
    /// A3 (plans/test-suite-remediation-plan.md): a Failed work unit's branch seed must be retained,
    /// NOT aged out — a failed work unit is resumable (Failed -> Cancelled -> Queued/Executing is a
    /// real human revival path), so its seed/merge base must survive for the revival. This matches the
    /// frozen `terminal_status_names` = {Completed, Merged} contract and nodalmerge's Rust GC; the C#
    /// SnapshotRetentionPolicy previously listed Failed as terminal and aged its seed out, so
    /// BlobGcService/WorkspaceCacheManager would delete the very blobs a revived Failed unit needs.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_retains_the_seed_of_a_Failed_work_unit_because_it_is_resumable()
    {
        var (nodeStore, registry) = await NewStoreAsync();

        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);

        var repoPath = @"C:\fake\repoFailedSeed";
        var repoRegistration = await registry.RegisterAsync(repoPath, "Repo Failed");
        var repoId = Path.GetFullPath(repoPath);

        // A generation that was live when the (now Failed) work unit's branch was created 50 days ago
        // — well past RetainIntermediateDays. If Failed were treated as terminal it would age out;
        // because a Failed unit is resumable, its seed must come out Active/retained instead.
        var snapSeed = new RepositorySnapshot(
            SnapshotId: "snap-failed-seed", RepositoryId: repoId, TreeHash: "tf", Generation: 1,
            CreatedAt: now - TimeSpan.FromDays(50));
        await WriteSnapshotAsync(nodeStore, snapSeed);

        var wuFailed = new WorkUnit(
            WorkUnitId: "wu-failed", Goal: "test", BranchId: "branch-failed",
            Status: WorkUnitStatus.Failed,
            CreatedAt: now - TimeSpan.FromDays(49), UpdatedAt: now - TimeSpan.FromDays(45),
            Owner: "test", AssignedAgent: null, SuccessCriteria: null, Metadata: null,
            ParentWorkUnitId: null, DependsOn: [], FileScope: [],
            RepositoryId: repoRegistration.RepositoryId);
        await WriteWorkUnitAsync(nodeStore, wuFailed);

        var policy = new SnapshotRetentionPolicy(
            nodeStore, registry, options: null,
            retentionOptions: new RetentionPolicyOptions { RetainIntermediateDays = RetainIntermediateDays },
            clock: clock);

        var report = await policy.ClassifyAsync();
        var entry = report.Snapshots.Single(e => e.SnapshotId == "snap-failed-seed");

        Assert.Equal(SnapshotRetentionClass.Active, entry.Class);
        Assert.True(entry.Retained, "a resumable Failed work unit's seed must be retained, not aged out");
        Assert.Contains("wu-failed", entry.Reason);
        Assert.Contains("snap-failed-seed", report.RetainedSnapshotIds);
    }

    /// <summary>
    /// Slice 6.5 Part 2 — WorkUnit.SeedSnapshotId, once persisted, pins the EXACT generation a
    /// branch was seeded from, rather than the timestamp proxy's "latest snapshot with CreatedAt at
    /// or before the work unit's own CreatedAt" approximation. Proves the two can disagree and the
    /// persisted field wins: an older generation is deliberately named as the pinned seed even
    /// though a newer generation also predates the work unit and would otherwise be the proxy's
    /// pick — only the pinned generation should come out Active; the intervening generation the
    /// proxy would have chosen instead gets no such protection and is left an ordinary Intermediate.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_uses_persisted_SeedSnapshotId_to_pin_the_exact_seed_generation_over_the_timestamp_proxy()
    {
        var (nodeStore, registry) = await NewStoreAsync();

        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);

        var repoPath = @"C:\fake\repoSeedPin";
        var repoRegistration = await registry.RegisterAsync(repoPath, "Repo Seed Pin");
        var repoId = Path.GetFullPath(repoPath);

        // Gen 0 — the TRUE seed generation, 60 days ago. This is what WorkUnit.SeedSnapshotId names.
        var snapTrueSeed = new RepositorySnapshot(
            SnapshotId: "snap-true-seed", RepositoryId: repoId, TreeHash: "t0", Generation: 0,
            CreatedAt: now - TimeSpan.FromDays(60));
        await WriteSnapshotAsync(nodeStore, snapTrueSeed);

        // Gen 1 — a LATER generation (20 days ago) that still predates the work unit's own
        // CreatedAt (10 days ago). Absent SeedSnapshotId, the timestamp proxy would pick THIS one
        // (latest snapshot with CreatedAt <= WorkUnit.CreatedAt) — the exact disagreement this test
        // exists to prove the pinned field resolves correctly.
        var snapProxyWouldPick = new RepositorySnapshot(
            SnapshotId: "snap-proxy-would-pick", RepositoryId: repoId, TreeHash: "t1", Generation: 1,
            CreatedAt: now - TimeSpan.FromDays(20));
        await WriteSnapshotAsync(nodeStore, snapProxyWouldPick);

        var wuPinned = new WorkUnit(
            WorkUnitId: "wu-seed-pinned", Goal: "test", BranchId: "branch-seed-pinned",
            Status: WorkUnitStatus.Executing,
            CreatedAt: now - TimeSpan.FromDays(10), UpdatedAt: now - TimeSpan.FromDays(10),
            Owner: "test", AssignedAgent: null, SuccessCriteria: null, Metadata: null,
            ParentWorkUnitId: null, DependsOn: [], FileScope: [],
            RepositoryId: repoRegistration.RepositoryId,
            SeedSnapshotId: "snap-true-seed");
        await WriteWorkUnitAsync(nodeStore, wuPinned);

        // Gen 2 — the repository's actual current head, newer than both generations above, so
        // "snap-proxy-would-pick" (Gen 1) isn't accidentally classified Active as the head itself —
        // it needs to stand or fall purely on whether anything pins it as a branch seed.
        var snapHead = new RepositorySnapshot(
            SnapshotId: "snap-seed-pin-head", RepositoryId: repoId, TreeHash: "t2", Generation: 2,
            CreatedAt: now - TimeSpan.FromDays(1));
        await WriteSnapshotAsync(nodeStore, snapHead);

        var policy = new SnapshotRetentionPolicy(
            nodeStore, registry, options: null,
            retentionOptions: new RetentionPolicyOptions { RetainIntermediateDays = RetainIntermediateDays },
            clock: clock);

        var report = await policy.ClassifyAsync();
        var byId = report.Snapshots.ToDictionary(e => e.SnapshotId);

        Assert.Equal(SnapshotRetentionClass.Active, byId["snap-true-seed"].Class);
        Assert.True(byId["snap-true-seed"].Retained);
        Assert.Contains("wu-seed-pinned", byId["snap-true-seed"].Reason);
        Assert.Contains("SeedSnapshotId", byId["snap-true-seed"].Reason);

        // The generation the timestamp proxy would have picked instead gets no Active protection
        // from this work unit — nothing else pins it either, so it's an ordinary Intermediate
        // (well within the 30-day retention window at only 20 days old, so still retained, just not
        // via the Active class).
        Assert.Equal(SnapshotRetentionClass.Intermediate, byId["snap-proxy-would-pick"].Class);
    }

    [Fact]
    public async Task ClassifyAsync_classifies_a_malformed_snapshot_node_as_Active_with_an_anomaly_reason()
    {
        var (nodeStore, registry) = await NewStoreAsync();
        await nodeStore.WriteNodeAsync(StudioNodeKind.RepositorySnapshotV1, "snap-malformed", "{ this is not valid json");

        var policy = new SnapshotRetentionPolicy(
            nodeStore, registry, options: null, retentionOptions: new RetentionPolicyOptions(),
            clock: new FixedTimeProvider(DateTimeOffset.UtcNow));

        var report = await policy.ClassifyAsync();

        var entry = Assert.Single(report.Snapshots);
        Assert.Equal("snap-malformed", entry.SnapshotId);
        Assert.Equal(SnapshotRetentionClass.Active, entry.Class);
        Assert.True(entry.Retained);
        Assert.Contains("Anomaly", entry.Reason);
        Assert.Contains("snap-malformed", report.RetainedSnapshotIds);
    }

    [Fact]
    public async Task ClassifyAsync_reports_an_anomaly_when_a_work_unit_node_fails_to_parse_but_demotes_nothing()
    {
        var (nodeStore, registry) = await NewStoreAsync();
        var now = DateTimeOffset.UtcNow;

        var repoPath = @"C:\fake\repoAnomaly";
        var repoId = Path.GetFullPath(repoPath);
        var snapshot = new RepositorySnapshot(
            SnapshotId: "snap-only", RepositoryId: repoId, TreeHash: "t0", Generation: 0,
            CreatedAt: now - TimeSpan.FromDays(5), Source: "Bootstrap");
        await WriteSnapshotAsync(nodeStore, snapshot);

        await nodeStore.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "wu-malformed", "{ not valid json either");

        var policy = new SnapshotRetentionPolicy(
            nodeStore, registry, options: null, retentionOptions: new RetentionPolicyOptions(),
            clock: new FixedTimeProvider(now));

        var report = await policy.ClassifyAsync();

        // The malformed work unit contributes nothing (can't be correlated to any generation), but
        // the bootstrap snapshot is still correctly Pinned — fail-safe bias never silently
        // demotes an unrelated, cleanly-readable snapshot because of it.
        var entry = Assert.Single(report.Snapshots);
        Assert.Equal(SnapshotRetentionClass.Pinned, entry.Class);
        Assert.Single(report.Anomalies);
        Assert.Contains("work-unit node(s) failed to parse", report.Anomalies[0]);
    }

    [Fact]
    public async Task ClassifyAsync_honors_admin_pins()
    {
        var (nodeStore, registry) = await NewStoreAsync();
        var now = DateTimeOffset.UtcNow;

        var repoPath = @"C:\fake\repoPin";
        var repoId = Path.GetFullPath(repoPath);
        // Old enough that, absent the admin pin, this would be an expired Intermediate.
        var snapshot = new RepositorySnapshot(
            SnapshotId: "snap-admin-pinned", RepositoryId: repoId, TreeHash: "t0", Generation: 0,
            CreatedAt: now - TimeSpan.FromDays(200));
        await WriteSnapshotAsync(nodeStore, snapshot);

        var policy = new SnapshotRetentionPolicy(
            nodeStore, registry, options: null,
            retentionOptions: new RetentionPolicyOptions { AdminPinnedSnapshotIds = ["snap-admin-pinned"] },
            clock: new FixedTimeProvider(now));

        var report = await policy.ClassifyAsync();

        var entry = Assert.Single(report.Snapshots);
        Assert.Equal(SnapshotRetentionClass.Pinned, entry.Class);
        Assert.Contains("Admin pin", entry.Reason);
    }
}
