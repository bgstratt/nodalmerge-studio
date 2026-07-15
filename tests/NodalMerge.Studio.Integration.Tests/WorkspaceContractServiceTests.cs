using System.Text.Json;
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
/// plans/harness-hosting-architecture.md Phase A.4 — WorkspaceContractService against the real
/// service graph: real IProjectionManager (EngineeringState), real IWorkUnitService, real
/// IFileWorkspaceService materialization into a branch workdir.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceContractServiceTests
{
    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    [Fact]
    public async Task AssembleAsync_resolves_the_root_goal_across_a_parent_work_unit_chain()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var contracts = app.Services.GetRequiredService<IWorkspaceContractService>();

        var root = await orchestrator.CreateWorkUnitAsync("Ship the feature", "user");
        var child = await workUnits.CreateAsync(new WorkUnit(
            "WU-Child-Contract", "Sub-task", root.BranchId, WorkUnitStatus.Created,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test", null, null, null,
            ParentWorkUnitId: root.WorkUnitId, DependsOn: [], FileScope: []));

        var bundle = await contracts.AssembleAsync(child.WorkUnitId);

        Assert.Equal(root.WorkUnitId, bundle.Manifest.GoalId);
        Assert.Equal(child.WorkUnitId, bundle.Manifest.WorkUnitId);
        Assert.Equal(root.Goal, bundle.Goal.Goal);
        Assert.Equal(child.WorkUnitId, bundle.WorkUnit.WorkUnitId);
        Assert.Equal(WorkspaceContractCapabilities.All, bundle.Manifest.Capabilities);

        // Regression (found live 2026-07-13): goal.md only ever carries the root/session goal —
        // for a fanned-out child that's every sibling's task text, not just this one's. Without
        // its own Goal on workunit.md, a claude-cli worker had nothing on disk naming its actual
        // slice and tried to do every task goal.md mentioned. bundle.WorkUnit.Goal must be the
        // CHILD's own scoped goal, distinct from the root's.
        Assert.Equal(child.Goal, bundle.WorkUnit.Goal);
        Assert.NotEqual(root.Goal, bundle.WorkUnit.Goal);
    }

    [Fact]
    public async Task MaterializeAsync_writes_workspace_files_readable_via_the_branch_workspace()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var contracts = app.Services.GetRequiredService<IWorkspaceContractService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Materialize test", "user");
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        await contracts.MaterializeAsync(wu.WorkUnitId);

        var manifestJson = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/manifest.json");
        Assert.NotNull(manifestJson);
        var manifest = JsonSerializer.Deserialize<WorkspaceContractManifest>(manifestJson!, JsonSerializerOptions.Web);
        Assert.Equal(wu.WorkUnitId, manifest!.WorkUnitId);

        var manifestMd = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/manifest.md");
        Assert.Contains(wu.WorkUnitId, manifestMd);

        Assert.NotNull(await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/state.json"));
        Assert.NotNull(await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/state.md"));
        Assert.NotNull(await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/constraints.json"));
        Assert.NotNull(await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/review-policy.json"));
    }

    [Fact]
    public async Task MaterializeAsync_writes_the_child_own_goal_not_the_root_session_goal_into_workunit_md()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var contracts = app.Services.GetRequiredService<IWorkspaceContractService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var root = await orchestrator.CreateWorkUnitAsync(
            "Complete ALL 4 tasks below.\n\nTask 1: fix the discount bug.\nTask 2: rename BuyerId.",
            "user");
        var child = await workUnits.CreateAsync(new WorkUnit(
            "WU-Child-OwnGoal", "Fix the discount bug", root.BranchId, WorkUnitStatus.Created,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test", null, null, null,
            ParentWorkUnitId: root.WorkUnitId, DependsOn: [], FileScope: []));
        await fileWorkspace.InitBranchAsync(child.BranchId);

        await contracts.MaterializeAsync(child.WorkUnitId);

        var workUnitMd = await fileWorkspace.ReadAsync(child.BranchId, ".workspace/workunit.md");
        Assert.Contains("Fix the discount bug", workUnitMd);
        Assert.DoesNotContain("Task 2: rename BuyerId", workUnitMd);
    }

    [Fact]
    public async Task MaterializeAsync_twice_produces_identical_deterministic_content()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var contracts = app.Services.GetRequiredService<IWorkspaceContractService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Determinism test", "user");
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        await contracts.MaterializeAsync(wu.WorkUnitId);
        var firstWorkUnit = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/workunit.json");
        var firstReviewPolicy = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/review-policy.json");

        await contracts.MaterializeAsync(wu.WorkUnitId);
        var secondWorkUnit = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/workunit.json");
        var secondReviewPolicy = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/review-policy.json");

        // workunit.json/review-policy.json carry no GeneratedAt timestamp — pure derivations from
        // work-unit state, so these must be byte-identical (principle WC-2). state.json legitimately
        // carries EngineeringState's GeneratedAt, which is out of scope for this assertion.
        Assert.Equal(firstWorkUnit, secondWorkUnit);
        Assert.Equal(firstReviewPolicy, secondReviewPolicy);
    }

    [Fact]
    public async Task RenderEngineeringStateMarkdownAsync_matches_the_materialized_state_md()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var contracts = app.Services.GetRequiredService<IWorkspaceContractService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Render parity test", "user");
        await fileWorkspace.InitBranchAsync(wu.BranchId);
        await contracts.MaterializeAsync(wu.WorkUnitId);

        var materializedStateMd = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/state.md");
        var rendered = await contracts.RenderEngineeringStateMarkdownAsync(wu.WorkUnitId);

        Assert.Equal(materializedStateMd, rendered);
    }

    [Fact]
    public async Task HarvestDecisionsAsync_records_a_JSON_decision_entry()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var contracts = app.Services.GetRequiredService<IWorkspaceContractService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Harvest JSON test", "user");
        await fileWorkspace.InitBranchAsync(wu.BranchId);
        await fileWorkspace.WriteAsync(
            wu.BranchId, ".workspace/decisions/0001.json",
            """{"type":"Decision","title":"Use JWT","body":"Reason: stateless"}""");

        var recorded = await contracts.HarvestDecisionsAsync(wu.WorkUnitId);

        var artifact = Assert.Single(recorded);
        Assert.Equal(ArtifactType.Decision, artifact.Type);
        Assert.Equal("Use JWT", artifact.Title);
        Assert.Equal(wu.WorkUnitId, artifact.OwnedByWorkUnitId);
    }

    [Fact]
    public async Task HarvestDecisionsAsync_records_a_markdown_frontmatter_decision_entry()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var contracts = app.Services.GetRequiredService<IWorkspaceContractService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Harvest markdown test", "user");
        await fileWorkspace.InitBranchAsync(wu.BranchId);
        await fileWorkspace.WriteAsync(
            wu.BranchId, ".workspace/decisions/0001.md",
            "---\ntype: Constraint\ntitle: No MediatR\n---\nTeam decided against it for this repo.");

        var recorded = await contracts.HarvestDecisionsAsync(wu.WorkUnitId);

        var artifact = Assert.Single(recorded);
        Assert.Equal(ArtifactType.Constraint, artifact.Type);
        Assert.Equal("No MediatR", artifact.Title);
        Assert.Equal("Team decided against it for this repo.", artifact.Body);
    }

    [Fact]
    public async Task HarvestDecisionsAsync_re_run_does_not_double_record()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var contracts = app.Services.GetRequiredService<IWorkspaceContractService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var artifactLineage = app.Services.GetRequiredService<IArtifactLineageService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Harvest idempotency test", "user");
        await fileWorkspace.InitBranchAsync(wu.BranchId);
        await fileWorkspace.WriteAsync(
            wu.BranchId, ".workspace/decisions/0001.md",
            "---\ntype: Decision\ntitle: Use Dapper\n---\nPerformance.");

        await contracts.HarvestDecisionsAsync(wu.WorkUnitId);
        // Simulates a crash-retry: the same numbered file is harvested again.
        var second = await contracts.HarvestDecisionsAsync(wu.WorkUnitId);

        Assert.Single(second);
        var chain = await artifactLineage.GetChainAsync(wu.WorkUnitId);
        Assert.Single(chain, a => a.Title == "Use Dapper");
    }

    [Fact]
    public async Task HarvestDecisionsAsync_rejects_a_Supersession_entry_with_no_supersedes()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var contracts = app.Services.GetRequiredService<IWorkspaceContractService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var wu = await orchestrator.CreateWorkUnitAsync("Harvest invalid supersession test", "user");
        await fileWorkspace.InitBranchAsync(wu.BranchId);
        await fileWorkspace.WriteAsync(
            wu.BranchId, ".workspace/decisions/0001.md",
            "---\ntype: Supersession\ntitle: Retire old choice\n---\nNo longer applicable.");

        var recorded = await contracts.HarvestDecisionsAsync(wu.WorkUnitId);

        Assert.Empty(recorded);
    }
}
