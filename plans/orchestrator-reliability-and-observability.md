# Orchestrator reliability & observability — findings from the harness-comparison-eval

## Status

**Phase 1 (all 4 items, including the two-track failure/recovery design + its
re-plan-the-slice AND continue-with-prior-context implementations and VS Code UI
wiring) + the prompt-caching fast-follow + Phase 3 item 2 (context compaction, now
confirmed working on a real run) + Phase 2 item 1 (per-goal cost/time guardrail) +
Phase 2 item 2 (CAS/merge-write-back reliability: auto-resync, multi-repo eviction
fix, CAS dual-write logging) + all three Phase 4 items (found via a live run:
planner-goal-context fix, and the destructive-merge-apply fix) are implemented,
built, and tested.** Phase 3 item 3 is still just this plan. Details on what actually
shipped vs. what was scoped down are in each section below — read those before
assuming an item is "done" in the exact shape originally proposed.

Verified: full .NET solution builds clean (`dotnet build`, 0 errors/warnings). Full test
suite: **555/555 pass** across every project. One test
(`DeadLetterIntegrationTests.Max_iterations_writes_dead_letter_and_DeadLettered_status`)
was updated earlier to match the new, longer dead-letter reason string (an intentional
wording change, not a regression). VS Code extension: `tsc --noEmit` clean and the real
`npm run compile` (esbuild) succeeds — plus the embedded webview JS (which `tsc` does
NOT actually type-check the contents of, a real gap in this codebase's own tooling) was
verified correct by evaluating it in a `vm` sandbox to get the true runtime string, not
just eyeballing the TS source.

**The 3 native-FFI `HostEngineNew` failures that persisted through the whole session
are now fixed too — root cause was in the sibling `nodalmerge` core repo, not staleness.**
The native `nodalmerge_host_ffi.dll` itself was already fresh (byte-identical to the
core repo's post-text-rewrite release build) — the bug was a NuGet packaging gap:
`nodalmerge-host/src/NodalMerge.DotNetHost/NodalMerge.DotNetHost.csproj` only declares
`PackageReference`s to `NodalMerge.DotNetHost.Native.win-x64`/`.linux-x64` when built
with `NodalMergeUseNuGetPackages=true`, but `pack-local-nuget.ps1` packed it with that
flag unset (defaulting false), so the shipped `NodalMerge.DotNetHost` nupkg's
`<dependencies>` never listed the native packages at all — confirmed directly by
inspecting the generated `.nuspec`. Any consumer referencing just `NodalMerge.DotNetHost`
(exactly what `NodalMerge.Studio.Host.csproj` does) therefore never pulled in the
native runtime transitively, hence `DllNotFoundException` at test time regardless of
how current the DLL's own contents were. Fixed in `pack-local-nuget.ps1`: reordered so
the native packages are packed *before* `NodalMerge.DotNetHost`, and that pack step now
passes `/p:NodalMergeUseNuGetPackages=true /p:NodalMergePackageVersion=$Version
--source $resolvedOutput --source https://api.nuget.org/v3/index.json` — the version
pin ensures its PackageReferences resolve to the exact native packages just built, and
the explicit `--source` makes this self-contained rather than depending on ambient
NuGet.config/global-cache state. Verified: the regenerated `NodalMerge.DotNetHost.0.1.2`
nuspec now lists both native packages as dependencies; a `dotnet restore` against the
repacked local artifacts resolves them into `project.assets.json`; the native DLL now
appears in test build output (`runtimes/win-x64/native/nodalmerge_host_ffi.dll`); and
all 3 previously-failing tests now pass.

## Context

Surfaced by running the first real [harness-comparison-eval](harness-comparison-eval.md)
Arm C session (Studio's own orchestrator, single-model Sonnet/Sonnet) against
`eval-stub-small`'s 4 combined tasks. That run cost $3.84 (1,588,219 input / 66,762
output tokens) and ended with 2/4 tasks merged, 1 dead-lettered, 1 never started. None
of what follows is about that eval's own scoring — it's four separate, real gaps in
Studio itself that the run happened to expose. Prioritized and phased per discussion.

## Phase 1 — now, in this order

### 1. Transient HTTP retry policy

**File**: `src/NodalMerge.Studio.AgentRuntime/LlmClient.cs`

Confirmed by direct code search: retry logic exists today, but only for one failure
class. `MalformedLlmResponseException` (thrown when a provider response fails JSON
parsing — the comment explicitly calls out DeepSeek mixing stray non-JSON into
tool-call arguments) is retried in `LlmClient.SendAsync`'s loop, `MaxRetries = 2`.
**Nothing retries HTTP-level transient errors.** Both `SendAnthropicAsync` and
`SendOpenAiAsync` throw a plain `HttpRequestException` the instant a non-success status
code comes back — no backoff, no `Retry-After` handling. This is exactly what happened
on the eval run: task-03's retry hit a 529 (Anthropic "Overloaded" — transient,
provider-side, not billed since no inference ran) and it propagated straight to
dead-letter with zero retry attempts.

**Implemented.** `LlmClient.SendAsync` now has a second catch branch alongside the
malformed-response one, gated on a new `LlmHttpException` (replaces the plain
`HttpRequestException` at both throw sites, carrying status code + any `Retry-After`
header) and an `IsTransient(statusCode)` check. Settled parameters:
- Transient status codes: 429, 500, 502, 503, and 529 (Anthropic "Overloaded" — no BCL
  enum member, cast from `529`).
- Backoff: honors `Retry-After` if the provider sent one; otherwise exponential with
  jitter (1s/2s/4s + up to 250ms random) via `ComputeBackoffDelay`.
- Retry count: its own constant, `MaxTransientRetries = 3`, separate from the
  malformed-response path's `MaxRetries = 2`.
- Added an `onTransientRetry` callback parameter (`Func<TransientRetryAttempt, Task>?`)
  threaded through `IAgentToolClient`/`DefaultAgentToolClient` so callers with domain
  context can observe each retry attempt — see item 3.

### 2. Redact credentials from dead-letter REST responses

**Files**: `src/NodalMerge.Studio.Contracts/Domain/DeadLetterEntry.cs`,
`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs` (`MapDeadLetterEndpoints`)

Found by accident mid-eval: a `GET /studio/dead-letter/by-work-unit/{workUnitId}` call
returned a live Anthropic API key in plaintext. **Storing the credential isn't the
bug** — `DeadLetterEntry.ApiKey` (+ `Model`/`BaseUrl`/`Provider`) is captured
deliberately, per its own doc comment, so a retry doesn't have to re-derive credentials
from the in-memory orchestrator registry, which is ephemeral and may already be gone by
the time a human gets around to retrying. **The bug is serialization**: all three GET
endpoints (`/studio/dead-letter`, `/studio/dead-letter/{entryId}`,
`/studio/dead-letter/by-work-unit/{workUnitId}`) do `Results.Ok(entry)` / `Results.Ok(list)`
directly on the raw record, shipping `ApiKey` verbatim over REST to any caller.

**Implemented.** Added a `RedactForRest(DeadLetterEntry)` helper in
`StudioRestEndpoints.cs` (masks `ApiKey` to `abc...wxyz` shape, or `***` if too short to
usefully mask) and applied it at all three GET endpoints
(`/studio/dead-letter`, `/studio/dead-letter/{entryId}`,
`/studio/dead-letter/by-work-unit/{workUnitId}`) plus the new history endpoint (item 3).
`POST .../retry` and `.../retry-with-context` read the entry directly via
`IDeadLetterService`, never through this redacted projection, so retry still works with
real credentials.

**Action item outside this plan, still open**: the key exposed during the eval session
needs to be fully deleted (not just disabled) and replaced — confirm this happened.

### 3. Surface transient retries + stitch dead-letter history into one view

Two gaps, same underlying problem — there's no way today to see *why* a work unit
struggled without manually reconstructing it (which is what happened this session: two
separate dead-letter entries, linked only by a `steeredFromDeadLetterEntryId` field on
one's metadata, had to be fetched and cross-referenced by hand to learn that task-03
died on "Max iterations reached," got manually steered to retry, and died again on a
transient 529).

**Backend implemented; VS Code extension UI wiring is NOT done — deferred, see below.**

1. **Emit an event per retry attempt.** Added `ExecutionEventKind.ProviderRetryAttempted`
   + `ProviderRetryAttemptedPayload` (agent, provider, status code, attempt number, max
   attempts, delay in ms, reason). `OrchestratorAgentLoop` and `WorkerAgentLoop` each
   gained an `IExecutionEventStream? events = null` constructor parameter and an
   `OnTransientRetryAsync` method wired into their `client.SendAsync(...)` calls via the
   new `onTransientRetry` callback from item 1; both instantiation sites in
   `InMemoryAgentRuntimeService` now pass its existing `_events` field through. No-ops
   safely when `sessionId` is null, matching how other session-scoped events already
   behave in this codebase.

   **Scope cut, on purpose**: only `OrchestratorAgentLoop` and `WorkerAgentLoop` are
   wired — those are the two loops actually implicated by the eval's failure.
   `PlannerAgentLoop`/`ReviewerAgentLoop`/`DomainAgentLoop` don't emit this event yet.
   Same mechanical pattern (constructor param + `OnTransientRetryAsync` + wire into
   their own `client.SendAsync` call), just not done here — pick up when one of those
   loops actually needs it, rather than wiring all five preemptively.

2. **Work unit dead-letter history, walkable as one call.** Added
   `IDeadLetterService.GetHistoryForWorkUnitAsync` (all entries for a work unit, oldest
   first — turns out every entry already carries `WorkUnitId`, so this is a query, not
   a `steeredFromDeadLetterEntryId`-chasing walk) plus
   `GET /studio/dead-letter/history/{workUnitId}` (redacted, like the other GET
   endpoints).

3. **VS Code extension UI wiring — now implemented too.** The panel already fetched
   *every* dead-letter entry globally (`/studio/dead-letter`, unfiltered) each 2s poll
   for its "Blocked Explorations" section — it just rendered one flat card per entry
   with no relationship shown between entries for the same work unit. Rather than call
   the new history endpoint (extra round-trip for data the panel already has),
   `renderBlockedExplorations` in `WorkspaceDashboardPanel.ts` now groups the existing
   list client-side by `workUnitId`: the latest entry is the primary card (unchanged
   retry-button behavior), earlier attempts collapse into an expandable `<details>`
   trail. Separately, the panel now also fetches `/studio/sessions/{sessionId}/events`
   (session-scoped, only when a session is selected) and counts `ProviderRetryAttempted`
   events per work unit, showing "N transient retries" inline on the card.

   **Caught and fixed a real subtlety while verifying this**: this file's webview JS
   lives inside a large TS template literal (`ET_JS`) that `tsc --noEmit` does NOT
   actually type-check the contents of — it only validates that the outer template
   literal itself is well-formed TS. A naive raw-text syntax check of the extracted
   template body produces false positives, because TS/JS escape-sequence collapsing
   (e.g. `\\'` → `\'`) hasn't happened yet in the raw source text. Verified the *real*
   runtime string correctly by evaluating the template literal in a `vm` sandbox first
   (letting the JS engine do the actual escape processing), then syntax-checking that
   result — confirmed valid — and additionally ran the project's real `npm run compile`
   (esbuild) to completion with no errors, not just `tsc`.

### 4. Kick back to planner for decomposition on `MaxIterationsExceeded`

Task-03 died on "Max iterations reached" *before* it ever hit the transient 529 —
its single slice bundled four real pieces of work (implement a thread-safe method, wire
it into a second component, write multiple tests including a concurrency test, then
iterate on `dotnet test`). That's naturally two slices, not one oversized one.

**Correction from investigation**: the worker default is actually `MaxIterations = 30`
(`profile?.MaxIterations ?? 30` in `WorkerAgentLoop`), not 25 — 25 is
`OrchestratorAgentLoop`'s own default (Planner: 15, Reviewer: 14). Task-03 ran as a
`worker`/`Execute`-stage job, so its budget was 30, still exhausted twice.

**Implemented now**: the worker-loop failure handler in `InMemoryAgentRuntimeService`
(where `AgentLoopCompletion.MaxIterationsExceeded` → `failureReason` is set, right
before `RecordDeadLetterAsync`) sets a longer reason string suggesting decomposition, so
it's visible wherever the dead-letter reason is displayed, instead of a human having to
intuit it (which is exactly what happened during the eval — steered "try again" without
considering decomposition).

**Full design settled (not yet built) — two distinct recovery tracks, not one.**
The eval's own retry attempt exposed that "dead-letter → retry" conflates two causes
that need different fixes:

- **Retry track** — a genuine failure (exception, or `Stalled`): the agent's *approach*
  was wrong, not its runway. Human steering (`RetryWithContextAsync`, already built) can
  sometimes correct a wrong assumption, but isn't reliable for a fundamentally flawed
  approach — that's more honestly fixed by **re-planning from scratch** (fresh
  decomposition, possibly different slice boundaries), not retrying the same scope with
  a note attached. Steering-only was the original fast-path to get something running,
  not a claim it's the right long-term answer for every failure — keep it available as
  an option, not the only option.
