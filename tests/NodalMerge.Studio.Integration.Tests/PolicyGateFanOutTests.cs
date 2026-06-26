using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// No orchestrator agent is spawned here (unlike FanOutServiceTests.cs) — spawning one starts a
// background agent loop whose own post-turn fan-out (OrchestratorAgentLoop.cs) can race this
// test's explicit TryFanOutFromPlanAsync call under heavy parallel test load, consuming the
// children-creation/enqueue work before the explicit call sees anything left to do.
[Trait("Category", "Integration")]
public class PolicyGateFanOutTests
{
    // Slice 14a success criterion: a trivial test-only rule registered at BeforeEnqueue, evaluated
    // against a real enqueue call, rejects it and the rejection is visible via the decision log.
    private sealed class GoalMustNotBeEmptyRule : IPolicyRule
    {
        public string RuleId => "goal-must-not-be-empty";

        public PolicyCheckpoint Checkpoint => PolicyCheckpoint.BeforeEnqueue;

        public Task<PolicyResult> EvaluateAsync(IReadOnlyDictionary<string, object?> context, CancellationToken ct = default)
        {
            var goal = context.TryGetValue("goal", out var value) ? value as string : null;
            return Task.FromResult(string.IsNullOrWhiteSpace(goal)
                ? new PolicyResult(false, [new PolicyViolation(RuleId, "goal must not be empty")])
                : new PolicyResult(true, []));
        }
    }

    [Fact]
    public async Task BeforeEnqueue_rule_rejection_blocks_enqueue_and_is_visible_in_decision_log()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton<IPolicyRule, GoalMustNotBeEmptyRule>();
            });

        var orchestrator  = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits     = app.Services.GetRequiredService<IWorkUnitService>();
        var artifacts     = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut        = app.Services.GetRequiredService<IFanOutService>();
        var decisionLog   = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        var parent = await orchestrator.CreateWorkUnitAsync("Build Foo", "test");

        var planJson = """
            {
              "slices": [
                {
                  "sliceId": "s1",
                  "goal": "",
                  "fileScope": ["src/Foo.cs"],
                  "dependsOn": [],
                  "steps": ["Create Foo.cs"]
                }
              ]
            }
            """;

        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", planJson));

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        Assert.Contains(FanOutAction.ChildrenCreated, result.Actions);
        Assert.DoesNotContain(FanOutAction.ChildEnqueued, result.Actions);
        Assert.Empty(result.EnqueuedWorkUnitIds);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        var s1 = children.Single(c => c.FanOutInfo?.SliceId == "s1");
        Assert.Equal(WorkUnitStatus.Created, s1.Status);

        var events = await decisionLog.GetEventsAsync(parent.WorkUnitId);
        Assert.Contains(events, e => e.Action == OrchestrationAction.PolicyBlocked);
    }
}
