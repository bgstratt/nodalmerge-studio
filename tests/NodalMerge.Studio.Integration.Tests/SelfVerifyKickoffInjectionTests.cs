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
/// Phase 9i — WorkspaceOptions.RequireBuildBeforeProposal/RequireTestBeforeProposal (the existing
/// Phase 6.6 policy-gate flags) now also drive an extra kickoff-message instruction telling the
/// worker to self-verify before proposing, instead of only failing the merge after the fact. Zero
/// behavioral change when both flags are off — asserted explicitly below.
/// </summary>
[Trait("Category", "Integration")]
public class SelfVerifyKickoffInjectionTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-self-verify-{Guid.NewGuid():N}");

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

    private async Task<string?> SpawnWorkerAndCaptureKickoffAsync(WorkspaceOptions options)
    {
        var fakeHandler = new CapturingLlmHandler();
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(options);
            });

        var workUnits    = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var taskCommands = app.Services.GetRequiredService<ITaskCommandService>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();

        var wu = await workUnits.CreateAsync(new WorkUnitCreateCommand(
            Goal: "Update the API", Owner: "test-owner", BranchId: $"wu-self-verify-{Guid.NewGuid():N}"));
        var task = await taskCommands.CreateAsync(new TaskCreateCommand(
            wu.WorkUnitId, "Update the API", "test task"));

        await agentControl.SpawnAsync(
            agentType: "worker",
            workUnitId: wu.WorkUnitId,
            taskId: task.TaskId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        return await PollForKickoffAsync(fakeHandler);
    }

    [Fact]
    public async Task Both_flags_off_leaves_kickoff_message_unchanged()
    {
        var kickoff = await SpawnWorkerAndCaptureKickoffAsync(new WorkspaceOptions { RootPath = _rootPath });

        Assert.NotNull(kickoff);
        Assert.DoesNotContain("requires a passing", kickoff);
        Assert.DoesNotContain("nm_v1_workspace_build", kickoff);
    }

    [Fact]
    public async Task RequireBuildBeforeProposal_adds_build_only_instruction()
    {
        var kickoff = await SpawnWorkerAndCaptureKickoffAsync(new WorkspaceOptions
        {
            RootPath = _rootPath,
            RequireBuildBeforeProposal = true,
        });

        Assert.NotNull(kickoff);
        Assert.Contains("requires a passing build before a merge proposal is accepted", kickoff);
        Assert.Contains("nm_v1_workspace_build", kickoff);
        Assert.DoesNotContain("build and test", kickoff);
    }

    [Fact]
    public async Task Both_flags_on_adds_combined_build_and_test_instruction()
    {
        var kickoff = await SpawnWorkerAndCaptureKickoffAsync(new WorkspaceOptions
        {
            RootPath = _rootPath,
            RequireBuildBeforeProposal = true,
            RequireTestBeforeProposal = true,
        });

        Assert.NotNull(kickoff);
        Assert.Contains("requires a passing build and test before a merge proposal is accepted", kickoff);
        Assert.Contains("nm_v1_workspace_build", kickoff);
        Assert.Contains("nm_v1_workspace_test", kickoff);
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
