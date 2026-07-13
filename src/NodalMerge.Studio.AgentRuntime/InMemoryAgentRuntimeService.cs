using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime;

public sealed class InMemoryAgentRuntimeService : IAgentRuntimeService, ISnapshotService, IAgentControlService, IHostedService
{
    private readonly ConcurrentDictionary<(string AgentId, string WorkUnitId), ExecutionSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, AgentRecord> _agents = new();
    private readonly ConcurrentDictionary<string, OrchestratorRegistration> _orchestratorRegistrations = new();
    // Safe-to-persist subset of OrchestratorRegistration (no ApiKey anywhere), rehydrated from
    // IStudioNodeStore on startup — this is what survives a Host restart. _orchestratorRegistrations
    // above is checked first (same-process hot path, has real credentials); this is the fallback.
    private readonly ConcurrentDictionary<string, OrchestratorRoutingConfig> _orchestratorRouting = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InMemoryAgentRuntimeService> _logger;
    private readonly IAgentProfileService _profileService;
    private readonly IWorkScheduler _scheduler;
    private readonly IExecutionEventStream _events;
    private readonly WorkspaceOptions _options;
    private readonly IFileLeaseService _fileLease;
    private readonly IStudioNodeStore _nodeStore;
    private readonly IRuntimeCredentialCache _credentialCache;
    private int _activeWorkerCount;
    private CancellationTokenSource? _pollCts;

    public InMemoryAgentRuntimeService(
        IServiceProvider serviceProvider,
        ILogger<InMemoryAgentRuntimeService> logger,
        IAgentProfileService profileService,
        IWorkScheduler scheduler,
        IExecutionEventStream events,
        WorkspaceOptions options,
        IFileLeaseService fileLease,
        IStudioNodeStore nodeStore,
        IRuntimeCredentialCache credentialCache)
    {
        _serviceProvider = serviceProvider;
        _logger          = logger;
        _profileService  = profileService;
        _scheduler       = scheduler;
        _events          = events;
        _options         = options;
        _fileLease       = fileLease;
        _nodeStore       = nodeStore;
        _credentialCache = credentialCache;
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
        string Provider, string Model, string BaseUrl, string ApiKey, string? ProfileId, string? AutoReviewProfileId,
        IReadOnlyDictionary<PipelineStage, OrchestratorCredentials>? StageCredentials = null,
        IReadOnlyList<string>? EnabledDomainAgents = null,
        string? CredentialRef = null);

