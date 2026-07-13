using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// Phase 1.4 two-track failure/recovery design (see
// plans/orchestrator-reliability-and-observability.md) — the re-plan-the-slice mechanism used by
// both the Retry track ("re-plan from scratch" instead of steering) and the Continue track
// ("re-plan the slice" instead of continuing with more iterations). Built on two primitives
// confirmed safe by reading the code, not assumed:
//   - FanOutService.EnsureChildWorkUnitsAsync is per-slice idempotent (skips any sliceId that
//     already has a child), so a new Plan artifact containing only the newly decomposed
//     sub-slices adds just those, leaving every existing sibling untouched.
//   - (DeadLettered, Cancelled) is already a valid WorkUnitTransitions transition, giving the
//     original failed slice a real terminal "superseded" status instead of lingering DeadLettered.
// Deliberately does NOT touch or resume the failed work unit itself — nothing here loops or
// bypasses MaxFailureAttempts, unlike IDeadLetterService.RetryWithContextAsync.
//
// plans/phase-d-implementation.md D3 — the planner spawn now goes through the same executor seam
// D1 put the scheduler-driven Plan-stage branch behind (IHarnessExecutorResolver.ResolveForProvider,
// capability-miss falls back to native), so an external harness can re-plan too, and un-couples "a
// plan exists" from "the native orchestrator produced it." A planner-selection call site (mirroring
// D2's OrchestratorAgentLoop hook) is consulted when the parent's Plan-stage Agent Topology
// assignment is auto/unset (GetCredentialsForStage returns null) — an explicit assignment still
// wins outright, and WorkspaceOptions.UsePlannerExecutorSelection off still means zero behavior
// change, same precedence rules D2 established.
public sealed class ReplanService(
    IDeadLetterService deadLetter,
    IWorkUnitService workUnits,
    IFanOutService fanOut,
    IAgentControlService agentControl,
    IServiceProvider serviceProvider,
    IFileLeaseService fileLease,
    IWorkScheduler scheduler) : IReplanService
{
    public async Task<ReplanResult> ReplanFailedSliceAsync(string entryId, CancellationToken cancellationToken = default)
    {
        var entry = await deadLetter.GetAsync(entryId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
            return new ReplanResult(ReplanOutcome.NotFound, "Dead-letter entry not found.");

        // Deliberately NOT gated by entry.MaxAttemptsReached/AttemptCount — that cap exists to stop
        // retrying the *same* work unit with the *same* approach (see
        // IDeadLetterService.RetryWithContextAsync's own doc comment for the identical reasoning).
        // Re-planning never resumes the failed work unit at all; it spawns fresh, independently-
        // budgeted siblings and marks the original terminal. A slice that already exhausted its
        // retry attempts is, if anything, the clearest case for reaching for this instead of retry.

        var failed = await workUnits.GetAsync(entry.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (failed?.ParentWorkUnitId is not { } parentWorkUnitId)
        {
            return new ReplanResult(
                ReplanOutcome.NotApplicable,
                "This work unit has no parent to attach new sibling slices to — re-planning only " +
                "applies to a fanned-out slice, not a top-level goal.");
        }

        // D3 — snapshot the staleness signal state up front (before this replan attempt can change
        // it) so it rides every returned ReplanResult below, success or failure alike: a human
        // deciding whether/why to replan wants to see this regardless of the outcome.
        var planStaleness = serviceProvider.GetService<IPlanStalenessService>();
        var stalenessSignal = planStaleness is not null
            ? await planStaleness.GetStateAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false)
            : null;

        // stageCreds != null is "topology assignment explicit" (D2's own discovery: "stageCreds is
        // null IS topology assignment auto/unset") — an explicit override skips the selector
        // entirely and its provider/model/profile win outright, same as OrchestratorAgentLoop's
        // InjectSpawnCredentialsAsync.
        var stageCreds = agentControl.GetCredentialsForStage(parentWorkUnitId, PipelineStage.Plan);
        var creds = stageCreds ?? agentControl.GetOrchestratorCredentials(parentWorkUnitId);
        if (creds is null)
        {
            return new ReplanResult(
                ReplanOutcome.PlanningFailed, "No LLM credentials resolvable for the parent work unit.",
                StalenessSignal: stalenessSignal);
        }

        string? provider = stageCreds?.Provider;
        AgentProfile? profile = null;

        if (stageCreds is null)
        {
            var plannerSelection = serviceProvider.GetService<IPlannerSelectionService>();
            if (plannerSelection is not null)
            {
                var goalUnit = await workUnits.GetAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
                if (goalUnit is not null)
                {
                    var selection = await plannerSelection
                        .SelectPlannerAsync(goalUnit, creds, cancellationToken).ConfigureAwait(false);

                    // selection.Provider is null when selection is disabled (WorkspaceOptions
                    // .UsePlannerExecutorSelection = false, the default) or the heuristic tier was
                    // used — in either case provider/profile stay exactly as they were before this
                    // slice, so the resolved executor below is byte-identical to pre-D3 behavior.
                    if (selection.Provider is not null)
                    {
                        provider = selection.Provider;
                        var profileService = serviceProvider.GetService<IAgentProfileService>();
                        if (profileService is not null)
                        {
                            profile = await profileService
                                .GetAsync(selection.ProfileId, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        provider ??= creds.Provider;

        var childrenBefore = await workUnits.GetChildrenAsync(parentWorkUnitId, cancellationToken)
            .ConfigureAwait(false);
        var existingIds = childrenBefore.Select(c => c.WorkUnitId).ToHashSet();

        var agentId = $"replanner-{Guid.NewGuid():N}";

        var scopedContext =
            $"[Re-plan request] The slice \"{failed.Goal}\" (work unit {failed.WorkUnitId}, file scope: " +
            $"{(failed.FileScope.Count > 0 ? string.Join(", ", failed.FileScope) : "none declared")}) failed " +
            $"and was dead-lettered. Reason: {entry.Reason} (kind: {entry.Kind}).\n\n" +
            "Review the failed slice in the context of the overall goal and decide the right fix — " +
            "you are NOT required to split it into multiple pieces. Choose whichever of these fits:\n" +
            "  - If the goal/steps were fine and it simply ran out of iterations or took a wrong " +
            "approach, replace it with a SINGLE revised slice (same scope, corrected/clarified goal " +
            "and steps) — record a plan with exactly one new slice.\n" +
            "  - If it was genuinely too large for one execution pass, split it into 2 or more " +
            "smaller sub-slices.\n" +
            "  - If working through it revealed the need for follow-up work beyond the original " +
            "scope, add extra new slices alongside the fix.\n" +
            "In every case, via nm_v1_artifact_record_plan: do not repeat or redefine any other " +
            "existing slice in this plan — your planContent should contain only the new slice(s) " +
            "replacing the failed one, each with its own sliceId distinct from every existing slice. " +
            "If two or more of your new slices declare overlapping fileScope paths, you MUST declare " +
            "dependsOn between them so they run in sequence instead of concurrently against the same " +
            "files — do not leave that ordering to be discovered by a file-lease conflict at runtime.";

        // D3 — resolve an executor through the seam exactly like the scheduler-driven Plan-stage
        // branch does (InMemoryAgentRuntimeService.RunScheduledWorkerAsync's Plan branch): a
        // capability miss (a CLI executor that hasn't wired planning mode) falls back to native
        // rather than failing the replan outright.
        var executorResolver = serviceProvider.GetRequiredService<IHarnessExecutorResolver>();
        var executor = executorResolver.ResolveForProvider(provider, profile?.Executor);
        if (!executor.Capabilities.SupportsPlanningMode)
            executor = executorResolver.Resolve("native");

        var planRequest = new HarnessRunRequest(
            HarnessMode.Plan, agentId, parentWorkUnitId, TaskId: string.Empty, profile, SessionId: null,
            IsResume: false, RuleFileContext: null, PromptGuidanceContext: scopedContext,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null,
            Provider: provider, Model: creds.Model, BaseUrl: creds.BaseUrl, ApiKey: creds.ApiKey);

        var planResult = await executor.RunAsync(planRequest, cancellationToken).ConfigureAwait(false);
        if (planResult.Completion != AgentLoopCompletion.Succeeded)
        {
            return new ReplanResult(
                ReplanOutcome.PlanningFailed,
                $"Re-plan attempt did not complete successfully (completion: {planResult.Completion}).",
                StalenessSignal: stalenessSignal);
        }

        await fanOut.TryFanOutFromPlanAsync(parentWorkUnitId, sessionId: null, cancellationToken).ConfigureAwait(false);

        var childrenAfter = await workUnits.GetChildrenAsync(parentWorkUnitId, cancellationToken)
            .ConfigureAwait(false);
        var newWorkUnitIds = childrenAfter
            .Select(c => c.WorkUnitId)
            .Where(id => !existingIds.Contains(id))
            .ToList();

        if (newWorkUnitIds.Count == 0)
        {
            return new ReplanResult(
                ReplanOutcome.NoNewSlicesProduced,
                "Planner completed but no new child work units were created from its plan.",
                StalenessSignal: stalenessSignal);
        }

        await workUnits.UpdateStatusAsync(failed.WorkUnitId, WorkUnitStatus.Cancelled, sessionId: null, cancellationToken)
            .ConfigureAwait(false);

        // The cancelled original slice is never coming back to release its own leases — same gap
        // WorkUnitCommandService.CancelAsync had, and the same fix: dead-letter's max-attempts path
        // already does this release-and-promote, cancellation elsewhere didn't.
        var promoted = await fileLease.ForceReleaseAllForWorkUnitAsync(failed.WorkUnitId, cancellationToken).ConfigureAwait(false);
        foreach (var promotedWorkUnitId in promoted)
            await scheduler.ClearAwaitingFileLeaseAsync(promotedWorkUnitId, cancellationToken).ConfigureAwait(false);

        return new ReplanResult(ReplanOutcome.Replanned, NewWorkUnitIds: newWorkUnitIds, StalenessSignal: stalenessSignal);
    }
}
