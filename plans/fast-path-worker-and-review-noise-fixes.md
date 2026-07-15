# Fast-path worker profile + review-loop noise fixes

Four small, independent fixes surfaced by inspecting a live atomic-goal run (goal
`b4705ccc…`, "add test.md", 2026-07-13). None are correctness-critical — the run merged
cleanly — but each wastes an LLM turn / bills the wrong model on *every* atomic goal, so
they compound. Implement all four; they don't depend on each other and can land in one PR.

**Explicitly out of scope:** the loopback/standalone-server bind hardening. That's a
separate later phase — do not touch host binding, CORS, or auth here.

Verification for the whole PR: `dotnet build NodalMerge.Studio.slnx`, then run the
affected unit-test projects (`NodalMerge.Studio.Merge.Tests`,
`NodalMerge.Studio.AgentRuntime.Tests`, and any `Tasks`/`McpServer` test project). Add the
new coverage described per-fix. Integration tests need native packages and can be skipped
if unavailable locally.

---

## Fix 1 — no-plan fast path must use the worker/Execute profile, not the planner's model

**Problem.** When a planner records no plan (the step-6a atomic-goal case), the work unit
is handed straight to execution. That re-enqueue reuses the *planner's own* queue-item
credentials verbatim, so the worker runs on the planner's model. Every other enqueue site
in the system re-resolves the Execute stage instead
(`FanOutService`, `AutomatedReviewGateService`, `ContinueService`, `InlineReviewerService`,
`ReplanService`, dead-letter retry) via
`GetCredentialsForStage(workUnitId, PipelineStage.Execute) ?? GetGoalDefaultCredentials(...)`.
This one path is the exception. Result: a configured worker/Execute-stage profile is
silently ignored for atomic goals, and the (usually pricier) planner model is billed.

**Location.** `src/NodalMerge.Studio.AgentRuntime/InMemoryAgentRuntimeService.cs`, the
`needsExecuteFallback` block (currently ~lines 563–571):

```csharp
if (needsExecuteFallback)
{
    await _scheduler.EnqueueAsync(
        item.WorkUnitId, "worker", item.TaskId, item.Model, item.BaseUrl,
        item.ApiKey, item.Provider, item.SessionId, item.CredentialRef, ct).ConfigureAwait(false);
    ...
}
```

**Change.** Resolve Execute-stage credentials the same way `FanOutService.RunFanOutAsync`
does, and fall back to the planner's item creds only if resolution returns null (so there's
no regression on the same-process hot path where a registration always exists). The
resolver methods (`GetCredentialsForStage`, `GetGoalDefaultCredentials`) are implemented on
*this same class* (`InMemoryAgentRuntimeService` is the `IAgentControlService`), so call
them directly:

```csharp
if (needsExecuteFallback)
{
    // Re-resolve the Execute-stage (worker) profile rather than reusing the planner's own
    // model — the planner and worker are different profile slots. Every other enqueue site
    // resolves stage creds this way; this fast path was the lone exception, so a configured
    // worker profile was silently ignored for atomic goals. Fall back to the planner's item
    // creds only if resolution yields nothing (keeps the pre-restart hot path unchanged).
    var execCreds = GetCredentialsForStage(item.WorkUnitId, PipelineStage.Execute)
        ?? GetGoalDefaultCredentials(item.WorkUnitId);
    await _scheduler.EnqueueAsync(
        item.WorkUnitId, "worker", item.TaskId,
        execCreds?.Model    ?? item.Model,
        execCreds?.BaseUrl  ?? item.BaseUrl,
        execCreds?.ApiKey   ?? item.ApiKey,
        execCreds?.Provider ?? item.Provider,
        item.SessionId,
        execCreds?.CredentialRef ?? item.CredentialRef,
        ct).ConfigureAwait(false);
    ...
}
```

Confirm `GoalDefaultCredentials` exposes `Provider, Model, BaseUrl, ApiKey, ProfileId,
CredentialRef` (it does — see the record used by `GetGoalDefaultCredentials`). Keep the
literal `"worker"` profile-slot argument unchanged; only the model/creds change.

**Test.** Add a unit test in `NodalMerge.Studio.AgentRuntime.Tests` (near the existing
`ReplanServiceTests` / scheduler-reinvocation coverage): register a goal whose Execute-stage
(or worker) credentials differ from the Plan-stage/default model, drive the no-plan
fallback, and assert the re-enqueued item carries the Execute-stage model, not the
planner's. If a direct scheduler-item assertion is awkward, assert on
`GetCredentialsForStage(Execute)` being the value that reaches `EnqueueAsync` via a scheduler
spy/fake.

---

## Fix 2 — `merge_validate` idempotent for an already-ReadyForReview proposal

**Problem.** The auto-reviewer's first cycle sometimes calls `nm_v1_merge_validate` on a
proposal already in `ReadyForReview` and gets
`"status ReadyForReview cannot transition to ReadyForReview"`. It recovers next cycle, but
it's a wasted turn and transcript noise on every such run.

