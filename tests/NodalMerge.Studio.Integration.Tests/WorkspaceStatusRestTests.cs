using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class WorkspaceStatusRestTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-status-{Guid.NewGuid():N}");

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
    public async Task Workspace_status_reports_changed_files_and_proposal_summary()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();

        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "src/keep.cs", "class Keep { }");
        await fileWorkspace.WriteAsync("main", "src/remove.cs", "class Remove { }");

        var workUnit = await orchestrator.CreateWorkUnitAsync(
            "Capture workspace status",
            "tester",
            seedFromBranchId: "main");

        await fileWorkspace.InitBranchAsync(workUnit.BranchId, seedFromBranchId: "main");
        await fileWorkspace.WriteAsync(workUnit.BranchId, "src/keep.cs", "class Keep { int Value => 1; }");
        await fileWorkspace.DeleteAsync(workUnit.BranchId, "src/remove.cs");
        await fileWorkspace.WriteAsync(workUnit.BranchId, "src/add.cs", "class Add { }");

        var proposal = await mergeCommands.ProposeAsync(
            workUnit.BranchId,
            "main",
            "Capture workspace status",
            workUnitId: workUnit.WorkUnitId);

        Assert.Equal(MergeProposalStatus.Draft, proposal.Status);

        var client = app.GetTestClient();
        var response = await client.GetAsync($"/studio/workspace-status?workUnitId={workUnit.WorkUnitId}&limit=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Match the host's ConfigureHttpJsonOptions (StudioServiceCollectionExtensions.cs): camelCase
        // property names (JsonSerializerOptions.Web) plus JsonStringEnumConverter for enum properties
        // — ReadFromJsonAsync's bare default options expect PascalCase names and numeric enums.
        var jsonOptions = new JsonSerializerOptions(JsonSerializerOptions.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var status = await response.Content.ReadFromJsonAsync<WorkspaceStatus>(jsonOptions);
        Assert.NotNull(status);
        Assert.Equal(workUnit.BranchId, status!.BranchId);
        Assert.Equal(workUnit.WorkUnitId, status.WorkUnitId);
        Assert.NotEmpty(status.ProposalSummaries);

        var summary = Assert.Single(status.ProposalSummaries);
        Assert.Equal(proposal.ProposalId, summary.ProposalId);
        Assert.Equal(MergeProposalStatus.Draft, summary.Status);

        Assert.Contains(status.ChangedFiles, file => file.Path == "src/add.cs" && file.ChangeKind == WorkspaceChangeKind.Added);
        Assert.Contains(status.ChangedFiles, file => file.Path == "src/keep.cs" && file.ChangeKind == WorkspaceChangeKind.Modified);
        Assert.Contains(status.ChangedFiles, file => file.Path == "src/remove.cs" && file.ChangeKind == WorkspaceChangeKind.Deleted);
        Assert.True(status.DiffStats is not null);
        Assert.True(status.DiffStats!.AddedFiles >= 1);
        Assert.True(status.DiffStats.ModifiedFiles >= 1);
        Assert.True(status.DiffStats.DeletedFiles >= 1);
    }
}
