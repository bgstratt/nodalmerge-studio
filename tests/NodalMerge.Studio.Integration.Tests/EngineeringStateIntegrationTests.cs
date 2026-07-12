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
/// plans/harness-hosting-architecture.md Phase A.2 — host-level proof that the EngineeringState
/// projection works through the real HTTP route with the real service graph (applied-proposal
/// rule driven by the real IMergeService, real IArtifactCommandService recording). The unit tests
/// in NodalMerge.Studio.Projections.Tests cover the fold logic against fakes; this covers the
/// wiring those fakes can't.
/// </summary>
[Trait("Category", "Integration")]
public class EngineeringStateIntegrationTests
{
    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    private static async Task<MergeProposal> ProposeAndApplyAsync(WebApplication app, WorkUnit wu, string proposalId)
    {
        var merge = app.Services.GetRequiredService<IMergeService>();
        await app.Services.GetRequiredService<IArtifactLineageService>().RecordAsync(new ArtifactRef(
            proposalId, ArtifactType.MergeProposal, wu.WorkUnitId, ArtifactStatus.Active,
            DateTimeOffset.UtcNow, wu.WorkUnitId, null));

        await merge.ProposeAsync(new MergeProposal(
            proposalId, wu.BranchId, "main", wu.Goal, "summary", "desc", null, null, 0.9,
            MergeProposalStatus.ReadyForReview, WorkUnitId: wu.WorkUnitId, SessionId: "session-" + proposalId));
        await merge.ReviewAsync(proposalId, MergeProposalStatus.Approved);
        return await merge.ApplyAsync(proposalId);
    }

    [Fact]
    public async Task Decision_on_a_merged_work_unit_reaches_the_projection_route_as_a_current_fact()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifactCommands = app.Services.GetRequiredService<IArtifactCommandService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Pick an ORM", "user");
        var decision = await artifactCommands.RecordAsync(wu.WorkUnitId, "Decision", "Use EF Core", "Reason: team familiarity");
        await ProposeAndApplyAsync(app, wu, "MP-EState-1");

        var client = app.GetTestClient();
        var response = await client.GetAsync("/studio/projections/EngineeringState?level=Normal");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("EngineeringState", doc.GetProperty("projectionType").GetString());

        var facts = doc.GetProperty("data").GetProperty("facts").EnumerateArray().ToList();
        var fact = Assert.Single(facts, f => f.GetProperty("artifactId").GetString() == decision.ArtifactId);
        Assert.True(fact.GetProperty("isCurrent").GetBoolean());
        Assert.Empty(fact.GetProperty("supersededBy").EnumerateArray());
    }

    [Fact]
    public async Task Decision_on_an_unmerged_work_unit_is_excluded_from_the_projection_route()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifactCommands = app.Services.GetRequiredService<IArtifactCommandService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Pick a cache", "user");
        var decision = await artifactCommands.RecordAsync(wu.WorkUnitId, "Decision", "Use Redis", "Reason: TTL support");
        // No proposal ever proposed/applied for this work unit — never promoted.

        var client = app.GetTestClient();
        var response = await client.GetAsync("/studio/projections/EngineeringState?level=Normal");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var facts = doc.GetProperty("data").GetProperty("facts").EnumerateArray().ToList();
        Assert.DoesNotContain(facts, f => f.GetProperty("artifactId").GetString() == decision.ArtifactId);
    }

    [Fact]
    public async Task Supersession_via_the_real_record_route_flips_the_ancestor_to_not_current()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifactCommands = app.Services.GetRequiredService<IArtifactCommandService>();

        var wu1 = await orchestrator.CreateWorkUnitAsync("Pick an ORM", "user");
        var original = await artifactCommands.RecordAsync(wu1.WorkUnitId, "Decision", "Use EF Core", "Reason: familiarity");
        await ProposeAndApplyAsync(app, wu1, "MP-EState-2a");

        var wu2 = await orchestrator.CreateWorkUnitAsync("Revisit ORM choice", "user");
        var successor = await artifactCommands.RecordAsync(
            wu2.WorkUnitId, "Decision", "Use Dapper", "Reason: performance", supersedes: [original.ArtifactId]);
        await ProposeAndApplyAsync(app, wu2, "MP-EState-2b");

        var client = app.GetTestClient();
        var response = await client.GetAsync("/studio/projections/EngineeringState?level=Normal");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var facts = doc.GetProperty("data").GetProperty("facts").EnumerateArray().ToList();

        var originalFact = Assert.Single(facts, f => f.GetProperty("artifactId").GetString() == original.ArtifactId);
        Assert.False(originalFact.GetProperty("isCurrent").GetBoolean());
        Assert.Contains(
            originalFact.GetProperty("supersededBy").EnumerateArray(),
            id => id.GetString() == successor.ArtifactId);

        var successorFact = Assert.Single(facts, f => f.GetProperty("artifactId").GetString() == successor.ArtifactId);
        Assert.True(successorFact.GetProperty("isCurrent").GetBoolean());
    }
}
