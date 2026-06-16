using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class MergeReconciliationServiceTests
{
    [Fact]
    public async Task TryReconcile_combines_non_overlapping_child_proposals()
    {
        var app = StudioWebApplication.Build([], configureServices: s => s.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits    = app.Services.GetRequiredService<IWorkUnitService>();
        var merge        = app.Services.GetRequiredService<IMergeService>();
        var artifacts    = app.Services.GetRequiredService<IArtifactLineageService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var reconciliation = app.Services.GetRequiredService<IMergeReconciliationService>();
        var review       = app.Services.GetRequiredService<IProposalReviewService>();

        var parent = await orchestrator.CreateWorkUnitAsync("parent goal", "test");
        var child1 = await orchestrator.CreateWorkUnitAsync(
            "slice one", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["src/Foo.cs"]);
        var child2 = await orchestrator.CreateWorkUnitAsync(
            "slice two", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["src/Bar.cs"]);

        await fileWorkspace.WriteAsync(child1.BranchId, "src/Foo.cs", "class Foo {}", CancellationToken.None);
        await fileWorkspace.WriteAsync(child2.BranchId, "src/Bar.cs", "class Bar {}", CancellationToken.None);

        async Task<string> ProposeChildAsync(WorkUnit child, string file)
        {
            var id = $"MP-{Guid.NewGuid():N}";
            var proposal = new MergeProposal(
                id, child.BranchId, "main", child.Goal, $"add {file}", $"add {file}",
                null, null, null, MergeProposalStatus.Draft,
                FilesTouched: [file], WorkUnitId: child.WorkUnitId);
            await merge.ProposeAsync(proposal);
            await merge.ValidateAsync(id);
            await artifacts.RecordAsync(new ArtifactRef(
                id, ArtifactType.MergeProposal, child.WorkUnitId, ArtifactStatus.Active,
                DateTimeOffset.UtcNow, child.WorkUnitId, null));
            await workUnits.UpdateStatusAsync(child.WorkUnitId, WorkUnitStatus.Queued);
            await workUnits.UpdateStatusAsync(child.WorkUnitId, WorkUnitStatus.Executing);
            await workUnits.UpdateStatusAsync(child.WorkUnitId, WorkUnitStatus.Proposed);
            return id;
        }

        var p1 = await ProposeChildAsync(child1, "src/Foo.cs");
        var p2 = await ProposeChildAsync(child2, "src/Bar.cs");

        var result = await reconciliation.TryReconcileAsync(parent.WorkUnitId);

        Assert.Equal(MergeReconciliationOutcome.Reconciled, result.Outcome);
        Assert.NotNull(result.ReconciledProposalId);

        var reconciled = await merge.GetAsync(result.ReconciledProposalId!);
        Assert.NotNull(reconciled);
        Assert.Equal(2, reconciled!.ReconciledFrom.Count);
        Assert.Contains("src/Foo.cs", reconciled.FilesTouched);
        Assert.Contains("src/Bar.cs", reconciled.FilesTouched);

        var p1After = await merge.GetAsync(p1);
        Assert.Equal(MergeProposalStatus.Superseded, p1After!.Status);

        var fileChanges = await review.GetFileChangesAsync(reconciled.ProposalId);
        Assert.Equal(2, fileChanges.Count);
        Assert.All(fileChanges, fc => Assert.False(string.IsNullOrEmpty(fc.AfterContent)));
    }
}
