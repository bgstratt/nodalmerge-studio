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
/// Phase 3 (plans/organizational-knowledge-and-workgroup-scope.md) — the REST contract the Insights
/// "Constraints" sub-tab consumes: GET /studio/constraints returns each applicable constraint labeled
/// with reach + application + enabled, and POST /studio/constraints/{id}/toggle flips the local
/// enabled state without touching the shared constraint.
/// </summary>
[Trait("Category", "Integration")]
public class ConstraintEndpointsTests
{
    [Fact]
    public async Task Constraints_endpoint_labels_scope_and_toggle_flips_enabled()
    {
        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();

        await artifacts.RecordAsync(new ArtifactRef(
            ArtifactId: "c-view", Type: ArtifactType.Constraint, ParentArtifactId: null,
            Status: ArtifactStatus.Approved, CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: "rule", Body: "do X",
            RepositoryId: null, Reach: ArtifactReach.Workgroup));

        var list = await client.GetFromJsonAsync<JsonElement>("/studio/constraints");
        var row = list.EnumerateArray().Single(e => e.GetProperty("artifactId").GetString() == "c-view");
        Assert.Equal("Workgroup", row.GetProperty("reach").GetString());
        Assert.True(row.GetProperty("appliesToAllRepos").GetBoolean());
        Assert.True(row.GetProperty("enabled").GetBoolean()); // enabled by default

        var toggle = await client.PostAsJsonAsync("/studio/constraints/c-view/toggle", new { disabled = true });
        toggle.EnsureSuccessStatusCode();

        var list2 = await client.GetFromJsonAsync<JsonElement>("/studio/constraints");
        var row2 = list2.EnumerateArray().Single(e => e.GetProperty("artifactId").GetString() == "c-view");
        Assert.False(row2.GetProperty("enabled").GetBoolean()); // now locally disabled

        // Re-enable.
        var reon = await client.PostAsJsonAsync("/studio/constraints/c-view/toggle", new { disabled = false });
        reon.EnsureSuccessStatusCode();
        var list3 = await client.GetFromJsonAsync<JsonElement>("/studio/constraints");
        var row3 = list3.EnumerateArray().Single(e => e.GetProperty("artifactId").GetString() == "c-view");
        Assert.True(row3.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Manually_added_constraint_is_global_and_surfaces_on_the_tab()
    {
        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();

        // Manually add a Workgroup, all-repos constraint (the 2×2 the promotion path can't express
        // arbitrarily — this is the missing manual-add-with-scope path).
        var add = await client.PostAsJsonAsync("/studio/constraints", new
        {
            title = "Always run migrations before integration tests",
            body = "do X before Y",
            reach = "Workgroup",
            repoSpecific = false,
        });
        add.EnsureSuccessStatusCode();
        var created = JsonDocument.Parse(await add.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("artifactId").GetString();

        // It's a global (null-owner) constraint, so it surfaces on the Constraints tab with its scope.
        var list = await client.GetFromJsonAsync<JsonElement>("/studio/constraints");
        var row = list.EnumerateArray().Single(e => e.GetProperty("artifactId").GetString() == id);
        Assert.Equal("Workgroup", row.GetProperty("reach").GetString());
        Assert.True(row.GetProperty("appliesToAllRepos").GetBoolean());
        Assert.True(row.GetProperty("enabled").GetBoolean());
        Assert.Equal("Always run migrations before integration tests", row.GetProperty("title").GetString());
    }
}