- **Continue track** — `MaxIterationsExceeded` specifically: nothing about hitting the
  ceiling implies the approach was wrong, only that the budget was too small for
  legitimate forward progress. The right fix here is **continuing with more iterations
  and the prior attempt's actual context** (not a fresh restart), or, if the slice
  itself looks too big, **re-planning the slice** (the additive-fan-out design below).

**Structural gap that has to close before either track can branch reliably**:
`DeadLetterEntry` has no structured signal for *which* of these happened today — only a
free-text `Reason` string (an exception message, or the two hardcoded strings this pass
introduced for `MaxIterationsExceeded`/`Stalled`). Needs a real `FailureKind` enum
(`Exception` / `MaxIterationsExceeded` / `Stalled`) captured at the point of
dead-lettering, so the UI/automation can show the right action pair instead of
string-matching a reason it happens to control today.

**Re-plan-the-slice mechanism (either track can use this one), designed via existing
primitives — no new state machine needed**: confirmed by reading the code, not assumed —
`FanOutService.ReadPlanFromArtifactAsync` reads the *latest* Plan artifact in the chain,
not the original, and `EnsureChildWorkUnitsAsync` is per-slice idempotent (skips any
`sliceId` that already has a child). So a **new** Plan artifact containing only the
newly decomposed sub-slices, recorded after a failure, gets picked up on the next
`TryFanOutFromPlanAsync` call and creates *only* those new children — existing slices
are untouched. `(DeadLettered, Cancelled)` is already a valid `WorkUnitTransitions`
transition, giving the original failed slice a real terminal "superseded" status. Concretely:
spawn a bounded `PlannerAgentLoop` scoped to just the failed slice's goal + failure
reason → it records a new Plan artifact with the decomposition → fan-out creates fresh
child work units (each with their own independent attempt budget — nothing loops) →
transition the original to `Cancelled`.

