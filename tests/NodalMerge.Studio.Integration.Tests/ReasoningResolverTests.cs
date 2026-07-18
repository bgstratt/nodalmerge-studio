using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Layer 2 L2.3 (plans/room-persistence-bloat.md) — the read half of reasoning sharing: local
/// ConversationLog when present, else the peer-published ConversationRef → CAS blob, unified as
/// ConversationLogEntry[] for the drawer.
/// </summary>
[Trait("Category", "Integration")]
public class ReasoningResolverTests
{
    [Fact]
    public async Task GetReasoningAsync_returns_local_entries_when_present()
    {
        var store = new InMemoryStudioNodeStore();
        var convo = new ConversationLogService(store);
        await convo.RecordAsync(new ConversationLogEntry(
            "LOG-1", "WU-1", "agent", "worker", null, 1, "local reasoning", [], [], "end_turn", DateTimeOffset.UtcNow));

        var resolver = new ReasoningResolverService(convo, store, new InMemoryBlobStoreProvider());
        var entries = await resolver.GetReasoningAsync("WU-1");

        Assert.Equal("local reasoning", Assert.Single(entries).AssistantText);
    }

    [Fact]
    public async Task GetReasoningAsync_falls_back_to_published_transcript_when_local_is_empty()
    {
        // Peer A publishes; peer B has the replicated ref + the pulled blob but no local ConversationLog.
        var storeA = new InMemoryStudioNodeStore();
        var blobs = new InMemoryBlobStoreProvider(); // shared content plane (simulates pull-on-demand)
        var convoA = new ConversationLogService(storeA);
        await convoA.RecordAsync(new ConversationLogEntry(
            "LOG-1", "WU-1", "agent", "worker", null, 1, "peer A reasoning",
            [new ConversationToolCall("tu-1", "Edit", "{\"path\":\"x.cs\"}")], [], "end_turn", DateTimeOffset.UtcNow));
        var cref = await new ReasoningPublisherService(convoA, blobs, storeA)
            .PublishAsync("WU-1", "SES-1", "repo-1", decisionId: "DEC-1");

        // Peer B: fresh node store carrying only the replicated ref, empty local ConversationLog.
        var storeB = new InMemoryStudioNodeStore();
        await ((IStudioNodeStore)storeB).WriteNodeAsync(
            StudioNodeKind.ConversationRefV1, cref!.RefId,
            System.Text.Json.JsonSerializer.Serialize(cref), "repo-1");

        var resolverB = new ReasoningResolverService(new ConversationLogService(storeB), storeB, blobs);
        var entries = await resolverB.GetReasoningAsync("WU-1");

        var entry = Assert.Single(entries);
        Assert.Equal("peer A reasoning", entry.AssistantText);
        Assert.Equal("Edit", entry.ToolCalls[0].Name);
        Assert.Equal("(remote)", entry.AgentId);
    }

    [Fact]
    public async Task GetReasoningAsync_returns_empty_when_no_local_and_no_ref()
    {
        var store = new InMemoryStudioNodeStore();
        var resolver = new ReasoningResolverService(
            new ConversationLogService(store), store, new InMemoryBlobStoreProvider());

        Assert.Empty(await resolver.GetReasoningAsync("WU-unknown"));
    }
}
