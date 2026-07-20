using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Regression coverage for a bug our own Review-stage dead-letter recording
/// (InlineReviewerService) newly made reachable: ContinueService used to unconditionally spawn a
/// WorkerAgentLoop regardless of the dead-letter entry's Stage, which for a Review-stage entry
/// meant misdispatching against a proposalId it would misinterpret as a taskId. Verifies Continue
/// now spawns a ReviewerAgentLoop (Reviewer tool set, including nm_v1_merge_review) for a
/// Review-stage entry, and does not flip WorkUnitStatus the way the Execute-stage path does.
/// </summary>
[Trait("Category", "Integration")]
public class ContinueReviewerDispatchTests
{
    [Fact]
    public async Task ContinueWithPriorContextAsync_ReviewStage_spawns_ReviewerAgentLoop_not_WorkerAgentLoop()
    {
        var handler = new ReviewerToolSetProbeHandler();
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(handler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var deadLetter      = app.Services.GetRequiredService<IDeadLetterService>();
        var continueService = app.Services.GetRequiredService<IContinueService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync(
            goal: "Add TryClaimForFulfillment to OrderService",
            owner: "integration-test");

        var entry = await deadLetter.RecordFailureAsync(
            wu.WorkUnitId,
            "reviewer-auto-test",
            PipelineStage.Review,
            "reviewer",
            "Reviewer agent ran out of iterations without submitting a decision.",
            taskId: "MP-fake-proposal",
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key",
            kind: FailureKind.MaxIterationsExceeded);

        var statusBefore = (await workUnits.GetAsync(wu.WorkUnitId))!.Status;

        var result = await continueService.ContinueWithPriorContextAsync(entry.EntryId);

        Assert.True(handler.SawReviewerTool, "expected the resumed loop's tool set to include nm_v1_merge_review (Reviewer-only)");
        Assert.False(handler.SawWorkerOnlyTool, "resumed loop must not be a WorkerAgentLoop for a Review-stage entry");
        Assert.Equal(ContinueOutcome.Continued, result.Outcome);

        // Unlike the Execute-stage path (which flips Retrying -> Executing around the loop), a
        // Review-stage Continue leaves WorkUnitStatus untouched — the nm_v1_merge_review tool call
        // itself is what should drive any status change, not a flip around the loop.
        var statusAfter = (await workUnits.GetAsync(wu.WorkUnitId))!.Status;
        Assert.Equal(statusBefore, statusAfter);
    }

    private sealed class ReviewerToolSetProbeHandler : HttpMessageHandler
    {
        public bool SawReviewerTool { get; private set; }
        public bool SawWorkerOnlyTool { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("tools", out var tools))
            {
                foreach (var t in tools.EnumerateArray())
                {
                    var name = t.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name == "nm_v1_merge_review")
                        SawReviewerTool = true;
                    // A Worker-only tool with no Reviewer equivalent — its presence would mean a
                    // WorkerAgentLoop's tool set was actually sent, not a ReviewerAgentLoop's.
                    if (name == "nm_v1_task_update")
                        SawWorkerOnlyTool = true;
                }
            }

            var json = JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = "Nothing further to check. Ending turn without a decision." } },
                stop_reason = "end_turn",
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