**Continue-with-prior-context mechanism — was blocked on Phase 3, now unblocked.**
`WorkerAgentLoop.RunAsync` always starts `messages` as a single fresh kickoff message,
even on `isResume` — that flag only appends a *text hint* ("check existing files"), it
never reconstructs the previous attempt's actual reasoning/tool-call history.
`ConversationLogEntry` already durably logs every turn, so the data needed for a real
continuation exists — `WorkerAgentLoop` just doesn't use it. Reconstructing that full
prior history and running it makes the next call's context *strictly larger* than the
original run — the original blocking concern was that doing this before compaction
existed would make an already-expensive case *more* expensive, not less.

That blocker is now resolved: Phase 4 item 1 confirmed compaction actually fires on a
real run (the token-curve evidence), and prompt caching is confirmed working on at
least the OpenAI-compatible path (Anthropic/`vscode-lm` still unverified, but nothing
depends on that specifically) — so a reconstructed-then-continued conversation will
have the *same* elision/rolling-summary compaction applied to it as any other run,
bounding its cost the same way. **Now being implemented** — see below for the
concrete plan: reconstruct `NmMessage`s from `ConversationLogEntry` rows, seed a fresh
`WorkerAgentLoop` with them plus a new iteration budget, and let the existing
compaction machinery do the same job it already does for a long single run. Manual
button only (mirroring Re-plan) — no automatic triggering, same posture as everything
else in this plan.

**Why `RetryWithContextAsync`-driven automatic retry was rejected outright (still
true)**: its own doc comment says it *bypasses* the normal `MaxFailureAttempts` cap —
automating a retry on this path risks an unbounded-cost loop if the first automatic
attempt doesn't help either. The re-plan-the-slice design above sidesteps this entirely:
it never retries the same work unit, it spawns fresh, independently-budgeted ones and
marks the original terminal.

**Implemented.**

1. **`FailureKind` enum** (`src/NodalMerge.Studio.Contracts/Domain/FailureKind.cs`):
   `Exception`, `MaxIterationsExceeded`, `Stalled`, `MissingCredentials`,
   `ProgressNotVerified`, `ReviewRejected` — the last two weren't in the original
   three-value sketch but turned out to be real, distinct paths already producing
   dead-letter entries (a worker claiming done without verified progress; automated/
   human review rejection exhausting its own retry count) that would have been
   misleadingly bucketed under `Exception` otherwise. Added as a trailing, defaulted
   field on `DeadLetterEntry` (non-breaking) and threaded through
   `IDeadLetterService.RecordFailureAsync` to all 5 real call sites (worker path,
   orchestrator's `MaxIterationsExceeded`/`Stalled` branch, orchestrator's exception
   catch, and both `AutomatedReviewGateService` rejection paths) — replacing what was
   previously undiscoverable except by matching against a free-text `Reason` string.

2. **Re-plan-the-slice mechanism** (`IReplanService`/`ReplanService` in
   `NodalMerge.Studio.AgentRuntime`, `POST /studio/dead-letter/{entryId}/replan`):
   given a dead-letter entry, resolves the failed work unit's parent (returns
   `NotApplicable` if there is none — this only applies to a fanned-out slice, not a
   top-level goal), spawns a bounded `PlannerAgentLoop` scoped to the **parent** work
   unit with the failed slice's goal/fileScope/failure-reason folded into
   `constraintsContext` (explicitly instructed to decompose *only* the failed goal
   into new sub-slices, not touch or repeat any other existing slice), calls
   `IFanOutService.TryFanOutFromPlanAsync` on the parent to materialize the new
   children, and — only if that actually produced new work units — transitions the
   original failed work unit to `Cancelled`. Every failure mode (planner didn't
   finish, planner "succeeded" but produced no usable new slices, no credentials
   resolvable) leaves the original dead-letter entry and work unit completely
   untouched rather than risking a partial/inconsistent state.

   This is the same mechanism serving both tracks: Retry-track "re-plan from
   scratch" and Continue-track "re-plan the slice" are the identical call against
   the identical service — the only difference is which UI action a human (or future
   automation) invokes it from.

   Verified: `ReplanServiceTests.cs` (`NodalMerge.Studio.AgentRuntime.Tests`, 4
   tests) covers every guard-clause branch (`NotFound`, `MaxAttemptsReached`,
   `NotApplicable`, missing-credentials → `PlanningFailed`) with fakes, no LLM call
   needed. `ReplanServiceIntegrationTests.cs`
   (`NodalMerge.Studio.Integration.Tests`, 1 test) exercises the full happy path
   end-to-end against the real DI graph with a scripted fake LLM handler: creates a
   parent + a "failed" child, dead-letters it, calls `ReplanFailedSliceAsync`, and
   confirms 2 new sibling work units exist and the original transitioned to
   `Cancelled`. Full solution: 559/559 tests pass (up from 555 — 4 new
   `ReplanServiceTests` + 1 new `ReplanServiceIntegrationTests` — no regressions).

**UI wiring — now implemented.** `WorkspaceDashboardPanel.ts`'s dead-letter card gained
a "Re-plan" button next to the existing "Retry" button, calling the same
`POST /studio/dead-letter/{entryId}/replan` endpoint regardless of track — the button
label switches between "Re-plan the slice" (when `FailureKind == MaxIterationsExceeded`,
the Continue track) and "Re-plan from scratch" (every other kind, the Retry track) purely
to match how a human would describe what just happened; the call and backend behavior
are identical either way. Unlike "Retry", the re-plan button is **not** hidden once
`maxAttemptsReached` — see the fix below.

**Real bug caught while wiring this: `ReplanService` originally gated on
`entry.MaxAttemptsReached`/`AttemptCount`, contradicting its own design rationale.**
The whole point of re-planning is that it sidesteps the retry-attempt cap — it never
resumes the failed work unit, only spawns fresh siblings — so blocking it once a slice
exhausts its retry attempts is exactly backwards: that's the clearest case for reaching
for re-plan *instead of* retry. Removed the guard (and its now-dead
`ReplanOutcome.MaxAttemptsReached` value/REST branch); the corresponding test was
rewritten to assert the opposite (an exhausted entry now proceeds past that check).

Verified: `tsc --noEmit` and `npm run compile` (esbuild) both clean. The embedded
webview JS (inside the `ET_JS` template literal, which neither of those actually
type-checks — see the earlier VS Code extension note in this doc) was verified
correct the same way as before: extracting the raw template body into a real module
file so Node's own parser collapses escape sequences exactly once, then
syntax-checking the resulting real runtime string via `new Function(...)` — confirmed
valid, not just eyeballed. Full solution: 559/559 tests pass (one Integration.Tests
run showed a single failure that did not reproduce on rerun — a timing-based polling
test flake, not a regression from these changes).

**Still deliberately deferred**: any *automatic* triggering of re-plan (e.g., firing it
on `MaxIterationsExceeded` without a human clicking anything, or the auto-timeout
pattern `ClarificationTimerService` already uses for unanswered clarification
questions) — considered and explicitly declined for now; revisit once there's real
usage data on how often a human actually reaches for re-plan over plain retry. If ever
built, re-plan is the only one of the two actions safe to automate this way (it never
loops or resumes the same work unit) — retry must stay human-triggered.

