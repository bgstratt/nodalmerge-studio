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
public sealed class MergeCommandService(IMergeService merge, IFileWorkspaceService fileWorkspace, IArtifactLineageService artifactLineage, IExecutionEventStream events, IStudioNodeStore nodeStore, IServiceProvider serviceProvider, IPolicyGateService? policyGate = null) : IMergeCommandService
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
            FilesTouched:     filesTouched);
        var created = await merge.ProposeAsync(proposal, cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<MergeProposalStatus>(decision, ignoreCase: true, out var status) ||
            status is not (MergeProposalStatus.Approved or MergeProposalStatus.Rejected))
        {
            throw new ArgumentException("Decision must be 'Approved' or 'Rejected'.", nameof(decision));
        }

        return automated
            ? await merge.AutomatedReviewAsync(proposalId, status, verificationResults ?? string.Empty, reviewerAgentId, cancellationToken).ConfigureAwait(false)
            : await merge.ReviewAsync(proposalId, status, cancellationToken).ConfigureAwait(false);
    }

    public Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default) =>
        merge.ApplyAsync(proposalId, cancellationToken);

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