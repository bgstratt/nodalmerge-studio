using System.Net;
using System.Net.Http.Json;
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
/// The read-only aggregate GET /studio/sessions/{id}/plan-tree that the Pathways → Plan view
/// consumes. Builds a 2-level recursive plan directly (root → one Compound sub-planner → two leaf
/// grandchildren, one providing a contract the other consumes, with a dependsOn between them) and
/// asserts the endpoint returns the nested nodes with correct depth/kind, the resolved slice
/// metadata (provides/consumes joined from the parent plan), and both a `depends` and a `contract`
/// edge — none of which is on the plain /workunits projection.
/// </summary>
[Trait("Category", "Integration")]
public class PlanTreeRestEndpointTests
{
    [Fact]
    public async Task PlanTree_returns_nested_nodes_with_kind_and_contract_edges()
    {
        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifactCommands = app.Services.GetRequiredService<IArtifactCommandService>();
        var sessions = app.Services.GetRequiredService<IExecutionSessionService>();

        // root → compound sub-planner "sub" → two leaf grandchildren "api" (provides c-user) and
        // "ui" (consumes c-user, dependsOn api).
        var root = await orchestrator.CreateWorkUnitAsync("Build the app", "test");
        var sub = await orchestrator.CreateWorkUnitAsync(
            "Build the backend subsystem", "test",
            parentWorkUnitId: root.WorkUnitId, sliceId: "sub", sliceKind: PlanSliceKind.Compound);
        var api = await orchestrator.CreateWorkUnitAsync(
            "Create the user endpoint", "test",
            parentWorkUnitId: sub.WorkUnitId, sliceId: "api", fileScope: ["src/Api.cs"]);
        var ui = await orchestrator.CreateWorkUnitAsync(
            "Create the user page", "test",
            parentWorkUnitId: sub.WorkUnitId, sliceId: "ui", fileScope: ["src/Ui.cs"],
            dependsOn: [api.WorkUnitId]);

        // Root plan: one compound slice. Sub-plan: the two leaf slices + the shared contract.
        await artifactCommands.RecordPlanAsync(root.WorkUnitId, JsonSerializer.Serialize(new PlanDocument(
            Slices: [new PlanSlice("sub", "Build the backend subsystem", ["src/**"], [], ["decompose"],
                Kind: PlanSliceKind.Compound)])));
        await artifactCommands.RecordPlanAsync(sub.WorkUnitId, JsonSerializer.Serialize(new PlanDocument(
            Slices:
            [
                new PlanSlice("api", "Create the user endpoint", ["src/Api.cs"], [], ["write Api.cs"],
                    Provides: ["c-user"]),
                new PlanSlice("ui", "Create the user page", ["src/Ui.cs"], ["api"], ["write Ui.cs"],
                    Consumes: ["c-user"]),
            ],
            Contracts: [new PlanContract("c-user", "GET /user → { id, name }", ["GET /user → { id, name }"])])));

        var session = await sessions.CreateAsync(root.WorkUnitId, "{}", []);

        var res = await client.GetAsync($"/studio/sessions/{session.SessionId}/plan-tree");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var tree = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(root.WorkUnitId, tree.GetProperty("rootWorkUnitId").GetString());

        var nodes = tree.GetProperty("nodes").EnumerateArray()
            .ToDictionary(n => n.GetProperty("workUnitId").GetString()!);
        Assert.Equal(4, nodes.Count);

        // Depth + kind: root=0/leaf, sub=1/compound, grandchildren=2/leaf.
        Assert.Equal(0, nodes[root.WorkUnitId].GetProperty("depth").GetInt32());
        Assert.Equal(1, nodes[sub.WorkUnitId].GetProperty("depth").GetInt32());
        Assert.Equal(2, nodes[api.WorkUnitId].GetProperty("depth").GetInt32());
        Assert.Equal("compound", nodes[sub.WorkUnitId].GetProperty("kind").GetString());
        Assert.Equal("leaf", nodes[api.WorkUnitId].GetProperty("kind").GetString());

        // Slice metadata joined from the parent plan: provides/consumes + authored slice goal.
        Assert.Equal("c-user", nodes[api.WorkUnitId].GetProperty("provides").EnumerateArray().Single().GetString());
        Assert.Equal("c-user", nodes[ui.WorkUnitId].GetProperty("consumes").EnumerateArray().Single().GetString());
        Assert.Equal("Create the user endpoint", nodes[api.WorkUnitId].GetProperty("sliceGoal").GetString());

        var edges = tree.GetProperty("edges").EnumerateArray().ToList();
        bool HasEdge(string from, string to, string kind) => edges.Any(e =>
            e.GetProperty("from").GetString() == from &&
            e.GetProperty("to").GetString() == to &&
            e.GetProperty("kind").GetString() == kind);

        Assert.True(HasEdge(root.WorkUnitId, sub.WorkUnitId, "parent"));
        Assert.True(HasEdge(sub.WorkUnitId, api.WorkUnitId, "parent"));
        Assert.True(HasEdge(api.WorkUnitId, ui.WorkUnitId, "depends"));   // ui dependsOn api
        Assert.True(HasEdge(api.WorkUnitId, ui.WorkUnitId, "contract"));  // api provides → ui consumes

        var contractEdge = edges.Single(e => e.GetProperty("kind").GetString() == "contract");
        Assert.Equal("c-user", contractEdge.GetProperty("contractId").GetString());
        Assert.Equal("c-user", tree.GetProperty("contracts").EnumerateArray().Single().GetProperty("contractId").GetString());
    }

    [Fact]
    public async Task PlanTree_returns_404_for_unknown_session()
    {
        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();

        var res = await client.GetAsync("/studio/sessions/does-not-exist/plan-tree");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
