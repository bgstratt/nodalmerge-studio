# Phase D implementation plan — plan ingestion; scheduler decomposes → coordinates

Child plan of `harness-hosting-architecture.md` Phase D. Written 2026-07-12 after code
recon (file:line refs verified that day, pre-refactor paths — the harness files are being
moved into `AgentRuntime/Harnesses/{ClaudeCode,Codex,Shared}/` folders; types are
unchanged). Slices sequential: D1 → D2 → D3.

## Status

- [x] D1 — `Plan` mode through the executor seam + plan.json contract + fold — shipped 2026-07-12,
      766/766 tests green (up from 758) — see "D1 implementation notes" below
- [x] D2 — Executor routing ("who plans this goal") via the profile selector — shipped 2026-07-12,
      769/769 tests green (up from 766) — see "D2 implementation notes" below
- [x] D3 — Plan-staleness / replan policy hardening — shipped 2026-07-12, 773/773 tests green
      (up from 769) — see "D3 implementation notes" below

## What the recon changed about D's shape

The parent plan treats plan ingestion as mostly-new machinery. It isn't:

- `PlanDocument`/`PlanSlice` (`Contracts/Domain/PlanDocument.cs`) already define the
  slice schema — `{sliceId, goal, fileScope[], dependsOn[], steps[]}` — and
  `PlanDocumentPaths.FileName = "plan.json"` already names the file.
- `FanOutService.ReadPlanFromArtifactAsync` already has a **fallback that reads
  `plan.json` directly off the parent branch** (`FanOutService.cs:165-174`) when no
  Plan artifact exists. An external harness that writes `plan.json` to the branch root
  is *already half-ingested* — what's missing is the mode plumbing, the contract schema,
  artifact normalization, and the trigger.
- `EnsureChildWorkUnitsAsync` already folds slices into WorkUnits with `dependsOn`,
  `fileScope`, `sliceId` (idempotent per sliceId) via
  `IOrchestratorService.CreateWorkUnitAsync`, and already auto-sequences overlapping
  FileScope siblings (`AutoSequenceOverlappingSiblingsAsync`). **The fold exists; D1
  reuses it unchanged.**

Two parent-plan open questions get answered here:

- **"Does PlannerAgentLoop survive?"** Yes — as `NativeHarnessExecutor`'s Plan mode.
  The Plan-stage branch in `RunScheduledWorkerAsync` (`InMemoryAgentRuntimeService.cs`
  ~327) moves behind the seam exactly like the worker branch did in B1; the native
  executor wraps `PlannerAgentLoop` for `Mode == Plan`.
- **"Does AP-4 extend to plan review?"** v1: no — plans auto-fold, same as native plans
  do today (they're not reviewed either; the execution outputs they produce still hit
  the AP-4 gate). Plan review becomes a `ReviewPolicy` knob later if eval data says
  harness plans need it. Don't build the gate now; don't block it either.

## D1 — Plan mode end-to-end

### D1.a Seam plumbing

