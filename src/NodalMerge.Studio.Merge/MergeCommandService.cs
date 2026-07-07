using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;
using StudioArtifactStatus = NodalMerge.Studio.Contracts.Domain.ArtifactStatus;

namespace NodalMerge.Studio.Merge;

// Slice 15d — the one implementation of merge commands shared by the MCP tool, the REST endpoint,
// and the agent-loop's in-process dispatcher. ProposeAsync now runs the full diff → artifact
// lineage → execution event → work-unit status transition chain that previously only the
// dispatcher executed.
// Slice 16g — ProposalCreated policy gate (including WorkspaceExecutionRule) fires before diff,
// attaching build/test results to VerificationResults.
public sealed class MergeCommandService(IMergeService merge, IFileWorkspaceService fileWorkspace, IArtifactLineageService artifactLineage, IExecutionEventStream events, IStudioNodeStore nodeStore, IServiceProvider serviceProvider, IPolicyGateService? policyGate = null, IWorkUnitService? workUnits = null, WorkspaceOptions? workspaceOptions = null) : IMergeCommandService
{
    public async Task<MergeProposal> ProposeAsync(
        string sourceBranch,
        string targetBranch,
        string summary,
        string? goal = null,
        string? changeDescription = null,
        string? workUnitId = null,
        string? agentId = null,
        string? model = null,
        string? provider = null,
        string? sessionId = null,
        string? commandId = null,
        string? noFileChangesJustification = null,
        CancellationToken cancellationToken = default)
    {
        // ── Idempotency ──────────────────────────────────────────────────────────
        if (commandId is not null)
        {
            var cachedJson = await nodeStore.ReadNodeAsync(StudioNodeKind.CommandResultV1, commandId, cancellationToken)
                .ConfigureAwait(false);
            if (cachedJson is not null)
            {
                var cachedProposal = JsonSerializer.Deserialize<MergeProposal>(cachedJson);
                if (cachedProposal is not null)
                    return cachedProposal;
            }
        }

        // ── Slice 16g — ProposalCreated policy gate ──────────────────────────────
        BranchExecutionResult? execResult = null;
        if (policyGate is not null)
        {
            var policyContext = new Dictionary<string, object?>
            {
                ["branchId"] = sourceBranch,
                ["workUnitId"] = workUnitId,
            };
            var gateResult = await policyGate.EvaluateAsync(
                PolicyCheckpoint.ProposalCreated, policyContext, cancellationToken)
                .ConfigureAwait(false);

            if (policyContext.TryGetValue("executionResult", out var obj) && obj is BranchExecutionResult er)
                execResult = er;

            if (!gateResult.Allowed)
            {
                // Proposal is still created but blocked status recorded
                var violationMessages = gateResult.Violations.Select(v => v.Message).ToList();
                var blockedVerification = JsonSerializer.Serialize(new
                {
                    blocked = true,
                    violations = violationMessages,
                    execution = execResult,
                });

                var blockedProposal = new MergeProposal(
                    $"MP-{Guid.NewGuid():N}",
                    sourceBranch, targetBranch,
                    goal ?? summary, summary, changeDescription ?? summary,
                    blockedVerification, null, null,
                    MergeProposalStatus.Rejected,  // blocked policies result in Rejected
                    WorkspaceChanges: null,
                    DiffGeneratedAt: null,
                    AgentId: agentId,
                    Model: model,
                    Provider: provider,
                    SessionId: sessionId,
                    WorkUnitId: workUnitId,
                    FilesTouched: []);
                var saved = await merge.ProposeAsync(blockedProposal, cancellationToken).ConfigureAwait(false);

                if (commandId is not null)
                {
                    await nodeStore.WriteNodeAsync(StudioNodeKind.CommandResultV1, commandId,
                        JsonSerializer.Serialize(saved), cancellationToken).ConfigureAwait(false);
                }
                return saved;
            }
        }

        // ── Diff generation ──────────────────────────────────────────────────────
        string? workspaceChanges = null;
        DateTimeOffset? diffGeneratedAt = null;
        try
        {
            workspaceChanges = await fileWorkspace.DiffAsync(sourceBranch, targetBranch, cancellationToken).ConfigureAwait(false);
            diffGeneratedAt  = DateTimeOffset.UtcNow;
        }
        catch { /* branch dirs may not exist yet; diff is best-effort */ }

        var filesTouched = ParseFilesTouched(workspaceChanges);
        if (filesTouched.Count == 0)
        {
            try
            {
                filesTouched = (await fileWorkspace.ListAsync(sourceBranch, ct: cancellationToken).ConfigureAwait(false)).ToList();
            }
            catch { /* best-effort */ }
        }

        // ── Guard: reject proposals with no actual file changes ──────────────────
        // Opt-in (WorkspaceOptions.EnforceExpectedOutputKind, default false) so this only fires for
        // work units that explicitly expect FileChange output and weren't given an explicit
        // noFileChangesJustification. Orchestration-only flows (reconciliation, fan-out tests, etc.)
        // that leave the option off, or work units set to KnowledgeArtifact/Either, are unaffected.
        if (filesTouched.Count == 0
            && workspaceOptions is { EnforceExpectedOutputKind: true }
            && string.IsNullOrWhiteSpace(noFileChangesJustification)
            && workUnitId is not null
            && workUnits is not null)
        {
            var owningWorkUnit = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
            if (owningWorkUnit?.ExpectedOutputKind == WorkUnitExpectedOutputKind.FileChange)
            {
                const string noChangesMessage =
                    "No file changes were detected and this task expects FileChange output. Call " +
                    "nm_v1_workspace_write to create or modify files, or resubmit with " +
                    "noFileChangesJustification explaining why no changes were needed.";
                var rejectedProposal = new MergeProposal(
                    $"MP-{Guid.NewGuid():N}",
                    sourceBranch, targetBranch,
                    goal ?? summary, summary, noChangesMessage,
                    null, null, null,
                    MergeProposalStatus.Rejected,
                    WorkspaceChanges: workspaceChanges,
                    DiffGeneratedAt:  diffGeneratedAt,
                    AgentId:          agentId,
                    Model:            model,
                    Provider:         provider,
                    SessionId:        sessionId,
                    WorkUnitId:       workUnitId,
                    FilesTouched:     []);
                var savedRejected = await merge.ProposeAsync(rejectedProposal, cancellationToken).ConfigureAwait(false);

                if (commandId is not null)
                {
                    await nodeStore.WriteNodeAsync(StudioNodeKind.CommandResultV1, commandId,
                        JsonSerializer.Serialize(savedRejected), cancellationToken).ConfigureAwait(false);
                }
                return savedRejected;
            }
        }

        // ── Proposal creation ────────────────────────────────────────────────────
        var proposalId = $"MP-{Guid.NewGuid():N}";

        // Attach execution result to VerificationResults when policy passed (blocked case
        // already handled above; this covers the pass-through path).
        string? verificationResults = null;
        if (execResult is not null)
        {
            verificationResults = JsonSerializer.Serialize(execResult);
        }

        var proposal = new MergeProposal(
            proposalId,
            sourceBranch,
            targetBranch,
            goal ?? summary,
            summary,
            changeDescription ?? summary,
            verificationResults, null, null,
            MergeProposalStatus.Draft,
            WorkspaceChanges: workspaceChanges,
            DiffGeneratedAt:  diffGeneratedAt,
            AgentId:          agentId,
            Model:            model,
            Provider:         provider,
            SessionId:        sessionId,
            WorkUnitId:       workUnitId,
            FilesTouched:     filesTouched,
            NoFileChangesJustification: noFileChangesJustification);
        var created = await merge.ProposeAsync(proposal, cancellationToken).ConfigureAwait(false);

        // ── Domain event ──────────────────────────────────────────────────────────
        var eventBus = serviceProvider.GetService(typeof(IParticipantEventBus)) as IParticipantEventBus;
        eventBus?.Publish(new ProposalCreatedEvent(
            created.ProposalId,
            workUnitId,
            created.SourceBranch,
            created.TargetBranch,
            DateTimeOffset.UtcNow));

        // ── Artifact lineage + execution event + status transition ─────────────────
        if (workUnitId is not null)
        {
            var chain = await artifactLineage.GetChainAsync(workUnitId, cancellationToken).ConfigureAwait(false);
            var taskRef = chain.LastOrDefault(a => a.Type == ArtifactType.Task);
            var parentArtifactId = taskRef?.ArtifactId ?? workUnitId;

            await artifactLineage.RecordAsync(new ArtifactRef(
                created.ProposalId,
                ArtifactType.MergeProposal,
                parentArtifactId,
                StudioArtifactStatus.Active,
                DateTimeOffset.UtcNow,
                workUnitId,
                agentId), cancellationToken).ConfigureAwait(false);

            if (sessionId is not null)
            {
                await events.AppendAsync(
                    sessionId,
                    workUnitId,
                    ExecutionEventKind.ArtifactProposed,
                    new ArtifactProposedPayload(created.ProposalId, workUnitId, filesTouched),
                    ct: cancellationToken).ConfigureAwait(false);
            }

            // Best-effort lifecycle side effect — a proposal means the work unit's
            // execution stage is done. Not worth failing the propose call over an
            // illegal transition (e.g. the legacy direct-spawn path never reaches
            // WorkUnitStatus.Executing).
            var workUnits = serviceProvider.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
            if (workUnits is not null)
            {
                try
                {
                    await workUnits.UpdateStatusAsync(workUnitId, WorkUnitStatus.Proposed, sessionId, cancellationToken).ConfigureAwait(false);
                    await workUnits.SetCurrentStageAsync(workUnitId, PipelineStage.Review, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException) { }
                catch (KeyNotFoundException) { }
            }
        }

        // ── Cache for idempotency ─────────────────────────────────────────────────
        if (commandId is not null)
        {
            await nodeStore.WriteNodeAsync(StudioNodeKind.CommandResultV1, commandId,
                JsonSerializer.Serialize(created), cancellationToken).ConfigureAwait(false);
        }

        // Slice 20b — auto-trigger ApplyAsync for non-HumanRequired policies. The apply call
        // hits the BeforeMerge gate which runs the inline reviewer, so the reviewer fires here
        // rather than requiring the human to click "Apply". Fire-and-forget: errors surface in
        // the proposal status (gate throws InvalidOperationException which is swallowed here).
        if (workUnitId is not null && policyGate is not null)
        {
            var workUnits = serviceProvider.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
            if (workUnits is not null)
            {
                var wu = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
                // See WorkspaceReviewScope — work units whose apply can reach the real repo (top-level
                // goals, plus any work unit explicitly linked to its own RepositoryId) are gated by
                // WorkspaceReviewPolicy; everything else by TaskReviewPolicy.
                var effectivePolicy = WorkspaceReviewScope.AppliesToRealRepo(wu) ? wu?.WorkspaceReviewPolicy : wu?.TaskReviewPolicy;
                if (effectivePolicy is ReviewPolicy.AgentApproval or ReviewPolicy.Hybrid)
                {
                    var proposalIdToApply = created.ProposalId;
                    _ = Task.Run(async () =>
                    {
                        try { await ApplyAsync(proposalIdToApply, CancellationToken.None, autoApplied: true).ConfigureAwait(false); }
                        catch { /* reviewer rejection or gate block — proposal stays in current state */ }
                    });
                }
            }
        }

        return created;
    }

    public Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default) =>
        merge.ValidateAsync(proposalId, cancellationToken);

