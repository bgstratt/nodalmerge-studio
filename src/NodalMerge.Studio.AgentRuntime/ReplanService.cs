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
public sealed class ReplanService(
    IDeadLetterService deadLetter,
    IWorkUnitService workUnits,
    IFanOutService fanOut,
    IAgentControlService agentControl,
    IServiceProvider serviceProvider) : IReplanService
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

        var creds = agentControl.GetCredentialsForStage(parentWorkUnitId, PipelineStage.Plan)
            ?? agentControl.GetOrchestratorCredentials(parentWorkUnitId);
        if (creds is null)
            return new ReplanResult(ReplanOutcome.PlanningFailed, "No LLM credentials resolvable for the parent work unit.");

        var childrenBefore = await workUnits.GetChildrenAsync(parentWorkUnitId, cancellationToken)
            .ConfigureAwait(false);
        var existingIds = childrenBefore.Select(c => c.WorkUnitId).ToHashSet();

        var agentId = $"replanner-{Guid.NewGuid():N}";
        var dispatcher = serviceProvider.GetRequiredService<McpToolDispatcher>();
        var llm = serviceProvider.GetRequiredService<LlmClient>();
        var conversationLog = serviceProvider.GetRequiredService<IConversationLogService>();
        var events = serviceProvider.GetService<IExecutionEventStream>();
        var agentClient = new DefaultAgentToolClient(creds.Provider, creds.Model, creds.BaseUrl, creds.ApiKey, llm, dispatcher);

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

        var plannerLoop = new PlannerAgentLoop(
            agentId, parentWorkUnitId, agentClient,
            profile: null, sessionId: null, onActivity: null,
            ruleFileContext: null, constraintsContext: scopedContext,
            conversationLog: conversationLog, events: events);

        var completion = await plannerLoop.RunAsync(cancellationToken).ConfigureAwait(false);
        if (completion != AgentLoopCompletion.Succeeded)
        {
            return new ReplanResult(
                ReplanOutcome.PlanningFailed,
                $"Re-plan attempt did not complete successfully (completion: {completion}).");
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
                "Planner completed but no new child work units were created from its plan.");
        }

        await workUnits.UpdateStatusAsync(failed.WorkUnitId, WorkUnitStatus.Cancelled, sessionId: null, cancellationToken)
            .ConfigureAwait(false);

        return new ReplanResult(ReplanOutcome.Replanned, NewWorkUnitIds: newWorkUnitIds);
    }
}
