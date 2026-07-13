using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 22/23 follow-up — real-HTTP coverage for:
///  - GET /studio/domain-agents (lists DomainAgentRegistry so the VS Code UI doesn't hardcode it)
///  - GET/POST /studio/options round-tripping EnabledDomainAgents (the Session Defaults toggle list)
///  - GET /studio/artifacts/{artifactId}/feedback (the surfaced/considered convenience query)
/// </summary>
[Trait("Category", "Integration")]
public class DomainAgentConfigAndFeedbackRestTests
{
    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    [Fact]
    public async Task DomainAgents_endpoint_lists_the_full_registry()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var agents = await client.GetFromJsonAsync<List<JsonElement>>("/studio/domain-agents");

        Assert.NotNull(agents);
        var names = agents!.Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Equal(DomainAgentRegistry.All.Select(d => d.Name), names);
    }

    // plans/harness-hosting-architecture.md Phase C.1 (phase-c-implementation.md C1.c).
    [Fact]
    public async Task Executors_endpoint_lists_both_registered_executors_with_capabilities()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var executors = await client.GetFromJsonAsync<List<JsonElement>>("/studio/executors");

        Assert.NotNull(executors);
        var byName = executors!.ToDictionary(e => e.GetProperty("name").GetString()!);
        Assert.Contains("native", byName.Keys);
        Assert.Contains("claude-code", byName.Keys);

        var native = byName["native"];
        Assert.Equal(JsonValueKind.Null, native.GetProperty("providerKey").ValueKind);
        var nativeCaps = native.GetProperty("capabilities");
        Assert.True(nativeCaps.GetProperty("supportsTurnTelemetry").GetBoolean());
        Assert.False(nativeCaps.GetProperty("supportsResume").GetBoolean());
        Assert.False(nativeCaps.GetProperty("supportsMcp").GetBoolean());
        Assert.False(nativeCaps.GetProperty("supportsPlanningMode").GetBoolean());

        var claudeCode = byName["claude-code"];
        Assert.Equal("claude-cli", claudeCode.GetProperty("providerKey").GetString());
        var claudeCaps = claudeCode.GetProperty("capabilities");
        Assert.True(claudeCaps.GetProperty("supportsTurnTelemetry").GetBoolean());
        Assert.True(claudeCaps.GetProperty("supportsResume").GetBoolean());
        Assert.True(claudeCaps.GetProperty("supportsHooks").GetBoolean());
        Assert.True(claudeCaps.GetProperty("supportsSubagents").GetBoolean());
        Assert.True(claudeCaps.GetProperty("supportsMcp").GetBoolean());
        Assert.False(claudeCaps.GetProperty("supportsPlanningMode").GetBoolean());
    }

    [Fact]
    public async Task Options_round_trips_EnabledDomainAgents()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var before = await client.GetFromJsonAsync<JsonElement>("/studio/options");
        Assert.Empty(before.GetProperty("enabledDomainAgents").EnumerateArray());

        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(before.GetRawText())!;
        body["enabledDomainAgents"] = new[] { "Security", "Architecture" };
        var postResponse = await client.PostAsJsonAsync("/studio/options", body);
        postResponse.EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<JsonElement>("/studio/options");
        var enabled = after.GetProperty("enabledDomainAgents").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["Security", "Architecture"], enabled);
    }

    [Fact]
    public async Task Artifact_feedback_endpoint_reports_surfaced_and_considered_events()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var events = app.Services.GetRequiredService<IExecutionEventStream>();
        var client = app.GetTestClient();

        const string artifactId = "KA-feedback-1";
        await events.AppendAsync(
            "SES-1", "WU-1", ExecutionEventKind.ArtifactSurfaced,
            new ArtifactSurfacedPayload(artifactId, "agent-1", "AgentWorkspace"));
        await events.AppendAsync(
            "SES-1", "WU-1", ExecutionEventKind.ArtifactConsideredInDecision,
            new ArtifactConsideredInDecisionPayload(artifactId, "MP-1", MergeProposalStatus.Approved));
        // A different artifact's events must not leak into this artifact's feedback.
        await events.AppendAsync(
            "SES-1", "WU-1", ExecutionEventKind.ArtifactSurfaced,
            new ArtifactSurfacedPayload("KA-other", "agent-1", "AgentWorkspace"));

        var feedback = await client.GetFromJsonAsync<JsonElement>($"/studio/artifacts/{artifactId}/feedback");

        Assert.Equal(artifactId, feedback.GetProperty("artifactId").GetString());
        var surfaced = Assert.Single(feedback.GetProperty("surfaced").EnumerateArray());
        Assert.Equal("agent-1", surfaced.GetProperty("surfacedToAgentId").GetString());
        var considered = Assert.Single(feedback.GetProperty("consideredIn").EnumerateArray());
        Assert.Equal("MP-1", considered.GetProperty("proposalId").GetString());
        Assert.Equal("Approved", considered.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Artifact_feedback_endpoint_returns_empty_lists_when_artifact_was_never_surfaced_or_considered()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var feedback = await client.GetFromJsonAsync<JsonElement>("/studio/artifacts/KA-unseen/feedback");

        Assert.Empty(feedback.GetProperty("surfaced").EnumerateArray());
        Assert.Empty(feedback.GetProperty("consideredIn").EnumerateArray());
    }
}
