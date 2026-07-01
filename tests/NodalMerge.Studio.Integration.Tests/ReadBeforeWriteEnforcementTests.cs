using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 9g — McpToolDispatcher.WorkspaceWriteAsync must reject overwriting a file that already
/// has content the calling agent never read through nm_v1_workspace_read (or itself just wrote)
/// in this branch. Drives a real WorkerAgentLoop through the actual singleton dispatcher (via
/// ReadBeforeWriteLlmHandler, no real LLM) so this proves the rule end-to-end through the same
/// code path a real agent uses, not a hand-constructed fake.
/// </summary>
[Trait("Category", "Integration")]
public class ReadBeforeWriteEnforcementTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-read-before-write-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    [Fact]
    public async Task Write_to_existing_unread_file_is_blocked_then_succeeds_after_read()
    {
        var fakeHandler = new ReadBeforeWriteLlmHandler();
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
            });

        var workUnits     = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var taskCommands  = app.Services.GetRequiredService<ITaskCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var agentControl  = app.Services.GetRequiredService<IAgentControlService>();

        var wu = await workUnits.CreateAsync(new WorkUnitCreateCommand(
            Goal: "Update existing.txt", Owner: "test-owner", BranchId: "wu-read-before-write"));
        var task = await taskCommands.CreateAsync(new TaskCreateCommand(
            wu.WorkUnitId, "Update existing.txt", "test task"));

        // Seeded directly through IFileWorkspaceService, bypassing the dispatcher entirely — the
        // dispatcher's read cache must have no entry for this path, exactly like a file an agent
        // has never looked at through its tools.
        await fileWorkspace.WriteAsync(wu.BranchId, "existing.txt", "original content");

        await agentControl.SpawnAsync(
            agentType: "worker",
            workUnitId: wu.WorkUnitId,
            taskId: task.TaskId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        var content = await PollForContentAsync(fileWorkspace, wu.BranchId, "existing.txt", "updated content");
        var newFileContent = await PollForContentAsync(fileWorkspace, wu.BranchId, "new.txt", "brand new");

        Assert.True(fakeHandler.SawBlockedError, "Expected the first write attempt to be blocked with a read-before-write error.");
        Assert.Equal("updated content", content);
        // A genuinely new path (never existed) needs no prior read — only overwriting existing,
        // unread content is blocked.
        Assert.Equal("brand new", newFileContent);
    }

    private static async Task<string?> PollForContentAsync(
        IFileWorkspaceService fileWorkspace, string branchId, string path, string expected, int timeoutSeconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var content = await fileWorkspace.ReadAsync(branchId, path);
            if (content == expected) return content;
            await Task.Delay(50);
        }
        return await fileWorkspace.ReadAsync(branchId, path);
    }
}
