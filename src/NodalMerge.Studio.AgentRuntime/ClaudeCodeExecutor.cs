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
    IMergeCommandService mergeCommands,
    IClarificationCommandService clarifications,
    IConversationLogService conversationLog,
    IRepositoryRegistryService repositoryRegistry,
    ClaudeCodeExecutorOptions options,
    ILogger<ClaudeCodeExecutor> logger,
    IExecutionEventStream? events = null) : IHarnessExecutor
{
    public string Name => "claude-code";

    // WorkUnit.Metadata key the claude CLI's own session id is persisted under between runs —
    // additive, no new typed field, matching the "Metadata for genuine ad hoc/future use"
    // convention (see WorkUnit.cs).
    private const string HarnessSessionMetadataKey = "claudeCodeSessionId";

    public async Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken ct = default)
    {
        var wu = await workUnits.GetAsync(request.WorkUnitId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Work unit '{request.WorkUnitId}' was not found.");
        var branchId = wu.BranchId;

        // Same "resolve the real on-disk directory" call WorkspaceExecutionService/
        // WorkspaceCacheManager already use — no new plumbing needed for cwd resolution.
        var workDir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Branch '{branchId}' has no working directory.");

        // Materializes .workspace/{manifest,goal,workunit,state,constraints,review-policy}.json
        // (+ .md siblings) — the runtime→harness half of the contract, built in Phase A.
        await workspaceContracts.MaterializeAsync(request.WorkUnitId, ct).ConfigureAwait(false);

        var (settingsPath, addDirRoots) = await WriteSettingsFileAsync(branchId, workDir, wu.ReferenceFiles, ct).ConfigureAwait(false);

        // Resume identity — only reused on an attested resume (IsResume, set by the caller from
        // ScheduledItem.AttemptCount > 0), not on every run, so a fresh first attempt never
        // accidentally resumes a stale session left over from an unrelated earlier attempt.
        var resumeSessionId = request.IsResume && wu.Metadata is { } metadata &&
            metadata.TryGetValue(HarnessSessionMetadataKey, out var priorSessionId)
                ? priorSessionId
                : null;

        var psi = BuildProcessStartInfo(workDir, settingsPath, request, addDirRoots, resumeSessionId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{options.ExecutablePath}'.");

        string? sessionId = null;
        string? resultText = null;
        string? subtype = null;
        var isError = false;
        int? inputTokens = null;
        int? outputTokens = null;
        double? totalCostUsd = null;
        var permissionDenials = new List<string>();

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // A partial/malformed line is not fatal to the run — skip it and keep reading.
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeProp))
                        continue;
                    var type = typeProp.GetString();

                    if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                        sessionId = sid.GetString();

                    switch (type)
                    {
                        case "assistant":
                            request.OnActivity?.Invoke(ExtractAssistantText(root));
                            break;
                        case "result":
                            subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                            isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                            resultText = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                                ? r.GetString() : null;
                            if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                                totalCostUsd = cost.GetDouble();
                            if (root.TryGetProperty("usage", out var usage))
                            {
                                if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var iv))
                                    inputTokens = iv;
                                if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var ov))
                                    outputTokens = ov;
                            }
                            if (root.TryGetProperty("permission_denials", out var denials) &&
                                denials.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var denial in denials.EnumerateArray())
                                    permissionDenials.Add(denial.GetRawText());
                            }
                            break;
                        // "system" (session init metadata), "rate_limit_event", "user" (tool
                        // results), and any future/unrecognized type are deliberately ignored —
                        // per-turn transcript parsing is Phase C.1, not B2/B3.
                    }
                }
            }

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            // session_id appears on every stream-json line (confirmed against the real CLI), so
            // even a killed run's session is resumable — persist it before returning.
            await PersistHarnessSessionIdAsync(request, sessionId, ct).ConfigureAwait(false);
            return new HarnessRunResult(
                AgentLoopCompletion.MaxIterationsExceeded,
                $"claude CLI run exceeded the {options.TimeoutSeconds}s wall-clock limit.",
                HarnessSessionId: sessionId);
        }

        // The process actually completed (didn't time out) — record what happened regardless of
        // outcome, so a failed run is still auditable, not just a successful one.
        await EmitPermissionDenialEventsAsync(request, permissionDenials, ct).ConfigureAwait(false);
        await PersistHarnessSessionIdAsync(request, sessionId, ct).ConfigureAwait(false);
        await RecordConversationLogAsync(
            request, resultText, inputTokens, outputTokens, sessionId, ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            logger.LogWarning(
                "[claude-code] workUnitId={WorkUnitId} exited with code {ExitCode}", request.WorkUnitId, process.ExitCode);
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                $"claude CLI exited with code {process.ExitCode}.",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        if (isError || subtype != "success")
        {
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                resultText ?? $"claude CLI reported subtype='{subtype}'.",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        return await HarvestAsync(
            request, wu.BranchId, resultText, inputTokens, outputTokens, totalCostUsd, sessionId, ct)
            .ConfigureAwait(false);
    }

    // plans/harness-hosting-architecture.md Phase B.3 — the runtime plays the role of "agent
    // calling nm_v1_artifact_record / nm_v1_clarification_request / nm_v1_merge_propose +
    // nm_v1_merge_validate" on the external harness's behalf, since a claude-code run has no
    // in-loop tool call doing any of this itself.
    private async Task<HarnessRunResult> HarvestAsync(
        HarnessRunRequest request, string branchId, string? resultText,
        int? inputTokens, int? outputTokens, double? totalCostUsd, string? sessionId, CancellationToken ct)
    {
        await workspaceContracts.HarvestDecisionsAsync(request.WorkUnitId, ct).ConfigureAwait(false);

        var inboxEntries = await workspaceContracts.HarvestInboxAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        if (inboxEntries.Count > 0)
        {
            // Same domain-level parking mechanism a native worker's own nm_v1_clarification_request
            // tool call triggers (ClarificationCommandService.RequestAsync marks the scheduler item
            // AwaitingResume and the work unit Waiting) — no new pause/resume plumbing needed here.
            // The outbox/--resume respawn half of this loop is not wired yet (see the plan's B3 notes).
            foreach (var entry in inboxEntries)
            {
                await clarifications.RequestAsync(
                    request.WorkUnitId, entry.Question,
                    requestedByAgentId: request.AgentId, sessionId: request.SessionId,
                    ct: ct).ConfigureAwait(false);
            }

            return new HarnessRunResult(
                AgentLoopCompletion.AwaitingClarification, FailureReason: null,
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        MergeProposal proposal;
        try
        {
            // targetBranch: "main" is a safe default — MergeCommandService.ProposeAsync's own
            // fan-out redirect (merge/{parentWorkUnitId}) overrides this internally for a
            // fanned-out child regardless of what the caller passes, same as the native worker's
            // own nm_v1_merge_propose tool call relies on.
            proposal = await mergeCommands.ProposeAsync(
                sourceBranch: branchId,
                targetBranch: "main",
                summary: resultText ?? "Claude Code run completed.",
                workUnitId: request.WorkUnitId,
                agentId: request.AgentId,
                model: Name,
                provider: "anthropic",
                sessionId: request.SessionId,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[claude-code] harvest ProposeAsync failed for workUnitId={WorkUnitId}", request.WorkUnitId);
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled, $"Harvest failed: {ex.Message}",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        // The mechanical hard gate (WorkspaceExecutionRule, fired inside ProposeAsync itself when
        // WorkspaceOptions.RequireBuildBeforeProposal/RequireTestBeforeProposal are set) rejects the
        // proposal before Draft, before diff, before artifact lineage — this is the real
        // "a build-breaking stub edit is blocked at the gate" guarantee, not the kickoff-contract
        // hint (WorkspaceContractReviewPolicy.SelfVerifyBuildRequired/TestRequired).
        if (proposal.Status == MergeProposalStatus.Rejected)
        {
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                proposal.ChangeDescription ?? "Merge proposal was rejected at the gate.",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        try
        {
            await mergeCommands.ValidateAsync(proposal.ProposalId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[claude-code] harvest ValidateAsync failed for workUnitId={WorkUnitId}", request.WorkUnitId);
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled, $"Proposal validation failed: {ex.Message}",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        return new HarnessRunResult(
            AgentLoopCompletion.Succeeded, FailureReason: null,
            resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
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
        HarnessRunRequest request, string? resultText,
        int? inputTokens, int? outputTokens, string? sessionId, CancellationToken ct)
    {
        // One entry per external run (CycleNumber 0) — a legitimate degraded-but-compatible use of
        // the per-turn DTO every native loop call site also writes to; per-turn transcript parsing
        // is Phase C.1, not B3.
        var entry = new ConversationLogEntry(
            LogId: $"CLE-{Guid.NewGuid():N}",
            WorkUnitId: request.WorkUnitId,
            AgentId: request.AgentId,
            AgentRole: "worker",
            TaskId: request.TaskId,
            CycleNumber: 0,
            AssistantText: resultText,
            ToolCalls: [],
            ToolResults: [],
            StopReason: "end_turn",
            OccurredAt: DateTimeOffset.UtcNow,
            SessionId: sessionId,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            Provider: "anthropic",
            Model: Name);

        try
        {
            await conversationLog.RecordAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let telemetry recording fail the run itself.
            logger.LogWarning(ex, "[claude-code] failed to record conversation log for workUnitId={WorkUnitId}", request.WorkUnitId);
        }
    }

    private static string? ExtractAssistantText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.ValueString() == "text" &&
                block.TryGetProperty("text", out var text))
                return text.GetString();
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* best-effort — the process may have already exited */ }
    }

    // Generated allowlist — Edit/Write/Read scoped to the branch workdir, Bash only for the
    // detected build/test commands per root (plan's resolved "generated allowlist --settings"
    // decision). Path pattern syntax verified against a real settings.json on this machine
    // (Read(//c/Users/.../**) — POSIX-style, lowercase drive letter, no colon) but the permission
    // schema itself is not documented anywhere Studio controls; revisit once exercised against
    // the real CLI end-to-end.
    private async Task<(string SettingsPath, IReadOnlyList<string> AddDirRoots)> WriteSettingsFileAsync(
        string branchId, string workDir, IReadOnlyList<FileReferenceV1>? referenceFiles, CancellationToken ct)
    {
        var workDirPattern = ToSettingsPattern(workDir);
        var allow = new List<string>
        {
            $"Edit({workDirPattern}/**)",
            $"Write({workDirPattern}/**)",
            $"Read({workDirPattern}/**)",
        };

        var profile = await workspaceProfiles.GetOrDetectAsync(branchId, ct).ConfigureAwait(false);
        foreach (var root in profile.Roots)
        {
            if (root.BuildCommand is { Length: > 0 } build)
                allow.Add($"Bash({build} *)");
            if (root.TestCommand is { Length: > 0 } test)
                allow.Add($"Bash({test} *)");
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

        var settingsJson = JsonSerializer.Serialize(new { permissions = new { allow } }, JsonSerializerOptions.Web);
        await fileWorkspace.WriteAsync(branchId, ".workspace/settings.json", settingsJson, ct).ConfigureAwait(false);
        return (Path.Combine(workDir, ".workspace", "settings.json"), addDirRoots);
    }

    private static string ToSettingsPattern(string path) =>
        "//" + path.Replace('\\', '/').Replace(":", "").ToLowerInvariant().TrimStart('/');

    private ProcessStartInfo BuildProcessStartInfo(
        string workDir, string settingsPath, HarnessRunRequest request,
        IReadOnlyList<string> addDirRoots, string? resumeSessionId)
    {
        // Studio-controlled, short, fixed prompt — the actual goal/context lives in
        // .workspace/goal.md + workunit.md + state.md, which the harness reads with its own file
        // tools. Keeping the -p argument itself short and free of arbitrary user content sidesteps
        // CLI-argument-quoting risk entirely (no goal text, however arbitrary, ever needs to
        // survive a cmd.exe /c round-trip).
        var prompt =
            "Read .workspace/goal.md, .workspace/workunit.md, and .workspace/state.md in this " +
            "directory, then complete the work described there. Record any Research/Decision/" +
            "Constraint knowledge via .workspace/decisions/, and blocking questions via " +
            ".workspace/inbox/. If you are resuming and were waiting on a question, check " +
            ".workspace/outbox/ for the answer before asking again.";

        var args = new List<string>
        {
            "-p", prompt,
            "--output-format", "stream-json",
            "--verbose",
            "--settings", settingsPath,
        };

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
        // auth is the default (nothing set here); only a profile that explicitly opts in gets the
        // caller-supplied credential injected as ANTHROPIC_API_KEY.
        if (request.Profile?.InjectApiKeyEnv == true && !string.IsNullOrEmpty(request.ApiKey))
            psi.EnvironmentVariables["ANTHROPIC_API_KEY"] = request.ApiKey;

        return psi;
    }
}

file static class JsonElementExtensions
{
    public static string? ValueString(this JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
