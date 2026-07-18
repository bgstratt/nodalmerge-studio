using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Layer 2 L2.1 (plans/room-persistence-bloat.md) — the durable, studio-owned LOCAL append log that
/// the four high-volume peer-local kinds now use instead of the CRDT room/sync graph. Contract tests
/// run against both implementations; routing tests prove each owning service persists to the local
/// log and rebuilds from it.
/// </summary>
[Trait("Category", "Integration")]
public class StudioLocalLogStoreTests
{
    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "studio-locallog-tests", Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> Stores()
    {
        yield return [new InMemoryStudioNodeStore()];
        yield return [new FileStudioLocalLogStore(new StudioLocalLogOptions { Directory = NewTempDir() })];
    }

    // ── contract ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Append_then_Get_round_trips(IStudioLocalLogStore store)
    {
        await store.AppendAsync("k1", "id1", "payload-1", DateTimeOffset.UtcNow);

        Assert.Equal("payload-1", await store.GetAsync("k1", "id1"));
        Assert.Null(await store.GetAsync("k1", "missing"));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Append_upserts_by_id(IStudioLocalLogStore store)
    {
        var t = DateTimeOffset.UtcNow;
        await store.AppendAsync("k1", "id1", "v1", t);
        await store.AppendAsync("k1", "id1", "v2", t.AddSeconds(1));

        Assert.Equal("v2", await store.GetAsync("k1", "id1"));
        var all = await store.ReadAllAsync("k1");
        Assert.Equal(("id1", "v2"), Assert.Single(all));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ReadAll_is_scoped_to_kind(IStudioLocalLogStore store)
    {
        await store.AppendAsync("kA", "a", "pa", DateTimeOffset.UtcNow);
        await store.AppendAsync("kB", "b", "pb", DateTimeOffset.UtcNow);

        var a = await store.ReadAllAsync("kA");
        Assert.Equal("a", Assert.Single(a).Id);
        Assert.Empty(await store.ReadAllAsync("kC"));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task PruneOlderThan_removes_old_keeps_new(IStudioLocalLogStore store)
    {
        var old = DateTimeOffset.UtcNow.AddHours(-2);
        var recent = DateTimeOffset.UtcNow;
        await store.AppendAsync("k", "old", "po", old);
        await store.AppendAsync("k", "new", "pn", recent);

        var removed = await store.PruneOlderThanAsync("k", DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(1, removed);
        var all = await store.ReadAllAsync("k");
        Assert.Equal("new", Assert.Single(all).Id);
    }

    [Fact]
    public async Task FileStore_persists_across_instances()
    {
        var dir = NewTempDir();
        var s1 = new FileStudioLocalLogStore(new StudioLocalLogOptions { Directory = dir });
        await s1.AppendAsync("k", "id", "payload", DateTimeOffset.UtcNow);

        var s2 = new FileStudioLocalLogStore(new StudioLocalLogOptions { Directory = dir });
        Assert.Equal("payload", await s2.GetAsync("k", "id"));
    }

    // ── routing: the owning services persist to the local log and rehydrate from it ──────────────

    [Fact]
    public async Task ExecutionEventStreamService_persists_to_local_log_and_rehydrates()
    {
        var store = new InMemoryStudioNodeStore();
        var svc = new ExecutionEventStreamService(store);
        await svc.AppendAsync("SES-1", "WU-1", ExecutionEventKind.SessionStarted, new { hello = "world" }, eventId: "EVT-1");

        var rows = await ((IStudioLocalLogStore)store).ReadAllAsync(StudioNodeKind.ExecutionEventV1);
        Assert.Single(rows);

        var svc2 = new ExecutionEventStreamService(store);
        await svc2.RehydrateAsync();
        Assert.Equal("EVT-1", Assert.Single(await svc2.GetSessionEventsAsync("SES-1")).EventId);
    }

    [Fact]
    public async Task ConversationLogService_persists_to_local_log_and_rehydrates()
    {
        var store = new InMemoryStudioNodeStore();
        var svc = new ConversationLogService(store);
        await svc.RecordAsync(new ConversationLogEntry(
            "LOG-1", "WU-1", "a", "worker", null, 1, "hi", [], [], "end_turn", DateTimeOffset.UtcNow));

        Assert.Single(await ((IStudioLocalLogStore)store).ReadAllAsync(StudioNodeKind.ConversationLogV1));

        var svc2 = new ConversationLogService(store);
        await svc2.RehydrateAsync();
        Assert.Single(await svc2.GetEntriesAsync("WU-1"));
    }

    [Fact]
    public async Task OrchestrationDecisionLogService_persists_to_local_log_and_rehydrates()
    {
        var store = new InMemoryStudioNodeStore();
        var svc = new OrchestrationDecisionLogService(store, new ExecutionEventStreamService(store));
        await svc.RecordAsync("WU-1", "orch", PipelineStage.Plan, "{}", OrchestrationAction.SpawnWorker, ["c1"], "reason");

        Assert.Single(await ((IStudioLocalLogStore)store).ReadAllAsync(StudioNodeKind.OrchestrationEventV1));

        var svc2 = new OrchestrationDecisionLogService(store, new ExecutionEventStreamService(store));
        await svc2.RehydrateAsync();
        Assert.Single(await svc2.GetEventsAsync("WU-1"));
    }
}
