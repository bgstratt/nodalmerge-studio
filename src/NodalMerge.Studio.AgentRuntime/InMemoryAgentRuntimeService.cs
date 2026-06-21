using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime;

public sealed class InMemoryAgentRuntimeService : IAgentRuntimeService, ISnapshotService, IAgentControlService, IHostedService
{
    private readonly ConcurrentDictionary<(string AgentId, string WorkUnitId), ExecutionSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, AgentRecord> _agents = new();
    private readonly ConcurrentDictionary<string, OrchestratorRegistration> _orchestratorRegistrations = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InMemoryAgentRuntimeService> _logger;
    private readonly IAgentProfileService _profileService;
    private readonly IWorkScheduler _scheduler;
    private readonly IExecutionEventStream _events;
    private readonly WorkspaceOptions _options;
    private int _activeWorkerCount;
    private CancellationTokenSource? _pollCts;

    public InMemoryAgentRuntimeService(
        IServiceProvider serviceProvider,
        ILogger<InMemoryAgentRuntimeService> logger,
        IAgentProfileService profileService,
        IWorkScheduler scheduler,
        IExecutionEventStream events,
        WorkspaceOptions options)
    {
        _serviceProvider = serviceProvider;
        _logger          = logger;
        _profileService  = profileService;
        _scheduler       = scheduler;
        _events          = events;
        _options         = options;
    }

    private sealed record AgentRecord(
        string AgentId,
        string WorkUnitId,
        string Status,
        string? TaskId = null,
        string? Model = null,
        string? BaseUrl = null,
        string? ApiKey = null,
        string? Provider = null,
        CancellationTokenSource? Cts = null,
        string? CurrentActivity = null);

    // Ephemeral UI chrome only — not part of durable DAG history (AP-5), so it's a plain
    // in-memory update rather than an ExecutionEventStream append.
    private void ReportActivity(string agentId, string? activity)
    {
        if (_agents.TryGetValue(agentId, out var r))
            _agents[agentId] = r with { CurrentActivity = activity };
    }

    // Captured at SpawnAsync("orchestrator", ...) time so ReinvokeOrchestratorAsync can restart
    // the loop with the same credentials/profile later, without the caller (WorkSchedulerService)
    // needing to remember or re-supply them.
    private sealed record OrchestratorRegistration(
        string Provider, string Model, string BaseUrl, string ApiKey, string? ProfileId, string? AutoReviewProfileId);

