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
/// Found via harness-comparison-eval — the planner dropped a literal method-signature contract
/// while paraphrasing a slice's own goal, and the worker had no way to know since it only ever
/// saw its own slice's Goal. Fix: fold the true top-level ancestor's original goal text back into
/// the worker's kickoff as reference material. This drives a real fan-out child through a
/// capturing fake LLM handler and asserts the first kickoff actually contains the root goal's
/// distinguishing text, not just the child's own (different) slice goal.
/// </summary>
[Trait("Category", "Integration")]
public class OriginalGoalKickoffInjectionTests
{
    private sealed class CapturingLlmHandler : HttpMessageHandler
    {
        public string? FirstKickoffMessage { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var messages = doc.RootElement.GetProperty("messages");

            if (FirstKickoffMessage is null)
                FirstKickoffMessage = messages[0].GetProperty("content").GetString();

            var json = JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = "Done." } },
                stop_reason = "end_turn"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task Worker_kickoff_message_includes_the_root_ancestors_original_goal_text()
    {
        var fakeHandler = new CapturingLlmHandler();
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var workUnits    = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var taskCommands = app.Services.GetRequiredService<ITaskCommandService>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();

        // Distinct literal contract in the root goal that a lossy slice paraphrase would drop —
        // mirrors the real TryClaimForFulfillment finding (a literal signature the planner's own
        // slice-goal paraphrase never repeated).
        const string literalContract = "bool TryClaimForFulfillment(string orderId)";
        var root = await workUnits.CreateAsync(new WorkUnitCreateCommand(
            Goal: $"Add a method to OrderService:\n```csharp\n{literalContract}\n```\nAtomically claim orders.",
            Owner: "test-owner",
            BranchId: "wu-root-goal"));

        var child = await workUnits.CreateAsync(new WorkUnitCreateCommand(
            Goal: "Add TryClaimForFulfillment to OrderService with an atomic compare-and-swap.",
            Owner: "test-owner",
            BranchId: "wu-child-goal",
            ParentWorkUnitId: root.WorkUnitId));

        var task = await taskCommands.CreateAsync(new TaskCreateCommand(
            child.WorkUnitId, child.Goal, "test task"));

        await agentControl.SpawnAsync(
            agentType: "worker",
            workUnitId: child.WorkUnitId,
            taskId: task.TaskId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        var kickoff = await PollForKickoffAsync(fakeHandler);

        // The work unit's own Goal is never embedded in the literal kickoff string (the worker
        // only discovers it via a nm_v1_workunit_get tool call) — what this fix actually adds is
        // the root ancestor's original goal text as reference material alongside that.
        Assert.NotNull(kickoff);
        Assert.Contains(literalContract, kickoff);
        Assert.Contains("Original request", kickoff); // labeled clearly, not silently merged in
    }

    private static async Task<string?> PollForKickoffAsync(CapturingLlmHandler handler, int timeoutSeconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (handler.FirstKickoffMessage is not null) return handler.FirstKickoffMessage;
            await Task.Delay(50);
        }
        return handler.FirstKickoffMessage;
    }
}