    public async Task<MergeProposal> ReviewAsync(
        string proposalId,
        string decision,
        string? verificationResults = null,
        bool automated = false,
        string? reviewerAgentId = null,
        string? notes = null,
        IReadOnlyList<string>? consideredArtifactIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<MergeProposalStatus>(decision, ignoreCase: true, out var status) ||
            status is not (MergeProposalStatus.Approved or MergeProposalStatus.Rejected))
        {
            throw new ArgumentException("Decision must be 'Approved' or 'Rejected'.", nameof(decision));
        }

        // Slice 20c — human override cancels any pending Hybrid timer.
        if (!automated)
        {
            var timerService = serviceProvider.GetService(typeof(IReviewTimerService)) as IReviewTimerService;
            if (timerService is not null)
                await timerService.TryCancelAsync(proposalId, cancellationToken).ConfigureAwait(false);
        }

        return automated
            ? await merge.AutomatedReviewAsync(proposalId, status, verificationResults ?? string.Empty, reviewerAgentId, consideredArtifactIds, cancellationToken).ConfigureAwait(false)
            : await merge.ReviewAsync(proposalId, status, notes, cancellationToken).ConfigureAwait(false);
    }

    // Slice 20b — BeforeMerge gate. For AgentApproval/Hybrid policies the AutoReviewRule runs the
    // reviewer inline and returns Allowed only when the proposal is approved. HumanRequired passes
    // through immediately (no behavioral change for the default policy).
    public async Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default, bool autoApplied = false)
    {
        if (policyGate is not null)
        {
            var proposal = await merge.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
            var workUnits = serviceProvider.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
            WorkUnit? workUnit = null;
            if (proposal?.WorkUnitId is { } wuid && workUnits is not null)
                workUnit = await workUnits.GetAsync(wuid, cancellationToken).ConfigureAwait(false);

            var ctx = new Dictionary<string, object?>
            {
                ["proposalId"] = proposalId,
                ["workUnitId"] = proposal?.WorkUnitId,
                ["workUnit"] = workUnit,
                ["proposal"] = proposal,
            };
            var gateResult = await policyGate
                .EvaluateAsync(PolicyCheckpoint.BeforeMerge, ctx, cancellationToken)
                .ConfigureAwait(false);

            if (!gateResult.Allowed)
            {
                var message = string.Join("; ", gateResult.Violations.Select(v => v.Message));
                throw new InvalidOperationException($"BeforeMerge policy blocked apply: {message}");
            }
        }

        return await merge.ApplyAsync(proposalId, cancellationToken, autoApplied).ConfigureAwait(false);
    }

    // ── Diff parsing (moved from McpToolDispatcher) ────────────────────────────

    private static IReadOnlyList<string> ParseFilesTouched(string? workspaceChanges)
    {
        if (string.IsNullOrEmpty(workspaceChanges))
            return [];

        var files = new List<string>();
        foreach (var rawLine in workspaceChanges.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var file =
                line.StartsWith("+++ ADDED: ", StringComparison.Ordinal)   ? line["+++ ADDED: ".Length..] :
                line.StartsWith("~~~ MODIFIED: ", StringComparison.Ordinal) ? line["~~~ MODIFIED: ".Length..] :
                line.StartsWith("--- DELETED: ", StringComparison.Ordinal) ? line["--- DELETED: ".Length..] :
                null;
            if (file is not null)
                files.Add(file);
        }

        return files;
    }
}