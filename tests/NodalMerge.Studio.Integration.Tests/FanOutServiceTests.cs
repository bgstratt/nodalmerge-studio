using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;
using StudioArtifactStatus = NodalMerge.Studio.Contracts.Domain.ArtifactStatus;


namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class FanOutServiceTests
{
    [Fact]
    public async Task TryFanOutFromPlan_enqueues_only_slices_with_satisfied_dependencies()
    {
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator  = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits     = app.Services.GetRequiredService<IWorkUnitService>();
        var artifacts     = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut        = app.Services.GetRequiredService<IFanOutService>();
        var agentControl  = app.Services.GetRequiredService<IAgentControlService>();

        var parent = await orchestrator.CreateWorkUnitAsync("Build Foo then Bar", "test");

        // Seed orchestrator credentials so fan-out can enqueue workers.
        await agentControl.SpawnAsync(
            "orchestrator", parent.WorkUnitId,
            model: "fake", baseUrl: "http://fake", apiKey: "fake");

        var planJson = """
            {
              "slices": [
                {
                  "sliceId": "s1",
                  "goal": "Implement Foo.cs",
                  "fileScope": ["src/Foo.cs"],
                  "dependsOn": [],
                  "steps": ["Create Foo.cs"]
                },
                {
                  "sliceId": "s2",
                  "goal": "Add Bar.cs",
                  "fileScope": ["src/Bar.cs"],
                  "dependsOn": ["s1"],
                  "steps": ["Create Bar.cs"]
                }
              ]
            }
            """;

        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}",
            ArtifactType.Plan,
            parent.WorkUnitId,
            StudioArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            parent.WorkUnitId,
            null,
            "Plan",
            planJson));

        // FanOutService.TryFanOutFromPlanAsync is idempotent and gated per parent (see
        // FanOutService._parentGates) precisely because the orchestrator loop spawned above also
        // calls it itself once it reaches end_turn (ImmediateEndTurnLlmHandler makes that
        // near-instant). Either this call or that background one can be the one that actually
        // creates the children, so assert on the converged final state rather than on which
        // caller's own result happened to report ChildrenCreated/EnqueuedWorkUnitIds.
        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        Assert.Equal(2, children.Count);
        var s1 = children.Single(c => c.FanOutInfo?.SliceId == "s1");
        var s2 = children.Single(c => c.FanOutInfo?.SliceId == "s2");

        Assert.Equal(WorkUnitStatus.Queued, s1.Status);
        Assert.Equal(WorkUnitStatus.Created, s2.Status);
        Assert.DoesNotContain(s2.WorkUnitId, result.EnqueuedWorkUnitIds);

        await workUnits.UpdateStatusAsync(s1.WorkUnitId, WorkUnitStatus.Executing);
        await workUnits.UpdateStatusAsync(s1.WorkUnitId, WorkUnitStatus.Proposed);

        // Phase 12 — a dependent is gated on Merged, not Proposed: a proposal awaiting review
        // isn't real content yet. Confirm the tighter gate actually holds s2 back here, then
        // advance s1 the rest of the way and confirm s2 becomes enqueueable only once it does.
        var stillWaitingResult = await fanOut.TryEnqueueReadyDependentsAsync(parent.WorkUnitId);
        Assert.DoesNotContain(s2.WorkUnitId, stillWaitingResult.EnqueuedWorkUnitIds);
        var s2StillWaiting = await workUnits.GetAsync(s2.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Created, s2StillWaiting!.Status);

        await workUnits.UpdateStatusAsync(s1.WorkUnitId, WorkUnitStatus.Merged);

        var dependentResult = await fanOut.TryEnqueueReadyDependentsAsync(parent.WorkUnitId);

        Assert.Contains(s2.WorkUnitId, dependentResult.EnqueuedWorkUnitIds);
        var s2Updated = await workUnits.GetAsync(s2.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Queued, s2Updated!.Status);
    }

    [Fact]
    public async Task TryFanOutFromPlan_refreshes_dependent_branch_from_merged_dependency()
    {
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator   = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var fileWorkspace   = app.Services.GetRequiredService<IFileWorkspaceService>();
        var artifacts       = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut          = app.Services.GetRequiredService<IFanOutService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var mergeCommands   = app.Services.GetRequiredService<IMergeCommandService>();

        var parent = await orchestrator.CreateWorkUnitAsync(
            "Build EncryptionService and a validator, then wire both up", "test");

        await agentControl.SpawnAsync(
            "orchestrator", parent.WorkUnitId,
            model: "fake", baseUrl: "http://fake", apiKey: "fake");

        // s3 dependsOn BOTH s1 and s2 — disjoint producers, disjoint files, zero overlap between
        // any pair. This is the shape that would expose a destructive "copy whole branch" refresh:
        // applying s2's branch after s1's would have to either skip s1's file or delete it,
        // there's no way a full-mirror copy could end up with both.
        var planJson = """
            {
              "slices": [
                {
                  "sliceId": "s1",
                  "goal": "Introduce EncryptionService",
                  "fileScope": ["src/security/EncryptionService.cs"],
                  "dependsOn": [],
                  "steps": ["Create EncryptionService.cs"]
                },
                {
                  "sliceId": "s2",
                  "goal": "Introduce InputValidator",
                  "fileScope": ["src/security/InputValidator.cs"],
                  "dependsOn": [],
                  "steps": ["Create InputValidator.cs"]
                },
                {
                  "sliceId": "s3",
                  "goal": "Wire controllers to EncryptionService and InputValidator",
                  "fileScope": ["src/api/SecureController.cs"],
                  "dependsOn": ["s1", "s2"],
                  "steps": ["Create SecureController.cs using both"]
                }
              ]
            }
            """;

        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}",
            ArtifactType.Plan,
            parent.WorkUnitId,
            StudioArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            parent.WorkUnitId,
            null,
            "Plan",
            planJson));

        await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        var s1 = children.Single(c => c.FanOutInfo?.SliceId == "s1");
        var s2 = children.Single(c => c.FanOutInfo?.SliceId == "s2");
        var s3 = children.Single(c => c.FanOutInfo?.SliceId == "s3");

        // s1 and s2 each "produce" their own file on their own branch, then merge — via the real
        // propose/review/apply command path (not a manually-flipped status), so each has an actual
        // MergeProposal artifact with a real FilesTouched list for the refresh step to read.
        await MergeWorkUnitAsync(workUnits, fileWorkspace, mergeCommands, s1, "src/security/EncryptionService.cs", "public class EncryptionService {}");
        await MergeWorkUnitAsync(workUnits, fileWorkspace, mergeCommands, s2, "src/security/InputValidator.cs", "public class InputValidator {}");

        await fanOut.TryEnqueueReadyDependentsAsync(parent.WorkUnitId);

        // s3's branch never declared an interest in either path (both are outside s3's own
        // fileScope), yet both must be present once s3 actually starts — and, critically, BOTH at
        // once: refreshing from s2 must not have wiped out what the s1 refresh just copied in.
        var encryptionInS3 = await fileWorkspace.ReadAsync(s3.BranchId, "src/security/EncryptionService.cs");
        var validatorInS3 = await fileWorkspace.ReadAsync(s3.BranchId, "src/security/InputValidator.cs");
        Assert.Equal("public class EncryptionService {}", encryptionInS3);
        Assert.Equal("public class InputValidator {}", validatorInS3);
    }

    private static async Task MergeWorkUnitAsync(
        IWorkUnitService workUnits,
        IFileWorkspaceService fileWorkspace,
        IMergeCommandService mergeCommands,
        WorkUnit unit,
        string path,
        string content)
    {
        await workUnits.UpdateStatusAsync(unit.WorkUnitId, WorkUnitStatus.Executing);
        await fileWorkspace.WriteAsync(unit.BranchId, path, content);

        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: unit.BranchId,
            targetBranch: "main",
            summary: $"Implement {unit.Goal}",
            workUnitId: unit.WorkUnitId);

        await mergeCommands.ValidateAsync(proposal.ProposalId);
        await mergeCommands.ReviewAsync(proposal.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposal.ProposalId);
    }
}
