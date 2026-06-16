using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class FanOutServiceTests
{
    [Fact]
    public async Task TryFanOutFromPlan_enqueues_only_slices_with_satisfied_dependencies()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator  = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits     = app.Services.GetRequiredService<IWorkUnitService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
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

        await fileWorkspace.WriteAsync(parent.BranchId, PlanDocumentPaths.FileName, planJson);

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        Assert.Contains(FanOutAction.ChildrenCreated, result.Actions);
        Assert.Single(result.EnqueuedWorkUnitIds);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        var s1 = children.Single(c => c.Metadata?[WorkUnitMetadataKeys.SliceId] == "s1");
        var s2 = children.Single(c => c.Metadata?[WorkUnitMetadataKeys.SliceId] == "s2");

        Assert.Equal(WorkUnitStatus.Queued, s1.Status);
        Assert.Equal(WorkUnitStatus.Created, s2.Status);
        Assert.DoesNotContain(s2.WorkUnitId, result.EnqueuedWorkUnitIds);

        await workUnits.UpdateStatusAsync(s1.WorkUnitId, WorkUnitStatus.Executing);
        await workUnits.UpdateStatusAsync(s1.WorkUnitId, WorkUnitStatus.Proposed);

        var dependentResult = await fanOut.TryEnqueueReadyDependentsAsync(parent.WorkUnitId);

        Assert.Contains(s2.WorkUnitId, dependentResult.EnqueuedWorkUnitIds);
        var s2Updated = await workUnits.GetAsync(s2.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Queued, s2Updated!.Status);
    }
}
