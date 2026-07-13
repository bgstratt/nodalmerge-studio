using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/review-seam-and-clarification-sessions.md S1 — a blocking clarification whose request
/// resolves no sessionId used to park the work unit (MarkAwaitingResumeAsync + Waiting) while
/// being INVISIBLE: the ClarificationRequested event (the sole source ListActiveRequestsAsync /
/// RespondAsync / ClarificationTimerService read from) was only appended when a session resolved.
/// Found by the C3 real-CLI smoke (2026-07-13): a stuck goal with no visible cause. RequestAsync
/// now falls back to the owning goal node's session, then a synthetic wu-{workUnitId} session, so
/// the event always exists.
/// </summary>
[Trait("Category", "Integration")]
public class ClarificationSessionFallbackTests
{
    private static WebApplication Build() => StudioWebApplication.Build(
        [],
        configureWebHost: webHost => webHost.UseTestServer(),
        llmHttpClient: new HttpClient(new ScriptedLlmHandler()),
        configureServices: services => services.AddInMemoryStorage());

    [Fact]
    public async Task Session_less_blocking_request_is_still_listable_and_answerable()
    {
        await using var app = Build();

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var clarifications = app.Services.GetRequiredService<IClarificationCommandService>();

        // No goal node, no scheduled item, no explicit session — the worst case every prior
        // session-less enqueue path (ContinueService, RequeueAsync, direct API callers) fed into.
        var wu = await orchestratorSvc.CreateWorkUnitAsync("Session-less clarification goal", "test");

        var result = await clarifications.RequestAsync(wu.WorkUnitId, "Which file should I touch?");
        Assert.True(result.ParkedAwaitingResponse);

        var active = await clarifications.ListActiveRequestsAsync();
        var item = Assert.Single(active, r => r.WorkUnitId == wu.WorkUnitId);
        Assert.Equal($"wu-{wu.WorkUnitId}", item.SessionId);
        Assert.Equal("Which file should I touch?", item.Question);

        // The other half of the old failure: RespondAsync resolved requests from the same event
        // map, so an invisible request was also unanswerable (KeyNotFoundException).
        var response = await clarifications.RespondAsync(wu.WorkUnitId, "touch notes.md", respondedBy: "test");
        Assert.Equal(item.RequestId, response.RequestId);
        Assert.True(response.Resumed);

        Assert.DoesNotContain(
            await clarifications.ListActiveRequestsAsync(),
            r => r.WorkUnitId == wu.WorkUnitId);
    }

    [Fact]
    public async Task Goal_node_session_wins_over_the_synthetic_fallback_for_fanned_out_children()
    {
        await using var app = Build();

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var clarifications = app.Services.GetRequiredService<IClarificationCommandService>();
        var goalNodes = app.Services.GetRequiredService<IGoalNodeService>();

        var root = await orchestratorSvc.CreateWorkUnitAsync("Root goal with a session", "test");
        var child = await orchestratorSvc.CreateWorkUnitAsync(
            "Fanned-out child", "test", parentWorkUnitId: root.WorkUnitId);

        var now = DateTimeOffset.UtcNow;
        await goalNodes.RecordAsync(new GoalNode(
            "goal-1", root.Goal, root.WorkUnitId, root.BranchId, GoalStatus.Exploring,
            now, now, "test", SessionId: "goal-session-1"));

        // The child has no goal node of its own — the fallback must walk ParentWorkUnitId to the
        // root and adopt the goal's session rather than minting a synthetic one.
        await clarifications.RequestAsync(child.WorkUnitId, "Blocking question from a child?");

        var active = await clarifications.ListActiveRequestsAsync();
        var item = Assert.Single(active, r => r.WorkUnitId == child.WorkUnitId);
        Assert.Equal("goal-session-1", item.SessionId);
    }
}