**Continue-with-prior-context — implemented.** `IContinueService`/`ContinueService`
(`NodalMerge.Studio.AgentRuntime`, `POST /studio/dead-letter/{entryId}/continue`):
given a dead-letter entry, returns `NotApplicable` for anything other than
`FailureKind.MaxIterationsExceeded` (Continue only makes sense there — for every other
kind the approach itself was the problem, not the budget). Pulls the failed run's own
`ConversationLogEntry` rows (filtered to that specific attempt's `AgentId`, since a work
unit can carry multiple prior attempts under different agent IDs across its
dead-letter/retry history), converts each cycle back into an `(assistant, user
tool-results)` message pair via `ContinueService.ReconstructTurns` (parsing each tool
call's stored `InputJson` back into a `JsonElement` and `.Clone()`-ing it so it's safe to
use once its backing `JsonDocument` is gone), and seeds a **fresh** `WorkerAgentLoop`
(new agent ID, fresh iteration budget) with the kickoff message followed by that
reconstructed history plus a short "you're continuing your own prior work, not starting
over" notice folded into the last message (appended if the last reconstructed message is
already `user`-role — the common case, since a `MaxIterationsExceeded` run's last
recorded turn is its own tool results with no subsequent assistant reply — or added as a
new trailing `user` message in the rarer case where the last cycle broke out without a
tool-result turn, keeping role alternation valid either way). Credentials come from the
dead-letter entry's own captured `Model`/`BaseUrl`/`ApiKey`/`Provider` first, falling back
to `IAgentControlService`'s live registry only for legacy entries recorded before
credential capture existed. On success, transitions the work unit `Retrying` →
`Executing`; on any other completion, records a fresh dead-letter entry with the same
`MaxIterationsExceeded` kind so Continue can be reached for again or a human can switch
tracks to re-plan instead.

The reconstructed conversation is deliberately **not** a cost-blowup risk despite being
strictly longer than the run that already hit the iteration cap: `WorkerAgentLoop`'s own
`ConversationCompactor` (elision + rolling summary) applies to it exactly the same as it
would to any long single run — that's what was blocking this feature until compaction
was confirmed working on a real run (Phase 4 item 1) and prompt caching was confirmed on
at least the OpenAI-compatible path. Manual button only, mirroring Re-plan — no automatic
triggering.

Verified: `ContinueServiceTests.cs` (`NodalMerge.Studio.AgentRuntime.Tests`, 5 tests) —
guard clauses (`NotFound`, `NotApplicable` for a non-`MaxIterationsExceeded` kind,
`NotCompleted` for unresolvable credentials) plus two direct unit tests of
`ReconstructTurns` (made `internal` specifically for this) confirming it rebuilds the
right assistant/tool-result turn structure per cycle, including cycles with no
`AssistantText`. `ContinueIntegrationTests.cs` (`NodalMerge.Studio.Integration.Tests`, 1
test) proves actual end-to-end reconstruction against the real DI graph: drives a worker
to `MaxIterationsExceeded` (`MaxIterations: 2`) via a fake LLM handler, then calls
`ContinueWithPriorContextAsync` against a handler (`ContinueLlmHandler`) that only ends
the turn once the incoming message array is longer than any request the original
exhausted run could have produced — proving the resumed run actually carried the prior
attempt's reconstructed history forward, not that it silently restarted (a fresh restart
would produce an identically short first request and would exhaust again, masking the
bug). Full solution: 575/575 tests pass (up from 570 — 5 new `ContinueServiceTests` + 1
new `ContinueIntegrationTests` — no regressions).

**Done**: the VS Code extension's dead-letter card now also has a "Continue" button
(next to Retry/Re-plan), gated on `FailureKind === 'MaxIterationsExceeded'` and the same
attempt-count cap as Retry (since Continue also resumes the same work unit), calling
`POST /studio/dead-letter/{entryId}/continue`.

## Phase 2 — item 1 implemented, item 2 substantially addressed

1. **Per-goal cost/time guardrail — implemented, alert-only.** Two explicit decisions
   made before writing code (both confirmed with the user, not assumed): (a) exceeding
   a cap only **alerts** — it never stops or interrupts in-flight work, since
   auto-stopping active agent work is a real, hard-to-reverse action that should stay a
   human's call; (b) the cap measures **total tokens**, not estimated USD cost — exact
   (already recorded per `ConversationLogEntry` cycle), immune to per-model pricing
   drift, and tokens are what actually caused the eval's own 1.59M-token blowup.

   `IGoalGuardrailService`/`GoalGuardrailService` (`NodalMerge.Studio.AgentRuntime`)
   computes status **on demand** — no background poller, no persisted "already
   alerted" state to go stale — by walking a goal's entire work-unit subtree
   (recursively, via `IWorkUnitService.GetChildrenAsync`) and summing
   `ConversationLogEntry.InputTokens`/`OutputTokens` across every work unit in it, plus
   elapsed time since the goal's `CreatedAt`. Two new `WorkspaceOptions` fields
   (`MaxGoalTokens`, `MaxGoalDurationMinutes`), both nullable and default `null`
   (disabled). Two new endpoints: `GET /studio/goals/guardrail-status` (all active
   top-level goals — non-terminal, `ParentWorkUnitId == null`) and
   `GET /studio/goals/{workUnitId}/guardrail-status` (one goal). VS Code extension:
   the active-goals dashboard card fetches this alongside its other polls and shows a
   "⚠ Guardrail exceeded" warning row (tokens and/or duration, whichever tripped) —
   purely informational, never disables or hides anything else on the card.

   Verified: `GoalGuardrailServiceTests.cs` (6 tests) covers subtree token summation
   across multiple levels, cap-crossing in both directions, no-cap-configured
   (never flags), duration-based flagging, and the active-goal filter (excludes
   `Completed`/`Merged`/`Cancelled`/`Failed` top-level goals and non-top-level work
   units). `tsc --noEmit` and `npm run compile` (esbuild) both clean; the embedded
   webview JS (which neither actually type-checks — see the earlier note in this doc)
   verified correct via the same real-runtime-string method as before. Full solution:
   **565/565 tests pass.**

   Considered and explicitly declined for now: extending
   `ClarificationTimerService`'s auto-timeout pattern (unanswered clarification
   questions auto-resume/auto-abandon after a configured window) to dead-letter
   entries or exceeded goals — a real, reusable mechanism for this shape of problem,
   but a new automation decision in its own right; revisit once there's real usage
   data. If ever built, re-plan (Phase 1.4) is the only action safe to fire that way —
   it never loops or resumes the same work unit, unlike retry.