- Add `Plan` to `HarnessMode` (additive enum growth — the whole reason it's an enum).
- Move the Plan-stage branch in `RunScheduledWorkerAsync` behind the seam: resolve the
  executor the same way the worker branch does (provider-driven,
  `ResolveExecutorName`), build a `HarnessRunRequest` with `Mode = Plan`. Gate: if the
  resolved executor's `Capabilities.SupportsPlanningMode` is false, fall back to
  native (never fail the item over a capability miss).
- `NativeHarnessExecutor`: `Mode == Plan` → construct `PlannerAgentLoop` (move the
  construction from the runtime service, preserving the `needsExecuteFallback`
  behavior — a Succeeded plan run with no recorded Plan artifact hands off to Execute).
  That fallback check stays in the caller (it's scheduler behavior, not executor
  behavior).
- Relax the CLI-provider hard block (`InMemoryAgentRuntimeService.cs:293-306`) for
  Plan stage only, keyed on the resolved executor's `SupportsPlanningMode` — Review
  stays blocked. Mirror the change in the extension's validation
  (`ArtifactExplorerPanel.ts` — planner role becomes assignable to CLI profiles;
  reviewer/orchestrator still rejected).

### D1.b Contract + external executors

- `WorkspaceContractPlan` DTO in `NodalMerge.Studio.Contracts` mirroring
  `PlanDocument` (slices with sliceId/goal/fileScope/dependsOn/steps), documented in
  `docs/contracts/workspace-contract-v1.md` as the additive plan.json entry its line
  147 already reserves. JSON canonical; the contract doc's existing principles apply
  (additive-only, unknown fields ignored).
- CLI executors on `Mode == Plan`:
  - Different kickoff prompt: read `.workspace/goal.md`/`state.md`, **write
    `.workspace/plan.json` conforming to the schema, implement nothing** (settings
    allowlist for a plan run: Read everywhere in workdir, Write only
    `.workspace/plan.json` — no Bash, no Edit).
  - Different harvest: no diff→merge.propose (a plan run must produce no code
    changes; a non-empty diff is recorded as a warning event and discarded), no
    build/test gate. Instead: parse `.workspace/plan.json` → validate against
    `WorkspaceContractPlan` (bad JSON/schema → run fails with a clear reason, native
    replan can pick it up) → record an `ArtifactType.Plan` artifact (normalized
    `PlanDocument` JSON, same shape `ArtifactRecordPlan` writes) → the existing
    orchestrator/fan-out path folds it with zero changes. Keep `decisions/` + `inbox/`
    harvest active (planners record research/decisions too).
  - `HarnessHarvestPipeline` (shared) gains the mode split so both adapters get it.
- Flip `SupportsPlanningMode: true` on ClaudeCode and Codex once wired (writing a JSON
  file is within both CLIs' verified abilities; no new CLI features are assumed).

### D1.c Trigger

Who enqueues a Plan-stage item pointed at an external executor: no new machinery —
it's the existing scheduler path (`GoalControlService`/`FanOutService` enqueue with a
profileId; a Plan-stage pipeline profile whose role's Model Profile is a CLI provider
now routes there). The orchestrator's own post-loop `TryFanOutFromPlanAsync` call and
rescue sweep are untouched.

### D1 acceptance

- Stub-CLI test (both adapters): Plan-mode run writes a valid plan.json → Plan
  artifact recorded → `FanOutService` creates children with dependsOn/fileScope →
  children enqueued. Mirror with an invalid plan.json → run fails cleanly, no
  children, no artifact.
- A Plan-mode run that also edited a source file: diff discarded, warning event
  emitted, plan still folds.
- Native path regression: scheduler-driven Plan item still runs PlannerAgentLoop via
  the seam with identical outcomes (including needsExecuteFallback).
- Full suite green; plan docs updated (this file + parent status + contract doc).

### D1 implementation notes (shipped 2026-07-12)

766/766 tests green (up from 758; 8 new tests: 3 in `ClaudeCodeExecutorPlanModeTests`, 3 in
`CodexCliExecutorPlanModeTests`, 2 in `NativePlanModeSeamTests`, all in
`tests/NodalMerge.Studio.Integration.Tests`). `DomainAgentConfigAndFeedbackRestTests`'
`Executors_endpoint_lists_both_registered_executors_with_capabilities` needed its two
`supportsPlanningMode: false` assertions flipped to `true` — expected mechanical fallout of D1.b's
capability flip, not a design deviation. No dev Studio Host lock was hit this session.

**D1.a — seam plumbing.** Landed as specified, with the "ResolveExecutorName" → `ResolveForProvider`
rename the task's structural-context note already called out. `HarnessMode.Plan` added
(`HarnessExecutorContracts.cs`). `NativeHarnessExecutor.RunAsync` branches on `request.Mode`:
`Plan` constructs `PlannerAgentLoop` from the request's fields and returns its completion directly;
everything else is the unchanged `WorkerAgentLoop` path. `NativeHarnessExecutor.Capabilities
.SupportsPlanningMode` flips to `true` — it's both the Mode==Plan implementation *and* the
capability-miss fallback target for a CLI executor that hasn't wired planning mode, so it has to
actually support it. `InMemoryAgentRuntimeService`'s Plan-stage branch now builds the same combined
constraints/prompt-guidance/engineering-state context it always did, resolves an executor via
`ResolveForProvider(provider, profile?.Executor)`, falls back to `Resolve("native")` when
`Capabilities.SupportsPlanningMode` is false, and runs it through the seam — `needsExecuteFallback`
(a Succeeded plan run with no recorded Plan artifact hands off to Execute) stayed exactly where it
was, in the caller, per the plan's explicit instruction. The CLI-provider hard block
(`InMemoryAgentRuntimeService.cs`, originally gating `PipelineStage.Plan or PipelineStage.Review`)
now only gates `Review` — Plan runs through the seam unconditionally and degrades on a capability
miss rather than failing loudly. `ArtifactExplorerPanel.ts`'s mirrored validation now allows
`stage === 'Execute' || stage === 'Plan'` for a CLI provider; the error message and its comment were
updated to say "Worker (Execute) and Planner (Plan)".

**D1.b — contract + external executors.** `WorkspaceContractPlan`/`WorkspaceContractPlanSlice`
added to `WorkspaceContract.cs`, field-for-field mirror of `PlanDocument`/`PlanSlice` including the
same `JsonPropertyName` values — kept as a distinct type from `PlanDocument` (not a reuse) because
one lives in the versioned, externally-implementable Workspace Contract surface and the other is
free to evolve as Studio's internal planning-scheduler shape changes; see the type's own doc
comment. `HarnessHarvestPipeline` gained a `Mode` split: the existing decisions/inbox harvest was
extracted into a shared `HarvestDecisionsAndInboxAsync` helper (byte-identical behavior, just
callable from both branches); `HarvestAsync` (Execute) is otherwise unchanged; a new
`HarvestPlanAsync` (Plan) skips diff→merge.propose and the build/test gate entirely. Diff-outside-
`.workspace/` detection reuses `IFileWorkspaceService.DiffAsync(branchId, "main")` — it already
excludes dot-hidden paths (Phase A.5), so `.workspace/plan.json` never shows up in it; any other
non-empty diff is logged, recorded as a new `HarnessPlanDiffDiscarded` execution event (payload:
`HarnessPlanDiffDiscardedPayload(AgentId, DiffSummary)`, keyed by `request.SessionId` — the Studio
session, not the harness's own transcript session/thread id, matching
`EmitPermissionDenialEventsAsync`'s existing convention), and discarded — never proposed. `.workspace
/plan.json` is read, deserialized as `WorkspaceContractPlan` (`PropertyNameCaseInsensitive`), and
validated (non-null, non-empty `Slices`, every slice has a non-empty `SliceId`/`Goal`) before being
re-serialized as a `PlanDocument` and recorded via `IArtifactCommandService.RecordPlanAsync` — the
exact shape `FanOutService.ReadPlanFromArtifactAsync` already reads, so the fold needed zero changes,
confirmed by the fan-out assertions in the new stub-CLI tests. Both `ClaudeCodeExecutor` and
`CodexCliExecutor` gained a `Mode == Plan` kickoff prompt (decompose, write `.workspace/plan.json`
matching the schema, implement nothing) and flipped `Capabilities.SupportsPlanningMode` to `true`.
`ClaudeCodeExecutor.WriteSettingsFileAsync` additionally takes the run's `HarnessMode` and generates
a narrower allowlist for Plan (`Read` everywhere in the workdir, `Write` scoped to exactly
`.workspace/plan.json`, no `Edit`, no `Bash`) — advisory, same posture as the Execute allowlist
always was. `CodexCliExecutor` has no per-path settings mechanism to narrow (confirmed in Phase C:
its own sandbox flag is coarse-grained and Studio doesn't treat it as the isolation boundary
anyway), so its Plan-mode enforcement is the kickoff prompt plus the harvest-level diff-discard
backstop — the same "the gate is the correctness mechanism, not the CLI's sandbox" posture
`CodexCliExecutorOptions.SandboxMode`'s own comment already documents.

**D1.c — trigger.** Verified via `NativePlanModeSeamTests` and the two `*PlanModeTests` files: no new
scheduler machinery was needed. `NativePlanModeSeamTests` additionally proves a genuinely new thing
no prior test covered — a scheduler-driven Plan item (`IWorkScheduler.EnqueueAsync` +
`PollSchedulerAsync`'s background hosted-service loop, the same queue-driven site
`HarnessExecutorSeamIntegrationTests` exercises for Worker/Execute) reaching `RunScheduledWorkerAsync`
end-to-end for the Plan stage; the pre-existing `PlannerHandoffRoutingTests` simulates the
acquire/record/release cycle by hand and never actually calls the seam.

**Discovered, relevant to D2/D3:**
- `PlanDocumentPaths.FileName` ("plan.json") is unused by the CLI-adapter path added here — the
  contract fixes the path at `.workspace/plan.json`, distinct from `FanOutService`'s pre-existing
  bare-root-`plan.json` fallback (`ReadPlanFromArtifactAsync`'s second branch, still there for a
  native planner that writes the file directly instead of using `ArtifactRecordPlan`). D1 never
  needed that fallback — the harvest step records the artifact itself — but it's worth noting the
  two `plan.json` locations are not the same file for whichever future work touches that fallback.
- `HarnessHarvestPipeline` now takes `IFileWorkspaceService` and `IArtifactCommandService` as
  constructor dependencies, alongside the pre-existing `IExecutionEventStream?` optional parameter —
  a fourth adapter gets Plan-mode harvest for free by construction, no extra wiring.
- D2's "who plans this goal" routing can now genuinely select between native and either CLI
  adapter for a Plan-stage spawn, since all three actually run Plan mode — D1 was a real
  prerequisite, not just plumbing.

## D2 — Executor routing ("who plans this goal")

The real selector is `IProfileSelectionService` (`LlmProfileSelectionService`) — the
parent plan's "`IAgentProfileSelectorService` (Slice 9d)" name is stale; note that in
the parent doc when D2 lands. Today it picks an **Execute-stage profile** for fanned-out
children (`FanOutService.EnqueueChildWorkerAsync:467`), deterministic FileScope-pattern
tier first, LLM tier opt-in (`UseLlmProfileSelection`, default off).

- Extend the selection result to carry provider/executor, and add a planner-selection
  entry point ("which profile+provider plans this goal") consulted where the Plan-stage
  item is enqueued — only when the role's topology assignment is `auto`/unset.
  **The manual Agent Topology assignment stays the explicit override** (parent plan's
  D2 note): a role with a concrete Model Profile assigned skips the selector entirely.
- Routing inputs the parent plan wants (task-type/size) don't exist as data yet —
  v1 routes on what's available (FileScope patterns, goal-text heuristics, the same
  tiers the Execute selector uses). Comparison-eval-driven routing tables are
  explicitly out of scope until the eval runs.
- Off by default, same as `UseLlmProfileSelection`.

### D2 acceptance

Deterministic tier test (planner profile selected by pattern), override test (explicit
topology assignment bypasses selector), default-off test.

### D2 implementation notes (shipped 2026-07-12)

769/769 tests green (up from 766; 3 new tests, all in `PlannerExecutorRoutingTests`,
`tests/NodalMerge.Studio.Integration.Tests`).

**Where "Plan-stage items get enqueued" actually is.** There's no dedicated scheduler-shaped
call site for this — the production path is `OrchestratorAgentLoop` deciding, via its own LLM
turn, to call the `nm_v1_scheduler_enqueue` tool with `profileId="planner"`. That tool call
already ran through `InjectSpawnCredentialsAsync` (renamed from the synchronous
`InjectSpawnCredentials` — D2 needed it async to consult the new selector) before dispatch,
which already resolves `IAgentControlService.GetCredentialsForStage(workUnitId, PipelineStage
.Plan)` — the Agent Topology per-stage override — and overwrites model/baseUrl/apiKey/provider
when it's set. That's the existing "auto/unset" signal the parent plan's D2 note points at:
`stageCreds is null` *is* "topology assignment auto/unset" for the Plan role, no new state
needed. The new planner-selection branch sits right before the existing credential-overwrite
lines, gated on `stage == PipelineStage.Plan && stageCreds is null && plannerSelection is not
null`, and only ever changes `dict["profileId"]`/the resolved provider — never runs when an
explicit override exists, never runs for Execute/Review tool calls. `IWorkScheduler.EnqueueAsync`
called directly (bypassing the orchestrator LLM turn entirely) — used by
`NativePlanModeSeamTests` and `IReplanService` — does **not** go through this hook; see
"Discovered, relevant to D3" below.

**Selection result + new interface.** `ProfileSelectionResult` (`ServiceContracts.cs`) gained an
optional trailing `string? Provider = null`. Every existing producer (FanOutService's
deterministic FileScope tier, `LlmProfileSelectionService`'s heuristic/LLM tiers) never sets it,
so it's `null` everywhere pre-D2 code runs — FanOutService.EnqueueChildWorkerAsync needed zero
changes and its behavior is unchanged byte-for-byte. A new `IPlannerSelectionService` (parallel
interface, not an overload on `IProfileSelectionService` — the Plan-stage candidate pool,
prompt, and heuristic default profile id are all different enough that sharing one method would
mean a stage parameter and Execute-only assumptions creeping into a shared abstraction) declares
one method, `SelectPlannerAsync(WorkUnit goalUnit, OrchestratorCredentials?, ct)`, implemented by
`PlannerSelectionService` (new file, `AgentRuntime/PlannerSelectionService.cs`). Unlike the
Execute path — where the deterministic FileScope tier lives in the caller (FanOutService) and
only the LLM/heuristic tier lives in the selection service — both tiers live inside
`PlannerSelectionService` here, because there's no fan-out-shaped caller to split them across;
`OrchestratorAgentLoop` just wants one answer. The deterministic tier reuses
`AgentWorkspaceService.MatchesGlob` (Storage project, already a public static method
FanOutService itself calls) against Plan-stage profiles' `FileScopePatterns`; the LLM tier
mirrors `LlmProfileSelectionService`'s prompt/parse/timeout shape almost verbatim, scoped to
Plan-stage candidates and goal text instead of a child work unit's. Provider is derived from the
selected profile's `Executor` field resolved through the already-registered
`IHarnessExecutorResolver.Resolve(...).ProviderKey` — the same channel every other
executor-routing decision in the codebase rides (native profiles resolve to `null`, meaning "no
override").

**Off-by-default, twice over.** `WorkspaceOptions.UsePlannerExecutorSelection` (new, default
`false`) gates the entire service — when off, `SelectPlannerAsync` returns the heuristic
("planner", no provider) without even listing agent profiles, mirroring
`UseLlmProfileSelection`'s own posture but gating the deterministic tier too (unlike the Execute
path, where the deterministic tier is unconditional and only the LLM fallback is flag-gated —
planner routing is a bigger behavior change than picking among Execute-stage workers, so the
whole feature stays opt-in for v1). Independently, `OrchestratorAgentLoop`'s new constructor
parameter `IPlannerSelectionService? plannerSelection = null` defaults to null, and its one
production construction site (`InMemoryAgentRuntimeService.StartOrchestratorLoop`) resolves it
via `GetService` (not `GetRequiredService`) — so even a caller that forgot to wire the flag still
can't reach the new branch structurally. `Toggle_off_by_default...` test locks in that with the
flag off, the enqueued `ScheduledItem.ProfileId`/`Provider`/`Model` are identical to what the
pre-D2 code would have produced, in the presence of a profile that *would* have matched the
deterministic tier had it been consulted.

**Test approach.** `PlannerExecutorRoutingTests` drives `OrchestratorAgentLoop` for real via the
existing `PlannerEnqueueOnlyLlmHandler` fixture (scripts the orchestrator's LLM turn straight to
`nm_v1_scheduler_enqueue(profileId="planner")`), then inspects `IWorkScheduler.ListPendingAsync()`
for the resulting `ScheduledItem`. Deliberately never calls `IAgentRuntimeService.StartAsync()` —
that starts the background scheduler poll loop, which would race to dequeue (and mutate/remove)
the very item the assertions need to inspect; `agentControl.SpawnAsync("orchestrator", ...)`
doesn't depend on the poll loop (it starts the orchestrator's own loop via `Task.Run` directly),
so skipping `StartAsync()` is safe and removes the race entirely, at the cost of this suite never
exercising an actual Plan-stage *run* — only the enqueue decision, which is D2's whole scope.

**Discovered, relevant to D3:**
- `IReplanService` and `NativePlanModeSeamTests`-style direct `IWorkScheduler.EnqueueAsync(...,
  "planner", ...)` calls bypass `OrchestratorAgentLoop.InjectSpawnCredentialsAsync` entirely —
  they never route through the new selector regardless of the flag, because there's no LLM tool
  call in between to intercept. D3's own text already flags "route ReplanService's planner spawn
  through the seam" as in-scope there; worth widening that note to say the planner-selection hook
  itself also needs a call site inside `ReplanService` (or a shared helper both callers use) if
  D3 wants replan spawns to honor `UsePlannerExecutorSelection` too — today they never will, flag
  or not.
- `ProfileSelectionResult.Provider` is genuinely generic (lives on the record every producer
  shares), so if a future slice wants FanOutService's Execute-stage child routing to also carry a
  provider (letting a child worker route to a CLI executor by FileScope pattern, not just by
  profile id), the plumbing on the result type is already there — only FanOutService's own
  `_scheduler.EnqueueAsync` call would need to start reading `selection.Provider`.

## D3 — Plan-staleness / replan hardening

`ReplanService` today: manual-only (REST/MCP dead-letter triggers), re-plans a failed
slice's parent with a fresh `PlannerAgentLoop`, folds additively (new sliceIds only),
cancels the failed slice, force-releases its leases — and does **not** invalidate
siblings.

D3 keeps the "don't build the planning scheduler early" discipline; concretely:

- Route ReplanService's planner spawn through the seam (it constructs
  `PlannerAgentLoop` directly today — after D1 it should resolve an executor like
  everything else, so an external harness can re-plan too). This also un-couples
  "a plan exists" from "the native orchestrator produced it" (the parent plan's
  explicit warning).
- Define staleness *signals* only (no auto-replan): plan artifact older than N
  superseding decisions on the same work-unit chain, or M dead-lettered slices from
  one plan — surfaced as an execution event / dashboard flag, human decides. Wire the
  existing manual replan triggers to show the signal. Automatic replan stays deferred
  (same reason as orchestrator-reliability plan 1.4b: RetryWithContextAsync bypasses
  failure caps; needs that verified safe first).
- Sibling invalidation stays out of scope (additive folding is the current, working
  semantic; changing it is a design decision for the planning-scheduler future, not a
  slice).

### D3 acceptance

Replan through the seam with a stub CLI planner (full replan cycle test); staleness
signal event emitted in a constructed scenario; no auto-replan behavior anywhere.

### D3 implementation notes (shipped 2026-07-12)

773/773 tests green (up from 769; 4 new tests: 1 in `ReplanExecutorSeamTests`, 2 in
`PlanStalenessSignalTests`, both in `tests/NodalMerge.Studio.Integration.Tests` — the existing
`ReplanServiceIntegrationTests` test already covers "flag off + no topology = native, byte-
identical," unmodified and still green). Known parallel-run flakes confirmed unrelated by
isolated rerun: `NativePlanModeSeamTests`, `ClarificationWorkflowTests`,
`CandidateBranchConflictTests` — none touch ReplanService/ArtifactCommandService/
InMemoryDeadLetterService, and each passes cleanly alone.

**Replan through the seam.** `ReplanService.ReplanFailedSliceAsync` no longer constructs
`PlannerAgentLoop`/`DefaultAgentToolClient` directly — it resolves an executor via
`IHarnessExecutorResolver.ResolveForProvider(provider, profile?.Executor)` (falling back to
`Resolve("native")` on a capability miss, mirroring D1.a's Plan-stage branch exactly) and runs a
`HarnessRunRequest(HarnessMode.Plan, ...)` through it, dropping the dispatcher/llm/
conversationLog locals entirely — `NativeHarnessExecutor`/`ClaudeCodeExecutor`/`CodexCliExecutor`
each resolve their own collaborators from the request, same as every other seam call site.
`provider`/`profile` resolution follows D2's discovered precedence: `stageCreds =
agentControl.GetCredentialsForStage(parentWorkUnitId, PipelineStage.Plan)` non-null is "topology
assignment explicit" (D2's own discovery note) and wins outright — `provider = stageCreds
.Provider`, the new `IPlannerSelectionService` call site is skipped entirely. When `stageCreds` is
null, a new call site (`serviceProvider.GetService<IPlannerSelectionService>()`, mirroring
`OrchestratorAgentLoop.InjectSpawnCredentialsAsync`'s hook) consults
`SelectPlannerAsync(goalUnit, creds, ct)`; `selection.Provider` is null whenever
`WorkspaceOptions.UsePlannerExecutorSelection` is off (the default) or the heuristic tier fired,
in which case `provider`/`profile` are left exactly as they were pre-D3 (`provider ??= creds
.Provider`, profile stays null) — the pre-existing `ReplanServiceIntegrationTests` test (flag off,
no topology override) still passes completely unmodified, confirming the "byte-identical" acceptance
criterion structurally rather than by writing a near-duplicate test. `ReplanExecutorSeamTests`
exercises the other path: an explicit Plan-stage Agent Topology assignment naming `claude-cli`
routes the replan to a stub CLI (same fixture shape as `ClaudeCodeExecutorPlanModeTests`), which
writes `.workspace/plan.json`, and the resulting two-slice plan folds through the completely
unmodified `FanOutService` path, cancelling the original failed slice and creating two named
siblings — proving "a plan exists" is now decoupled from "the native orchestrator produced it," the
parent plan's explicit D3 warning.

**Staleness signals.** New `IPlanStalenessService` (Core `ServiceContracts.cs`, implemented by
`PlanStalenessService` in `NodalMerge.Studio.Storage` — placed there, not AgentRuntime, because
both hook points it's called from live in Storage and Storage doesn't reference AgentRuntime) has
two `Notify*` entry points wired into the two checkpoints the task named: `ArtifactCommandService
.RecordAsync` calls `NotifySupersedingDecisionRecordedAsync(recorded)` right after recording any
artifact (no-ops unless it's a `Decision` with a non-empty `Supersedes`), and
`InMemoryDeadLetterService.RecordFailureAsync` calls `NotifySliceDeadLetteredAsync(workUnitId)`
right after the work unit's status update to `DeadLettered` (best-effort — the pre-existing
`catch (InvalidOperationException) { }` around that update is untouched, so an illegal transition
still doesn't block dead-lettering, it just means the staleness count for that unit doesn't budge
either, same as it wouldn't have shown as dead-lettered to a human reading WorkUnit status anyway).
Both are optional/nullable constructor parameters (`IPlanStalenessService? planStaleness = null`),
same convention as every other optional collaborator in these two services, registered as a real
singleton in `ServiceCollectionExtensions.AddRehydratableServices` ahead of both consumers (DI
resolves lazily by type, not registration order, so this is documentation, not a requirement).

"Same work-unit chain" (the task's phrase) resolved to a concrete, cheap scope after reading how
the two signals' own data is shaped: the plan owner (the work unit `RecordPlanAsync` was called
against) plus its *immediate* fanned-out children — one level, matching
`NotifySliceDeadLetteredAsync`'s own sibling scope exactly (dead-lettered slices are always
immediate children of the plan owner; `ReplanService`'s own additive-fold model never nests a
plan's children further). `CountSupersedingDecisionsAsync` walks that one-level set and sums
`IArtifactLineageService.GetChainAsync` hits per work unit, filtered to `Decision` artifacts with
`Supersedes.Count > 0` and `CreatedAt >= plannedAt` (`>=` not `>` — deliberately: a decision
recorded in the same clock tick as the plan still counts, there's no meaningful ordering claim to
make at sub-millisecond granularity either way). Finding "the plan" for a decision walks WorkUnit
ancestors (`ParentWorkUnitId`) from the decision's own `OwnedByWorkUnitId` for the nearest
self-owned `Plan` artifact — mirrors `ArtifactCommandService.CollectChainWithAncestorsAsync`'s own
walk. `GetStateAsync(planOwnerWorkUnitId)` is the on-demand read the manual replan triggers use;
`ReplanService` calls it once per `ReplanFailedSliceAsync` invocation (before the replan attempt,
so the snapshot reflects "why this may be worth replanning" regardless of the outcome) and attaches
it to every returned `ReplanResult` that has a resolved parent work unit (`NotFound`/`NotApplicable`
leave it null — there's no plan owner to report on). The REST endpoint and the internal
`DeadLetterTools.ReplanAsync` MCP tool both already serialize the whole `ReplanResult`, so the new
field rides along for free; `ExternalGoalTools.GoalRecoverAsync`'s `replan` action built its own
anonymous response object and needed one line added (`stalenessSignal = result.StalenessSignal`) on
the success branch only — failure branches there were already terse error objects with no room
for extra context, matching the REST/internal-MCP tools' own posture.

New `ExecutionEventKind.PlanStalenessSignalRaised` + `PlanStalenessSignalPayload` follow the
`HarnessPlanDiffDiscarded` pattern the task named, with one deliberate deviation: `AppendAsync`
requires a non-null `sessionId`, but neither hook point has a live Studio session (a decision or
dead-letter can originate from any agent's own session, not a fixed one) — `PlanWorkUnitId` is
used as the `sessionId` too, so `GetSessionEventsAsync(planWorkUnitId)` finds it in addition to the
session-agnostic `GetEventsByKindAsync`, and `ExecutionEventStreamService` places no constraint on
`sessionId` being a real session (confirmed by reading it — it's a free-form index key).

**No auto-replan, verified structurally, not just by absence.** `IPlanStalenessService`'s two
`Notify*` methods return `Task`, take no scheduler/`IWorkScheduler` dependency, and
`PlanStalenessSignalTests.Dead_lettered_siblings_at_threshold_raise_the_signal_but_one_below_does_not`
asserts `(await scheduler.ListPendingAsync())` is empty after the threshold-crossing dead-letter
that raises the event — proving the signal path genuinely cannot enqueue anything, not just that
this particular test run happened not to observe an enqueue.

**Thresholds.** `WorkspaceOptions.PlanStalenessSupersedingDecisionThreshold` (default 3) and
`PlanStalenessDeadLetteredSliceThreshold` (default 2), both plain `int` opt-in-by-magnitude knobs
(no separate on/off flag needed — the signal is passive, so there's no behavior to gate the way
`UsePlannerExecutorSelection` gates an active routing change).

**Deviation from the task's literal wording.** The task said "hook the cheapest correct spot" for
both signals without naming them exactly; "promoted superseding decisions" in the task prose is
read here as "recorded" (there's no separate promotion/approval step for a `Decision` artifact in
the current model — `ArtifactStatus.Active` is the only status a freshly recorded one gets), not a
distinct `ArtifactStatus.Approved` gate.

**What's still open for Phase E / the orchestrator-as-service future.** Sibling invalidation
(explicitly out of scope here, per the plan) — a replanned slice's siblings are never told their
plan changed; a human reading the staleness signal is the whole mitigation for now. Auto-replan
itself stays deferred pending the same safety verification `RetryWithContextAsync`'s failure-cap
bypass needs (unchanged from the task's own framing). The staleness *count* is currently
recomputed on every `Notify*`/`GetStateAsync` call (no caching) — fine at today's fan-out sizes
(a handful of siblings per plan) but would need memoizing if a future slice makes plans
meaningfully wider. `IPlanStalenessService` has no REST/MCP surface of its own yet beyond riding
`ReplanResult` — a future dashboard widget wanting "list every currently-stale plan across the
workspace" would need a new query method (`GetStateAsync` is single-work-unit only today).

## Out of scope for D (explicitly)

- Orchestrator coordination via harness — never (AP-6). The orchestrator's LLM→service
  evolution is its own future plan; D only moves *decomposition authorship*.
- Review-mode delegation, plan review gates, sibling invalidation, routing tables from
  eval data, spawn-time advisory claim pre-acquisition (FanOutService's overlap
  auto-sequencing already covers the collision-cost case; revisit with the multi-dev
  topology work).