    // ── IHostedService ─────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RehydrateOrchestratorRoutingAsync(cancellationToken).ConfigureAwait(false);
        await RehydrateInterruptedAgentsAsync(cancellationToken).ConfigureAwait(false);
        _pollCts = new CancellationTokenSource();
        _ = Task.Run(() => PollSchedulerAsync(_pollCts.Token), CancellationToken.None);
    }

    // Restores the safe (no-ApiKey) subset of every orchestrator's registration so
    // GetAutoReviewProfileId/GetEnabledDomainAgents/routing work immediately after a restart, with
    // zero credential resupply required — _orchestratorRegistrations itself (with real credentials)
    // intentionally stays empty until something re-spawns or resupplies via the Resume flow.
    private async Task RehydrateOrchestratorRoutingAsync(CancellationToken ct)
    {
        try
        {
            var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.OrchestratorRoutingV1, ct)
                .ConfigureAwait(false);
            foreach (var (entityId, payloadJson) in records)
            {
                var routing = JsonSerializer.Deserialize<OrchestratorRoutingConfig>(payloadJson);
                if (routing is not null)
                    _orchestratorRouting[entityId] = routing;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Rehydration] Failed to restore orchestrator routing config.");
        }
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
            catch (ObjectDisposedException)
            {
                // Host shutdown can dispose the DI root while the poll loop is unwinding.
                // That's terminal for this runtime instance, not an operational failure.
                break;
            }
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
            catch (ObjectDisposedException) { break; }
            catch { /* timer processing is best-effort */ }

            // Phase 1 — check clarification timeouts on each scheduler tick.
            try
            {
                var clarificationTimer = _serviceProvider.GetService(typeof(NodalMerge.Studio.Storage.IClarificationTimerService)) as NodalMerge.Studio.Storage.IClarificationTimerService;
                if (clarificationTimer is not null)
                    await clarificationTimer.ProcessExpiredAsync(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { break; }
            catch { /* timer processing is best-effort */ }

            try { await Task.Delay(_options.SchedulerPollIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunScheduledWorkerAsync(ScheduledItem item, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeWorkerCount);
        var cts = new CancellationTokenSource();
        var success = false;
        var awaitingFileLease = false;
        var awaitingClarification = false;
        var awaitingCredentials = false;
        var needsExecuteFallback = false;
        string? failureReason = null;
        var failureKind = FailureKind.Exception;

        // Resolved before the try block (not inside it) so the catch/finally below — which
        // reference agentId for logging/agent-record cleanup — can still see it even if profile
        // resolution or anything after it throws.
        var profile = item.ProfileId is not null
            ? await _profileService.GetAsync(item.ProfileId, ct).ConfigureAwait(false)
            : null;
        // Agent IDs used to be hardcoded "worker-{guid}" regardless of which loop actually ran
        // (Planner/Reviewer/Worker) — indistinguishable in the conversation log and Activity
        // Center from a real Execute-stage worker, which made a Planner-profile run pointed at
        // an already-leaf work unit (see the leaf-slice guard above) look exactly like a worker
        // had gone rogue and started planning. Naming it after the resolved profile makes the log
        // honest about which loop is actually executing.
        var agentId = $"{profile?.AgentProfileId ?? "worker"}-{Guid.NewGuid():N}";

        try
        {
            var provider = item.Provider ?? "anthropic";
            var model    = item.Model    ?? string.Empty;
            var baseUrl  = item.BaseUrl  ?? string.Empty;
            var apiKey   = item.ApiKey;
            var taskId   = item.TaskId   ?? string.Empty;
            // GetService, not GetRequiredService: this resolver is only used below for the
            // canRun/Plan-Review CLI-provider gates, which must degrade gracefully (no CLI
            // providers registered) rather than throw when a test double's IServiceProvider
            // doesn't carry it — the real executor resolution a few lines down still uses
            // GetRequiredService since it's on the actual run path, not a gate.
            var harnessExecutorResolver = _serviceProvider.GetService<IHarnessExecutorResolver>();

            // item.ApiKey is [JsonIgnore]d out of persistence — a rehydrated item (Host restart)
            // deserializes it as null. Re-resolve from the shared cache via CredentialRef before
            // falling through to the "missing credentials" failure path below. A cache miss here
            // (nobody has resupplied yet) parks the item rather than dead-lettering it — it's not
            // actually broken, it just needs a human/the extension to click Resume.
            if (string.IsNullOrEmpty(apiKey) && !string.IsNullOrWhiteSpace(item.CredentialRef))
            {
                var cached = _credentialCache.TryGet(item.CredentialRef);
                if (cached is null)
                {
                    awaitingCredentials = true;
                }
                else
                {
                    apiKey = cached.ApiKey;
                    if (!string.IsNullOrEmpty(cached.Provider)) provider = cached.Provider;
                    if (!string.IsNullOrEmpty(cached.Model)) model = cached.Model;
                    if (!string.IsNullOrEmpty(cached.BaseUrl)) baseUrl = cached.BaseUrl;
                }
            }
            apiKey ??= string.Empty;

            _agents[agentId] = new AgentRecord(agentId, item.WorkUnitId, "active", taskId, model, baseUrl, apiKey, provider, cts);

            // claude-cli needs no baseUrl/apiKey (ambient CLI auth; blank model = the CLI's own
            // default) — its only gate is not being parked for a credential-cache re-resolve.
            var canRun = !awaitingCredentials &&
                ((harnessExecutorResolver?.IsCliProvider(provider) ?? false)
                    || (!string.IsNullOrWhiteSpace(baseUrl) && apiKey is not null
                        && (!string.IsNullOrWhiteSpace(model)
                            || provider.Equals("openai", StringComparison.OrdinalIgnoreCase))));

            if (awaitingCredentials)
            {
                _logger.LogInformation(
                    "[Agent {AgentId}] Parking workUnit={WorkUnitId} awaiting credentials for ref={CredentialRef}",
                    agentId, item.WorkUnitId, item.CredentialRef);
            }
            else if (!canRun)
            {
                _logger.LogWarning("[Agent {AgentId}] Scheduled worker will NOT run — missing credentials.", agentId);
                failureReason = "Missing LLM credentials";
                failureKind = FailureKind.MissingCredentials;
            }
            else if ((harnessExecutorResolver?.IsCliProvider(provider) ?? false) &&
                     profile?.Stage is PipelineStage.Review)
            {
                // Only the Worker/Execute and Plan construction sites are behind the executor seam
                // (B1 + plans/phase-d-implementation.md D1.a); Review still constructs the native
                // loop class directly, and that would hand claude-cli "credentials" to LlmClient.
                // Fail loudly instead of producing a confusing HTTP error mid-loop. Lifted when
                // Review mode lands behind the seam.
                _logger.LogWarning(
                    "[Agent {AgentId}] Scheduled {Stage} run will NOT start — the claude-cli provider only supports Execute/Plan-stage roles today.",
                    agentId, profile.Stage);
                failureReason = $"The Claude Code CLI provider cannot run {profile.Stage}-stage roles yet — only Execute (worker) and Plan. Assign an API-based Model Profile to this role.";
                failureKind = FailureKind.MissingCredentials;
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
                var agentClient = new DefaultAgentToolClient(provider, model, baseUrl, apiKey ?? string.Empty, llm, dispatcher);
                var conversationLog = _serviceProvider.GetRequiredService<IConversationLogService>();
                var ruleFileContext = await BuildRuleFileContextAsync(item.WorkUnitId, ct).ConfigureAwait(false);

                AgentLoopCompletion completion;
                var workerProgressVerified = true;
                if (profile?.Stage == PipelineStage.Plan)
                {
                    // plans/phase-d-implementation.md D1.a — Plan-stage construction moves behind
                    // the executor seam, the same way the Worker/Execute branch below already
                    // does: resolve via provider, build a HarnessRunRequest, let the executor
                    // construct whatever loop it needs (NativeHarnessExecutor wraps
                    // PlannerAgentLoop for Mode==Plan; a CLI executor writes .workspace/plan.json).
                    // A resolved executor that hasn't wired planning mode yet (Capabilities
                    // .SupportsPlanningMode false) degrades to native rather than failing the item
                    // over a capability miss — same "never fail on capability miss" posture as the
                    // CLI-provider gate above.
                    var constraintsContext = await BuildConstraintsContextAsync(item.WorkUnitId, ct).ConfigureAwait(false);
                    var promptGuidanceContext = await BuildPromptGuidanceContextAsync(PipelineStage.Plan, ct).ConfigureAwait(false);
                    var engineeringStateContext = await BuildEngineeringStateContextAsync(item.WorkUnitId, ct).ConfigureAwait(false);
                    var combinedContext = string.Join("\n\n", new[] { constraintsContext, promptGuidanceContext, engineeringStateContext }.Where(s => s is not null));

                    var planExecutorResolver = _serviceProvider.GetRequiredService<IHarnessExecutorResolver>();
                    var planExecutor = planExecutorResolver.ResolveForProvider(provider, profile?.Executor);
                    if (!planExecutor.Capabilities.SupportsPlanningMode)
                        planExecutor = planExecutorResolver.Resolve("native");

                    var planRequest = new HarnessRunRequest(
                        HarnessMode.Plan, agentId, item.WorkUnitId, taskId, profile, item.SessionId,
                        IsResume: item.AttemptCount > 0, ruleFileContext,
                        PromptGuidanceContext: combinedContext.Length == 0 ? null : combinedContext,
                        SelfVerifyBuild: false, SelfVerifyTest: false,
                        OnActivity: a => ReportActivity(agentId, a),
                        Provider: provider, Model: model, BaseUrl: baseUrl, ApiKey: apiKey);
                    var planResult = await planExecutor.RunAsync(planRequest, cts.Token).ConfigureAwait(false);
                    completion = planResult.Completion;

                    // A Planner that correctly concludes "nothing to decompose here" ends its turn
                    // without recording a Plan artifact — that's Succeeded, not a failure, but
                    // nothing else in this pipeline ever calls IFanOutService for a scheduler-driven
                    // Plan item (only OrchestratorAgentLoop and IReplanService do, for their own
                    // targets), so without this the work unit would just go idle after a
                    // "successful" no-op. Hand it straight to Execute instead.
                    if (completion == AgentLoopCompletion.Succeeded)
                    {
                        var artifactLineageForPlan = _serviceProvider.GetService<IArtifactLineageService>();
                        var recordedPlan = artifactLineageForPlan is not null &&
                            (await artifactLineageForPlan.GetChainAsync(item.WorkUnitId, ct).ConfigureAwait(false))
                                .Any(a => a.Type == ArtifactType.Plan && a.OwnedByWorkUnitId == item.WorkUnitId);
                        if (!recordedPlan)
                            needsExecuteFallback = true;
                    }
                }
                else if (profile?.Stage == PipelineStage.Review)
                {
                    var proposalId = string.IsNullOrWhiteSpace(taskId) ? string.Empty : taskId;
                    var reviewerLoop = new ReviewerAgentLoop(
                        agentId, item.WorkUnitId, proposalId, agentClient,
                        profile, item.SessionId, a => ReportActivity(agentId, a),
                        conversationLog: conversationLog, events: _events, logger: _logger);
                    completion = await reviewerLoop.RunAsync(cts.Token).ConfigureAwait(false);
                }
                else
                {
                    // AttemptCount > 0 means this item was leased at least once before — either a
                    // normal failure-retry or, per Phase 8c, a resume after a Host restart
                    // interrupted it. Either way the worker should check existing branch/task
                    // state before assuming a clean start.
    // promptGuidanceContext below carries universal KnowledgeGuideline constraints (the same
                    // feed Orchestrator/Planner already get) and Execute-stage PromptImprovement guidance —
                    // NOT the original top-level goal text. That was tried (BuildOriginalGoalContextAsync,
                    // removed) as a blanket fix for planners dropping literal contract details while
                    // paraphrasing a slice goal, but it injected the full unbounded root goal into every
                    // fanned-out worker's kickoff regardless of need, which both confused workers with
                    // irrelevant scope and multiplied token cost across siblings. The real fix belongs at
                    // the source (Planner prompt: copy literal contracts verbatim into the slice's own
                    // goal/steps) with Reviewer as the backstop (walks ParentWorkUnitId on demand via
                    // nm_v1_workunit_get if a proposal looks like it may have lost a literal detail) —
                    // both pull context only when actually needed instead of pushing it to everyone.
                    var workerConstraintsContext = await BuildConstraintsContextAsync(item.WorkUnitId, ct).ConfigureAwait(false);
                    var workerPromptGuidance = await BuildPromptGuidanceContextAsync(PipelineStage.Execute, ct).ConfigureAwait(false);
                    var workerEngineeringState = await BuildEngineeringStateContextAsync(item.WorkUnitId, ct).ConfigureAwait(false);
                    var workerCombinedGuidance = string.Join("\n\n", new[] { workerConstraintsContext, workerPromptGuidance, workerEngineeringState }.Where(s => s is not null));
                    // Snapshotted before the loop runs: if the task was already Completed coming
                    // in (e.g. re-queued after a rejection, before the underlying task got reset —
                    // see AutomatedReviewGateService), the agent can't legitimately transition it
                    // and a post-run "Completed" check alone would be fooled by that stale state.
                    var taskServiceForVerify = _serviceProvider.GetService<ITaskService>();
                    var taskStatusBeforeRun = !string.IsNullOrWhiteSpace(taskId) && taskServiceForVerify is not null
                        ? (await taskServiceForVerify.GetAsync(taskId, ct).ConfigureAwait(false))?.Status
                        : null;
                    // plans/harness-hosting-architecture.md Phase B.1, extended by
                    // plans/phase-d-implementation.md D1.a — Worker/Execute and Plan-stage
                    // construction both go through the executor seam now; Review above still stays
                    // on the native ReviewerAgentLoop class directly (out of scope so far).
                    // agentClient/conversationLog built above are unused here (used only by the
                    // Review branch) — NativeHarnessExecutor resolves its own from the request's
                    // Provider/Model/BaseUrl/ApiKey. GetRequiredService here (unlike the nullable
                    // fetch above used only for the gates) — this is the real run path, which has
                    // always required a registered resolver.
                    var executorResolver = _serviceProvider.GetRequiredService<IHarnessExecutorResolver>();
                    var executor = executorResolver.ResolveForProvider(provider, profile?.Executor);
                    var harnessRequest = new HarnessRunRequest(
                        HarnessMode.Execute, agentId, item.WorkUnitId, taskId, profile, item.SessionId,
                        IsResume: item.AttemptCount > 0, ruleFileContext,
                        PromptGuidanceContext: workerCombinedGuidance.Length == 0 ? null : workerCombinedGuidance,
                        SelfVerifyBuild: _options.RequireBuildBeforeProposal,
                        SelfVerifyTest: _options.RequireTestBeforeProposal,
                        OnActivity: a => ReportActivity(agentId, a),
                        Provider: provider, Model: model, BaseUrl: baseUrl, ApiKey: apiKey);
                    var harnessResult = await executor.RunAsync(harnessRequest, cts.Token).ConfigureAwait(false);
                    completion = harnessResult.Completion;

                    if (completion == AgentLoopCompletion.Succeeded)
                    {
                        workerProgressVerified = await VerifyWorkerProgressAsync(
                            item.WorkUnitId, taskId, agentId, taskStatusBeforeRun, ct).ConfigureAwait(false);
                    }
                }

                if (completion == AgentLoopCompletion.Succeeded && workerProgressVerified)
                    success = true;
                else if (completion == AgentLoopCompletion.AwaitingFileLease)
                    awaitingFileLease = true;
                else if (completion == AgentLoopCompletion.AwaitingClarification)
                    awaitingClarification = true;
                else if (completion == AgentLoopCompletion.MaxIterationsExceeded)
                {
                    // Phase 1.4 (partial) — surface the decomposition suggestion wherever this
                    // reason is displayed (dead-letter REST responses, retry UI, execution
                    // timeline), rather than leaving a human to intuit it, as happened when this
                    // exact reason showed up during the harness-comparison-eval and got manually
                    // steered toward "fix the build" instead. Fully automatic re-planning
                    // (spawning smaller sub-tasks without a human in the loop) is deferred — see
                    // plans/orchestrator-reliability-and-observability.md Phase 1.4b — since
                    // RetryWithContextAsync bypasses the normal MaxFailureAttempts cap and an
                    // automatic retry-on-this-reason loop needs that verified safe first.
                    failureReason = "Max iterations reached — this task may have been too large " +
                        "for one execution pass. Consider decomposing it into 2+ smaller sub-tasks " +
                        "before retrying, rather than retrying the same scope unchanged.";
                    failureKind = FailureKind.MaxIterationsExceeded;
                }
                else if (completion == AgentLoopCompletion.Succeeded && !workerProgressVerified)
                {
                    failureReason = "Agent ended its turn without completing the task or producing a merge proposal.";
                    failureKind = FailureKind.ProgressNotVerified;
                }
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
                await RecordDeadLetterAsync(item, agentId, profile, failureReason, failureKind, ct).ConfigureAwait(false);
            }

            cts.Dispose();
            Interlocked.Decrement(ref _activeWorkerCount);

            // Phase 12 — a lease conflict isn't success or failure: park the item (kept queued,
            // not removed/dead-lettered) instead of calling ReleaseAsync, which would otherwise
            // treat this as a plain failure and drop it.
            if (awaitingFileLease)
            {
                await _scheduler.MarkAwaitingFileLeaseAsync(item.WorkUnitId, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "[Scheduler] Parked workUnit={WorkUnitId} awaiting file lease", item.WorkUnitId);
            }
            else if (awaitingCredentials)
            {
                await _scheduler.MarkAwaitingCredentialsAsync(item.WorkUnitId, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "[Scheduler] Parked workUnit={WorkUnitId} awaiting credentials", item.WorkUnitId);
            }
            else if (awaitingClarification)
            {
                _logger.LogInformation(
                    "[Scheduler] Parked workUnit={WorkUnitId} awaiting clarification", item.WorkUnitId);
            }
            else
            {
                await _scheduler.ReleaseAsync(item.WorkUnitId, success).ConfigureAwait(false);
                _logger.LogInformation(
                    "[Scheduler] Released workUnit={WorkUnitId} success={Success}", item.WorkUnitId, success);

                // Must run AFTER ReleaseAsync, not before: EnqueueAsync's AddOrUpdate leaves a
                // still-leased item untouched, so re-enqueuing while this run's own queue entry is
                // still leased would silently no-op instead of scheduling the Execute pass.
                if (needsExecuteFallback)
                {
                    await _scheduler.EnqueueAsync(
                        item.WorkUnitId, "worker", item.TaskId, item.Model, item.BaseUrl,
                        item.ApiKey, item.Provider, item.SessionId, item.CredentialRef, ct).ConfigureAwait(false);
                    _logger.LogInformation(
                        "[Scheduler] Planner recorded no plan for workUnit={WorkUnitId} — " +
                        "enqueued directly to Execute.", item.WorkUnitId);
                }
            }
        }
    }

    // A worker stopping with stopReason "end_turn" only means the model stopped talking — it says
    // nothing about whether real work happened. WorkerAgentLoop reports that as Succeeded
    // unconditionally, so this re-checks for an actual outcome before trusting it: either the task
    // transitioned to Completed during this run (not just already Completed coming in — that's
    // the stale-state trap a re-queued-after-rejection task can fall into), or this agent's own run
    // produced a MergeProposal for the work unit.
    private async Task<bool> VerifyWorkerProgressAsync(
        string workUnitId,
        string? taskId,
        string agentId,
        NodalMerge.Studio.Contracts.Domain.TaskStatus? taskStatusBeforeRun,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(taskId) &&
            taskStatusBeforeRun != NodalMerge.Studio.Contracts.Domain.TaskStatus.Completed)
        {
            var taskService = _serviceProvider.GetService<ITaskService>();
            var task = taskService is not null
                ? await taskService.GetAsync(taskId, ct).ConfigureAwait(false)
                : null;
            if (task?.Status == NodalMerge.Studio.Contracts.Domain.TaskStatus.Completed)
                return true;
        }

        var mergeService = _serviceProvider.GetService<IMergeService>();
        if (mergeService is not null)
        {
            var proposals = await mergeService.ListAsync(cancellationToken: ct).ConfigureAwait(false);
            if (proposals.Any(p => p.WorkUnitId == workUnitId && p.AgentId == agentId))
                return true;
        }

        return false;
    }

    private async Task RecordDeadLetterAsync(
        ScheduledItem item,
        string agentId,
        AgentProfile? profile,
        string reason,
        FailureKind kind,
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
            model: item.Model,
            baseUrl: item.BaseUrl,
            apiKey: item.ApiKey,
            provider: item.Provider,
            kind: kind,
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

    public async Task<string> SpawnAsync(
        string agentType,
        string workUnitId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        string? profileId = null,
        string? autoReviewProfileId = null,
        IReadOnlyDictionary<PipelineStage, OrchestratorCredentials>? stageCredentials = null,
        IReadOnlyList<string>? enabledDomainAgents = null,
        string? credentialRef = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = $"{agentType}-{Guid.NewGuid():N}";

        AgentProfile? profile = profileId is not null
            ? _profileService.GetAsync(profileId, cancellationToken).GetAwaiter().GetResult()
            : null;

        CancellationTokenSource? cts = null;
        var resolvedProvider = provider ?? "anthropic";
        // claude-cli needs no baseUrl/apiKey/model (ambient CLI auth, CLI-default model) — but it
        // only routes through the executor seam for worker spawns; an orchestrator must stay on an
        // API provider (its coordination loop is native by design — AP-6).
        // GetService, not GetRequiredService: this is a gate that must degrade gracefully rather
        // than throw when a test double's IServiceProvider doesn't carry the resolver (mirrors
        // RunScheduledWorkerAsync's identical gate above).
        var harnessExecutorResolverForSpawnGate = _serviceProvider.GetService<IHarnessExecutorResolver>();
        var canStartLoop = (harnessExecutorResolverForSpawnGate?.IsCliProvider(resolvedProvider) ?? false)
            ? agentType == "worker"
            : !string.IsNullOrWhiteSpace(baseUrl) && apiKey is not null
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
                    resolvedProvider, loopModel, baseUrl!, apiKey ?? string.Empty, profileId, autoReviewProfileId,
                    stageCredentials, enabledDomainAgents, credentialRef);

                _credentialCache.Capture(credentialRef, resolvedProvider, loopModel, baseUrl, apiKey);

                // The safe subset — no ApiKey anywhere in this record — persisted so
                // GetAutoReviewProfileId/GetEnabledDomainAgents/routing survive a Host restart even
                // though _orchestratorRegistrations above (the hot path, with real credentials)
                // doesn't. See RehydrateOrchestratorRoutingAsync for the read side.
                var routing = new OrchestratorRoutingConfig(
                    workUnitId, resolvedProvider, loopModel, baseUrl!, profileId, autoReviewProfileId,
                    credentialRef, stageCredentials, enabledDomainAgents);
                _orchestratorRouting[workUnitId] = routing;
                await _nodeStore.WriteNodeAsync(
                    StudioNodeKind.OrchestratorRoutingV1, workUnitId,
                    JsonSerializer.Serialize(routing), cancellationToken).ConfigureAwait(false);
            }
            else if (agentType == "worker" && taskId is not null)
                StartWorkerLoop(agentId, workUnitId, taskId, resolvedProvider, loopModel, baseUrl ?? string.Empty, apiKey ?? string.Empty, profile, cts);
            else
                cts.Dispose();
        }

        _agents[agentId] = new AgentRecord(agentId, workUnitId, "active", taskId, model, baseUrl, apiKey, provider, cts);
        return agentId;
    }

    private readonly record struct ResolvedOrchestratorCredentials(
        string Provider, string Model, string BaseUrl, string ApiKey, string? ProfileId, string? CredentialRef);

    // Shared by ReinvokeOrchestratorAsync and ResupplyCredentialsAsync — both need the exact same
    // "same-process hot path, then rehydrated routing + IRuntimeCredentialCache, then override*
    // params" resolution, and both need to (re-)persist whatever they land on so a *future*
    // restart/lookup recovers it too. The only difference between the two callers is whether an
    // orchestrator loop then gets spawned on top of this — see ResupplyCredentialsAsync's own doc
    // comment on IAgentControlService for why a requeue doesn't always want that.
    private async Task<ResolvedOrchestratorCredentials?> ResolveAndPersistCredentialsAsync(
        string workUnitId,
        string? overrideModel, string? overrideBaseUrl, string? overrideApiKey, string? overrideProvider,
        string? overrideProfileId, string? overrideCredentialRef,
        string actionName,
        CancellationToken cancellationToken)
    {
        string? provider = overrideProvider, model = overrideModel, baseUrl = overrideBaseUrl;
        string? apiKey = overrideApiKey, profileId = overrideProfileId, credentialRef = overrideCredentialRef;
        string? autoReviewProfileId = null;
        IReadOnlyDictionary<PipelineStage, OrchestratorCredentials>? stageCredentials = null;
        IReadOnlyList<string>? enabledDomainAgents = null;

        // Same-process hot path first (real ApiKey already in memory, no cache lookup needed).
        if (_orchestratorRegistrations.TryGetValue(workUnitId, out var reg))
        {
            provider ??= reg.Provider; model ??= reg.Model; baseUrl ??= reg.BaseUrl; apiKey ??= reg.ApiKey;
            profileId ??= reg.ProfileId; credentialRef ??= reg.CredentialRef;
            autoReviewProfileId = reg.AutoReviewProfileId;
            stageCredentials = reg.StageCredentials; enabledDomainAgents = reg.EnabledDomainAgents;
        }
        else if (_orchestratorRouting.TryGetValue(workUnitId, out var routing))
        {
            // Rehydrated-safe fields survive a restart with no credential resupply needed;
            // ApiKey never does (by design — see IRuntimeCredentialCache's doc comment), so this
            // is the fallback that was missing entirely before: without it, a work unit whose
            // orchestrator was spawned before a Host restart could never be reinvoked again —
            // every child that finished afterward silently failed to wake it back up, leaving the
            // orchestrator (and the whole goal) idle forever even after all its work completed.
            provider ??= routing.Provider; model ??= routing.Model; baseUrl ??= routing.BaseUrl;
            profileId ??= routing.ProfileId; credentialRef ??= routing.CredentialRef;
            autoReviewProfileId = routing.AutoReviewProfileId;
            stageCredentials = routing.StageCredentials; enabledDomainAgents = routing.EnabledDomainAgents;
            var cached = _credentialCache.TryGet(credentialRef);
            apiKey ??= cached?.ApiKey;
            model ??= cached?.Model; baseUrl ??= cached?.BaseUrl; provider ??= cached?.Provider;
        }
        // No registration and no routing at all — this work unit's orchestrator predates both
        // (spawned before OrchestratorRoutingConfig persistence existed at all, so there's
        // nothing on record to recover from). Automatic reinvocation (WorkSchedulerService.
        // ReleaseAsync's success path) never passes override params, so for that caller this
        // correctly falls straight through to the apiKey-is-null no-op below, same as before.
        // A manual recovery action (e.g. the goal workspace's "Reinvoke Orchestrator" button,
        // prompting for a profile when none is on record) can still recover it by supplying
        // everything needed itself.

        if (string.IsNullOrWhiteSpace(baseUrl) || apiKey is null)
        {
            _logger.LogWarning(
                "[Orchestrator] {ActionName} for workUnit={WorkUnitId} skipped — no credentials resolvable " +
                "(registration cold after a restart, and IRuntimeCredentialCache has nothing for " +
                "CredentialRef={CredentialRef}). Needs a manual retry with resupplied credentials.",
                actionName, workUnitId, credentialRef ?? "(none)");
            return null;
        }

        provider ??= "anthropic";
        model ??= string.Empty;
        _credentialCache.Capture(credentialRef, provider, model, baseUrl, apiKey);

        // Re-warm the hot path so subsequent same-process calls (GetOrchestratorCredentials,
        // GetAutoReviewProfileId, another ReinvokeOrchestratorAsync/ResupplyCredentialsAsync, ...)
        // skip the cache lookup — and (re)persist the safe routing config, so a *future* restart
        // can recover this orchestrator too, closing the gap that got it here in the first place.
        _orchestratorRegistrations[workUnitId] = new OrchestratorRegistration(
            provider, model, baseUrl, apiKey, profileId, autoReviewProfileId,
            stageCredentials, enabledDomainAgents, credentialRef);
        var routingToPersist = new OrchestratorRoutingConfig(
            workUnitId, provider, model, baseUrl, profileId, autoReviewProfileId,
            credentialRef, stageCredentials, enabledDomainAgents);
        _orchestratorRouting[workUnitId] = routingToPersist;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.OrchestratorRoutingV1, workUnitId,
            JsonSerializer.Serialize(routingToPersist), cancellationToken).ConfigureAwait(false);

        return new ResolvedOrchestratorCredentials(provider, model, baseUrl, apiKey, profileId, credentialRef);
    }

    public async Task ReinvokeOrchestratorAsync(
        string workUnitId,
        string? sessionId = null,
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideProfileId = null,
        string? overrideCredentialRef = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAndPersistCredentialsAsync(
            workUnitId, overrideModel, overrideBaseUrl, overrideApiKey, overrideProvider,
            overrideProfileId, overrideCredentialRef, "Reinvoke", cancellationToken).ConfigureAwait(false);
        if (resolved is not { } creds)
            return;

        var agentId = $"orchestrator-{Guid.NewGuid():N}";
        var cts = new CancellationTokenSource();
        _agents[agentId] = new AgentRecord(agentId, workUnitId, "active", null, creds.Model, creds.BaseUrl, creds.ApiKey, creds.Provider, cts);

        AgentProfile? profile = creds.ProfileId is not null
            ? _profileService.GetAsync(creds.ProfileId, cancellationToken).GetAwaiter().GetResult()
            : null;

        StartOrchestratorLoop(agentId, workUnitId, creds.Provider, creds.Model, creds.BaseUrl, creds.ApiKey, profile, cts, sessionId);
    }

    public async Task<bool> ResupplyCredentialsAsync(
        string workUnitId,
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideProfileId = null,
        string? overrideCredentialRef = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAndPersistCredentialsAsync(
            workUnitId, overrideModel, overrideBaseUrl, overrideApiKey, overrideProvider,
            overrideProfileId, overrideCredentialRef, "Requeue credential resupply", cancellationToken).ConfigureAwait(false);
        return resolved is not null;
    }

    // Same-process hot path first (real ApiKey, no cache lookup needed). Falls back to the
    // rehydrated-safe routing config + IRuntimeCredentialCache, which is the only way this can
    // return anything non-null after a Host restart — and only once something has resupplied the
    // credential (e.g. the Resume flow). A cache miss there means a genuinely missing credential,
    // not a bug: the caller (e.g. AutomatedReviewGateService) already treats null as "not enabled."
    public OrchestratorCredentials? GetOrchestratorCredentials(string workUnitId)
    {
        if (_orchestratorRegistrations.TryGetValue(workUnitId, out var reg))
            return new OrchestratorCredentials(reg.Provider, reg.Model, reg.BaseUrl, reg.ApiKey, reg.ProfileId, reg.CredentialRef);

        if (!_orchestratorRouting.TryGetValue(workUnitId, out var routing))
            return null;

        var cached = _credentialCache.TryGet(routing.CredentialRef);
        if (cached is null)
            return null;

        return new OrchestratorCredentials(cached.Provider, cached.Model, cached.BaseUrl, cached.ApiKey, routing.ProfileId, routing.CredentialRef);
    }

    public OrchestratorCredentials? GetCredentialsForStage(string workUnitId, PipelineStage stage)
    {
        if (_orchestratorRegistrations.TryGetValue(workUnitId, out var reg))
            return reg.StageCredentials?.GetValueOrDefault(stage);

        if (!_orchestratorRouting.TryGetValue(workUnitId, out var routing) ||
            routing.StageCredentials?.GetValueOrDefault(stage) is not { } stageRouting)
            return null;

        var cached = _credentialCache.TryGet(stageRouting.CredentialRef);
        return cached is null ? null : stageRouting with { ApiKey = cached.ApiKey };
    }

    // Pure routing data — safe to persist in full, so this resolves correctly right after a
    // restart with zero credential resupply needed. This is what fixes review routing silently
    // falling back to human review after a Host restart.
    public string? GetAutoReviewProfileId(string workUnitId) =>
        _orchestratorRegistrations.TryGetValue(workUnitId, out var reg) ? reg.AutoReviewProfileId
        : _orchestratorRouting.TryGetValue(workUnitId, out var routing) ? routing.AutoReviewProfileId
        : null;

    public string? GetOrchestratorProfileId(string workUnitId) =>
        _orchestratorRegistrations.TryGetValue(workUnitId, out var reg) ? reg.ProfileId
        : _orchestratorRouting.TryGetValue(workUnitId, out var routing) ? routing.ProfileId
        : null;

    public IReadOnlyList<string>? GetEnabledDomainAgents(string workUnitId) =>
        _orchestratorRegistrations.TryGetValue(workUnitId, out var reg) ? reg.EnabledDomainAgents
        : _orchestratorRouting.TryGetValue(workUnitId, out var routing) ? routing.EnabledDomainAgents
        : null;

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
                var agentClient = new DefaultAgentToolClient(provider, model, baseUrl, apiKey ?? string.Empty, llm, dispatcher);
                var artifactLineage = _serviceProvider.GetRequiredService<IArtifactLineageService>();
                var projections = _serviceProvider.GetRequiredService<IProjectionManager>();
                var decisionLog = _serviceProvider.GetRequiredService<IOrchestrationDecisionLogService>();
                var fanOut = _serviceProvider.GetRequiredService<IFanOutService>();
                var mergeReconciliation = _serviceProvider.GetRequiredService<IMergeReconciliationService>();
                var automatedReview = _serviceProvider.GetRequiredService<IAutomatedReviewGateService>();
                var merge = _serviceProvider.GetRequiredService<IMergeService>();
                var workUnits = _serviceProvider.GetRequiredService<IWorkUnitService>();
                var workspaceOptions = _serviceProvider.GetRequiredService<WorkspaceOptions>();
                var findingsService = _serviceProvider.GetRequiredService<IFindingService>();
                var conversationLog = _serviceProvider.GetRequiredService<IConversationLogService>();
                var plannerSelection = _serviceProvider.GetService<IPlannerSelectionService>();
                var loop = new OrchestratorAgentLoop(
                    agentId, workUnitId, agentClient,
                    artifactLineage, projections, decisionLog, fanOut, mergeReconciliation, automatedReview, merge, workUnits,
                    findingsService,
                    profile, sessionId, workspaceOptions.StallDetectionCycles, a => ReportActivity(agentId, a),
                    conversationLog: conversationLog, agentControl: this, events: _events,
                    plannerSelection: plannerSelection, logger: _logger);
                var completion = await loop.RunAsync(cts.Token).ConfigureAwait(false);
                if (completion is AgentLoopCompletion.MaxIterationsExceeded or AgentLoopCompletion.Stalled)
                {
                    var deadLetter = _serviceProvider.GetService<IDeadLetterService>();
                    if (deadLetter is not null)
                    {
                        var reason = completion == AgentLoopCompletion.Stalled
                            ? $"Stall: no artifact change after {workspaceOptions.StallDetectionCycles} cycles."
                            : "Max iterations reached";
                        var kind = completion == AgentLoopCompletion.Stalled
                            ? FailureKind.Stalled
                            : FailureKind.MaxIterationsExceeded;
                        await deadLetter.RecordFailureAsync(
                            workUnitId,
                            agentId,
                            profile?.Stage ?? PipelineStage.Orchestrate,
                            profile?.AgentProfileId ?? "orchestrator",
                            reason,
                            sessionId: sessionId,
                            model: model,
                            baseUrl: baseUrl,
                            apiKey: apiKey,
                            provider: provider,
                            kind: kind,
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
                        model: model,
                        baseUrl: baseUrl,
                        apiKey: apiKey,
                        provider: provider,
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
                var ruleFileContext = await BuildRuleFileContextAsync(workUnitId, cts.Token).ConfigureAwait(false);
                var workerConstraintsContext = await BuildConstraintsContextAsync(workUnitId, cts.Token).ConfigureAwait(false);
                var workerPromptGuidance = await BuildPromptGuidanceContextAsync(PipelineStage.Execute, cts.Token).ConfigureAwait(false);
                var workerEngineeringState = await BuildEngineeringStateContextAsync(workUnitId, cts.Token).ConfigureAwait(false);
                var workerCombinedGuidance = string.Join("\n\n", new[] { workerConstraintsContext, workerPromptGuidance, workerEngineeringState }.Where(s => s is not null));
                // plans/harness-hosting-architecture.md Phase B.1 — same seam as
                // RunScheduledWorkerAsync's Worker branch. Preserves this site's exact existing
                // behavior: no sessionId, completion discarded, no scheduler/dead-letter
                // interaction (zero behavior change is B1's acceptance bar for this path).
                var harnessExecutorResolver = _serviceProvider.GetRequiredService<IHarnessExecutorResolver>();
                var executor = harnessExecutorResolver.ResolveForProvider(provider, profile?.Executor);
                var harnessRequest = new HarnessRunRequest(
                    HarnessMode.Execute, agentId, workUnitId, taskId, profile, SessionId: null,
                    IsResume: false, ruleFileContext,
                    PromptGuidanceContext: workerCombinedGuidance.Length == 0 ? null : workerCombinedGuidance,
                    SelfVerifyBuild: _options.RequireBuildBeforeProposal,
                    SelfVerifyTest: _options.RequireTestBeforeProposal,
                    OnActivity: a => ReportActivity(agentId, a),
                    Provider: provider, Model: model, BaseUrl: baseUrl, ApiKey: apiKey);
                await executor.RunAsync(harnessRequest, cts.Token).ConfigureAwait(false);
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

    public async Task StopAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var current = GetRequired(agentId);
        current.Cts?.Cancel();
        _agents[agentId] = current with { Status = "stopped", Cts = null, CurrentActivity = null };

        // Phase 12 — an explicit stop is a deliberate "abandon this run," unlike a transient
        // failure with retries left (where the lease must stay held — see ForceReleaseAll's only
        // other caller, InMemoryDeadLetterService, gated on MaxAttemptsReached for the same
        // reason): nothing will automatically retry after a human stops it, so any file lease(s)
        // it held would otherwise strand their wait queues forever with no recovery path. Does
        // NOT fire on host-restart cancellation (IHostedService.StopAsync, a different method) —
        // that path is the existing AwaitingResume rehydrate-and-approve flow, where the lease
        // staying held across the restart is correct.
        var promoted = await _fileLease.ForceReleaseAllForWorkUnitAsync(current.WorkUnitId, cancellationToken)
            .ConfigureAwait(false);
        foreach (var promotedWorkUnitId in promoted)
            await _scheduler.ClearAwaitingFileLeaseAsync(promotedWorkUnitId, cancellationToken).ConfigureAwait(false);
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

    // Mirrors the register/report/cleanup shape SpawnAsync's own loops use (see StartWorkerLoop
    // above) for a caller that isn't itself a background Task.Run — InlineReviewerService is
    // awaited synchronously by AutoReviewRule's BeforeMerge gate, so there's no fire-and-forget
    // wrapper of its own to hang a finally off. Owning the whole `run` delegate here (rather than
    // exposing separate Register/Unregister calls) makes it structurally impossible for a future
    // edit to skip cleanup and leak a stuck "active" entry.
    public async Task<TResult> TrackInlineAgentAsync<TResult>(
        string agentId,
        string workUnitId,
        string? taskId,
        Func<Action<string?>, Task<TResult>> run,
        CancellationToken cancellationToken = default)
    {
        _agents[agentId] = new AgentRecord(agentId, workUnitId, "active", taskId);
        try
        {
            return await run(a => ReportActivity(agentId, a)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_agents.TryGetValue(agentId, out var r))
            {
                var msg = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
                _agents[agentId] = r with { Status = $"failed:{msg}", CurrentActivity = null };
            }
            throw;
        }
        finally
        {
            if (_agents.TryGetValue(agentId, out var r) && r.Status == "active")
                _agents[agentId] = r with { Status = "stopped", CurrentActivity = null };
        }
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

    // Promoted Knowledge Findings (global Constraint artifacts, no owning work unit) plus this
    // work unit's own ancestor-chain constraints — previously computed by AgentWorkspace's
    // InheritedConstraints field but never read by any agent loop. The orchestrator fetches the
    // live projection every cycle and folds this in itself; the planner has no projection-fetch
    // loop of its own, so it's resolved once up front here, same shape as ruleFileContext above.
    private async Task<string?> BuildConstraintsContextAsync(string workUnitId, CancellationToken ct)
    {
        try
        {
            var projections = _serviceProvider.GetRequiredService<IProjectionManager>();
            var result = await projections.GetAsync(
                new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: workUnitId),
                ct).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
            if (payload is null || payload.InheritedConstraints.Count == 0) return null;

            var lines = payload.InheritedConstraints.Select(c => $"- {c.Title ?? c.ArtifactId}: {c.Body ?? ""}");
            return "Known constraints — durable guidance from prior runs; apply unless this work unit's goal explicitly says otherwise:\n"
                + string.Join("\n", lines);
        }
        catch
        {
            return null;
        }
    }

    // plans/harness-hosting-architecture.md Phase A.5 — native-loop kickoff injection of the
    // EngineeringState projection's rendered markdown, so a native worker gets the same "living
    // timeline" context an external harness reads from .workspace/state.md, without needing a file
    // read of its own. Same shape as BuildRuleFileContextAsync/BuildConstraintsContextAsync above:
    // resolved once up front, never throws.
    //
    // WorkspaceContractService.RenderEngineeringStateMarkdownAsync always renders something (even
    // "no facts yet") because MaterializeAsync needs state.md to exist regardless — this sentinel
    // is how the kickoff-injection caller (which wants null-on-empty, like BuildConstraintsContextAsync)
    // recognizes the empty case without a second method on the service. Must match
    // WorkspaceContractService.RenderEngineeringStateMarkdown's empty-Facts branch exactly.
    private const string NoEngineeringStateFactsMarkdown = "# Engineering state\n\nNo promoted facts yet.\n";

    private async Task<string?> BuildEngineeringStateContextAsync(string workUnitId, CancellationToken ct)
    {
        try
        {
            var contracts = _serviceProvider.GetRequiredService<IWorkspaceContractService>();
            var rendered = await contracts.RenderEngineeringStateMarkdownAsync(workUnitId, ct).ConfigureAwait(false);
            return rendered == NoEngineeringStateFactsMarkdown ? null : rendered;
        }
        catch
        {
            return null;
        }
    }

    // Promoted PromptImprovement findings targeting this stage — stage-scoped, unlike the
    // universal constraints above. Used by loops (Planner, Worker) that have no projection-fetch
    // loop of their own and so resolve this once up front, same shape as the helpers above.
    private async Task<string?> BuildPromptGuidanceContextAsync(PipelineStage stage, CancellationToken ct)
    {
        try
        {
            var findings = _serviceProvider.GetRequiredService<IFindingService>();
            var promptGuidance = await findings.ListPromotedPromptGuidanceAsync(stage, ct).ConfigureAwait(false);
            if (promptGuidance.Count == 0) return null;

            var lines = promptGuidance.Select(f => $"- {f.Title}: {f.Summary}");
            return "[Process guidance — promoted prompt improvements for this stage]\n" + string.Join("\n", lines);
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
        services.AddSingleton<LlmClient>(sp =>
            new LlmClient(llmHttpClient ?? new HttpClient(), sp.GetRequiredService<ILogger<LlmClient>>()));
        services.AddSingleton<IInsightLlmAnalyzerService, InsightLlmAnalyzerService>();
        services.AddSingleton<IProfileSelectionService, LlmProfileSelectionService>();
        // plans/phase-d-implementation.md D2 — "who plans this goal" executor routing, off by
        // default (WorkspaceOptions.UsePlannerExecutorSelection).
        services.AddSingleton<IPlannerSelectionService, PlannerSelectionService>();
        // Slice 20b — inline reviewer for AgentApproval/Hybrid BeforeMerge gate.
        services.AddSingleton<IInlineReviewerService, InlineReviewerService>();
        // Slice 21/22 — reactive domain agents, disabled by default (WorkspaceOptions.EnabledDomainAgents).
        services.AddSingleton<IDomainAgentTriggerService, DomainAgentTriggerService>();
        // Phase 1.4 — re-plan-the-slice mechanism for both failure-recovery tracks.
        services.AddSingleton<IReplanService, ReplanService>();
        // Phase 2 item 1 — per-goal cost/time guardrail (alert-only, computed on demand).
        services.AddSingleton<IGoalGuardrailService, GoalGuardrailService>();
        // Phase 1.4 Continue-track — resume a dead-lettered work unit with reconstructed prior context.
        services.AddSingleton<IContinueService, ContinueService>();
        // Phase 10 — LLM-backed merge provider (sits inside AgentRuntime where LlmClient lives).
        services.AddSingleton<ILlmMergeProvider, LlmMergeProvider>();
        // plans/harness-hosting-architecture.md Phase B.1 — the executor seam. NativeHarnessExecutor
        // wraps the current loop; future adapters (ClaudeCodeExecutor, Phase B.2) register
        // alongside it as additional IHarnessExecutor implementations, no resolver changes needed.
        services.AddSingleton<IHarnessExecutor, NativeHarnessExecutor>();
        services.AddSingleton<IHarnessExecutorResolver, HarnessExecutorResolver>();
        // phase-c-implementation.md C2 — the harvest block shared by every external CLI adapter
        // (decisions/inbox harvest, merge.propose + merge.validate, the build/test gate). Registered
        // once, ahead of the adapters that depend on it.
        services.AddSingleton<HarnessHarvestPipeline>();
        // Phase B.2 — the first external adapter. ClaudeCodeExecutorOptions.ExecutablePath
        // defaults to "claude" (resolved via PATH); tests override to a stub CLI — see
        // ClaudeCodeExecutorOptions' own doc comment for why the real binary must never run in
        // automated tests.
        services.AddSingleton(new ClaudeCodeExecutorOptions());
        services.AddSingleton<IHarnessExecutor, ClaudeCodeExecutor>();
        // phase-c-implementation.md C2 — the second external adapter, proving the executor seam
        // isn't Claude-shaped. CodexCliExecutorOptions.ExecutablePath defaults to "codex" (resolved
        // via PATH); tests override to a stub CLI — see CodexCliExecutorOptions' own doc comment for
        // why the real binary must never run in automated tests.
        services.AddSingleton(new CodexCliExecutorOptions());
        services.AddSingleton<IHarnessExecutor, CodexCliExecutor>();
        // Phase C.4 (phase-c-implementation.md C3) — the /mcp-harness bearer-token map. Singleton,
        // in-memory only; see HarnessMcpTokenService's own doc comment for the restart/resume story.
        services.AddSingleton<IHarnessMcpTokenService, HarnessMcpTokenService>();
        return services;
    }
}
