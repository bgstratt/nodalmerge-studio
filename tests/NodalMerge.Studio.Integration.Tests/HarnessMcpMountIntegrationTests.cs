using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/harness-hosting-architecture.md Phase C.4 (phase-c-implementation.md C3) — the slim
/// "/mcp-harness" MCP mount, exercised as a real MCP client would (initialize + tools/call over
/// Streamable HTTP), against a live TestServer-backed host. No real `claude` binary involved — this
/// proves the mount plumbing (token -> work unit resolution, rejection of bad tokens, "/mcp" left
/// unwidened) independent of any CLI adapter.
/// </summary>
[Trait("Category", "Integration")]
public class HarnessMcpMountIntegrationTests
{
    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    private static async Task<McpClient> ConnectAsync(HttpClient httpClient, string path, string? bearerToken)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, path),
        };
        if (bearerToken is not null)
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearerToken}" };

        var transport = new HttpClientTransport(options, httpClient, loggerFactory: null, ownsHttpClient: false);
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    [Fact]
    public async Task A_valid_token_resolves_artifact_record_to_the_right_work_unit()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var tokens = app.Services.GetRequiredService<IHarnessMcpTokenService>();
        var artifacts = app.Services.GetRequiredService<IArtifactCommandService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise the /mcp-harness mount", "integration-test");
        var token = tokens.Mint(wu.WorkUnitId, sessionId: "session-mcp-harness-1", agentId: "agent-mcp-harness-1");

        var httpClient = app.GetTestClient();
        await using var client = await ConnectAsync(httpClient, "/mcp-harness", token);

        var result = await client.CallToolAsync(
            "nm_v1_artifact_record",
            new Dictionary<string, object?>
            {
                ["type"] = "Research",
                ["title"] = "harness-mount-test",
                ["body"] = "Recorded via the /mcp-harness mount by a real MCP client.",
            },
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        Assert.Contains("artifactId", text);

        var recorded = await artifacts.ListAsync(wu.WorkUnitId, includeAncestors: false, CancellationToken.None);
        Assert.Contains(recorded, a => a.Title == "harness-mount-test");
    }

    [Fact]
    public async Task An_invalid_token_is_rejected_without_touching_any_work_unit()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var httpClient = app.GetTestClient();
        await using var client = await ConnectAsync(httpClient, "/mcp-harness", "this-token-was-never-minted");

        var result = await client.CallToolAsync(
            "nm_v1_artifact_record",
            new Dictionary<string, object?> { ["type"] = "Research", ["title"] = "t", ["body"] = "b" },
            cancellationToken: CancellationToken.None);

        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        Assert.Contains("revoked", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_revoked_token_is_rejected_after_revocation()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var tokens = app.Services.GetRequiredService<IHarnessMcpTokenService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise token revocation", "integration-test");
        var token = tokens.Mint(wu.WorkUnitId, sessionId: null, agentId: "agent-mcp-harness-2");
        tokens.Revoke(token);

        var httpClient = app.GetTestClient();
        await using var client = await ConnectAsync(httpClient, "/mcp-harness", token);

        var result = await client.CallToolAsync(
            "nm_v1_artifact_record",
            new Dictionary<string, object?> { ["type"] = "Research", ["title"] = "t", ["body"] = "b" },
            cancellationToken: CancellationToken.None);

        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        Assert.Contains("revoked", text, StringComparison.OrdinalIgnoreCase);
    }

    // Proves the "/mcp" mount was not widened by C3: its tool list is still exactly the 5 external
    // classes' tools, none of the harness-only nm_v1_* names.
    [Fact]
    public async Task The_external_mcp_mount_does_not_list_any_harness_only_tools()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var httpClient = app.GetTestClient();
        await using var client = await ConnectAsync(httpClient, "/mcp", bearerToken: null);

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var names = tools.Select(t => t.Name).ToList();

        Assert.DoesNotContain("nm_v1_artifact_record", names);
        Assert.DoesNotContain("nm_v1_workspace_symbol_definition", names);
        Assert.DoesNotContain("nm_v1_doc_fetch", names);
        Assert.DoesNotContain("nm_v1_clarification_request", names);
    }

    [Fact]
    public async Task The_harness_mount_lists_exactly_the_C4_subset()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var httpClient = app.GetTestClient();
        await using var client = await ConnectAsync(httpClient, "/mcp-harness", bearerToken: "irrelevant-for-listing");

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Equal(
            new HashSet<string>
            {
                "nm_v1_workspace_symbol_definition",
                "nm_v1_workspace_symbol_references",
                "nm_v1_workspace_symbol_implementation",
                "nm_v1_doc_fetch",
                "nm_v1_artifact_record",
                "nm_v1_artifact_query",
                "nm_v1_clarification_request",
            },
            names);
    }
}
