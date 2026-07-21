using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/first-class-goals-and-materialization.md Phase 1 — a ROOT work unit created through
/// POST /studio/workunits (every run strategy's path) auto-persists a repo-scoped GoalNode so the
/// goal replicates to same-repo peers, and GET /studio/goals returns stored goals UNION any
/// work-unit-only goal (so a peer's replicated work unit is never masked by a local stored goal).
/// </summary>
[Trait("Category", "Integration")]
public class FirstClassGoalReplicationTests : IAsyncLifetime
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-fcgoal-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_repoPath);

    private async Task<WebApplication> StartAppAsync()
    {
        Directory.CreateDirectory(_repoPath);
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "Program.cs"), "// seed");
        var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        return app;
    }

    private static async Task<string> PostWorkUnitAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsJsonAsync("/studio/workunits", body);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("workUnitId").GetString()!;
    }

    [Fact]
    public async Task Root_workunit_create_auto_persists_a_repo_scoped_goal()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();
        var goalNodes = app.Services.GetRequiredService<IGoalNodeService>();

        var wuId = await PostWorkUnitAsync(client, new { goal = "Ship the feature", owner = "user", repositoryPath = _repoPath });

        var goal = await goalNodes.GetAsync(wuId);
        Assert.NotNull(goal);
        Assert.Equal("Ship the feature", goal!.Goal);
        Assert.Equal(wuId, goal.WorkUnitId);
        // Repo-scoped (non-null RepositoryId) is what routes GoalV1 into the repo room so it replicates.
        Assert.False(string.IsNullOrEmpty(goal.RepositoryId));
    }

    [Fact]
    public async Task Child_workunit_create_does_not_persist_a_goal()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();
        var goalNodes = app.Services.GetRequiredService<IGoalNodeService>();

        var rootId = await PostWorkUnitAsync(client, new { goal = "Parent goal", owner = "user", repositoryPath = _repoPath });
        var childId = await PostWorkUnitAsync(client, new
        {
            goal = "Sub-task",
            owner = "user",
            parentWorkUnitId = rootId,
            repositoryPath = _repoPath,
        });

        Assert.NotNull(await goalNodes.GetAsync(rootId));
        Assert.Null(await goalNodes.GetAsync(childId)); // only roots are goals
    }

    [Fact]
    public async Task Goals_list_unions_stored_goals_with_goal_less_work_units()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();

        // A: a work unit WITH a stored goal (the endpoint's auto-goal fires).
        await PostWorkUnitAsync(client, new { goal = "alpha", owner = "user", repositoryPath = _repoPath });

        // B: a root work unit WITHOUT a stored goal — created straight through the command service,
        // standing in for a peer's replicated work unit that arrived without a local GoalNode.
        await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("beta", "user", RepositoryPath: _repoPath));

        var root = JsonDocument.Parse(await client.GetStringAsync("/studio/goals")).RootElement;
        var goalTexts = root.GetProperty("goals").EnumerateArray()
            .Select(g => g.GetProperty("goal").GetString()).ToList();

        // Pre-fix, any single stored goal (alpha) made the endpoint drop every work-unit-only goal
        // (beta) — the masking that hid a peer's goals. The union surfaces both.
        Assert.Contains("alpha", goalTexts);
        Assert.Contains("beta", goalTexts);
        Assert.Equal("goal-store+work-units", root.GetProperty("source").GetString());
    }
}
