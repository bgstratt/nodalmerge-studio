using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) — the harvest block
// (decisions/inbox harvest, merge.propose + merge.validate, the mechanical build/test gate,
// AwaitingClarification pause) extracted out of ClaudeCodeExecutor.RunAsync's private HarvestAsync
// method so a second adapter (CodexCliExecutor) can call the exact same pipeline instead of
// duplicating it. This is C2's "prove the seam isn't Claude-shaped" requirement: every parameter
// below is either already executor-agnostic (branchId, resultText, tokens) or was pulled out of
// what used to be implicit "Name"/"anthropic" literals (executorName, providerName). Behavior for
// claude-code is unchanged byte-for-byte — only the call site moved.
//
// plans/phase-d-implementation.md D1.b — gains a Mode split: HarvestAsync (Execute) is unchanged;
// HarvestPlanAsync (Plan) skips diff→merge.propose and the build/test gate entirely (a plan run
// must produce no code changes — a non-empty diff outside .workspace/ is discarded with a warning
// event, never proposed) and instead parses/validates .workspace/plan.json and records it as a
// Plan artifact. decisions/ + inbox/ harvest stays shared between both modes (planners record
// research/decisions too, and can still ask blocking questions).
internal sealed class HarnessHarvestPipeline(
    IWorkspaceContractService workspaceContracts,
    IClarificationCommandService clarifications,
    IMergeCommandService mergeCommands,
    IFileWorkspaceService fileWorkspace,
    IArtifactCommandService artifactCommands,
    IMergeService merge,
    ILogger<HarnessHarvestPipeline> logger,
    IExecutionEventStream? events = null)
{
    private const string PlanFilePath = ".workspace/plan.json";

    // plans/review-seam-and-clarification-sessions.md S2 — the Review-mode contract pair: the
    // pipeline materializes the request (proposal metadata + diff) before the CLI spawns, the
    // harness writes its verdict, HarvestReviewAsync maps it onto AutomatedReviewAsync. File-based
    // like plan.json so codex (no MCP) gets review mode without any new tool surface.
    private const string ReviewRequestFilePath = ".workspace/review-request.json";
    private const string ReviewVerdictFilePath = ".workspace/review.json";

    private static readonly JsonSerializerOptions PlanJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<HarnessRunResult> HarvestAsync(
        HarnessRunRequest request, string branchId, string? resultText,
        int? inputTokens, int? outputTokens, double? totalCostUsd, string? sessionId,
        string executorName, string providerName, CancellationToken ct)
    {
        var inboxResult = await HarvestDecisionsAndInboxAsync(
            request, resultText, inputTokens, outputTokens, totalCostUsd, sessionId, ct).ConfigureAwait(false);
        if (inboxResult is not null)
            return inboxResult;

        if (request.Mode == HarnessMode.Plan)
        {
            return await HarvestPlanAsync(
                request, branchId, resultText, inputTokens, outputTokens, totalCostUsd, sessionId, ct)
                .ConfigureAwait(false);
        }

        if (request.Mode == HarnessMode.Review)
        {
            return await HarvestReviewAsync(
                request, branchId, resultText, inputTokens, outputTokens, totalCostUsd, sessionId, ct)
                .ConfigureAwait(false);
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
                summary: resultText ?? $"{executorName} run completed.",
                workUnitId: request.WorkUnitId,
                agentId: request.AgentId,
                model: executorName,
                provider: providerName,
                sessionId: request.SessionId,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Executor}] harvest ProposeAsync failed for workUnitId={WorkUnitId}", executorName, request.WorkUnitId);
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
            logger.LogWarning(ex, "[{Executor}] harvest ValidateAsync failed for workUnitId={WorkUnitId}", executorName, request.WorkUnitId);
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled, $"Proposal validation failed: {ex.Message}",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        return new HarnessRunResult(
            AgentLoopCompletion.Succeeded, FailureReason: null,
            resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
    }

    // Shared by both modes — a planner records research/decisions too, and can still ask a
    // blocking question via .workspace/inbox/, same pause mechanism a native worker's own
    // nm_v1_clarification_request tool call triggers. Returns null to mean "no clarification
    // pause, keep going"; non-null is the terminal AwaitingClarification result the caller returns
    // as-is.
    private async Task<HarnessRunResult?> HarvestDecisionsAndInboxAsync(
        HarnessRunRequest request, string? resultText,
        int? inputTokens, int? outputTokens, double? totalCostUsd, string? sessionId, CancellationToken ct)
    {
        await workspaceContracts.HarvestDecisionsAsync(request.WorkUnitId, ct).ConfigureAwait(false);

        var inboxEntries = await workspaceContracts.HarvestInboxAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        if (inboxEntries.Count == 0)
            return null;

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

    // plans/phase-d-implementation.md D1.b — Plan-mode harvest: no diff→merge.propose, no
    // build/test gate (a plan run must produce no code changes). Instead: discard-with-warning any
    // diff outside .workspace/ (the plan-mode kickoff/allowlist says "implement nothing", but that's
    // advisory for a CLI executor Studio doesn't fully sandbox — see the settings-allowlist
    // comment on each adapter's Plan-mode kickoff), then parse/validate .workspace/plan.json and
    // record it as a Plan artifact in the exact shape FanOutService.ReadPlanFromArtifactAsync
    // already reads (a raw PlanDocument-shaped JSON body — same as ArtifactRecordPlan writes).
    private async Task<HarnessRunResult> HarvestPlanAsync(
        HarnessRunRequest request, string branchId, string? resultText,
        int? inputTokens, int? outputTokens, double? totalCostUsd, string? sessionId, CancellationToken ct)
    {
        try
        {
            // IFileWorkspaceService.DiffAsync already excludes hidden (dot-prefixed) paths —
            // .workspace/plan.json never appears in this diff, so any non-empty result here is a
            // genuine source-file edit the plan-mode kickoff told the harness not to make.
            var diff = await fileWorkspace.DiffAsync(branchId, "main", ct).ConfigureAwait(false);
            if (!diff.StartsWith("No differences", StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "[{AgentId}] Plan-mode run for workUnitId={WorkUnitId} produced a discarded diff outside .workspace/.",
                    request.AgentId, request.WorkUnitId);
                // Studio's own session id (request.SessionId), not the harness's transcript
                // session/thread id — same convention EmitPermissionDenialEventsAsync uses for
                // HarnessPermissionDenied, since ExecutionEventStream is keyed by Studio session.
                if (events is not null && request.SessionId is not null)
                {
                    await events.AppendAsync(
                        request.SessionId, request.WorkUnitId, ExecutionEventKind.HarnessPlanDiffDiscarded,
                        new HarnessPlanDiffDiscardedPayload(request.AgentId, diff),
                        ct: ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // Diff is best-effort diagnostics here, same posture MergeCommandService.ProposeAsync
            // takes for its own diff generation — never let it fail an otherwise-valid plan run.
            logger.LogWarning(ex, "[{AgentId}] Plan-mode diff check failed for workUnitId={WorkUnitId}", request.AgentId, request.WorkUnitId);
        }

        string? planFileContent;
        try
        {
            planFileContent = await fileWorkspace.ReadAsync(branchId, PlanFilePath, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            planFileContent = null;
        }

        if (string.IsNullOrWhiteSpace(planFileContent))
        {
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                $"Plan-mode run did not write {PlanFilePath} — no plan to record.",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        WorkspaceContractPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<WorkspaceContractPlan>(planFileContent, PlanJsonOpts);
        }
        catch (JsonException ex)
        {
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                $"{PlanFilePath} is not valid JSON: {ex.Message}",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        if (plan is null || plan.Slices is null || plan.Slices.Count == 0 ||
            plan.Slices.Any(s => string.IsNullOrWhiteSpace(s.SliceId) || string.IsNullOrWhiteSpace(s.Goal)))
        {
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                $"{PlanFilePath} does not conform to the WorkspaceContractPlan schema " +
                "(each slice needs a non-empty sliceId and goal) — a native replan can pick this up.",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        // Normalize into the exact PlanDocument-shaped JSON ArtifactRecordPlan already writes — the
        // two DTOs share field-for-field JSON property names (see WorkspaceContractPlan's own doc
        // comment), so this is a lossless re-serialization, not a lossy conversion.
        var normalizedPlan = new PlanDocument(
            [.. plan.Slices.Select(s => new PlanSlice(s.SliceId, s.Goal, s.FileScope, s.DependsOn, s.Steps))]);
        var normalizedPlanJson = JsonSerializer.Serialize(normalizedPlan);

        await artifactCommands.RecordPlanAsync(request.WorkUnitId, normalizedPlanJson, ct).ConfigureAwait(false);

        return new HarnessRunResult(
            AgentLoopCompletion.Succeeded, FailureReason: null,
            resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
    }

    // plans/review-seam-and-clarification-sessions.md S2 — called by a CLI adapter before
    // spawning a Review-mode run. The harness works in the proposal's source-branch workdir (the
    // changed files are just there), so this file is the metadata framing: which proposal, what
    // the worker claimed, and the diff vs the target. Returns null when the proposal exists (the
    // happy path) or a terminal Stalled result the adapter returns as-is when it doesn't — a
    // review run without a proposal has nothing to decide.
    public async Task<HarnessRunResult?> MaterializeReviewRequestAsync(
        HarnessRunRequest request, string branchId, CancellationToken ct)
    {
        var proposal = string.IsNullOrWhiteSpace(request.TaskId)
            ? null
            : await merge.GetAsync(request.TaskId, ct).ConfigureAwait(false);
        if (proposal is null)
        {
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                $"Review-mode run has no reviewable proposal (TaskId='{request.TaskId}').");
        }

        var reviewRequestJson = JsonSerializer.Serialize(new
        {
            proposalId = proposal.ProposalId,
            workUnitId = proposal.WorkUnitId,
            goal = proposal.Goal,
            summary = proposal.Summary,
            changeDescription = proposal.ChangeDescription,
            filesTouched = proposal.FilesTouched,
            noFileChangesJustification = proposal.NoFileChangesJustification,
            sourceBranch = proposal.SourceBranch,
            targetBranch = proposal.TargetBranch,
            diff = proposal.WorkspaceChanges,
        }, PlanJsonOpts);
        await fileWorkspace.WriteAsync(branchId, ReviewRequestFilePath, reviewRequestJson, ct).ConfigureAwait(false);
        return null;
    }

    private sealed record ReviewVerdictFile(
        string? Decision,
        string? VerificationResults,
        IReadOnlyList<string>? ConsideredArtifactIds);

    // Review-mode harvest: no diff→propose (the branch under review legitimately differs from the
    // target — that IS the proposal) and no plan-mode diff-discard for the same reason. Parse
    // .workspace/review.json and make the exact IMergeService.AutomatedReviewAsync call the native
    // nm_v1_merge_review dispatcher makes (decision must be Approved/Rejected; verificationResults
    // required — same rules), so every downstream behavior (rejection retry cycles via
    // AutomatedReviewGateService, evidence, hybrid timers) is identical by construction. A
    // missing/invalid verdict is Stalled — the same "inconclusive review" family
    // InlineReviewerService dead-letters, handled by Retry/Continue rather than guessed at here.
    private async Task<HarnessRunResult> HarvestReviewAsync(
        HarnessRunRequest request, string branchId, string? resultText,
        int? inputTokens, int? outputTokens, double? totalCostUsd, string? sessionId, CancellationToken ct)
    {
        string? verdictContent;
        try
        {
            verdictContent = await fileWorkspace.ReadAsync(branchId, ReviewVerdictFilePath, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            verdictContent = null;
        }

        // Recovery for a misplaced verdict: the review prompt asks the model to cd back to the root
        // before writing .workspace/review.json, but a model that ran tests from a nested project
        // dir and forgot leaves the verdict in <subdir>/.workspace/review.json instead — a real
        // outcome observed live. Rather than Stall a review that genuinely produced a verdict, look
        // for a stray review.json under any nested .workspace/ and adopt it (most-recent wins).
        if (string.IsNullOrWhiteSpace(verdictContent))
        {
            verdictContent = await TryRecoverMisplacedVerdictAsync(request, branchId, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(verdictContent))
        {
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                $"Review-mode run did not write {ReviewVerdictFilePath} — no decision to record.",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        ReviewVerdictFile? verdict;
        try
        {
            verdict = JsonSerializer.Deserialize<ReviewVerdictFile>(verdictContent, PlanJsonOpts);
        }
        catch (JsonException ex)
        {
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                $"{ReviewVerdictFilePath} is not valid JSON: {ex.Message}",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        if (verdict is null ||
            !Enum.TryParse<MergeProposalStatus>(verdict.Decision, ignoreCase: true, out var decision) ||
            decision is not (MergeProposalStatus.Approved or MergeProposalStatus.Rejected) ||
            string.IsNullOrWhiteSpace(verdict.VerificationResults))
        {
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                $"{ReviewVerdictFilePath} must carry decision=Approved|Rejected and non-empty " +
                "verificationResults — same requirements as nm_v1_merge_review(automated=true).",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        try
        {
            await merge.AutomatedReviewAsync(
                request.TaskId, decision, verdict.VerificationResults, request.AgentId,
                verdict.ConsideredArtifactIds, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "[{AgentId}] Review-mode AutomatedReviewAsync failed for proposalId={ProposalId}",
                request.AgentId, request.TaskId);
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled, $"Recording the review decision failed: {ex.Message}",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        return new HarnessRunResult(
            AgentLoopCompletion.Succeeded, FailureReason: null,
            resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
    }

    // Best-effort recovery for a verdict the model wrote from a directory it cd'd into (e.g. a
    // nested test project) without cd'ing back — it lands in <subdir>/.workspace/review.json rather
    // than the branch-root .workspace/review.json the settings allowlist permits and the read above
    // targets. Returns the stray verdict's content (most-recently-written wins) or null if there is
    // none. Never throws — any failure degrades to the normal missing-verdict Stall.
    private async Task<string?> TryRecoverMisplacedVerdictAsync(
        HarnessRunRequest request, string branchId, CancellationToken ct)
    {
        try
        {
            var workDir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
                return null;

            var canonicalRoot = Path.GetFullPath(Path.Combine(workDir, ".workspace", "review.json"));

            var stray = Directory.EnumerateFiles(workDir, "review.json", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(p => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(p)), ".workspace", StringComparison.OrdinalIgnoreCase))
                .Where(p => !string.Equals(p, canonicalRoot, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (stray is null)
                return null;

            var content = await File.ReadAllTextAsync(stray, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return null;

            var relative = Path.GetRelativePath(workDir, stray).Replace('\\', '/');
            logger.LogWarning(
                "[{AgentId}] Review verdict recovered from misplaced path '{Relative}' — the model did " +
                "not write it to the branch-root {Canonical}. Adopting it for proposalId={ProposalId}.",
                request.AgentId, relative, ReviewVerdictFilePath, request.TaskId);

            return content;
        }
        catch
        {
            // Best-effort — any failure falls through to the normal missing-verdict Stall.
            return null;
        }
    }
}
