using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 9e — the AgentWorkspace projection (what an in-flight Worker/Planner/Orchestrator agent
/// actually sees) now includes the detected project roots, so an agent can tell a repo holds more
/// than one project before deciding where a file belongs, instead of only finding out via
/// nm_v1_workspace_list with no stack/boundary context at all.
/// </summary>
[Trait("Category", "Integration")]
public class AgentWorkspaceProjectionProfileTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-projection-profile-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
            });

    [Fact]
    public async Task AgentWorkspace_projection_includes_detected_roots()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var workUnits = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var projections = app.Services.GetRequiredService<IProjectionManager>();

        var wu = await workUnits.CreateAsync(new WorkUnitCreateCommand(
            Goal: "Update the compliment endpoint", Owner: "test-owner", BranchId: "wu-profile-test"));

        await fileWorkspace.WriteAsync(wu.BranchId, "backend/Host.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        await fileWorkspace.WriteAsync(wu.BranchId, "frontend/package.json", """{"scripts":{"build":"vite build"}}""");

        var result = await projections.GetAsync(new ProjectionRequest(
            ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: wu.WorkUnitId));

        var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(
            result.DataJson, JsonSerializerOptions.Web);

        Assert.NotNull(payload!.Roots);
        Assert.Equal(2, payload.Roots!.Count);
        Assert.Contains(payload.Roots, r => r.RelativePath == "backend" && r.Stack == "dotnet");
        Assert.Contains(payload.Roots, r => r.RelativePath == "frontend" && r.Stack == "npm");
    }

    /// <summary>
    /// Phase 1c (plans/organizational-knowledge-and-workgroup-scope.md) — constraint injection applies
    /// the application filter: a repo-specific constraint (RepositoryId set) is injected only when the
    /// work unit targets that repo; a constraint with no RepositoryId applies to all repos. (Reach
    /// governs replication, not whether a constraint fires here.)
    /// </summary>
    [Fact]
    public async Task Injected_constraints_are_filtered_by_repository_application_scope()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var projections = app.Services.GetRequiredService<IProjectionManager>();

        var wu = await orchestrator.CreateWorkUnitAsync(goal: "g", owner: "o", repositoryId: "repoA");

        ArtifactRef Constraint(string id, string? repositoryId) => new(
            ArtifactId: id, Type: ArtifactType.Constraint, ParentArtifactId: null,
            Status: ArtifactStatus.Approved, CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: id, Body: "b",
            RepositoryId: repositoryId, Reach: ArtifactReach.Workgroup);

        await artifacts.RecordAsync(Constraint("c-repoA", "repoA")); // this repo
        await artifacts.RecordAsync(Constraint("c-repoB", "repoB")); // a different repo
        await artifacts.RecordAsync(Constraint("c-all", null));      // all repos

        var result = await projections.GetAsync(new ProjectionRequest(
            ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: wu.WorkUnitId));
        var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(
            result.DataJson, JsonSerializerOptions.Web);

        var ids = payload!.InheritedConstraints.Select(c => c.ArtifactId).ToHashSet();
        Assert.Contains("c-repoA", ids);        // repo-specific for THIS repo → injected
        Assert.Contains("c-all", ids);          // no RepositoryId → applies everywhere
        Assert.DoesNotContain("c-repoB", ids);  // repo-specific for a DIFFERENT repo → filtered out
    }

    /// <summary>
    /// Item 3 S2 (plans/vision-punchlist-remediation.md) — a retired constraint stops being injected.
    /// The global arm is defensive (nothing in the product can currently move a global off Approved),
    /// so the load-bearing assertion here is the FIRST one: an ordinary Approved constraint must still
    /// inject. The filter is a blocklist precisely so it can't silently zero out the whole feature.
    /// </summary>
    [Fact]
    public async Task Retired_constraints_are_excluded_from_injection()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var projections = app.Services.GetRequiredService<IProjectionManager>();

        var wu = await orchestrator.CreateWorkUnitAsync(goal: "g", owner: "o", repositoryId: "repoA");

        ArtifactRef Constraint(string id) => new(
            ArtifactId: id, Type: ArtifactType.Constraint, ParentArtifactId: null,
            Status: ArtifactStatus.Approved, CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: id, Body: "b",
            RepositoryId: null, Reach: ArtifactReach.Workgroup);

        await artifacts.RecordAsync(Constraint("c-live"));
        await artifacts.RecordAsync(Constraint("c-invalidated"));
        await artifacts.RecordAsync(Constraint("c-superseded"));
        await artifacts.RecordAsync(Constraint("c-rejected"));

        await artifacts.InvalidateAsync("c-invalidated", "no longer applies");
        await artifacts.UpdateStatusAsync("c-superseded", ArtifactStatus.Superseded);
        await artifacts.UpdateStatusAsync("c-rejected", ArtifactStatus.Rejected);

        var result = await projections.GetAsync(new ProjectionRequest(
            ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: wu.WorkUnitId));
        var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(
            result.DataJson, JsonSerializerOptions.Web);

        var ids = payload!.InheritedConstraints.Select(c => c.ArtifactId).ToHashSet();
        Assert.Contains("c-live", ids);              // still Approved → still injected
        Assert.DoesNotContain("c-invalidated", ids);
        Assert.DoesNotContain("c-superseded", ids);
        Assert.DoesNotContain("c-rejected", ids);
    }

    /// <summary>
    /// Item 3 S2 — the arm that actually bites. Lineage (work-unit-owned) constraints are created
    /// Active and CAN be retired in the product: an agent calling nm_v1_artifact_invalidate, or the
    /// InvalidateAsync cascade stamping InvalidatedByArtifactId on a descendant when the artifact it
    /// was derived from is invalidated. Both were still being injected into every descendant's prompt.
    /// </summary>
    [Fact]
    public async Task Invalidated_ancestor_constraints_are_excluded_from_injection()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var projections = app.Services.GetRequiredService<IProjectionManager>();

        var parent = await orchestrator.CreateWorkUnitAsync(goal: "parent", owner: "o", repositoryId: "repoA");
        var child = await orchestrator.CreateWorkUnitAsync(
            goal: "child", owner: "o", repositoryId: "repoA", parentWorkUnitId: parent.WorkUnitId);

        ArtifactRef Owned(string id, ArtifactType type, string? parentArtifactId) => new(
            ArtifactId: id, Type: type, ParentArtifactId: parentArtifactId,
            Status: ArtifactStatus.Active, CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: parent.WorkUnitId, OwnedByAgentId: null, Title: id, Body: "b");

        await artifacts.RecordAsync(Owned("lc-keep", ArtifactType.Constraint, null));
        await artifacts.RecordAsync(Owned("lc-invalid", ArtifactType.Constraint, null));
        // A constraint derived from a Research artifact — invalidating the Research cascades an
        // InvalidatedByArtifactId stamp onto this constraint without changing its own Status.
        await artifacts.RecordAsync(Owned("r-root", ArtifactType.Research, null));
        await artifacts.RecordAsync(Owned("lc-cascade", ArtifactType.Constraint, "r-root"));

        await artifacts.InvalidateAsync("lc-invalid", "retired directly");
        await artifacts.InvalidateAsync("r-root", "research superseded");

        var result = await projections.GetAsync(new ProjectionRequest(
            ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: child.WorkUnitId));
        var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(
            result.DataJson, JsonSerializerOptions.Web);

        var ids = payload!.InheritedConstraints.Select(c => c.ArtifactId).ToHashSet();
        Assert.Contains("lc-keep", ids);              // inherited from the ancestor, still live
        Assert.DoesNotContain("lc-invalid", ids);     // Status == Invalidated
        Assert.DoesNotContain("lc-cascade", ids);     // InvalidatedByArtifactId set by the cascade
    }

    /// <summary>
    /// Phase 3 (plans/organizational-knowledge-and-workgroup-scope.md) — the Insights local toggle: a
    /// constraint disabled on this peer is subtracted from injection, without affecting any other
    /// constraint; re-enabling restores it. (Suppression is local — it never touches the shared copy.)
    /// </summary>
    [Fact]
    public async Task Locally_disabled_constraints_are_excluded_from_injection()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var toggles = app.Services.GetRequiredService<IConstraintToggleService>();
        var projections = app.Services.GetRequiredService<IProjectionManager>();

        var wu = await orchestrator.CreateWorkUnitAsync(goal: "g", owner: "o", repositoryId: "repoA");

        ArtifactRef Constraint(string id) => new(
            ArtifactId: id, Type: ArtifactType.Constraint, ParentArtifactId: null,
            Status: ArtifactStatus.Approved, CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: id, Body: "b",
            RepositoryId: null, Reach: ArtifactReach.Workgroup);

        await artifacts.RecordAsync(Constraint("c-keep"));
        await artifacts.RecordAsync(Constraint("c-off"));

        async Task<HashSet<string>> InjectedIdsAsync()
        {
            var result = await projections.GetAsync(new ProjectionRequest(
                ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: wu.WorkUnitId));
            var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(
                result.DataJson, JsonSerializerOptions.Web);
            return payload!.InheritedConstraints.Select(c => c.ArtifactId).ToHashSet();
        }

        Assert.Contains("c-off", await InjectedIdsAsync()); // baseline: both injected

        await toggles.SetDisabledAsync("c-off", disabled: true);
        var afterOff = await InjectedIdsAsync();
        Assert.DoesNotContain("c-off", afterOff);  // suppressed locally
        Assert.Contains("c-keep", afterOff);        // every other constraint unaffected

        await toggles.SetDisabledAsync("c-off", disabled: false);
        Assert.Contains("c-off", await InjectedIdsAsync()); // re-enabled
    }
}