**Location.** `src/NodalMerge.Studio.Merge/InMemoryMergeService.cs`, `ValidateAsync`
(~lines 105–113).

**Change.** Make *only* the `ReadyForReview → ReadyForReview` case a benign no-op that
returns the proposal unchanged. Every other non-transitionable status (Approved, Rejected,
Merged, …) must still throw — do not broaden this to all statuses.

```csharp
var proposal = GetRequired(proposalId);

// Idempotent for the already-validated case: a defensive re-validate (e.g. the auto-reviewer
// calling validate before checking status) returns the proposal unchanged rather than
// erroring. Other non-Draft statuses still can't transition here.
if (proposal.Status == MergeProposalStatus.ReadyForReview)
    return proposal;

if (!MergeProposalTransitions.CanTransition(proposal.Status, MergeProposalStatus.ReadyForReview))
    throw new InvalidOperationException(...);
```

Note: the early return skips the `MergeProposalStatusChanged` event append — correct, since
nothing changed.

**Test — MUST update existing.** `tests/NodalMerge.Studio.Merge.Tests/InMemoryMergeServiceTests.cs`
has `ValidateAsync_rejects_non_Draft_proposal` (~line 334) that validates twice and asserts
the second call **throws**. That assertion is now wrong. Split it:
- Rename/repurpose one test to assert the second validate on an already-`ReadyForReview`
  proposal is an idempotent no-op returning status `ReadyForReview` (no throw).
- Keep a genuine-rejection case using a truly non-transitionable status (e.g. validate,
  then move to `Rejected` or `Approved` via the normal path, then assert `ValidateAsync`
  throws `InvalidOperationException`).
Leave `ValidateAsync_throws_for_unknown_proposal` (KeyNotFoundException) untouched.

---

## Fix 3 — `nm_v1_task_update` clean no-op when the id is a work unit with no task

**Problem.** On the no-plan direct-execution path the worker has no task record, but the
generic worker prompt unconditionally instructs it to call `nm_v1_task_update` (to
`InProgress`, then `Completed`). The model passes the workUnitId as the taskId and gets
`"Task '…' was not found."` twice per atomic goal — wasted turns and a misleading error.
(`VerifyWorkerProgressAsync` already tolerates a missing task by checking for a merge
proposal instead, so nothing downstream needs the task.)

**Chosen approach: tool-side soft no-op** (robust across all callers; no prompt-assembly
plumbing). Prompt-side conditionalizing is the alternative but the generic worker prompt
can't know at authoring time whether a task exists, so prefer the tool fix.

**Location.** `src/NodalMerge.Studio.McpServer/Tools/TaskTools.cs`, `UpdateAsync`
(~lines 27–50).

**Change.** Inject `IWorkUnitService` into `TaskTools`. In the `catch (KeyNotFoundException)`
branch, before returning an error, check whether the supplied id is actually a known work
unit; if so, return a benign success instead of an error:

```csharp
public sealed class TaskTools(ITaskCommandService taskCommands, IWorkUnitService workUnits)
...
catch (KeyNotFoundException)
{
    // Direct-execution (no-plan) path: the worker has no task record, but the generic
    // worker prompt still tells it to update one, so it passes its workUnitId as the taskId.
    // Treat that as a clean no-op success rather than a misleading "not found" error — the
    // work unit is real, there's simply no task to move. A genuinely unknown id still errors.
    var wu = await workUnits.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
    if (wu is not null)
        return McpJson.Ok(new { updated = false, reason = "No task record for this work unit; nothing to update." });

    return McpJson.Error(McpToolNames.TaskUpdate, $"Task '{taskId}' was not found.");
}
```

Confirm `IWorkUnitService.GetAsync(string, CancellationToken)` is the right signature (it is
— used throughout, e.g. `HarnessWorkerTools`/`EvidenceTools`) and that `TaskTools` is
DI-constructed so the added ctor param resolves. Do not change `TaskAssign`'s behavior.

**Test.** In the McpServer/Tasks tool tests: (a) updating a real task still works and still
errors for a genuinely unknown id; (b) calling `UpdateAsync` with a valid workUnitId that
has no task returns a success envelope with `updated: false`, not an error.

---

## Fix 4 — README tool-count typo (66 → 117)

**Problem.** `README.md` says **117** `nm_v1_*` tools in three places but calls the
api-reference the "complete **66**-tool catalog" at line 393. `docs/reference/api-reference.md`
itself is already correct (117 at its lines 12 and 112). Single stale number in the README.

**Location & change.** `README.md` line 393 only:
`complete 66-tool catalog` → `complete 117-tool catalog`.
No other file needs editing; do not touch `api-reference.md`.

---

## Definition of done

- `dotnet build NodalMerge.Studio.slnx` clean.
- `NodalMerge.Studio.Merge.Tests` green with the updated `ValidateAsync` tests.
- `NodalMerge.Studio.AgentRuntime.Tests` green with the new fast-path credential test.
- Task-tool tests green with the new no-op coverage.
- README line 393 reads 117.
