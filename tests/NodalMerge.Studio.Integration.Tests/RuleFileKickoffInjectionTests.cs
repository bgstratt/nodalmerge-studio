using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 9h — a root's AGENTS.md-equivalent must reach the agent through the kickoff message,
/// not just sit inert in the WorkspaceProfile. Drives a real WorkerAgentLoop spawn (via
/// InMemoryAgentRuntimeService.StartWorkerLoop) through a capturing fake LLM handler and asserts
/// the first request's kickoff text actually contains the rule file's content.
/// </summary>
[Trait("Category", "Integration")]
public class RuleFileKickoffInjectionTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-rule-file-kickoff-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private sealed class CapturingLlmHandler : HttpMessageHandler
    {
        public string? FirstKickoffMessage { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var messages = doc.RootElement.GetProperty("messages");

            if (FirstKickoffMessage is null)
                FirstKickoffMessage = messages[0].GetProperty("content").GetString();

            var json = JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = "Done." } },
                stop_reason = "end_turn"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task Worker_kickoff_message_includes_the_branchs_rule_file_content()
    {
        var fakeHandler = new CapturingLlmHandler();
        await using var app = StudioWebApplication.Build(
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
            Goal: "Update the API", Owner: "test-owner", BranchId: "wu-rule-file-kickoff"));
        var task = await taskCommands.CreateAsync(new TaskCreateCommand(
            wu.WorkUnitId, "Update the API", "test task"));

        await fileWorkspace.WriteAsync(wu.BranchId, "backend/Host.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        await fileWorkspace.WriteAsync(wu.BranchId, "backend/AGENTS.md",
            "Never touch the legacy payments module.");

        await agentControl.SpawnAsync(
            agentType: "worker",
            workUnitId: wu.WorkUnitId,
            taskId: task.TaskId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        var kickoff = await PollForKickoffAsync(fakeHandler);

        Assert.NotNull(kickoff);
        Assert.Contains("backend", kickoff);
        Assert.Contains("Never touch the legacy payments module.", kickoff);
    }

    private static async Task<string?> PollForKickoffAsync(CapturingLlmHandler handler, int timeoutSeconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (handler.FirstKickoffMessage is not null) return handler.FirstKickoffMessage;
            await Task.Delay(50);
        }
        return handler.FirstKickoffMessage;
    }
}
