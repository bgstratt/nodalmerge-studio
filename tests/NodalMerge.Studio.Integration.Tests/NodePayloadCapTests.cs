using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Layer 2 L2.2 / L2.2b (plans/room-persistence-bloat.md) — uncapped free-text fields must be
/// truncated before they are serialized into a node payload. AssistantText / tool-call InputJson
/// bloat the peer-local `studio` room; ArtifactRef.Body and DecisionNode.Rationale ride
/// RepoScopedKinds, so an uncapped body would replicate to every peer. Cap is consistent with
/// ConversationLogService's pre-existing 20 KB tool-result cap.
/// </summary>
[Trait("Category", "Integration")]
public class NodePayloadCapTests
{
    private const int Cap = 20_000;
    private const string Marker = "...truncated";

    private static string Big(int len) => new('x', len);

    // ── L2.2 — ConversationLog AssistantText / InputJson ──────────────────────

    [Fact]
    public async Task RecordAsync_caps_oversized_AssistantText_with_marker()
    {
        var svc = new ConversationLogService(new InMemoryStudioNodeStore());
        var huge = Big(Cap + 5_000);

        var stored = await svc.RecordAsync(MakeEntry(assistantText: huge));

        Assert.NotNull(stored.AssistantText);
        Assert.Equal(Cap + Marker.Length, stored.AssistantText!.Length);
        Assert.EndsWith(Marker, stored.AssistantText);
        Assert.StartsWith(huge[..Cap], stored.AssistantText);
    }

    [Fact]
    public async Task RecordAsync_caps_oversized_tool_call_InputJson_with_marker()
    {
        var svc = new ConversationLogService(new InMemoryStudioNodeStore());
        var huge = Big(Cap + 5_000);
        var entry = MakeEntry(toolCalls: [new ConversationToolCall("tu-1", "Write", huge)]);

        var stored = await svc.RecordAsync(entry);

        var call = Assert.Single(stored.ToolCalls);
        Assert.Equal(Cap + Marker.Length, call.InputJson.Length);
        Assert.EndsWith(Marker, call.InputJson);
    }

    [Fact]
    public async Task RecordAsync_leaves_small_fields_untouched()
    {
        var svc = new ConversationLogService(new InMemoryStudioNodeStore());
        var entry = MakeEntry(assistantText: "short reasoning",
            toolCalls: [new ConversationToolCall("tu-1", "Read", "{\"path\":\"a.cs\"}")]);

        var stored = await svc.RecordAsync(entry);

        Assert.Equal("short reasoning", stored.AssistantText);
        Assert.DoesNotContain(Marker, stored.ToolCalls[0].InputJson);
    }

    // ── L2.2b — replicated (repo-scoped) bodies ───────────────────────────────

    [Fact]
    public async Task RecordAsync_caps_oversized_ArtifactRef_Body_with_marker()
    {
        var svc = new ArtifactLineageService(new InMemoryStudioNodeStore());
        var huge = Big(Cap + 5_000);

        var stored = await svc.RecordAsync(new ArtifactRef(
            "ART-1", ArtifactType.Plan, null, ArtifactStatus.Active, DateTimeOffset.UtcNow,
            "WU-1", null, Title: "plan", Body: huge));

        Assert.NotNull(stored.Body);
        Assert.Equal(Cap + Marker.Length, stored.Body!.Length);
        Assert.EndsWith(Marker, stored.Body);
    }

    [Fact]
    public async Task RecordAsync_caps_oversized_Decision_Rationale_with_marker()
    {
        var svc = new DecisionNodeService(new InMemoryStudioNodeStore());
        var huge = Big(Cap + 5_000);

        var stored = await svc.RecordAsync(new DecisionNode(
            "DEC-1", "WU-1", "MP-1", DecisionOutcome.Accepted, "reviewer", "model", "provider",
            0.8, Rationale: huge, DecidedAt: DateTimeOffset.UtcNow));

        Assert.NotNull(stored.Rationale);
        Assert.Equal(Cap + Marker.Length, stored.Rationale!.Length);
        Assert.EndsWith(Marker, stored.Rationale);
    }

    private static ConversationLogEntry MakeEntry(
        string? assistantText = "text",
        IReadOnlyList<ConversationToolCall>? toolCalls = null) =>
        new(
            LogId: $"conv-{Guid.NewGuid():N}",
            WorkUnitId: "WU-1",
            AgentId: "agent-1",
            AgentRole: "worker",
            TaskId: null,
            CycleNumber: 1,
            AssistantText: assistantText,
            ToolCalls: toolCalls ?? [],
            ToolResults: [],
            StopReason: "end_turn",
            OccurredAt: DateTimeOffset.UtcNow);
}
