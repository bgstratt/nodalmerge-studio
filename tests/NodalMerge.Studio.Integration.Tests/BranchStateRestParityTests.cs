using System.Net;
using System.Net.Http.Json;
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
/// Slice 15a — nm_v1_branch_checkout/nm_v1_branch_status had no REST route at all (MCP-only,
/// a real fallback gap when MCP is disabled). These lock in the new REST routes and confirm
/// State's existing routes already matched StateTools.cs, since both transports call
/// IBranchService/IKnownGoodStateService directly with no extra logic of their own.
/// </summary>
[Trait("Category", "Integration")]
public class BranchStateRestParityTests
{
    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    [Fact]
    public async Task Checkout_returns_ok_for_an_existing_branch()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var branches = app.Services.GetRequiredService<IBranchService>();
        await branches.CreateBranchAsync("feature-x");

        var client = app.GetTestClient();
        var response = await client.PostAsync("/studio/branches/feature-x/checkout", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Status_reports_active_for_a_created_branch_and_unknown_for_a_missing_one()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var branches = app.Services.GetRequiredService<IBranchService>();
        await branches.CreateBranchAsync("feature-y");

        var client = app.GetTestClient();
        var known = await client.GetFromJsonAsync<BranchStatus>("/studio/branches/feature-y/status");
        var unknown = await client.GetFromJsonAsync<BranchStatus>("/studio/branches/does-not-exist/status");

        Assert.Equal("active", known!.Status);
        Assert.Equal("unknown", unknown!.Status);
    }

    [Fact]
    public async Task State_endpoints_already_match_MCP_StateTools_round_trip()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var markResponse = await client.PostAsJsonAsync("/studio/state/markKnownGood", new
        {
            branchId = "feature-z",
            nodeId = "n/a",
            description = "checkpoint before risky refactor",
        });
        markResponse.EnsureSuccessStatusCode();
        var marked = await markResponse.Content.ReadFromJsonAsync<KnownGoodState>();

        var found = await client.GetFromJsonAsync<List<KnownGoodState>>("/studio/state/knownGood/feature-z");
        Assert.Contains(found!, s => s.StateId == marked!.StateId);

        var checkoutResponse = await client.PostAsJsonAsync("/studio/state/checkoutKnownGood", new
        {
            stateId = marked!.StateId,
        });
        checkoutResponse.EnsureSuccessStatusCode();
    }
}