2. **File workspace / working-copy merge process exploration — architecture reviewed,
   three concrete gaps found and fixed; the two-gate design itself confirmed sound.**
   The two-gate design (each worker gets its own branch → results propose into the
   goal's own internal main work copy → a separate, later gate applies the chosen
   result into the real git-backed repository) is intentional — built for
   experimentation/pick-the-winner workflows, not a bug. A full architecture review
   this session confirmed the actual model: the on-disk branch working directory is
   the real source of truth during a run (every agent tool call and merge-apply step
   reads/writes disk directly); the CAS (Blake3-hashed blobs) + `RepositorySnapshot`
   (flat path→blobId map, SHA-256 tree fingerprint) is a best-effort audit/
   reconstruction trail derived from disk writes, not the other way around — not the
   "repo fully lives in a DAG" model that was worth explicitly ruling out. The
   already-built `WorkspaceCacheManager` (Phase 8) cleanup/rematerialize feature is
   exactly the mechanism for evicting completed work units' branch directories once
   safe, and re-deriving them from CAS+snapshot later — it just had one dependency gap
   (below) keeping it from actually firing.

   **Gap 1 — nothing ever refreshed the CAS snapshot after a merge, and a deeper
   pre-existing bug meant it couldn't have anyway.** `RepositorySnapshot` only
   advanced via `RepositoryImportService.EnsureBootstrappedAsync`'s Case 1 (bootstrap)
   / Case 2 (diff-and-resnapshot) logic — but that method is gated by an in-memory
   "already bootstrapped" set, so **every call after a repository's first goal
   creation was a complete no-op for the rest of that process's lifetime**, no matter
   how many merges landed or how many times a resync was requested (this affected the
   pre-existing `ManualRefresh` REST/MCP trigger too — it had never actually refreshed
   a snapshot past first bootstrap, for anyone). Fixed by splitting
   `IRepositoryImportService` into `EnsureBootstrappedAsync` (unchanged — bootstrap
   once, skip on repeat, right for `GoalCreation`/`StartupRecovery`) and a new
   `ForceSyncAsync` that always re-runs Case 1/Case 2 regardless of the bootstrap
   gate. `InMemoryMergeService.ApplyAsync` now calls
   `IRepositorySyncService.SyncBranchFromRepositoryAsync("main", writeBackPath,
   SyncTrigger.PostMergeWriteBack, ct)` (new trigger value) right after its write-back
   completes — swallowed on failure (a resync failure must never roll back an
   already-completed merge) — and `RepositorySyncService` routes `PostMergeWriteBack`/
   `ManualRefresh` through `ForceSyncAsync`, `GoalCreation`/`StartupRecovery` through
   the unchanged bootstrap-or-skip path. Scoped deliberately to the global default
   repo only (`writeBackPath == _workspaceOptions.SeedRepositoryPath`) — a multi-repo
   work unit's own resolved path is a different physical repo than `"main"`'s
   directory mirrors, so syncing `"main"` against it would diff the wrong pairing.

   Considered and ruled out as a real risk: whether a live resync could disturb an
   in-progress agent's already-materialized files ("shifting sands"). Traced both
   read paths — `InitBranchAsync` no-ops the instant a branch directory is non-empty,
   and the only other snapshot-consuming path, `MaterializeFileAsync` (the Phase 11
   on-demand fallback for `FileScope`-scoped branches), fires *only* when a file has
   never been read into that branch before (`McpToolDispatcher.WorkspaceReadAsync`'s
   `ReadAsync` returning null is the sole call site). Neither can ever overwrite a
   file an agent has already touched. The one real (narrow) effect is that a scoped
   branch's very first fetch of a not-yet-materialized file might now see fresher
   content than before — a strict improvement over permanent staleness, not a
   regression, since dependent workers already receive completed changes through an
   entirely separate mechanism (direct `CopyFilesAsync` at merge-apply time, plus the
   file-lease release-and-resume hook), never through snapshot resync.

   **Gap 2 — `WorkspaceCacheManager` resolved the wrong repository for multi-repo
   goals.** `GetRepositoryId()` always used the global `WorkspaceOptions.
   SeedRepositoryPath`, ignoring a work unit's own registered `RepositoryId` — so
   eviction-safety and rematerialization checked/rebuilt from the wrong repository's
   snapshot entirely for any multi-repo setup. Replaced with an async
   `GetRepositoryIdAsync(WorkUnit, ct)` that resolves the work unit's own
   `RepositoryId` through `IRepositoryRegistryService` first (mirroring
   `InMemoryMergeService`'s existing write-back resolution), falling back to the
   global default when unset or unresolvable.

   **Gap 3 — the CAS dual-write's "can't do this" path was completely silent.**
   `FileSystemWorkspaceService.CanEmitOps` no-opped with zero logging whenever the
   blob store, op service, or seed path weren't all configured. Now logs once per
   instance (this class is a singleton) at `Information` — not `Warning`, since an
   unconfigured CAS is a legitimate, common, intentional choice, not a
   misconfiguration — naming which of the three conditions failed; resumes silently
   with no re-log once configuration completes at runtime (confirmed elsewhere that
   REST/MCP mutate the shared `WorkspaceOptions` instance directly).

   Verified: 17 new tests, full solution **597/597 pass** (up from 580 — no
   regressions). `InMemoryMergeServiceTests.cs` (4 tests, fake
   `IRepositorySyncService`) covers the trigger firing/not-firing/surviving-a-throw.
   `PostMergeResyncIntegrationTests.cs` (1 test) proves the *real* end-to-end fix —
   against the actual `RepositoryImportService`, not a fake — confirming a fresh
   `RepositorySnapshot` (new `SnapshotId`, incremented `Generation`, later
   `CreatedAt`, correct `BaseSnapshotId` lineage) exists after a real merge, which is
   exactly what would have failed before the `ForceSyncAsync` split.
   `WorkspaceCacheManagerMultiRepoTests.cs` (1 test) reproduces the multi-repo
   eviction bug directly: two goals against two real repos, only one resynced,
   confirms eviction is correct for both (`true`/directory deleted for the resynced
   one, `false`/directory intact for the other) — before the fix this would have
   given the wrong answer for at least one. `FileSystemWorkspaceServiceCasLoggingTests.cs`
   (4 tests, via a custom `ILoggerProvider` rather than naming the internal
   `FileSystemWorkspaceService` type directly) covers logs-once, no-relog-on-repeat,
   silent-when-fully-configured, and silent-resume-once-configured-at-runtime.

   Follow-up hardening: `RepositoryImportServiceTests.cs` (6 tests) — this repo had
   no dedicated unit coverage of `IRepositoryImportService` at all before now, only
   indirect exercise through higher-level tests. Pins down the exact contract the fix
   depends on: `EnsureBootstrappedAsync` bootstraps once then no-ops forever (right
   for `GoalCreation`/`StartupRecovery`); `ForceSyncAsync` always re-diffs regardless
   of that gate, correctly no-ops when nothing changed on disk (the `hasChanges`
   guard), correctly detects a deleted file, and itself marks the repository
   bootstrapped so a later `EnsureBootstrappedAsync` call doesn't redundantly re-run
   Case 1/2. One more test added to the existing `WorkspaceSwitchTests.cs` —
   `REST_switch_to_the_same_repository_again_after_a_disk_change_advances_the_snapshot`
   — proves the fix at the actual `POST /studio/workspace/switch` HTTP endpoint (the
   real `ManualRefresh` call site a human/tool actually hits), not just at the
   service-call level.

   Considered and explicitly deferred (not part of this pass, no code changes): a
   fourth issue found during the same review — `LocalFilesystemProjectionMaterializer`
   writes unreviewed proposal content into the real repo the instant any proposal
   reaches `Proposed`, entirely bypassing `ReviewPolicy`. Confirmed dormant in
   practice — it only fires when the legacy global `SeedRepositoryPath` option is set,
   which the normal per-goal `RepositoryId` flow never touches (traced every call
   site: `WorkUnitCommandService.CreateAsync`'s repo-registry flow never sets it) —
   and has zero test coverage either way. Revisit only if/when the legacy "set seed
   repository path" affordance is actually exposed to users.

## Phase 3 — separate, larger multi-phase project (after Phase 1 ships)

**Compaction / caching / context.** The token/cost blowup (1.59M input tokens for one
combined-session run) traces to `OrchestratorAgentLoop`/`WorkerAgentLoop` resending the
full, ever-growing message history every cycle. Item 1 below (prompt caching) was
pulled forward and done as a fast-follow, ahead of the rest of this phase — the
remainder is still just this plan.

### 1. Prompt caching — DONE (fast-follow, ahead of the rest of this phase)

`LlmClient.SendAnthropicAsync` now marks two `cache_control: {type: "ephemeral"}`
breakpoints, both static across every cycle of a work unit's loop:
- The system prompt, changed from a plain string to a one-block array with the
  breakpoint on that block.
- The last tool definition in the (otherwise identical every cycle) tools array —
  Anthropic caches everything up to and including a marked block, so this covers the
  whole tools list.

Also added `cache_read_input_tokens`/`cache_creation_input_tokens` parsing onto
`AnthropicUsage`, logged (not yet threaded into `ConversationLogEntry`/UI) specifically
so caching can be *verified* against a real call rather than trusted blindly — a
non-zero `cache_read_input_tokens` on the second+ cycle of a work unit confirms it's
actually working.

**Caching is not standardized across providers — researched and confirmed, not
assumed.** Anthropic requires the explicit `cache_control` markers above; OpenAI
(gpt-4o/4.1+, prompts ≥1024 tokens) and DeepSeek both cache **automatically**, no
request-side changes at all, just different response shapes: OpenAI nests it under
`usage.prompt_tokens_details.cached_tokens`, DeepSeek is flat
(`prompt_cache_hit_tokens`/`prompt_cache_miss_tokens`). A single `OpenAiUsage` record
absorbs both as optional fields with no branching needed (`System.Text.Json` leaves
absent fields null) — implemented, with the same kind of log-line verification hook as
the Anthropic path, in `SendOpenAiAsync`. This means the OpenAI-compatible path was
likely *already* benefiting from caching for free, with zero visibility until now.

**Deliberately NOT done, scoped out for risk/complexity reasons**: caching the
growing `messages` array itself — the actual biggest piece of the cost problem, bigger
than system+tools. That needs a moving cache breakpoint on the second-to-last message
each cycle, which requires forcing the last message out of the existing
string-shorthand serialization into block form. Skipped for now because (a) it's more
invasive than the system/tools change, and (b) it should be verified against a real
Anthropic call before trusting, which didn't happen this pass (no API calls were made
from this session). Do that verification — confirm `cache_read_input_tokens > 0` shows
up on a real second-cycle call — before extending caching to messages.

### 2. Context compaction — design settled, implementing now

Confirmed by reading `WorkerAgentLoop`/`OrchestratorAgentLoop`/etc.: `messages` is a
single `List<NmMessage>` that only ever grows (one assistant turn + one tool-result
turn appended per iteration) and is resent in full on every `client.SendAsync` call —
this, not any one bad call, is what produced the eval's 1.59M-input-token session.
Two questions had to be settled before writing code, both researched rather than
assumed:

- **How much does tool-result truncation alone buy?** Tool payloads (file reads,
  search hits, build/test output) dominate a long session's resent tokens — assistant
  reasoning text is comparatively small. So eliding *stale* tool-result bodies is a
  large, safe first lever. It has a ceiling, though: it shrinks each kept item, it
  doesn't cap how many distinct substantive tool calls a long session accumulates.
- **Is dropped history recoverable elsewhere?** Partially. `ArtifactRecord`/
  `ArtifactQuery` (Research/Decision/Constraint) are durable and re-fetchable by the
  agent itself, so anything already promoted to an artifact survives compaction for
  free. `ConversationLogEntry` durably logs every raw turn too, but only for
  human/audit consumption today — the agent has no tool to query its own prior turns
  back out of it. So a raw tool result the agent never promoted to an artifact is a
  real (not just theoretical) loss if elided outright.
- **Precedent**: Claude Code's own `/compact` (and equivalent behavior in other modern
  coding agents) is a rolling LLM summary, not mere truncation — once context nears
  budget, one extra call condenses older history into a recap (goals, files touched,
  decisions made, remaining plan), and raw turns are dropped in favor of that recap
  plus the most recent turns verbatim. Truncation alone is not the industry norm; it's
  the cheap complement to a summary, not a replacement for one.

