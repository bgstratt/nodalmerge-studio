using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/first-class-goals-and-materialization.md Phase 2 — a peer's goal replicates in as a
/// repo-scoped GoalNode even when its root work unit isn't in this peer's work-unit view. The
/// WorkspacePathways projection must emit a readable GoalStarted node from that GoalNode, so a peer's
/// goal shows with its goal text instead of only bare work/proposal ids.
/// </summary>
[Trait("Category", "Integration")]
public class PathwaysPeerGoalTests
{
    [Fact]
    public async Task Pathways_renders_a_replicated_goal_with_no_local_work_unit()
    {
        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();
        var goals = app.Services.GetRequiredService<IGoalNodeService>();

        // Simulate a peer's goal that replicated in as a GoalNode — no local root work unit exists.
        await goals.RecordAsync(new GoalNode(
            GoalId: "WU-peer-1",
            Goal: "Peer's goal text",
            WorkUnitId: "WU-peer-1",
            BranchId: "b-peer",
            Status: GoalStatus.Exploring,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Owner: "user"));

        var doc = JsonDocument.Parse(
            await client.GetStringAsync("/studio/projections/WorkspacePathways?level=Normal")).RootElement;
        var nodes = doc.GetProperty("data").GetProperty("nodes").EnumerateArray().ToList();

        var goalNode = nodes.FirstOrDefault(n =>
            n.GetProperty("kind").GetString() == "GoalStarted"
            && n.GetProperty("workUnitId").GetString() == "WU-peer-1");

        Assert.Equal(JsonValueKind.Object, goalNode.ValueKind); // the peer goal is present as a node
        Assert.Equal("Peer's goal text", goalNode.GetProperty("summary").GetString());
    }
}
