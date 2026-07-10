using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// nm_v1_workspace_read used to always return a file's full content, which could blow out an
/// agent's context on a large file. It now windows to the first 2000 lines by default and accepts
/// offset/limit to page further — this drives a real WorkerAgentLoop through the actual dispatcher
/// (via WorkspaceReadWindowingLlmHandler, no real LLM) to confirm both the default cap and paging
/// actually work end-to-end.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceReadWindowingTests
{
    [Fact]
    public async Task Large_file_read_is_windowed_by_default_and_pages_via_offset()
    {
        const int totalLines = 2500;
        var lines = Enumerable.Range(1, totalLines).Select(i => $"line-{i}").ToArray();
        var content = string.Join('\n', lines);

        var fakeHandler = new WorkspaceReadWindowingLlmHandler { Path = "big.txt", SecondOffset = 2001 };
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var workUnits     = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var taskCommands  = app.Services.GetRequiredService<ITaskCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var agentControl  = app.Services.GetRequiredService<IAgentControlService>();

        var wu = await workUnits.CreateAsync(new WorkUnitCreateCommand(
            Goal: "Read big.txt", Owner: "test-owner", BranchId: "wu-read-window"));
        var task = await taskCommands.CreateAsync(new TaskCreateCommand(
            wu.WorkUnitId, "Read big.txt", "test task"));

        await fileWorkspace.WriteAsync(wu.BranchId, "big.txt", content);

        await agentControl.SpawnAsync(
            agentType: "worker",
            workUnitId: wu.WorkUnitId,
            taskId: task.TaskId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        var secondResult = await PollForSecondResultAsync(fakeHandler);

        Assert.NotNull(fakeHandler.FirstReadResult);
        using var first = JsonDocument.Parse(fakeHandler.FirstReadResult!);
        Assert.True(first.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(totalLines, first.RootElement.GetProperty("totalLines").GetInt32());
        Assert.Equal(1, first.RootElement.GetProperty("startLine").GetInt32());
        Assert.Equal(2000, first.RootElement.GetProperty("endLine").GetInt32());
        var firstContent = first.RootElement.GetProperty("content").GetString()!;
        Assert.Equal(2000, firstContent.Split('\n').Length);
        Assert.StartsWith("line-1\n", firstContent, StringComparison.Ordinal);
        Assert.EndsWith("line-2000", firstContent, StringComparison.Ordinal);

        Assert.NotNull(secondResult);
        using var second = JsonDocument.Parse(secondResult!);
        var secondContent = second.RootElement.GetProperty("content").GetString()!;
        Assert.Equal(500, secondContent.Split('\n').Length); // lines 2001..2500
        Assert.StartsWith("line-2001\n", secondContent, StringComparison.Ordinal);
        Assert.EndsWith("line-2500", secondContent, StringComparison.Ordinal);
        // Window exactly reaches EOF, so no truncated=true on this page.
        Assert.False(second.RootElement.TryGetProperty("truncated", out _));
    }

    private static async Task<string?> PollForSecondResultAsync(
        WorkspaceReadWindowingLlmHandler handler, int timeoutSeconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (handler.SecondReadResult is not null) return handler.SecondReadResult;
            await Task.Delay(50);
        }
        return handler.SecondReadResult;
    }
}