**Implemented.** `ConversationCompactor` (new file,
`src/NodalMerge.Studio.AgentRuntime/ConversationCompactor.cs`) implements both
mechanisms below and is wired into `WorkerAgentLoop` and `OrchestratorAgentLoop`'s
per-cycle loop, called every iteration right before `client.SendAsync`. Verified: full
solution builds clean; `NodalMerge.Studio.AgentRuntime.Tests` 47/47 pass;
`NodalMerge.Studio.Integration.Tests` 367/367 pass (the native-FFI packaging bug that
had been causing 3 failures here throughout the session is now fixed too — see Status
above).

**Mechanism verified via a fake `IAgentToolClient`, not a live call — and this
mattered.** Added `InternalsVisibleTo` from `NodalMerge.Studio.AgentRuntime` to its
test project (standard pattern for testing `internal` logic) plus
`ConversationCompactorTests.cs` (8 tests): elision trigger/threshold behavior, the
rolling-summary trigger firing exactly once past `SummaryTriggerCount`, tail-window
correctness, and safe no-op on an empty summarization response — all driven by a fake
client returning canned `LlmResponse`s, zero network calls, zero cost. **This caught a
real bug before any live call would have needed to**: the first implementation
inserted the recap as its own new `"user"`-role message immediately after the
also-`"user"`-role kickoff message, producing two adjacent same-role turns and breaking
the strict user/assistant alternation Anthropic/OpenAI both require. An
alternation-focused test failed immediately on this. Fixed by folding the recap into
the kickoff message's own content instead of inserting a new message — the same
fold-into-one-message convention `OrchestratorAgentLoop` already uses for its
per-cycle delta/constraints text. This is exactly the kind of structural bug a mocked
test catches for free that a live call might not have surfaced on the first try (a
real provider may well have just tolerated or silently reinterpreted the malformed
role sequence rather than erroring loudly). A live call still has a place — see below —
but only for what a mock can't tell you: whether a real model's summary is a *useful*
recap, and whether the request/response wire format round-trips against a live
Anthropic/OpenAI-compatible endpoint. Recommended for that: one cheap DeepSeek (or
Haiku-tier) call, not Sonnet.

**Settled design — both mechanisms, not either/or, fixed-turn-count trigger:**

1. **Tool-result elision (always on, no threshold)**: once a tool-result message is
   more than `K` turns old (proposed `K=4`) and a *newer* message exists, replace its
   body with a short placeholder (tool name + a one-line "elided, N chars" marker)
   rather than resending the full payload every cycle. Zero extra LLM calls, applies
   from turn 1, no risk to reasoning continuity since assistant turns are never
   touched — only their tool-result inputs are.
2. **Rolling summary (fixed turn-count trigger)**: once `messages.Count` exceeds a
   fixed threshold (proposed 20 — roughly 10 turns of user+assistant pairs, comfortably
   before a 25-30-iteration budget is exhausted), spend one extra `client.SendAsync`
   call with a dedicated summarization prompt against everything older than the
   trailing window (proposed last 6 messages kept verbatim), and replace that older
   segment with a single synthesized `user` message containing the recap. Runs at most
   once or twice per work unit's loop, not every cycle — the threshold check only fires
   when history is long enough to matter.