    // ── IHostedService ─────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RehydrateInterruptedAgentsAsync(cancellationToken).ConfigureAwait(false);
        _pollCts = new CancellationTokenSource();
        _ = Task.Run(() => PollSchedulerAsync(_pollCts.Token), CancellationToken.None);
    }

    // Slice 19d — on startup, any work unit that was Executing/Active/Retrying when the host
    // last stopped has no live agent loop. Register a synthetic "interrupted" agent record so
    // the Execution Timeline shows these as Interrupted instead of silently absent.
    private async Task RehydrateInterruptedAgentsAsync(CancellationToken ct)
    {
        try
        {
            var workUnits = _serviceProvider.GetService<IWorkUnitService>();
            if (workUnits is null) { return; }

            var all = await workUnits.ListAsync(branchId: null, ct).ConfigureAwait(false);
            foreach (var wu in all)
            {
                var isRunning = wu.Status is WorkUnitStatus.Active or WorkUnitStatus.Executing or WorkUnitStatus.Retrying;
                if (!isRunning || wu.AssignedAgent is null) { continue; }

                // Only register if no live agent slot already covers this work unit
                var hasLiveAgent = _agents.Values.Any(a => a.WorkUnitId == wu.WorkUnitId && a.Cts is not null);
                if (hasLiveAgent) { continue; }

                _agents.TryAdd(wu.AssignedAgent, new AgentRecord(wu.AssignedAgent, wu.WorkUnitId, "interrupted"));
                _logger.LogInformation(
                    "[Rehydration] Work unit {WorkUnitId} was interrupted — agent {AgentId} marked as interrupted.",
                    wu.WorkUnitId, wu.AssignedAgent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Rehydration] Failed to sweep for interrupted agents.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pollCts?.Cancel();
        return Task.CompletedTask;
    }

    // ── Scheduler polling loop ─────────────────────────────────────────────

    private async Task PollSchedulerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_activeWorkerCount < _options.MaxConcurrentWorkers)
                {
                    var pollerId = $"poller-{Guid.NewGuid():N}";
                    var item = await _scheduler.TryAcquireAsync(pollerId, ct).ConfigureAwait(false);
                    if (item is not null)
                    {
                        _logger.LogInformation(
                            "[Scheduler] Acquired workUnit={WorkUnitId} taskId={TaskId} profile={ProfileId}",
                            item.WorkUnitId, item.TaskId ?? "(none)", item.ProfileId);

                        _ = Task.Run(() => RunScheduledWorkerAsync(item, ct), CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Scheduler] Poll iteration failed.");
            }

            // Slice 20c — check Hybrid review timers on each scheduler tick.
            try
            {
                var timerService = _serviceProvider.GetService(typeof(IReviewTimerService)) as IReviewTimerService;
                if (timerService is not null)
                    await timerService.ProcessExpiredAsync(ct).ConfigureAwait(false);
            }
            catch { /* timer processing is best-effort */ }

            try { await Task.Delay(_options.SchedulerPollIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunScheduledWorkerAsync(ScheduledItem item, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeWorkerCount);
        var agentId = $"worker-{Guid.NewGuid():N}";
        var cts = new CancellationTokenSource();
        var success = false;
        string? failureReason = null;

        try
        {
            var profile = item.ProfileId is not null
                ? await _profileService.GetAsync(item.ProfileId, ct).ConfigureAwait(false)
                : null;

            var provider = item.Provider ?? "anthropic";
            var model    = item.Model    ?? string.Empty;
            var baseUrl  = item.BaseUrl  ?? string.Empty;
            var apiKey   = item.ApiKey   ?? string.Empty;
            var taskId   = item.TaskId   ?? string.Empty;

            _agents[agentId] = new AgentRecord(agentId, item.WorkUnitId, "active", taskId, model, baseUrl, apiKey, provider, cts);

            var canRun = !string.IsNullOrWhiteSpace(baseUrl) && apiKey is not null
                && (!string.IsNullOrWhiteSpace(model)
                    || provider.Equals("openai", StringComparison.OrdinalIgnoreCase));

            if (!canRun)
            {
                _logger.LogWarning("[Agent {AgentId}] Scheduled worker will NOT run — missing credentials.", agentId);
                failureReason = "Missing LLM credentials";
            }
            else
            {
                if (item.SessionId is not null)
                {
                    await _events.AppendAsync(
                        item.SessionId,
                        item.WorkUnitId,
                        ExecutionEventKind.WorkUnitStarted,
                        new WorkUnitStartedPayload(item.WorkUnitId, agentId),
                        ct: ct).ConfigureAwait(false);
                }

                var dispatcher = _serviceProvider.GetRequiredService<McpToolDispatcher>();
                var llm = _serviceProvider.GetRequiredService<LlmClient>();
                var ruleFileContext = await BuildRuleFileContextAsync(item.WorkUnitId, ct).ConfigureAwait(false);

                AgentLoopCompletion completion;
                if (profile?.Stage == PipelineStage.Plan)
                {
                    var plannerLoop = new PlannerAgentLoop(
                        agentId, item.WorkUnitId, provider, model, baseUrl, apiKey!,
                        dispatcher, llm, profile, item.SessionId, a => ReportActivity(agentId, a),
                        ruleFileContext);
                    completion = await plannerLoop.RunAsync(cts.Token).ConfigureAwait(false);
                }
                else if (profile?.Stage == PipelineStage.Review)
                {
                    var proposalId = string.IsNullOrWhiteSpace(taskId) ? string.Empty : taskId;
                    var reviewerLoop = new ReviewerAgentLoop(
                        agentId, item.WorkUnitId, proposalId, provider, model, baseUrl, apiKey!,
                        dispatcher, llm, profile, item.SessionId, a => ReportActivity(agentId, a));
                    completion = await reviewerLoop.RunAsync(cts.Token).ConfigureAwait(false);
                }
                else
                {
                    // AttemptCount > 0 means this item was leased at least once before — either a
                    // normal failure-retry or, per Phase 8c, a resume after a Host restart
                    // interrupted it. Either way the worker should check existing branch/task
                    // state before assuming a clean start.
                    var loop = new WorkerAgentLoop(
                        agentId, item.WorkUnitId, taskId, provider, model, baseUrl, apiKey!,
                        dispatcher, llm, profile, item.SessionId, a => ReportActivity(agentId, a),
                        isResume: item.AttemptCount > 0, ruleFileContext: ruleFileContext,
                        selfVerifyBuild: _options.RequireBuildBeforeProposal,
                        selfVerifyTest: _options.RequireTestBeforeProposal);
                    completion = await loop.RunAsync(cts.Token).ConfigureAwait(false);
                }

                if (completion == AgentLoopCompletion.Succeeded)
                    success = true;
                else if (completion == AgentLoopCompletion.MaxIterationsExceeded)
                    failureReason = "Max iterations reached";
            }

            if (_agents.TryGetValue(agentId, out var r) && r.Status == "active")
                _agents[agentId] = r with { Status = "stopped", Cts = null, CurrentActivity = null };
        }
        catch (OperationCanceledException)
        {
            if (_agents.TryGetValue(agentId, out var r))
                _agents[agentId] = r with { Status = "stopped", Cts = null, CurrentActivity = null };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Agent {AgentId}] Scheduled worker loop failed.", agentId);
            failureReason = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            if (_agents.TryGetValue(agentId, out var r))
            {
                var msg = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
                _agents[agentId] = r with { Status = $"failed:{msg}", Cts = null, CurrentActivity = null };
            }
        }
        finally
        {
            if (failureReason is not null)
            {
                var profile = item.ProfileId is not null
                    ? await _profileService.GetAsync(item.ProfileId, ct).ConfigureAwait(false)
                    : null;
                await RecordDeadLetterAsync(item, agentId, profile, failureReason, ct).ConfigureAwait(false);
            }

            cts.Dispose();
            Interlocked.Decrement(ref _activeWorkerCount);
            await _scheduler.ReleaseAsync(item.WorkUnitId, success).ConfigureAwait(false);
            _logger.LogInformation(
                "[Scheduler] Released workUnit={WorkUnitId} success={Success}", item.WorkUnitId, success);
        }
    }

    private async Task RecordDeadLetterAsync(
        ScheduledItem item,
        string agentId,
        AgentProfile? profile,
        string reason,
        CancellationToken ct)
    {
        var deadLetter = _serviceProvider.GetService<IDeadLetterService>();
        if (deadLetter is null)
            return;

        await deadLetter.RecordFailureAsync(
            item.WorkUnitId,
            agentId,
            profile?.Stage ?? PipelineStage.Execute,
            item.ProfileId,
            reason,
            string.IsNullOrWhiteSpace(item.TaskId) ? null : item.TaskId,
            sessionId: item.SessionId,
            cancellationToken: ct).ConfigureAwait(false);
    }

    // ── IAgentRuntimeService ───────────────────────────────────────────────

    public Task<ExecutionSnapshot> GetSnapshotAsync(
        string agentId,
        string workUnitId,
        CancellationToken cancellationToken = default)
    {
        _snapshots.TryGetValue((agentId, workUnitId), out var snapshot);
        snapshot ??= new ExecutionSnapshot(
            agentId,
            workUnitId,
            null, null, null,
            [], [], 0, 0, null);

        return Task.FromResult(snapshot);
    }

    public Task RecordActionAsync(
        string agentId,
        string workUnitId,
        string action,
        CancellationToken cancellationToken = default)
    {
        var key = (agentId, workUnitId);
        var current = _snapshots.GetOrAdd(
            key,
            _ => new ExecutionSnapshot(agentId, workUnitId, null, null, null, [], [], 0, 0, null));

        var actions = current.RecentActions.ToList();
        actions.Add(action);
        _snapshots[key] = current with { RecentActions = actions };
        return Task.CompletedTask;
    }

    // ── ISnapshotService ───────────────────────────────────────────────────

    Task<ExecutionSnapshot> ISnapshotService.GetAsync(
        string agentId,
        string workUnitId,
        CancellationToken cancellationToken) =>
        GetSnapshotAsync(agentId, workUnitId, cancellationToken);

    public Task<string> CompareAsync(
        string agentId,
        string workUnitId,
        string otherAgentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("[]");

    // ── IAgentControlService ───────────────────────────────────────────────

    public Task<string> SpawnAsync(
        string agentType,
        string workUnitId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        string? profileId = null,
        string? autoReviewProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = $"{agentType}-{Guid.NewGuid():N}";

        AgentProfile? profile = profileId is not null
            ? _profileService.GetAsync(profileId, cancellationToken).GetAwaiter().GetResult()
            : null;

        CancellationTokenSource? cts = null;
        var resolvedProvider = provider ?? "anthropic";
        var canStartLoop = !string.IsNullOrWhiteSpace(baseUrl) && apiKey is not null
            && (!string.IsNullOrWhiteSpace(model)
                || resolvedProvider.Equals("openai", StringComparison.OrdinalIgnoreCase));
        _logger.LogInformation(
            "[Agent {AgentId}] Spawn — agentType={AgentType} provider={Provider} model={Model} baseUrl={BaseUrl} profileId={ProfileId} canStartLoop={CanStart}",
            agentId, agentType, resolvedProvider, model ?? "(none)", baseUrl ?? "(none)", profileId ?? "(none)", canStartLoop);
        if (!canStartLoop)
            _logger.LogWarning("[Agent {AgentId}] Loop will NOT start — missing credentials or model. baseUrl={BaseUrl} model={Model} provider={Provider}",
                agentId, baseUrl ?? "(none)", model ?? "(none)", resolvedProvider);
        if (canStartLoop)
        {
            cts = new CancellationTokenSource();
            var loopModel = model ?? string.Empty;
            if (agentType == "orchestrator")
            {
                StartOrchestratorLoop(agentId, workUnitId, resolvedProvider, loopModel, baseUrl!, apiKey ?? string.Empty, profile, cts);
                _orchestratorRegistrations[workUnitId] = new OrchestratorRegistration(
                    resolvedProvider, loopModel, baseUrl!, apiKey ?? string.Empty, profileId, autoReviewProfileId);
            }
            else if (agentType == "worker" && taskId is not null)
                StartWorkerLoop(agentId, workUnitId, taskId, resolvedProvider, loopModel, baseUrl!, apiKey ?? string.Empty, profile, cts);
            else
                cts.Dispose();
        }

        _agents[agentId] = new AgentRecord(agentId, workUnitId, "active", taskId, model, baseUrl, apiKey, provider, cts);
        return Task.FromResult(agentId);
    }

    public Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        if (!_orchestratorRegistrations.TryGetValue(workUnitId, out var reg))
            return Task.CompletedTask;

        var agentId = $"orchestrator-{Guid.NewGuid():N}";
        var cts = new CancellationTokenSource();
        _agents[agentId] = new AgentRecord(agentId, workUnitId, "active", null, reg.Model, reg.BaseUrl, reg.ApiKey, reg.Provider, cts);

        AgentProfile? profile = reg.ProfileId is not null
            ? _profileService.GetAsync(reg.ProfileId, cancellationToken).GetAwaiter().GetResult()
            : null;

        StartOrchestratorLoop(agentId, workUnitId, reg.Provider, reg.Model, reg.BaseUrl, reg.ApiKey, profile, cts, sessionId);
        return Task.CompletedTask;
    }

    public OrchestratorCredentials? GetOrchestratorCredentials(string workUnitId)
    {
        if (!_orchestratorRegistrations.TryGetValue(workUnitId, out var reg))
            return null;

        return new OrchestratorCredentials(reg.Provider, reg.Model, reg.BaseUrl, reg.ApiKey, reg.ProfileId);
    }

    public string? GetAutoReviewProfileId(string workUnitId)
    {
        if (!_orchestratorRegistrations.TryGetValue(workUnitId, out var reg))
            return null;

        return reg.AutoReviewProfileId;
    }

    private void StartOrchestratorLoop(
        string agentId,
        string workUnitId,
        string provider,
        string model,
        string baseUrl,
        string apiKey,
        AgentProfile? profile,
        CancellationTokenSource cts,
        string? sessionId = null)
    {
        _logger.LogInformation("[Agent {AgentId}] Starting orchestrator loop — provider={Provider} model={Model} baseUrl={BaseUrl}",
            agentId, provider, model, baseUrl);
        _ = Task.Run(async () =>
        {
            try
            {
                var dispatcher = _serviceProvider.GetRequiredService<McpToolDispatcher>();
                var llm = _serviceProvider.GetRequiredService<LlmClient>();
                var artifactLineage = _serviceProvider.GetRequiredService<IArtifactLineageService>();
                var projections = _serviceProvider.GetRequiredService<IProjectionManager>();
                var decisionLog = _serviceProvider.GetRequiredService<IOrchestrationDecisionLogService>();
                var fanOut = _serviceProvider.GetRequiredService<IFanOutService>();
                var mergeReconciliation = _serviceProvider.GetRequiredService<IMergeReconciliationService>();
                var automatedReview = _serviceProvider.GetRequiredService<IAutomatedReviewGateService>();
                var workUnits = _serviceProvider.GetRequiredService<IWorkUnitService>();
                var workspaceOptions = _serviceProvider.GetRequiredService<WorkspaceOptions>();
                var loop = new OrchestratorAgentLoop(
                    agentId, workUnitId, provider, model, baseUrl, apiKey, dispatcher, llm,
                    artifactLineage, projections, decisionLog, fanOut, mergeReconciliation, automatedReview, workUnits,
                    profile, sessionId, workspaceOptions.StallDetectionCycles, a => ReportActivity(agentId, a));
                var completion = await loop.RunAsync(cts.Token).ConfigureAwait(false);
                if (completion is AgentLoopCompletion.MaxIterationsExceeded or AgentLoopCompletion.Stalled)
                {
                    var deadLetter = _serviceProvider.GetService<IDeadLetterService>();
                    if (deadLetter is not null)
                    {
                        var reason = completion == AgentLoopCompletion.Stalled
                            ? $"Stall: no artifact change after {workspaceOptions.StallDetectionCycles} cycles."
                            : "Max iterations reached";
                        await deadLetter.RecordFailureAsync(
                            workUnitId,
                            agentId,
                            profile?.Stage ?? PipelineStage.Orchestrate,
                            profile?.AgentProfileId ?? "orchestrator",
                            reason,
                            sessionId: sessionId,
                            cancellationToken: cts.Token).ConfigureAwait(false);
                    }
                }
                _logger.LogInformation("[Agent {AgentId}] Orchestrator loop completed.", agentId);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agent {AgentId}] Orchestrator loop failed.", agentId);
                var deadLetter = _serviceProvider.GetService<IDeadLetterService>();
                if (deadLetter is not null)
                {
                    await deadLetter.RecordFailureAsync(
                        workUnitId,
                        agentId,
                        profile?.Stage ?? PipelineStage.Orchestrate,
                        profile?.AgentProfileId ?? "orchestrator",
                        ex.Message.Length > 200 ? ex.Message[..200] : ex.Message,
                        sessionId: sessionId,
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                if (_agents.TryGetValue(agentId, out var r))
                {
                    var msg = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
                    _agents[agentId] = r with { Status = $"failed:{msg}", Cts = null, CurrentActivity = null };
                }
            }
            finally
            {
                if (_agents.TryGetValue(agentId, out var r) && r.Status == "active")
                    _agents[agentId] = r with { Status = "stopped", Cts = null, CurrentActivity = null };
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private void StartWorkerLoop(
        string agentId,
        string workUnitId,
        string taskId,
        string provider,
        string model,
        string baseUrl,
        string apiKey,
        AgentProfile? profile,
        CancellationTokenSource cts)
    {
        _logger.LogInformation("[Agent {AgentId}] Starting worker loop — provider={Provider} model={Model} taskId={TaskId}",
            agentId, provider, model, taskId);
        _ = Task.Run(async () =>
        {
            try
            {
                var dispatcher = _serviceProvider.GetRequiredService<McpToolDispatcher>();
                var llm = _serviceProvider.GetRequiredService<LlmClient>();
                var ruleFileContext = await BuildRuleFileContextAsync(workUnitId, cts.Token).ConfigureAwait(false);
                var loop = new WorkerAgentLoop(
                    agentId, workUnitId, taskId, provider, model, baseUrl, apiKey, dispatcher, llm, profile,
                    onActivity: a => ReportActivity(agentId, a), ruleFileContext: ruleFileContext,
                    selfVerifyBuild: _options.RequireBuildBeforeProposal,
                    selfVerifyTest: _options.RequireTestBeforeProposal);
                await loop.RunAsync(cts.Token).ConfigureAwait(false);
                _logger.LogInformation("[Agent {AgentId}] Worker loop completed.", agentId);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agent {AgentId}] Worker loop failed.", agentId);
                if (_agents.TryGetValue(agentId, out var r))
                {
                    var msg = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
                    _agents[agentId] = r with { Status = $"failed:{msg}", Cts = null, CurrentActivity = null };
                }
            }
            finally
            {
                if (_agents.TryGetValue(agentId, out var r) && r.Status == "active")
                    _agents[agentId] = r with { Status = "stopped", Cts = null, CurrentActivity = null };
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    public Task PauseAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var current = GetRequired(agentId);
        _agents[agentId] = current with { Status = "paused" };
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var current = GetRequired(agentId);
        _agents[agentId] = current with { Status = "active" };
        return Task.CompletedTask;
    }

    public Task StopAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var current = GetRequired(agentId);
        current.Cts?.Cancel();
        _agents[agentId] = current with { Status = "stopped", Cts = null, CurrentActivity = null };
        return Task.CompletedTask;
    }

    public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default)
    {
        _agents.TryGetValue(agentId, out var record);
        return Task.FromResult(record?.Status ?? "unknown");
    }

    public Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var active = _agents.Values
            .Where(a => a.Status == "active")
            .Select(a => new AgentInfo(a.AgentId, a.WorkUnitId, a.Status, a.CurrentActivity))
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentInfo>>(active);
    }

    public Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var all = _agents.Values
            .Select(a => new AgentInfo(a.AgentId, a.WorkUnitId, a.Status, a.CurrentActivity))
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentInfo>>(all);
    }

    private AgentRecord GetRequired(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var record))
            throw new KeyNotFoundException($"Agent '{agentId}' was not found.");
        return record;
    }

    // Phase 9h — best-effort root-level instruction injection (AGENTS.md/CLAUDE.md/.clinerules/
    // .cursorrules). Resolved once up front and appended to the kickoff message rather than the
    // static system prompt, since it's per-branch data, not a constant. Never throws — a missing
    // work unit, an undetectable profile, or a repo with no rule files at all just means no
    // context gets appended, not a failed spawn.
    private async Task<string?> BuildRuleFileContextAsync(string workUnitId, CancellationToken ct)
    {
        try
        {
            var workUnits = _serviceProvider.GetRequiredService<IWorkUnitService>();
            var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (wu is null) return null;

            var profiles = _serviceProvider.GetRequiredService<IWorkspaceProfileService>();
            var profile = await profiles.GetOrDetectAsync(wu.BranchId, ct).ConfigureAwait(false);

            var sections = profile.Roots
                .Where(r => r.RuleFileContent is not null)
                .Select(r =>
                {
                    var label = r.RelativePath.Length == 0 ? "repo root" : $"'{r.RelativePath}'";
                    return $"Project root {label} ({r.Stack}) has its own instructions — follow them:\n\n{r.RuleFileContent}";
                })
                .ToList();

            return sections.Count == 0 ? null : string.Join("\n\n", sections);
        }
        catch
        {
            return null;
        }
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioAgentRuntime(this IServiceCollection services, HttpClient? llmHttpClient = null)
    {
        services.AddSingleton<InMemoryAgentRuntimeService>();
        services.AddSingleton<IAgentRuntimeService>(sp => sp.GetRequiredService<InMemoryAgentRuntimeService>());
        services.AddSingleton<ISnapshotService>(sp => sp.GetRequiredService<InMemoryAgentRuntimeService>());
        services.AddSingleton<IAgentControlService>(sp => sp.GetRequiredService<InMemoryAgentRuntimeService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InMemoryAgentRuntimeService>());
        services.AddSingleton<McpToolDispatcher>();
        services.AddSingleton<LlmClient>(_ => new LlmClient(llmHttpClient ?? new HttpClient()));
        services.AddSingleton<IProfileSelectionService, LlmProfileSelectionService>();
        // Slice 20b — inline reviewer for AgentApproval/Hybrid BeforeMerge gate.
        services.AddSingleton<IInlineReviewerService, InlineReviewerService>();
        return services;
    }
}
