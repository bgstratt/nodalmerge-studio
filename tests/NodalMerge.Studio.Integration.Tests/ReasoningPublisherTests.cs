using System.Text;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Layer 2 L2.3 (plans/room-persistence-bloat.md) — publishing a bounded, peer-visible reasoning
/// transcript to the CAS + a repo-scoped ConversationRef, and linking a DecisionV1 to it so a peer
/// can trace decision → reasoning.
/// </summary>
[Trait("Category", "Integration")]
public class ReasoningPublisherTests
{
    private static async Task SeedConversationAsync(ConversationLogService convo, string workUnitId)
    {
        await convo.RecordAsync(new ConversationLogEntry(
            "LOG-1", workUnitId, "agent", "worker", null, 1,
            "Looking at the auth module.",
            [new ConversationToolCall("tu-1", "Read", "{\"path\":\"auth.cs\"}")],
            [new ConversationToolResult("tu-1", "SECRET-FILE-BODY-should-not-be-published", false)],
            "tool_use", DateTimeOffset.UtcNow));
        await convo.RecordAsync(new ConversationLogEntry(
            "LOG-2", workUnitId, "agent", "worker", null, 2,
            "Refactored the token check\nadded a guard clause.",
            [new ConversationToolCall("tu-2", "Edit", "{\"path\":\"auth.cs\",\"edit\":\"...\"}")],
            [new ConversationToolResult("tu-2", "ok", false)],
            "end_turn", DateTimeOffset.UtcNow.AddSeconds(1)));
    }

    [Fact]
    public async Task PublishAsync_writes_blob_and_repo_scoped_ref_with_reasoning_and_tool_intent()
    {
        var store = new InMemoryStudioNodeStore();
        var blobs = new InMemoryBlobStoreProvider();
        var convo = new ConversationLogService(store);
        await SeedConversationAsync(convo, "WU-1");

        var publisher = new ReasoningPublisherService(convo, blobs, store);
        var cref = await publisher.PublishAsync("WU-1", "SES-1", "repo-1", decisionId: "DEC-1");

        Assert.NotNull(cref);
        Assert.Equal(2, cref!.CycleCount);
        Assert.Equal("DEC-1", cref.DecisionId);
        Assert.Equal("repo-1", cref.RepositoryId);

        // ref node persisted under the repo-scoped ConversationRefV1 kind
        Assert.NotNull(await store.ReadNodeAsync(StudioNodeKind.ConversationRefV1, cref.RefId));

        // blob persisted; it carries reasoning + tool intent, NOT tool-result bodies
        var blob = await blobs.TryGetBlobAsync(cref.TranscriptBlobHash);
        Assert.True(blob.Found);
        var json = Encoding.UTF8.GetString(blob.Bytes!);
        var transcript = JsonSerializer.Deserialize<PublishedReasoningTranscript>(json)!;
        Assert.Equal(2, transcript.Cycles.Count);
        Assert.Equal("Looking at the auth module.", transcript.Cycles[0].AssistantText);
        Assert.Equal("Read", transcript.Cycles[0].ToolCalls[0].Name);
        Assert.DoesNotContain("SECRET-FILE-BODY-should-not-be-published", json);
    }

    [Fact]
    public async Task PublishAsync_label_is_first_line_of_last_cycle_assistant_text()
    {
        var store = new InMemoryStudioNodeStore();
        var convo = new ConversationLogService(store);
        await SeedConversationAsync(convo, "WU-1");

        var cref = await new ReasoningPublisherService(convo, new InMemoryBlobStoreProvider(), store)
            .PublishAsync("WU-1", null, "repo-1");

        Assert.Equal("Refactored the token check", cref!.Label);
    }

    [Fact]
    public async Task PublishAsync_returns_null_when_no_conversation_history()
    {
        var store = new InMemoryStudioNodeStore();
        var blobs = new InMemoryBlobStoreProvider();

        var cref = await new ReasoningPublisherService(new ConversationLogService(store), blobs, store)
            .PublishAsync("WU-empty", null, "repo-1");

        Assert.Null(cref);
        Assert.Equal(0, blobs.Count);
    }

    [Fact]
    public async Task DecisionNodeService_with_publisher_sets_ReasoningRefId_and_links_ref_to_decision()
    {
        var store = new InMemoryStudioNodeStore();
        var convo = new ConversationLogService(store);
        await SeedConversationAsync(convo, "WU-1");
        var publisher = new ReasoningPublisherService(convo, new InMemoryBlobStoreProvider(), store);
        var svc = new DecisionNodeService(store, workUnits: null, reasoningPublisher: publisher);

        var decision = await svc.RecordAsync(new DecisionNode(
            "DEC-1", "WU-1", "MP-1", DecisionOutcome.Accepted, "reviewer", "model", "provider",
            0.9, "approved", DateTimeOffset.UtcNow, SessionId: "SES-1", RepositoryId: "repo-1"));

        Assert.NotNull(decision.ReasoningRefId);
        var refJson = await store.ReadNodeAsync(StudioNodeKind.ConversationRefV1, decision.ReasoningRefId!);
        Assert.NotNull(refJson);
        var cref = JsonSerializer.Deserialize<ConversationRef>(refJson!)!;
        Assert.Equal("DEC-1", cref.DecisionId);
        Assert.Equal("WU-1", cref.WorkUnitId);
    }
}
