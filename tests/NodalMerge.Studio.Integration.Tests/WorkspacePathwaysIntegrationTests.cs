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
/// plans/pathways-workspace-history.md — host-level proof that the WorkspacePathways projection
/// works through the real HTTP route with the real service graph: enum route parsing,
/// ProjectionManager's DI-injected event stream, event-sourced node derivation from the events
/// InMemoryMergeService actually appends (not test-seeded ones), and the response envelope the
/// webview parses. The unit tests cover the derivation logic against fakes; this covers the
/// wiring those fakes can't.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspacePathwaysIntegrationTests
{
    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    // ReviewAsync updates the proposal's lineage artifact — normally recorded by
    // MergeCommandService.ProposeAsync; these tests propose via IMergeService directly (no
    // diff machinery needed), so they record the same ArtifactRef the command service would.
    private static Task RecordProposalArtifactAsync(WebApplication app, string proposalId, WorkUnit wu) =>
        app.Services.GetRequiredService<IArtifactLineageService>().RecordAsync(new ArtifactRef(
            proposalId, ArtifactType.MergeProposal, wu.WorkUnitId, ArtifactStatus.Active,
            DateTimeOffset.UtcNow, wu.WorkUnitId, null));

    [Fact]
    public async Task Reviewed_and_applied_proposal_reaches_the_projection_route_as_an_Integration_node()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var merge        = app.Services.GetRequiredService<IMergeService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Ship the pathways feature", "user");

        var proposal = await merge.ProposeAsync(new MergeProposal(
            ProposalId: "MP-INT-1",
            SourceBranch: wu.BranchId,
            TargetBranch: "main",
            Goal: wu.Goal,
            Summary: "pathways integration test proposal",
            ChangeDescription: "adds a file",
            VerificationResults: null,
            RollbackPlan: null,
            Confidence: 0.9,
            Status: MergeProposalStatus.ReadyForReview,
            WorkUnitId: wu.WorkUnitId,
            AgentId: "agent-int-1",
            Model: "test-model",
            SessionId: "session-int-1"));

        await RecordProposalArtifactAsync(app, proposal.ProposalId, wu);
        await merge.ReviewAsync(proposal.ProposalId, MergeProposalStatus.Approved);
        await merge.ApplyAsync(proposal.ProposalId);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/studio/projections/WorkspacePathways?level=Normal");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("v1", doc.GetProperty("contractVersion").GetString());
        Assert.Equal("WorkspacePathways", doc.GetProperty("projectionType").GetString());

        var nodes = doc.GetProperty("data").GetProperty("nodes").EnumerateArray().ToList();
        var edges = doc.GetProperty("data").GetProperty("edges").EnumerateArray().ToList();

        var goalNode = Assert.Single(nodes, n => n.GetProperty("kind").GetString() == "GoalStarted");
        Assert.Equal("Human", goalNode.GetProperty("actorKind").GetString());
        Assert.Equal($"goal:{wu.WorkUnitId}", goalNode.GetProperty("nodeId").GetString());

        var integration = Assert.Single(nodes, n => n.GetProperty("kind").GetString() == "Integration");
        Assert.Equal("proposal:MP-INT-1:integration", integration.GetProperty("nodeId").GetString());
        // Event-sourced: MergeApplied's OccurredAt, not a DiffGeneratedAt approximation — and the
        // reviewer identity ReviewAsync recorded ("user" default for the human path).
        Assert.Equal("user", integration.GetProperty("reviewedBy").GetString());
        Assert.Equal("test-model", integration.GetProperty("actorModel").GetString());

        Assert.Contains(edges, e =>
            e.GetProperty("fromNodeId").GetString() == $"goal:{wu.WorkUnitId}" &&
            e.GetProperty("toNodeId").GetString() == "proposal:MP-INT-1:integration");
    }

    [Fact]
    public async Task Superseded_after_merge_keeps_the_Integration_node_through_the_real_event_log()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var merge        = app.Services.GetRequiredService<IMergeService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Reconciliation constituent", "user");
        await merge.ProposeAsync(new MergeProposal(
            "MP-INT-2", wu.BranchId, "main", wu.Goal, "constituent", "desc", null, null, null,
            MergeProposalStatus.ReadyForReview, WorkUnitId: wu.WorkUnitId, SessionId: "session-int-2"));

        await RecordProposalArtifactAsync(app, "MP-INT-2", wu);
        await merge.ReviewAsync("MP-INT-2", MergeProposalStatus.Approved);
        await merge.ApplyAsync("MP-INT-2");
        // The retroactive-history-rewrite scenario: reconciliation supersedes an already-merged
        // constituent. State-derived, the Integration node would vanish; event-sourced it stays.
        await merge.SupersedeAsync("MP-INT-2", "MP-RECONCILED-9");

        var client = app.GetTestClient();
        var response = await client.GetAsync("/studio/projections/WorkspacePathways");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var nodes = doc.GetProperty("data").GetProperty("nodes").EnumerateArray().ToList();
        var edges = doc.GetProperty("data").GetProperty("edges").EnumerateArray().ToList();

        Assert.Contains(nodes, n => n.GetProperty("nodeId").GetString() == "proposal:MP-INT-2:integration");
        Assert.Contains(nodes, n => n.GetProperty("nodeId").GetString() == "proposal:MP-INT-2:superseded");
        Assert.Contains(edges, e =>
            e.GetProperty("fromNodeId").GetString() == "proposal:MP-INT-2:integration" &&
            e.GetProperty("toNodeId").GetString() == "proposal:MP-INT-2:superseded");
    }
}
