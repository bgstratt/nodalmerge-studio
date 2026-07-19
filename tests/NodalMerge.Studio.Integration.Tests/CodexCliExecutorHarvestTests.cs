using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) —
/// CodexCliExecutor's harvest pipeline against a stub CLI, proving the extracted
/// <see cref="HarnessHarvestPipeline"/> genuinely works for a second adapter (the "seam isn't
/// Claude-shaped" requirement) — mirrors one case from ClaudeCodeExecutorHarvestTests.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "LocalCliProcess")]
public class CodexCliExecutorHarvestTests : IDisposable
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"codex-harvest-stub-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_stubDir))
            Directory.Delete(_stubDir, recursive: true);
    }

    private string WriteStub(string name, string script)
    {
        Directory.CreateDirectory(_stubDir);
        var path = Path.Combine(_stubDir, name);
        File.WriteAllText(path, script);
        return path;
    }

    private static string SuccessJsonl(string threadId = "stub-thread-harvest") => string.Join('\n',
        $$"""{"type":"thread.started","thread_id":"{{threadId}}"}""",
        """{"type":"turn.started"}""",
        """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Implemented the change."}}""",
        """{"type":"turn.completed","usage":{"input_tokens":50,"output_tokens":10}}""");

    private IServiceProvider BuildApp(string executablePath)
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ScriptedLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new CodexCliExecutorOptions { ExecutablePath = executablePath, TimeoutSeconds = 30 });
            });
        return app.Services;
    }

    [Fact]
    public async Task FullCycle_stub_edit_reaches_ReadyForReview_and_can_be_approved_and_applied()
    {
        var jsonlPath = Path.Combine(_stubDir, "output.jsonl");
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(jsonlPath, SuccessJsonl());
        var stub = WriteStub("stub-codex.cmd",
            "@echo off\r\nset /p dummy=\r\necho real change > change.txt\r\ntype \"%~dp0output.jsonl\"\r\n");

        var services = BuildApp(stub);
        var orchestratorSvc = services.GetRequiredService<IOrchestratorService>();
        var fileWorkspace = services.GetRequiredService<IFileWorkspaceService>();
        var merge = services.GetRequiredService<IMergeService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise the C2 harvest pipeline", "integration-test");
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        var executor = resolver.Resolve("codex");
        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-harvest-1", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: "session-harvest-1", IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);

        var proposals = await merge.ListAsync();
        var proposal = Assert.Single(proposals, p => p.WorkUnitId == wu.WorkUnitId);
        Assert.Equal(MergeProposalStatus.ReadyForReview, proposal.Status);
        Assert.Equal("agent-codex-harvest-1", proposal.AgentId);
        Assert.Equal("codex", proposal.Model);

        var approved = await merge.ReviewAsync(proposal.ProposalId, MergeProposalStatus.Approved);
        var merged = await merge.ApplyAsync(approved.ProposalId);
        Assert.Equal(MergeProposalStatus.Merged, merged.Status);
    }
}
