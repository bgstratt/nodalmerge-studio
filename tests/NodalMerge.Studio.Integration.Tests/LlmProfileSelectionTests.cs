using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 5 slice 12d — LLM-driven profile selection. Today's heuristic for child work units
/// fanned out from a plan is simply "always 'worker'" (FanOutService.EnqueueChildWorkerAsync);
/// this slice makes that overridable by an LLM call, gated by
/// WorkspaceOptions.UseLlmProfileSelection (default off).
/// </summary>
[Trait("Category", "Integration")]
public class LlmProfileSelectionTests
{
    private const string PlanJson = """
        {
          "slices": [
            {
              "sliceId": "s1",
              "goal": "Implement Foo.cs",
              "fileScope": ["src/Foo.cs"],
              "dependsOn": [],
              "steps": ["Create Foo.cs"]
            }
          ]
        }
        """;

    [Fact]
    public async Task Toggle_on_and_LLM_selects_known_profile_is_used_with_reason_recorded()
    {
        var handler = new ProfileSelectionLlmHandler("worker", "goal clearly maps to execute stage");

        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(handler),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions { UseLlmProfileSelection = true });
            });

        var decisionLog = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();
        var parent = await SeedPlanAndSpawnOrchestratorAsync(app);

        var enqueueEvent = await WaitForEnqueueEventAsync(decisionLog, parent.WorkUnitId);

        Assert.True(handler.ProfileSelectionCallCount >= 1);
        Assert.StartsWith("LLM selected worker:", enqueueEvent.Reason, StringComparison.Ordinal);
        Assert.Equal("worker", SelectedProfileId(enqueueEvent));
    }

    [Fact]
    public async Task Toggle_on_and_LLM_returns_unknown_profile_falls_back_to_heuristic_without_error()
    {
        var handler = new ProfileSelectionLlmHandler("totally-not-a-real-profile", "made up");

        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(handler),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions { UseLlmProfileSelection = true });
            });

        var decisionLog = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();
        var parent = await SeedPlanAndSpawnOrchestratorAsync(app);

        var enqueueEvent = await WaitForEnqueueEventAsync(decisionLog, parent.WorkUnitId);

        Assert.Contains("unknown profile 'totally-not-a-real-profile'", enqueueEvent.Reason, StringComparison.Ordinal);
        Assert.Equal("worker", SelectedProfileId(enqueueEvent));
    }

    [Fact]
    public async Task Toggle_off_by_default_never_calls_llm_for_profile_selection()
    {
        var handler = new ProfileSelectionLlmHandler("worker", "irrelevant — should never be asked");

        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(handler),
            configureServices: services => services.AddInMemoryStorage());

        var decisionLog = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();
        var parent = await SeedPlanAndSpawnOrchestratorAsync(app);

        var enqueueEvent = await WaitForEnqueueEventAsync(decisionLog, parent.WorkUnitId);

        Assert.Equal(0, handler.ProfileSelectionCallCount);
        Assert.Equal("LLM profile selection is disabled; using heuristic default.", enqueueEvent.Reason);
        Assert.Equal("worker", SelectedProfileId(enqueueEvent));
    }

    private static string? SelectedProfileId(OrchestrationEvent e)
    {
        using var doc = JsonDocument.Parse(e.InputProjectionSnapshot);
        return doc.RootElement.GetProperty("selectedProfileId").GetString();
    }

    // Plan is recorded as a DAG artifact *before* the orchestrator is spawned, and fan-out is
    // triggered only once — by the background orchestrator loop itself after it ends its (single,
    // immediate end_turn) turn, per OrchestratorAgentLoop.RunAsync's unconditional
    // TryFanOutFromPlanAsync call. Calling IFanOutService.TryFanOutFromPlanAsync a second time
    // from the test directly (as FanOutServiceTests.cs does, racing against the very loop this
    // call also spawns) hits a pre-existing FanOutService.EnsureChildWorkUnitsAsync concurrency
    // gap where two concurrent fan-outs for the same parent each create a child for the same
    // slice before either becomes visible to the other — out of scope to fix here.
    private static async Task<WorkUnit> SeedPlanAndSpawnOrchestratorAsync(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        var orchestrator  = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts     = app.Services.GetRequiredService<IArtifactLineageService>();
        var agentControl  = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime  = app.Services.GetRequiredService<NodalMerge.Studio.AgentRuntime.InMemoryAgentRuntimeService>();

        await agentRuntime.StartAsync(CancellationToken.None);

        var parent = await orchestrator.CreateWorkUnitAsync("Build Foo", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", PlanJson));

        await agentControl.SpawnAsync(
            "orchestrator", parent.WorkUnitId,
            model: "fake", baseUrl: "http://fake", apiKey: "fake");

        return parent;
    }

    private static async Task<OrchestrationEvent> WaitForEnqueueEventAsync(
        IOrchestrationDecisionLogService decisionLog, string workUnitId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var events = await decisionLog.GetEventsAsync(workUnitId);
            var match = events.FirstOrDefault(e => e.Action == OrchestrationAction.Enqueue);
            if (match is not null) return match;
            await Task.Delay(50);
        }

        Assert.Fail($"No Enqueue orchestration event recorded for work unit '{workUnitId}' within the deadline.");
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>
/// Distinguishes the lightweight profile-selection call (identified by its system prompt marker,
/// present in the request body for both Anthropic and OpenAI-compatible wire formats) from any
/// other LLM call the background orchestrator loop happens to make — so tests can assert the
/// selection call did/did not happen without being confused by unrelated traffic.
/// </summary>
internal sealed class ProfileSelectionLlmHandler(string profileId, string explanation) : HttpMessageHandler
{
    private const string Marker = "You select which agent profile should execute a child work unit";

    public int ProfileSelectionCallCount;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var text = "ok";
        if (body.Contains(Marker, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref ProfileSelectionCallCount);
            text = JsonSerializer.Serialize(new { profileId, explanation });
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    content = new[] { new { type = "text", text } },
                    stop_reason = "end_turn",
                }),
                Encoding.UTF8,
                "application/json"),
        };
    }
}