**Scope for first implementation pass**: land this in `WorkerAgentLoop` first (the loop
directly implicated in the eval's cost blowup and the one most likely to run long),
verify it against a real call (confirm the summary call actually fires at the
threshold and that token counts on subsequent cycles actually drop), then extend the
same pattern to `OrchestratorAgentLoop`. `PlannerAgentLoop`/`ReviewerAgentLoop`/
`DomainAgentLoop` have much lower iteration ceilings (15/14/definition-specific) and
weren't implicated by the eval — deferred until one of them actually needs it, same
scoping call already made for Phase 1.3's retry-event wiring.

### 3. Projection review — not started

Once 1–2 exist, revisit whether the per-worker projection (each worker is meant to get a
scoped view of just its slice of the work) is behaving as intended at real task
complexity and iteration-budget levels — especially relevant alongside Phase 1 item 4's
partial decomposition-guidance change, since better slicing plus cheaper context might
change what a reasonable default iteration budget even looks like.

## Phase 4 — findings from a live run (`liveCompaction`, DeepSeek, combined 4-task goal)

A real 4-task goal run (not a scripted eval) against a fresh MiniLedger checkout,
graded with `grade-task.ps1` after the fact. Surfaced three more findings, one fixed
here, one confirmed working, one diagnosed and deliberately deferred.

### 1. Context compaction — confirmed working on a real run (indirect evidence)

No direct instrumentation exists to prove this outright (`ConversationCompactor` still
logs nothing — see the still-open item below), but the per-cycle `InputTokens` recorded
in `ConversationLogEntry` for the two longest-running workers in this goal show a
signature that only makes sense if the rolling-summary path fired exactly where
designed: tokens climb turn over turn, then **drop** sharply right around the
`SummaryTriggerCount=20`-message threshold (cycle 9→10 in both cases: 11445→9990 on one
worker, 14481→8693 on the other). A naturally-growing uncompacted transcript doesn't
drop mid-task. Real (if indirect) confirmation that Phase 3.2 works end to end, not just
under mocks.

### 2. Planner-summarization defect — real, found, fixed

The planner paraphrases each slice's own `goal` field from the original top-level
request — and in this run, dropped a literal method-signature contract
(`bool TryClaimForFulfillment(string orderId)`) while writing one slice's goal, leaving
only a vaguer restatement ("atomic compare-and-swap... exactly one caller wins the
race"). The worker had no way to know the literal contract existed — `successCriteria`
was null, and nothing else in the pipeline ever exposed the original top-level goal text
to a fanned-out child. It built a defensible alternative (`Order? TryClaimForFulfillment`
instead of `bool`) that failed the task's own hidden test on a pure signature mismatch —
not a reasoning failure, an information-availability failure.

**Fixed, two changes:**
- `InMemoryAgentRuntimeService.BuildOriginalGoalContextAsync` (new) walks a work unit's
  `ParentWorkUnitId` chain to the true root (fan-out can in principle nest more than one
  level) and, when the root's goal differs from the child's own, folds the root's
  original text into the worker's kickoff as clearly-labeled reference material — "the
  slice goal may only summarize part of it; if they conflict, the original request is
  authoritative." Wired into both `WorkerAgentLoop` construction sites (scheduled-worker
  and direct-spawn/resume) alongside the existing constraints/prompt-guidance context.
  This is the fix that actually closes the gap — even a good-faith planner paraphrase
  can drop something, so the worker needs its own access to the source of truth.
- `AgentLoopPrompts.Planner`'s system prompt (step 6) now explicitly instructs
  preserving literal contracts (exact signatures, return types, field/property names,
  file formats, error messages) verbatim into a slice's own goal/steps rather than
  paraphrasing them away — cheaper to have landed, but relies on the model actually
  following it, unlike the structural fix above.

Verified: `OriginalGoalKickoffInjectionTests.cs` (new, Integration.Tests) drives a real
fan-out child through a capturing fake LLM handler and confirms the root's literal
contract text actually reaches the worker's kickoff message, labeled clearly. Full
solution: 369/369 tests pass (up from 368 — no regressions).

**Amended (2026-07-10):** `BuildOriginalGoalContextAsync` and its test were removed.
It pushed the full, unbounded root goal text into every fanned-out worker's kickoff
independently — real cost (duplicated per sibling, no size cap) for a fix aimed at one
specific class of defect (planner dropping a literal contract), and it was confusing
workers with root-goal context outside their own slice's scope. The Planner-prompt half
of the fix above (copy literal contracts verbatim into the slice's own goal/steps) stays
as the primary defense. The backstop moved to `AgentLoopPrompts.Reviewer` (step 5): when
a proposal's goal looks like a prose paraphrase that could be hiding a dropped literal
contract, the reviewer is instructed to walk `parentWorkUnitId` via `nm_v1_workunit_get`
up to the root on demand — pulled only when something in the diff looks suspect, rather
than pushed unconditionally to every worker.

### 3. Destructive merge-apply on multi-sibling fan-out — diagnosed, fix deferred (in progress)

**Root cause, fully confirmed by reading the code.** Two independent sibling slices
(neither depends on the other) landing on the same shared target branch — normal for
any multi-task fan-out goal — can silently clobber each other. `InMemoryMergeService
.ApplyAsync` lands every proposal via `FileSystemWorkspaceService.ApplyBranchAsync`,
which does a **destructive full mirror**: delete every file in the target absent from
the source, then copy the source over. Neither sibling's branch was ever refreshed with
the other's changes (`FanOutService.RefreshBranchFromDependenciesAsync` only refreshes
*declared* dependencies) — so whichever sibling's proposal is applied *second* mirrors
its own (older) view of the world onto the target, silently reverting whatever the
first one already landed for any file the second's own branch doesn't also carry
forward. In the live run this is exactly why the Subscription slice's `LineItem.cs`
change vanished after the TryClaimForFulfillment slice was applied afterward — despite
both work units and the top-level goal all correctly showing `Merged`.

This is the **same class of danger `FanOutService.RefreshBranchFromDependenciesAsync`'s
own comment already flagged** and specifically avoided by using additive `CopyFilesAsync`
instead — `InMemoryMergeService.ApplyAsync` just never got the same treatment.

**Why the existing safety net (`MergeReconciliationService`) didn't catch this.**
`WorkSchedulerService` already calls `MergeReconciliationService.TryReconcileAsync`
unconditionally on every worker completion — it is *not* gated by `ReviewPolicy`, and it
already does the right thing (additive `CopyFilesAsync`, plus real overlap detection via
`DetectOverlappingFilesAsync` — a two-pass check: cheap `FilesTouched`-set intersection,
then a genuine line-range diff against each proposal's own `base/{proposalId}` snapshot,
so two proposals editing different regions of the same file aren't falsely flagged).
But `TryReconcileAsync` only produces a combined proposal once *every* sibling has
reached `Proposed`/`Merged` *simultaneously*. In this run, two of the four slices
depended on a third — under `HumanRequired` review, the only way that dependency ever
reaches `Merged` is a human individually approving+applying it, which necessarily
happens *before* the dependent slices even exist to be reconciled with. Reconciliation
can never combine "all 4 as of one snapshot" when 2 of them don't exist yet when the
other 2 need approval — this is a structural conflict between dependency-gated fan-out
and single-combined-approval, not a bug in the reconciliation gate itself. Also worth
knowing: overlap detection compares proposals against each other, not against the
live target branch's current state — a residual gap in the same vein.

**Implemented.** `InMemoryMergeService.ApplyAsync` no longer calls the destructive
`ApplyBranchAsync` at all — a new `TryApplyAdditivelyAsync` reads each touched path's
`base/{proposalId}` (fork point), the proposal's own branch, and (when relevant — see
below) the current target, and:
- Categorizes each path as add/modify (copy) or delete (path present in base, absent in
  the proposal's branch) — never touches any file the proposal itself didn't change.
- Detects real conflicts by reusing `MergeReconciliationService`'s own line-range-overlap
  primitives (`LineDiffer.Diff` + interval overlap — extracted into a new shared
  `LineRangeConflictDetector` so both call sites use the identical logic, not a
  duplicate copy that could drift): diff `base/{proposalId}` against the *current*
  target to get drift ranges, diff it against the proposal's own branch to get the
  proposal's own changed ranges, and check whether they genuinely overlap. Both
  comparisons share the same "before" side, so the ranges are directly comparable with
  no coordinate translation.
- On a real overlap: throws (blocking the apply) and writes a `merge-conflict-report.md`
  onto the proposal's own branch — deliberately conservative for this first pass, no
  automatic rebase/resolution, a human resolves it using the diff editor that already
  exists (`GET /studio/merges/{id}/file-changes`).

**Scoped precisely to the actual bug, found by a real test failure, not by inspection.**
Drift-conflict checking only runs when the proposal's owning work unit has a
`ParentWorkUnitId` (i.e., it's a fan-out child landing on its parent's own branch — the
literal scenario this whole investigation is about). Running it unconditionally broke
`MultiRepoWriteBackTests` — two *independent* top-level goals for two entirely separate
repositories that happen to both target the literal branch name `"main"` (the ordinary
default, not a real relationship) — because write-back for a repo-backed goal always
sources from the proposal's own `SourceBranch` directly, never from whatever ends up on
the shared target, so a textual collision there isn't a real conflict for that case. Two
unrelated goals aren't "siblings" in the sense this fix cares about; scoping to
`ParentWorkUnitId is not null` is the precise, justified boundary.

Verified: `SiblingProposalApplyTests.cs` (new, 2 tests) reproduces the exact bug shape
directly — two fan-out siblings with genuinely disjoint files, applied one at a time,
confirming the second apply no longer reverts the first's file; and a second test with a
real overlapping edit on the same line of the same file, confirming it throws with a
clear message and leaves the first sibling's already-landed change intact rather than
partially applying. Full solution: 569/569 tests pass (up from 565 — no regressions,
including `MultiRepoWriteBackTests` once the fix was properly scoped).

## Remaining open items

- [x] ~~Confirm the leaked API key was fully deleted~~ — confirmed done.
- [x] ~~Wire `ProviderRetryAttempted` events and dead-letter history into the VS Code
  extension's UI~~ — done (see Phase 1.3 item 3 above): dead-letter entries group by
  work unit with an expandable history trail, and a session's `ProviderRetryAttempted`
  count shows inline on the card.
- [x] ~~Verify prompt caching actually works against a real call~~ — confirmed on the
  OpenAI-compatible path (DeepSeek): real cache-hit tokens observed on a live run.
  **Not yet confirmed for Anthropic or `vscode-lm`** — those paths are unverified, but
  no longer blocking anything (see the Continue-track note below).
- [x] ~~Extend `ProviderRetryAttempted` emission to `PlannerAgentLoop`/
  `ReviewerAgentLoop`/`DomainAgentLoop`~~ — done: all five loop types now take
  `IExecutionEventStream? events` and wire `OnTransientRetryAsync` into their
  `client.SendAsync` call the same way.
- [x] ~~Two-track failure/recovery design (Phase 1.4)~~ — fully implemented: `FailureKind`
  enum on `DeadLetterEntry`, `ReplanService` (re-plan-the-slice, used by both tracks),
  and `ContinueService` (continue-with-prior-context, Continue-track's other option) —
  see the "Continue-with-prior-context — implemented" subsection above. VS Code UI has
  Retry/Re-plan buttons; the Continue button is the one remaining wiring gap (below).
- **Extend prompt caching to the growing `messages` array** — still the bigger cost
  lever, still not attempted; lower priority now that elision/rolling-summary
  compaction already bounds the growing-history cost problem from a different angle.
- [x] ~~Verify the rolling-summary compaction path against one real, cheap call~~ —
  confirmed (Phase 4 item 1): a real `liveCompaction` run's per-cycle `InputTokens`
  show the exact drop signature the design predicts, right at the `SummaryTriggerCount`
  threshold, on two separate workers.
- ~~Extend `ConversationCompactor` to `PlannerAgentLoop`/`ReviewerAgentLoop`/
  `DomainAgentLoop`~~ — **dropped, not just deferred** (explicit call): their much
  lower iteration ceilings mean they're unlikely to ever need it: not worth carrying
  as an open item.
- [x] ~~Add logging to `ConversationCompactor`~~ — done. Both `ElideStaleToolResults`
  and `ApplyRollingSummaryIfDueAsync` now take optional `ILogger?`/`agentId`/
  `workUnitId` parameters (all nullable/defaulted — non-breaking for existing callers
  and tests) and log at `Information` when they actually fire: elision logs the count
  of tool results elided and total chars saved; rolling summary logs turns condensed,
  chars in the recap, trailing messages kept verbatim, and the summarization call's
  elapsed time. Rolling summary also logs a `Warning` when the summarization call
  returns unusable text (history is kept, not lost) and an `Information` note when it's
  due but has no assistant-aligned boundary to summarize up to yet. Wired from all four
  real call sites (`WorkerAgentLoop`/`OrchestratorAgentLoop`, each constructed from
  `InMemoryAgentRuntimeService` — passing its existing `_logger` — and from
  `ContinueService`, resolving `ILogger<ContinueService>` via DI) — so this no longer
  requires inferring from token-count curves the way confirming Phase 4 item 1 did.
  Verified: 6 new `ConversationCompactorTests` (a `FakeLogger` capturing level +
  formatted message) cover both the "fires" and "no-op, logs nothing" paths for each
  mechanism, plus the warning-on-empty-summary path. Full solution: 580/580 tests pass
  (up from 575 — no regressions).
- [x] ~~Implement the destructive merge-apply fix (Phase 4 item 3)~~ — done: additive
  apply + drift-conflict detection scoped to fan-out children, block+report (not
  auto-resolve) on a genuine conflict. Automated requeue/rebase on conflict is
  explicitly deferred — revisit once there's real signal on how often true conflicts
  actually occur, same "wait and see" pattern used for auto-triggering re-plan/retry
  earlier in this plan.
- [x] ~~Phase 3 item 3 (projection review)~~ — **substantially addressed, not a
  separate remaining exercise.** The original concern was whether a worker's scoped
  context is adequate at real task complexity; the Phase 4 planner-goal-context
  investigation found and fixed exactly that failure mode (a worker missing a literal
  contract because its own slice goal paraphrased it away) via
  `BuildOriginalGoalContextAsync` + the strengthened planner prompt. The narrower
  "does the default iteration budget still make sense given better slicing" question
  from the original wording wasn't separately re-litigated, but isn't blocking
  anything either — revisit only if a future run's iteration counts suggest it matters.
- [x] ~~Implement Continue-track: continue with more iterations and reconstructed
  prior context~~ — done (see the "Continue-with-prior-context — implemented"
  subsection above). Manual button only (mirroring Re-plan), not automatic — same
  "human decides, not the system" posture as every other failure-recovery action in
  this plan. VS Code "Continue" button is also wired up (dead-letter card, next to
  Retry/Re-plan).
- [x] ~~Phase 2 item 2 (file-workspace/merge-copy exploration)~~ — done: architecture
  reviewed (on-disk branch directory is the real source of truth; CAS+snapshot is a
  best-effort audit/reconstruction trail, not the other way around — confirmed, not
  assumed), three concrete gaps found and fixed (auto-resync after merge write-back,
  including a deeper pre-existing bug where the resync mechanism itself never
  advanced past first bootstrap; `WorkspaceCacheManager`'s multi-repo resolution;
  CAS dual-write logging) — see the Phase 2 section above for full detail. One
  further issue (unreviewed-proposal auto-write-back in
  `LocalFilesystemProjectionMaterializer`) found, confirmed dormant in current usage,
  and explicitly deferred — not part of this pass.
