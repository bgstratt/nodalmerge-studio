using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase B.2 — spawns the real `claude` CLI in a branch's
// materialized working directory and harvests a run summary from its --output-format stream-json
// output. Field shapes below are grounded in one real `claude -p --output-format stream-json`
// invocation captured 2026-07-12 against claude 2.1.177 (see the plan's B2 section) — not the
// unverified assumptions in eval-harness/run-one.ps1. Parsing is deliberately defensive: unknown
// line types/fields are ignored rather than throwing, since the CLI's own JSON shape is not a
// frozen contract Studio controls.
internal sealed class ClaudeCodeExecutor(
    IFileWorkspaceService fileWorkspace,
    IWorkUnitService workUnits,
    IWorkspaceProfileService workspaceProfiles,
    IWorkspaceContractService workspaceContracts,
    IConversationLogService conversationLog,
    IRepositoryRegistryService repositoryRegistry,
    IHarnessMcpTokenService harnessMcpTokens,
    HarnessHarvestPipeline harvest,
    ClaudeCodeExecutorOptions options,
    ILogger<ClaudeCodeExecutor> logger,
    IExecutionEventStream? events = null,
    // Optional so direct-construction test call sites keep compiling — when null, Review mode
    // just falls back to wu.BranchId (today's, occasionally-wrong, behavior).
    IMergeService? merge = null) : IHarnessExecutor
{
    public string Name => "claude-code";

    public string DisplayName => "Claude Code CLI";

    public string? ProviderKey => "claude-cli";

    // Phase C.1 — turn telemetry (ClaudeTranscriptParser), resume (the CLI's own session_id +
    // --resume, shipped B3), hooks/subagents/MCP (the underlying `claude` binary's own features,
    // reachable via the generated --settings file today). plans/phase-d-implementation.md D1.b —
    // SupportsPlanningMode flips true now that Mode==Plan is wired: a different kickoff prompt
    // (write .workspace/plan.json, implement nothing) and a Write-only-plan.json settings
    // allowlist, both in BuildProcessStartInfo/WriteSettingsFileAsync below.
    // plans/review-seam-and-clarification-sessions.md S2 — SupportsReviewMode flips true with
    // Mode==Review wired: a review-request contract file in, a .workspace/review.json verdict out
    // (see BuildProcessStartInfo's Review kickoff + WriteSettingsFileAsync's Review allowlist).
    public HarnessCapabilities Capabilities { get; } = new(
        SupportsTurnTelemetry: true, SupportsResume: true, SupportsHooks: true,
        SupportsSubagents: true, SupportsMcp: true, SupportsPlanningMode: true,
        SupportsReviewMode: true);

    // WorkUnit.Metadata key the claude CLI's own session id is persisted under between runs —
    // additive, no new typed field, matching the "Metadata for genuine ad hoc/future use"
    // convention (see WorkUnit.cs).
    private const string HarnessSessionMetadataKey = "claudeCodeSessionId";

    // The server name the generated .workspace/mcp.json registers the "/mcp-harness" mount under —
    // also the name the settings allowlist's "mcp__<server>" entry must reference, so the two
    // generators below must agree.
    private const string HarnessMcpServerName = "nodalmerge-harness";

    // Must match WorkspaceContractService.MaterializeAsync's own stem list exactly (manifest,
    // goal, workunit, state, constraints, review-policy) — see this file's RunAsync for why.
    private static readonly string[] ContractFileStems =
        ["manifest", "goal", "workunit", "state", "constraints", "review-policy"];

    public async Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken ct = default)
    {
        var wu = await workUnits.GetAsync(request.WorkUnitId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Work unit '{request.WorkUnitId}' was not found.");
        var branchId = wu.BranchId;

        // Reviewing a goal-level RECONCILED proposal: proposal.SourceBranch is
        // merge/{parentWorkUnitId} — a different branch from the owning work unit's own BranchId
        // (its pristine, pre-reconciliation content). For an ordinary per-task proposal the two
        // coincide by construction, so this was invisible there; only reconciled proposals actually
        // differ. Using wu.BranchId unconditionally materialized the reviewer into the WRONG
        // directory — one that never received the reconciled changes — producing a false "the file
        // doesn't exist" rejection even though NodalMerge's own diff engine (proposal.
        // WorkspaceChanges) correctly showed the content present in the real source branch. Found
        // live 2026-07-13 on a reconciled welcome-endpoint proposal.
        if (request.Mode == HarnessMode.Review && merge is not null)
        {
            // TaskId doubles as the proposal id for a Review-mode request (see
            // InlineReviewerService/AutomatedReviewGateService's enqueue call sites — both pass the
            // MergeProposal's own id positionally where an Execute-mode request would pass a task id).
            var proposal = await merge.GetAsync(request.TaskId, ct).ConfigureAwait(false);
            if (proposal is not null)
                branchId = proposal.SourceBranch;
        }

        // Same "resolve the real on-disk directory" call WorkspaceExecutionService/
        // WorkspaceCacheManager already use — no new plumbing needed for cwd resolution.
        var workDir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Branch '{branchId}' has no working directory.");

        // Materializes .workspace/{manifest,goal,workunit,state,constraints,review-policy}.json
        // (+ .md siblings) — the runtime→harness half of the contract, built in Phase A.
        // IWorkspaceContractService.MaterializeAsync always writes into wu.BranchId internally
        // (it takes a workUnitId, not a branchId, and has no override) — when branchId above was
        // redirected to proposal.SourceBranch (a reconciled review), the CLI's actual cwd is a
        // DIFFERENT directory than the one these contract files just landed in. Confirmed live
        // 2026-07-13: the reviewer's very first tool call, Read(".workspace/goal.md"), came back
        // "File does not exist" — it was looking in merge/{parentWorkUnitId} for a file that only
        // ever gets written to wu.BranchId. Copy the same 12 contract files over so both directories
        // agree; a no-op when branchId == wu.BranchId (every non-reconciled review).
        await workspaceContracts.MaterializeAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        if (!string.Equals(branchId, wu.BranchId, StringComparison.Ordinal))
        {
            var contractFiles = ContractFileStems
                .SelectMany(stem => new[] { $".workspace/{stem}.json", $".workspace/{stem}.md" })
                .ToArray();
            await fileWorkspace.CopyFilesAsync(wu.BranchId, branchId, contractFiles, ct).ConfigureAwait(false);
        }

        // plans/review-seam-and-clarification-sessions.md S2 — Review mode additionally needs the
        // proposal metadata + diff framing on disk before the CLI spawns; a Review run with no
        // reviewable proposal is terminal (nothing to decide), not worth a paid spawn.
        if (request.Mode == HarnessMode.Review)
        {
            var reviewSetupFailure = await harvest.MaterializeReviewRequestAsync(request, branchId, ct).ConfigureAwait(false);
            if (reviewSetupFailure is not null)
                return reviewSetupFailure;
        }

        var (settingsPath, addDirRoots) = await WriteSettingsFileAsync(
            branchId, workDir, wu.ReferenceFiles, request.Mode, ct).ConfigureAwait(false);

        // Resume identity — only reused on an attested resume (IsResume, set by the caller from
        // ScheduledItem.AttemptCount > 0), not on every run, so a fresh first attempt never
        // accidentally resumes a stale session left over from an unrelated earlier attempt.
        //
        // Never for Review mode, though: AttemptCount > 0 is true for every retry after the FIRST
        // one, forever — there is no separate "genuinely continue" vs. "this is attempt N, start
        // clean" signal at this layer, so every retry (plain or with added steering context) kept
        // resuming the exact same original --resume session id. A review's job is "reach one
        // verdict, write one file"; once the model concludes (even wrongly, e.g. narrating a
        // verdict without writing .workspace/review.json) that conclusion lives in the CLI's own
        // session memory and gets carried into every future --resume of it, so a "retry" just
        // re-confirms the same stale belief ("I already approved this") instead of re-examining
        // anything. Confirmed live 2026-07-13: three consecutive retries of the same reviewer, same
        // sessionId, each one increasingly convinced it had "already" written the verdict in a
        // "prior cycle" it never actually completed. Studio's own Continue path (ContinueService +
        // ReviewerAgentLoop's priorTurns) already provides a controlled, explicit "resume with
        // context" for review — that's the correct mechanism for genuine continuation; blind CLI
        // session resume for Review mode has no upside and this concrete downside.
        var resumeSessionId = request.Mode != HarnessMode.Review && request.IsResume
            && wu.Metadata is { } metadata && metadata.TryGetValue(HarnessSessionMetadataKey, out var priorSessionId)
                ? priorSessionId
                : null;

        // Phase C.4 (phase-c-implementation.md C3) — the slim "/mcp-harness" mount, gated on the
        // adapter's own declared capability (never on the vendor CLI's theoretical ceiling — see
        // Capabilities' own doc comment) and on the Host's listening address actually being known
        // (headless BuildPeer callers never populate HarnessMcpBaseUrl, so this degrades to "no MCP
        // mount this run" there, same as a pre-C3 run). Token is minted fresh per run and revoked on
        // every exit path below (harvest, timeout-kill) — never reused across runs/resumes.
        string? harnessMcpToken = null;
        string? mcpConfigPath = null;
        if (Capabilities.SupportsMcp && !string.IsNullOrEmpty(options.HarnessMcpBaseUrl))
        {
            harnessMcpToken = harnessMcpTokens.Mint(request.WorkUnitId, request.SessionId, request.AgentId);
            mcpConfigPath = await WriteMcpConfigFileAsync(branchId, workDir, harnessMcpToken, ct).ConfigureAwait(false);
        }

        var psi = BuildProcessStartInfo(workDir, settingsPath, mcpConfigPath, request, addDirRoots, resumeSessionId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{options.ExecutablePath}'.");

        // Thin pump — all interpretation of the stream-json lines lives in ClaudeTranscriptParser
        // (Phase C.1), versioned separately from this executor. OnActivity is invoked by the
        // parser itself as assistant lines stream in, same timing as before C1.
        var parser = ClaudeTranscriptParser.Create(request.OnActivity);

        // Phase C.4 (phase-c-implementation.md C3) — the token is revoked at every exit from this
        // point on (timeout-kill below, or harvest's own return further down), never left live past
        // the run it was minted for.
        try
        {
            // Drained concurrently with stdout — stderr is redirected (so it doesn't leak to the
            // Host console) and a full pipe buffer would otherwise deadlock the child. Its content
            // is the ONLY diagnostic a failed CLI run produces (a nonzero exit emits no stream-json
            // at all), so it must reach the failure reason, not the void (found live 2026-07-13:
            // a failing `claude` spawn surfaced as a bare "exited with code 1").
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
                // session_id appears on every stream-json line (confirmed against the real CLI), so
                // even a killed run's session is resumable — persist it before returning.
                var partial = parser.BuildSummary();
                await PersistHarnessSessionIdAsync(request, partial.SessionId, ct).ConfigureAwait(false);
                return new HarnessRunResult(
                    AgentLoopCompletion.MaxIterationsExceeded,
                    $"claude CLI run exceeded the {options.TimeoutSeconds}s wall-clock limit.",
                    HarnessSessionId: partial.SessionId);
            }

            var summary = parser.BuildSummary();

            // The process actually completed (didn't time out) — record what happened regardless of
            // outcome, so a failed run is still auditable, not just a successful one.
            await EmitPermissionDenialEventsAsync(request, summary.PermissionDenials, ct).ConfigureAwait(false);
            await PersistHarnessSessionIdAsync(request, summary.SessionId, ct).ConfigureAwait(false);
            await RecordConversationLogAsync(request, summary, ct).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var stderrTail = await ReadStderrTailAsync(stderrTask).ConfigureAwait(false);
                logger.LogWarning(
                    "[claude-code] workUnitId={WorkUnitId} exited with code {ExitCode}. stderr: {Stderr}",
                    request.WorkUnitId, process.ExitCode, stderrTail ?? "(empty)");
                return new HarnessRunResult(
                    AgentLoopCompletion.Stalled,
                    $"claude CLI exited with code {process.ExitCode}." +
                        (stderrTail is null ? "" : $" stderr: {stderrTail}"),
                    summary.ResultText, summary.InputTokens, summary.OutputTokens, summary.TotalCostUsd, summary.SessionId);
            }

            if (summary.IsError || summary.Subtype != "success")
            {
                return new HarnessRunResult(
                    AgentLoopCompletion.Stalled,
                    summary.ResultText ?? $"claude CLI reported subtype='{summary.Subtype}'.",
                    summary.ResultText, summary.InputTokens, summary.OutputTokens, summary.TotalCostUsd, summary.SessionId);
            }

            // branchId, not wu.BranchId: for a reconciled review these differ (branchId was
            // redirected to proposal.SourceBranch above), and this is the read-back step for the
            // exact .workspace/review.json the CLI just wrote — HarvestReviewAsync reads from
            // whatever branch is passed here. Passing wu.BranchId reads the WRONG directory (the
            // goal's own untouched branch, which never receives this write), so harvest always saw
            // no file even when the CLI had just written one successfully. Confirmed live
            // 2026-07-13: an Edit tool call to review.json returned "updated successfully" in the
            // same run that then dead-lettered with "did not write .workspace/review.json."
            return await harvest.HarvestAsync(
                request, branchId, summary.ResultText, summary.InputTokens, summary.OutputTokens,
                summary.TotalCostUsd, summary.SessionId, Name, "anthropic", ct)
                .ConfigureAwait(false);
        }
        finally
        {
            if (harnessMcpToken is not null)
                harnessMcpTokens.Revoke(harnessMcpToken);
        }
    }

    private async Task EmitPermissionDenialEventsAsync(
        HarnessRunRequest request, IReadOnlyList<string> permissionDenials, CancellationToken ct)
    {
        if (permissionDenials.Count == 0 || events is null || request.SessionId is null)
            return;

        foreach (var rawDenial in permissionDenials)
        {
            string? toolName = null;
            string? reason = null;
            try
            {
                using var doc = JsonDocument.Parse(rawDenial);
                var root = doc.RootElement;
                toolName = TryGetAnyString(root, "tool_name", "toolName", "tool");
                reason = TryGetAnyString(root, "reason", "message");
            }
            catch (JsonException) { /* keep RawJson regardless — best-effort field extraction only */ }

            await events.AppendAsync(
                request.SessionId, request.WorkUnitId, ExecutionEventKind.HarnessPermissionDenied,
                new HarnessPermissionDeniedPayload(request.AgentId, toolName, reason, rawDenial),
                ct: ct).ConfigureAwait(false);
        }
    }

    private static string? TryGetAnyString(JsonElement root, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private async Task PersistHarnessSessionIdAsync(HarnessRunRequest request, string? sessionId, CancellationToken ct)
    {
        if (sessionId is null)
            return;

        try
        {
            await workUnits.SetMetadataAsync(request.WorkUnitId, HarnessSessionMetadataKey, sessionId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let telemetry/resume bookkeeping fail the run itself — same posture as
            // RecordConversationLogAsync below.
            logger.LogWarning(ex, "[claude-code] failed to persist harness session id for workUnitId={WorkUnitId}", request.WorkUnitId);
        }
    }

    private async Task RecordConversationLogAsync(
        HarnessRunRequest request, TranscriptRunSummary summary, CancellationToken ct)
    {
        // Phase C.1 — per-turn entries (CycleNumber 0..N-1) plus one terminal run-level entry
        // (CycleNumber N, unchanged "CLE-" LogId prefix from pre-C1). Degrades to exactly one
        // entry when the transcript's format wasn't recognized (ClaudeTranscriptParser.V1's own
        // degrade rule) — identical in shape to the pre-C1 single-entry behavior.
        var entries = ClaudeConversationLogMapper.BuildEntries(
            summary, request.WorkUnitId, request.AgentId, request.TaskId, Name, request.Mode);

        try
        {
            foreach (var entry in entries)
                await conversationLog.RecordAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let telemetry recording fail the run itself.
            logger.LogWarning(ex, "[claude-code] failed to record conversation log for workUnitId={WorkUnitId}", request.WorkUnitId);
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* best-effort — the process may have already exited */ }
    }

    // Bounded so a chatty CLI can't balloon a dead-letter reason; trimmed to the first ~500 chars
    // because CLI errors (auth, bad flag, missing binary) always front-load the useful line.
    internal static async Task<string?> ReadStderrTailAsync(Task<string> stderrTask)
    {
        try
        {
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
            if (stderr.Length == 0) return null;
            return stderr.Length <= 500 ? stderr : stderr[..500] + "…";
        }
        catch
        {
            return null; // diagnostics only — never fail the failure path itself
        }
    }

    // Generated allowlist — Edit/Write/Read scoped to the branch workdir, Bash only for the
    // detected build/test commands per root (plan's resolved "generated allowlist --settings"
    // decision). Path pattern syntax verified against a real settings.json on this machine
    // (Read(//c/Users/.../**) — POSIX-style, lowercase drive letter, no colon) but the permission
    // schema itself is not documented anywhere Studio controls; revisit once exercised against
    // the real CLI end-to-end.
    //
    // plans/phase-d-implementation.md D1.b — Mode==Plan gets a narrower allowlist: Read everywhere
    // in the workdir (a planner needs to see the whole codebase to slice it), Write ONLY
    // .workspace/plan.json, no Edit, no Bash. This is advisory (the CLI's own permission-prompt
    // enforcement, same as the Execute allowlist always was), not a sandbox Studio verifies — the
    // real backstop is HarnessHarvestPipeline.HarvestPlanAsync discarding any diff outside
    // .workspace/ it finds anyway.
    private async Task<(string SettingsPath, IReadOnlyList<string> AddDirRoots)> WriteSettingsFileAsync(
        string branchId, string workDir, IReadOnlyList<FileReferenceV1>? referenceFiles, HarnessMode mode, CancellationToken ct)
    {
        var workDirPattern = ToSettingsPattern(workDir);
        List<string> allow;
        if (mode == HarnessMode.Plan)
        {
            // "Edit(...)", not "Write(...)": the CLI has no Write permission-rule type — Edit
            // rules gate ALL file-modifying tools (Write/Edit/NotebookEdit). Found by the real-CLI
            // Plan-mode smoke (2026-07-13, claude 2.1.207): a Write(...) rule never matches (even
            // "Write(**)" is denied), so in -p mode the planner stalled at an unanswerable
            // permission prompt and no plan.json was ever written; the same path as an Edit(...)
            // rule passes with zero denials. Execute mode below was never bitten because it always
            // emitted an Edit(.../**) entry alongside the (inert) Write one.
            allow =
            [
                $"Read({workDirPattern}/**)",
                $"Edit({workDirPattern}/.workspace/plan.json)",
            ];
        }
        else if (mode == HarnessMode.Review)
        {
            // plans/review-seam-and-clarification-sessions.md S2 — a reviewer reads the whole
            // branch and writes ONLY the verdict file; no general Edit, so the run can't change
            // what it's reviewing. Build/test Bash entries are included below: verification is
            // half the reviewer's job (the CLI equivalent of the native loop's
            // nm_v1_workspace_build/_test tools).
            allow =
            [
                $"Read({workDirPattern}/**)",
                $"Edit({workDirPattern}/.workspace/review.json)",
            ];
            await AddBuildTestBashAllowsAsync().ConfigureAwait(false);
        }
        else
        {
            allow =
            [
                $"Edit({workDirPattern}/**)",
                $"Write({workDirPattern}/**)",
                $"Read({workDirPattern}/**)",
            ];
            await AddBuildTestBashAllowsAsync().ConfigureAwait(false);
        }

        async Task AddBuildTestBashAllowsAsync()
        {
            var profile = await workspaceProfiles.GetOrDetectAsync(branchId, ct).ConfigureAwait(false);
            foreach (var root in profile.Roots)
            {
                if (root.BuildCommand is { Length: > 0 } build)
                    allow.Add($"Bash({build} *)");
                if (root.TestCommand is { Length: > 0 } test)
                    allow.Add($"Bash({test} *)");
            }
        }

        // Cross-repo pointers (WorkUnit.ReferenceFiles) live outside the branch workdir entirely —
        // resolved via IRepositoryRegistryService, one --add-dir + Read-only allow entry per
        // distinct registered repository. Read-only, not Edit/Write: ReferenceFiles is documented
        // as "not write-gating like FileScope; just where to look" (WorkUnit.cs).
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
                allow.Add($"Read({ToSettingsPattern(repository.Path)}/**)");
            }
        }

        // Phase C.4 — the harness MCP mount's tools must be pre-authorized like everything else:
        // in -p mode a non-allowlisted MCP tool call hits an unanswerable permission prompt.
        // Found by the real-CLI C3 smoke (2026-07-13, claude 2.1.207): the CLI called
        // mcp__nodalmerge-harness__nm_v1_clarification_request, stalled on "needs permission",
        // and ended the run with no clarification ever reaching the Studio side. "mcp__<server>"
        // allows every tool on that server — appropriate here since the mount's tool set is
        // Studio-curated (HarnessWorkerTools) rather than arbitrary third-party tools. Gated on
        // the same condition RunAsync uses to write mcp.json, so a run with no mount never
        // allowlists a server it doesn't have.
        if (Capabilities.SupportsMcp && !string.IsNullOrEmpty(options.HarnessMcpBaseUrl))
            allow.Add($"mcp__{HarnessMcpServerName}");

        var settingsJson = JsonSerializer.Serialize(new { permissions = new { allow } }, JsonSerializerOptions.Web);
        await fileWorkspace.WriteAsync(branchId, ".workspace/settings.json", settingsJson, ct).ConfigureAwait(false);
        return (Path.Combine(workDir, ".workspace", "settings.json"), addDirRoots);
    }

    private static string ToSettingsPattern(string path) =>
        "//" + path.Replace('\\', '/').Replace(":", "").ToLowerInvariant().TrimStart('/');

    // Phase C.4 (phase-c-implementation.md C3) — the slim harness-scoped MCP mount. Written
    // alongside .workspace/settings.json (same directory, same "generated, never hand-edited"
    // posture) and consumed by claude via --mcp-config. Format verified against claude 2.1.177's
    // documented `.mcp.json` shape for an HTTP-type server entry: a "mcpServers" map keyed by
    // server name, each entry carrying type/url/headers — the "headers" field is exactly the
    // bearer-token carrier the plan's decided design calls for.
    private async Task<string> WriteMcpConfigFileAsync(string branchId, string workDir, string token, CancellationToken ct)
    {
        var mcpConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [HarnessMcpServerName] = new
                {
                    type = "http",
                    url = $"{options.HarnessMcpBaseUrl!.TrimEnd('/')}/mcp-harness",
                    headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
                },
            },
        };
        var mcpConfigJson = JsonSerializer.Serialize(mcpConfig, JsonSerializerOptions.Web);
        await fileWorkspace.WriteAsync(branchId, ".workspace/mcp.json", mcpConfigJson, ct).ConfigureAwait(false);
        return Path.Combine(workDir, ".workspace", "mcp.json");
    }

    private ProcessStartInfo BuildProcessStartInfo(
        string workDir, string settingsPath, string? mcpConfigPath, HarnessRunRequest request,
        IReadOnlyList<string> addDirRoots, string? resumeSessionId)
    {
        // Studio-controlled, short, fixed prompt — the actual goal/context lives in
        // .workspace/goal.md + workunit.md + state.md, which the harness reads with its own file
        // tools. Keeping the -p argument itself short and free of arbitrary user content sidesteps
        // CLI-argument-quoting risk entirely (no goal text, however arbitrary, ever needs to
        // survive a cmd.exe /c round-trip).
        //
        // plans/phase-d-implementation.md D1.b — Mode==Plan gets a distinct kickoff: decompose,
        // write .workspace/plan.json, implement nothing. The settings allowlist above already
        // restricts Write to that one path and drops Edit/Bash entirely; this prompt is the
        // cooperative half of that contract for a CLI that (unlike the settings file) has no
        // hard-enforced sandbox Studio controls.
        // plans/review-seam-and-clarification-sessions.md S2 — Mode==Review mirrors Plan's shape:
        // a distinct kickoff naming the contract file in (.workspace/review-request.json) and the
        // verdict file out (.workspace/review.json), with the settings allowlist above restricting
        // writes to exactly that verdict file. The verificationResults wording matches the native
        // nm_v1_merge_review tool's own schema description — on Rejected it is the only feedback
        // the retried worker ever sees.
        var prompt = request.Mode == HarnessMode.Review
            ? "You are reviewing a merge proposal. Read .workspace/goal.md, .workspace/workunit.md, " +
              "and .workspace/review-request.json in this directory — this working directory " +
              "contains the PROPOSED state of the branch, and review-request.json carries the " +
              "proposal's summary, files touched, and diff against the target. Inspect the changed " +
              "files, check them against the goal and any recorded constraints, and run the " +
              "project's build/test commands if available to verify — if that requires a nested " +
              "project directory, pass it as an argument (e.g. `dotnet test tests/Project` or " +
              "`npm test --prefix tests/Project`) rather than `cd`-ing into it, since your shell " +
              "keeps that directory for every later command including your final write below; if " +
              "you do `cd` anywhere, `cd` back to this root directory before writing your verdict. " +
              "Then write your verdict to .workspace/review.json (relative to THIS root directory, " +
              "not any subdirectory you may have cd'd into) as JSON matching this shape exactly: " +
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

        // Phase C.4 (phase-c-implementation.md C3) — only mentioned when the mount is actually
        // active (mcpConfigPath non-null), matching --mcp-config below: a harness without the mount
        // must not be told about tools it can't reach.
        if (mcpConfigPath is not null)
        {
            prompt +=
                " You also have MCP tools mounted (nm_v1_workspace_symbol_definition/_references/" +
                "_implementation for semantic code navigation, nm_v1_doc_fetch for external " +
                "documentation, nm_v1_artifact_record/_query for durable knowledge notes, and " +
                "nm_v1_clarification_request to ask a blocking question and wait for the answer " +
                "in this same turn instead of writing to .workspace/inbox/) — prefer them over the " +
                "file-based fallbacks above when applicable.";
        }

        var args = new List<string>
        {
            "-p", prompt,
            "--output-format", "stream-json",
            "--verbose",
            "--settings", settingsPath,
        };

        if (mcpConfigPath is not null)
        {
            args.Add("--mcp-config");
            args.Add(mcpConfigPath);
        }

        // Model Profile-driven model selection ("claude-cli" provider carries the profile's model
        // through the per-stage credential channel). Blank = the CLI's own configured default.
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.Add("--model");
            args.Add(request.Model);
        }

        foreach (var addDirRoot in addDirRoots)
        {
            args.Add("--add-dir");
            args.Add(addDirRoot);
        }

        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            args.Add("--resume");
            args.Add(resumeSessionId);
        }

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Windows resolves `claude` (like npm/npx) to a *.cmd shim only cmd.exe knows how to
        // launch directly — same reasoning as WorkspaceExecutionService.CreateProcessStartInfo.
        // Kept uniform (always wrapped) rather than special-cased per executable so the wrapping
        // logic itself gets exercised by the stub-CLI tests too, not just the real claude path.
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

        // Resolved decision "ambient auth, key opt-in" (plan's Decisions section) — ambient CLI
        // auth is the default (nothing set here). Two opt-in paths inject the caller-supplied
        // credential as ANTHROPIC_API_KEY: AgentProfile.InjectApiKeyEnv (REST/headless), or a
        // "claude-cli" Model Profile with a stored key — storing a key on that profile *is* the
        // opt-in gesture there (leaving its key blank keeps ambient auth).
        var injectKey = request.Profile?.InjectApiKeyEnv == true ||
            string.Equals(request.Provider, ProviderKey, StringComparison.OrdinalIgnoreCase);
        if (injectKey && !string.IsNullOrEmpty(request.ApiKey))
            psi.EnvironmentVariables["ANTHROPIC_API_KEY"] = request.ApiKey;

        return psi;
    }
}
