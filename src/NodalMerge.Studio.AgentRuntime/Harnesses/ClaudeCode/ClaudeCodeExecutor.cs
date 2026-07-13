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
    IExecutionEventStream? events = null) : IHarnessExecutor
{
    public string Name => "claude-code";

    public string DisplayName => "Claude Code CLI";

    public string? ProviderKey => "claude-cli";

    // Phase C.1 — turn telemetry (ClaudeTranscriptParser), resume (the CLI's own session_id +
    // --resume, shipped B3), hooks/subagents/MCP (the underlying `claude` binary's own features,
    // reachable via the generated --settings file today). SupportsPlanningMode stays false until
    // Phase D actually wires a Plan mode through this adapter — the flag declares what *this
    // adapter* supports, not the vendor CLI's theoretical ceiling.
    public HarnessCapabilities Capabilities { get; } = new(
        SupportsTurnTelemetry: true, SupportsResume: true, SupportsHooks: true,
        SupportsSubagents: true, SupportsMcp: true, SupportsPlanningMode: false);

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
                logger.LogWarning(
                    "[claude-code] workUnitId={WorkUnitId} exited with code {ExitCode}", request.WorkUnitId, process.ExitCode);
                return new HarnessRunResult(
                    AgentLoopCompletion.Stalled,
                    $"claude CLI exited with code {process.ExitCode}.",
                    summary.ResultText, summary.InputTokens, summary.OutputTokens, summary.TotalCostUsd, summary.SessionId);
            }

            if (summary.IsError || summary.Subtype != "success")
            {
                return new HarnessRunResult(
                    AgentLoopCompletion.Stalled,
                    summary.ResultText ?? $"claude CLI reported subtype='{summary.Subtype}'.",
                    summary.ResultText, summary.InputTokens, summary.OutputTokens, summary.TotalCostUsd, summary.SessionId);
            }

            return await harvest.HarvestAsync(
                request, wu.BranchId, summary.ResultText, summary.InputTokens, summary.OutputTokens,
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
            summary, request.WorkUnitId, request.AgentId, request.TaskId, Name);

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
                ["nodalmerge-harness"] = new
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
        var prompt =
            "Read .workspace/goal.md, .workspace/workunit.md, and .workspace/state.md in this " +
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
