using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) — spawns the real
// `codex` CLI (codex-cli) in a branch's materialized working directory and harvests a run summary
// from its `codex exec --json` output. Field shapes are grounded in real
// `codex exec --json --skip-git-repo-check` captures against codex-cli 0.144.1 (2026-07-12) — see
// this repo's codex-probe/capture-2..5. Mirrors ClaudeCodeExecutor's shape (workspace contract
// materialization, thin stdout pump, shared HarnessHarvestPipeline) — the differences that remain
// (no --settings allowlist file, no MCP mount, exit-code-only success judgment, "exec resume
// <threadId>" argument ordering, mandatory stdin close) are all things the real CLI captures proved,
// not assumptions carried over from claude.
internal sealed class CodexCliExecutor(
    IFileWorkspaceService fileWorkspace,
    IWorkUnitService workUnits,
    IWorkspaceContractService workspaceContracts,
    IConversationLogService conversationLog,
    IRepositoryRegistryService repositoryRegistry,
    HarnessHarvestPipeline harvest,
    CodexCliExecutorOptions options,
    ILogger<CodexCliExecutor> logger) : IHarnessExecutor
{
    public string Name => "codex";

    public string DisplayName => "Codex CLI";

    public string? ProviderKey => "codex-cli";

    // Verified 2026-07-12 against codex-cli 0.144.1 (codex-probe captures, see this file's header):
    // turn.completed carries usage tokens on every successful run -> SupportsTurnTelemetry true;
    // `codex exec resume <thread_id>` verified working (capture-5-resume, same thread_id echoed,
    // context retained) -> SupportsResume true. SupportsMcp is NOT verified by any capture in this
    // session — left false rather than assumed from codex's public docs (the task's own ground rule:
    // no training-data assumptions about the CLI). SupportsHooks/SupportsSubagents: no equivalent
    // observed in any capture either. plans/phase-d-implementation.md D1.b — SupportsPlanningMode
    // flips true: writing a JSON file (.workspace/plan.json) is within codex's verified abilities
    // (it already writes files under `-s workspace-write`/`danger-full-access`); no new CLI feature
    // is assumed, unlike hooks/subagents/MCP above which would be.
    // plans/review-seam-and-clarification-sessions.md S2 — SupportsReviewMode flips true with
    // Mode==Review wired: like Plan mode, reading/writing a JSON contract file is within codex's
    // verified abilities; enforcement is prompt-only (no --settings equivalent), backstopped by
    // HarnessHarvestPipeline.HarvestReviewAsync judging only the verdict file.
    public HarnessCapabilities Capabilities { get; } = new(
        SupportsTurnTelemetry: true, SupportsResume: true, SupportsHooks: false,
        SupportsSubagents: false, SupportsMcp: false, SupportsPlanningMode: true,
        SupportsReviewMode: true);

    // WorkUnit.Metadata key the codex CLI's own thread id is persisted under between runs — mirrors
    // ClaudeCodeExecutor's "claudeCodeSessionId" convention (additive Metadata entry, no new typed
    // field on WorkUnit).
    private const string HarnessThreadMetadataKey = "codexThreadId";

    public async Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken ct = default)
    {
        var wu = await workUnits.GetAsync(request.WorkUnitId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Work unit '{request.WorkUnitId}' was not found.");
        var branchId = wu.BranchId;

        var workDir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Branch '{branchId}' has no working directory.");

        // Materializes .workspace/{manifest,goal,workunit,state,constraints,review-policy}.json
        // (+ .md siblings) — same runtime->harness contract claude-code uses; codex reads the same
        // files via its own file tools, per the shared kickoff prompt below.
        await workspaceContracts.MaterializeAsync(request.WorkUnitId, ct).ConfigureAwait(false);

        // plans/review-seam-and-clarification-sessions.md S2 — same Review-mode contract
        // materialization as ClaudeCodeExecutor: proposal metadata + diff framing on disk before
        // the spawn; no reviewable proposal is terminal, not worth a spawn.
        if (request.Mode == HarnessMode.Review)
        {
            var reviewSetupFailure = await harvest.MaterializeReviewRequestAsync(request, branchId, ct).ConfigureAwait(false);
            if (reviewSetupFailure is not null)
                return reviewSetupFailure;
        }

        var addDirRoots = await ResolveAddDirRootsAsync(wu.ReferenceFiles, ct).ConfigureAwait(false);

        // Resume identity — only reused on an attested resume (IsResume), not on every run, same
        // gating as ClaudeCodeExecutor's resumeSessionId.
        var resumeThreadId = request.IsResume && wu.Metadata is { } metadata &&
            metadata.TryGetValue(HarnessThreadMetadataKey, out var priorThreadId)
                ? priorThreadId
                : null;

        var psi = BuildProcessStartInfo(workDir, request, addDirRoots, resumeThreadId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{options.ExecutablePath}'.");

        // Verified finding (codex-probe capture-1) — stdin MUST be closed. With stdin attached codex
        // prints "Reading additional input from stdin..." and can hang the run indefinitely (the
        // unclosed capture hung 5 minutes before being killed manually). Redirecting stdin and
        // closing it immediately gives codex a closed pipe instead of an open, empty console handle.
        process.StandardInput.Close();

        var parser = CodexTranscriptParser.Create(request.OnActivity);

        // Drained concurrently with stdout — same rationale as ClaudeCodeExecutor: stderr is the
        // only diagnostic a nonzero exit produces, and an undrained redirected pipe can deadlock.
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false)) is not null)
                parser.Accept(line);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var partial = parser.BuildSummary();
            await PersistHarnessThreadIdAsync(request, partial.ThreadId, ct).ConfigureAwait(false);
            return new HarnessRunResult(
                AgentLoopCompletion.MaxIterationsExceeded,
                $"codex CLI run exceeded the {options.TimeoutSeconds}s wall-clock limit.",
                HarnessSessionId: partial.ThreadId);
        }

        var summary = parser.BuildSummary();
        await PersistHarnessThreadIdAsync(request, summary.ThreadId, ct).ConfigureAwait(false);
        await RecordConversationLogAsync(request, summary, ct).ConfigureAwait(false);

        // codex's JSON has no terminal "result"/subtype/is_error event (unlike claude) and no cost
        // field (ChatGPT-seat auth, not billed-per-token API auth) — success is judged purely by
        // process exit code, per the task brief's verified findings. CostUsd stays null throughout.
        if (process.ExitCode != 0)
        {
            var stderrTail = await ClaudeCodeExecutor.ReadStderrTailAsync(stderrTask).ConfigureAwait(false);
            logger.LogWarning(
                "[codex] workUnitId={WorkUnitId} exited with code {ExitCode}. stderr: {Stderr}",
                request.WorkUnitId, process.ExitCode, stderrTail ?? "(empty)");
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                $"codex CLI exited with code {process.ExitCode}." +
                    (stderrTail is null ? "" : $" stderr: {stderrTail}"),
                summary.ResultText, summary.InputTokens, summary.OutputTokens, CostUsd: null, summary.ThreadId);
        }

        return await harvest.HarvestAsync(
            request, branchId, summary.ResultText, summary.InputTokens, summary.OutputTokens,
            totalCostUsd: null, summary.ThreadId, Name, "openai", ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> ResolveAddDirRootsAsync(
        IReadOnlyList<FileReferenceV1>? referenceFiles, CancellationToken ct)
    {
        // Cross-repo pointers (WorkUnit.ReferenceFiles) live outside the branch workdir — one
        // --add-dir per distinct registered repository, mirroring ClaudeCodeExecutor's own
        // reference-file loop (minus the settings-file allow entry, since codex has no equivalent
        // generated-allowlist file — its own sandbox flag (-s) is the write gate here).
        var addDirRoots = new List<string>();
        if (referenceFiles is { Count: > 0 })
        {
            var repositoryIds = referenceFiles.Select(f => f.RepositoryId).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var repositoryId in repositoryIds)
            {
                var repository = await repositoryRegistry.GetAsync(repositoryId, ct).ConfigureAwait(false);
                if (repository is null)
                    continue;
                addDirRoots.Add(repository.Path);
            }
        }
        return addDirRoots;
    }

    private async Task PersistHarnessThreadIdAsync(HarnessRunRequest request, string? threadId, CancellationToken ct)
    {
        if (threadId is null)
            return;

        try
        {
            await workUnits.SetMetadataAsync(request.WorkUnitId, HarnessThreadMetadataKey, threadId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let telemetry/resume bookkeeping fail the run itself — same posture as
            // ClaudeCodeExecutor.PersistHarnessSessionIdAsync.
            logger.LogWarning(ex, "[codex] failed to persist harness thread id for workUnitId={WorkUnitId}", request.WorkUnitId);
        }
    }

    private async Task RecordConversationLogAsync(
        HarnessRunRequest request, CodexTranscriptRunSummary summary, CancellationToken ct)
    {
        var entries = CodexConversationLogMapper.BuildEntries(
            summary, request.WorkUnitId, request.AgentId, request.TaskId, Name, request.Mode);

        try
        {
            foreach (var entry in entries)
                await conversationLog.RecordAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let telemetry recording fail the run itself.
            logger.LogWarning(ex, "[codex] failed to record conversation log for workUnitId={WorkUnitId}", request.WorkUnitId);
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* best-effort — the process may have already exited */ }
    }

    private ProcessStartInfo BuildProcessStartInfo(
        string workDir, HarnessRunRequest request, IReadOnlyList<string> addDirRoots, string? resumeThreadId)
    {
        // Studio-controlled, short, fixed prompt — same reasoning as ClaudeCodeExecutor's own kickoff
        // prompt: the actual goal/context lives in .workspace/*.md, keeping the CLI-argument-quoting
        // surface free of arbitrary user content. codex has no MCP mount (Capabilities.SupportsMcp is
        // false — unverified), so unlike claude's prompt this never mentions mounted tools.
        //
        // plans/phase-d-implementation.md D1.b — Mode==Plan gets a distinct kickoff, mirroring
        // ClaudeCodeExecutor's own Plan-mode prompt. Unlike claude-code, codex has no per-path
        // --settings allowlist to mechanically restrict writes to .workspace/plan.json — this
        // prompt is the only "implement nothing" enforcement on codex's side, backstopped by
        // HarnessHarvestPipeline.HarvestPlanAsync discarding any diff it finds anyway.
        // plans/review-seam-and-clarification-sessions.md S2 — Review kickoff mirrors
        // ClaudeCodeExecutor's word-for-word; with no --settings allowlist on codex, this prompt
        // (plus the harvest judging only .workspace/review.json) is the whole enforcement.
        var prompt = request.Mode == HarnessMode.Review
            ? "You are reviewing a merge proposal. Read .workspace/goal.md, .workspace/workunit.md, " +
              "and .workspace/review-request.json in this directory — this working directory " +
              "contains the PROPOSED state of the branch, and review-request.json carries the " +
              "proposal's summary, files touched, and diff against the target. Inspect the changed " +
              "files, check them against the goal and any recorded constraints, and run the " +
              "project's build/test commands if available to verify. Then write your verdict to " +
              ".workspace/review.json as JSON matching this shape exactly: " +
              "{\"decision\":\"Approved\",\"verificationResults\":\"...\"} — decision must be " +
              "\"Approved\" or \"Rejected\"; verificationResults is required, and on Rejected it " +
              "is the ONLY explanation the retried worker will see, so be specific about what to " +
              "fix. Do NOT create, edit, or delete any other file. Record any Research/Decision/" +
              "Constraint knowledge via .workspace/decisions/, and blocking questions via " +
              ".workspace/inbox/."
            : request.Mode == HarnessMode.Plan
            ? "Read .workspace/goal.md, .workspace/workunit.md, and .workspace/state.md in this " +
              "directory, then decompose the work into slices. Write your plan to " +
              ".workspace/plan.json as JSON matching this shape exactly: " +
              "{\"slices\":[{\"sliceId\":\"s1\",\"goal\":\"...\",\"fileScope\":[\"path/to/file\"]," +
              "\"dependsOn\":[],\"steps\":[\"...\"]}]} — sliceId and goal are required on every " +
              "slice; fileScope/dependsOn/steps may be empty arrays. Do NOT edit, create, or " +
              "delete any other file — implement nothing, only plan. Record any Research/" +
              "Decision/Constraint knowledge via .workspace/decisions/, and blocking questions " +
              "via .workspace/inbox/."
            : "Read .workspace/goal.md, .workspace/workunit.md, and .workspace/state.md in this " +
              "directory, then complete the work described there. Record any Research/Decision/" +
              "Constraint knowledge via .workspace/decisions/, and blocking questions via " +
              ".workspace/inbox/. If you are resuming and were waiting on a question, check " +
              ".workspace/outbox/ for the answer before asking again.";

        // Verified invocation shape (codex-probe captures): `codex exec [resume <thread_id>] --json
        // --skip-git-repo-check -C <workdir> -s <sandbox> [-m <model>] [--add-dir <dir>] "<prompt>"`.
        // "exec resume <thread_id>" — resume is a positional subcommand of exec, not a flag like
        // claude's --resume; ordering matters (capture-5-resume: `codex exec resume <id> --json
        // --skip-git-repo-check "<prompt>"`).
        var args = new List<string> { "exec" };
        if (!string.IsNullOrEmpty(resumeThreadId))
        {
            args.Add("resume");
            args.Add(resumeThreadId);
        }

        args.Add("--json");
        args.Add("--skip-git-repo-check");
        args.Add("-C");
        args.Add(workDir);
        args.Add("-s");
        args.Add(options.SandboxMode);

        // Model Profile-driven model selection, same "blank = CLI default" convention claude-cli
        // uses.
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.Add("-m");
            args.Add(request.Model);
        }

        foreach (var addDirRoot in addDirRoots)
        {
            args.Add("--add-dir");
            args.Add(addDirRoot);
        }

        args.Add(prompt);

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Windows resolves `codex` (npm-installed, like `claude`) to a *.cmd shim only cmd.exe knows
        // how to launch directly — same reasoning as ClaudeCodeExecutor.BuildProcessStartInfo. Kept
        // uniform (always wrapped) rather than special-cased so the wrapping logic itself gets
        // exercised by the stub-CLI tests too.
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(options.ExecutablePath);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
        }
        else
        {
            psi.FileName = options.ExecutablePath;
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
        }

        foreach (var key in new[] { "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS" })
            psi.EnvironmentVariables.Remove(key);

        // "ambient auth, key opt-in" mirrors claude-cli's ANTHROPIC_API_KEY rule (see
        // ClaudeCodeExecutor.BuildProcessStartInfo's own comment) — NOT independently verified
        // against a real codex run in this session (all captures used ambient ChatGPT-seat login,
        // which is the verified default); this mirrors the established pattern rather than a
        // confirmed codex-specific env var name.
        var injectKey = request.Profile?.InjectApiKeyEnv == true ||
            string.Equals(request.Provider, ProviderKey, StringComparison.OrdinalIgnoreCase);
        if (injectKey && !string.IsNullOrEmpty(request.ApiKey))
            psi.EnvironmentVariables["OPENAI_API_KEY"] = request.ApiKey;

        return psi;
    }
}
