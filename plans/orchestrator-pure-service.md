# Orchestrator → pure service; "Orchestrator profile" → "Default profile"

## Status

- [x] M1 — Concept swap: goal-level **Default profile** replaces "orchestrator credentials"
      (behavior-neutral rename + back-compat rehydration) — shipped 2026-07-13, 781/781 tests
      green (net +1: legacy-kind rehydration test). Landed as specified: identifier renames
      across 29 files (`GoalDefaultCredentials`, `GoalRoutingConfig`, `GoalCredentialRegistration`,
      `GetGoalDefaultCredentials`, `GetGoalDefaultProfileId`), `GoalRoutingV1` node kind with
      legacy-first read-both rehydration (current kind wins), additive `defaultProfileId` REST
      field beside `orchestratorProfileId`, and the CLI-provider baseUrl/apiKey resupply wart
      fixed (blank = ambient CLI auth; `HarnessReviewModeSeamTests` inline-route test now
      registers claude-cli with no placeholder and asserts registration succeeds).
- [x] M2 — Delete the orchestrator LLM loop; replace with the deterministic
      **GoalCoordinator** service (reconciliation work units included) — shipped 2026-07-13,
      780/780 tests green (net −1: the Projection-Diffing stall dead-letter test died with the
      stall detector it pinned). Landed as specified, with these notes:
      - `IGoalCoordinator` (Core) + `GoalCoordinator` (AgentRuntime): `StartGoalAsync` =
        `ConvergeAsync(ensurePlanner: true)`. The **ensurePlanner guard** is the one new invariant:
        planner enqueue fires only when a goal has no plan, no children, and no queue item, and
        only from goal start or *manual* reinvoke — an automatic sweep after a planner that
        legitimately produced no plan can never re-enqueue planners in a loop.
      - `SpawnAsync("orchestrator")` kept as an alias: registers Default-profile credentials
        (CLI providers now valid for it — the AP-6 restriction is gone server-side) and awaits
        `StartGoalAsync` **inline** (deterministic and fast, unlike the loop) — all four external
        call sites (extension, ExternalGoalTools, ReconciliationAgentService, experiments) work
        unchanged, and reconciliation units migrated for free.
      - `ReinvokeOrchestratorAsync` = optional credential-resupply + credential-free
        `ConvergeAsync` (new `ensurePlanner` param; REST manual endpoint passes true and lost its
        409/422 outcomes). The scheduler release path's duplicated inline sweeps were deleted —
        the reinvoke call IS the sweep now; the plan-stage fast-path fan-out and the
        reviewer-rejection special case stayed.
      - Decision log: coordinator records under stable id `goal-coordinator` (SpawnPlanner,
        AwaitReview/Escalate/NoOp reconciliation outcomes); fan-out keeps recording as `fanout`.
      - Stalled detector (REST) unchanged: it already keyed off pending queue items + any active
        agent under the whole root, both of which remain meaningful.
      - Test migration: AutonomousReviewTests/FullAgentCycleTests/SchedulerReinvocationTests now
        drive planner → fan-out → worker → review through the real scheduler (scripted handlers
        gained a planner branch; the orchestrator branches were deleted). FullAgentCycle's human
        flow is two-stage now: approve the child's proposal, then approve+apply the reconciled
        workspace proposal (the child's is consumed/Superseded by reconciliation — by design).
        Tests needing the queue must `agentRuntime.StartAsync` (the old direct-spawn flow
        didn't). `ScriptedLlmHandler` deliberately kept its dead orchestrator branch out of
        worker-only tests' way — untouched.
- [x] M3 — Extension: Agent Topology relabel, AP-6 gate deletion, credential-free Reinvoke —
      shipped 2026-07-13 (tsc + esbuild + webview-smoke all green). Notes:
      - Topology UI says **Default** everywhere ("Default Profile" column/label, "— inherit
        Default —"); the template wire field stays `orchestrator` so stored templates round-trip
        (per resolved question 1's read-both posture — display-only relabel client-side).
      - Built-in Default profile relabeled ("Default", still `id: 'orchestrator'`, still
        vscode-lm per resolved question 2).
      - ArtifactExplorerPanel's AP-6 CLI block deleted (any provider valid for the Default
        profile); `handleReinvokeOrchestrator` lost the profile picker — reinvoke is
        credential-free, sends resolvable Default-profile creds along only as a registry re-warm;
        button relabeled "↺ Reinvoke", stalled tooltip reworded to convergence-sweep language.
      - `defaultProfileId` read from GET /studio/goals with fallback to the legacy
        `orchestratorProfileId`.
      - No spawn-path change was needed: POST /studio/agents/spawn with agentType
        "orchestrator" is the kept alias (M2).
      - Pending manual smoke (do alongside M4's eval): create a goal with a claude-cli Default
        profile and nothing else configured → plan → fan-out → work → agent review → merge →
        workspace review → materialized, no API key anywhere.
- [x] M4 — Cleanup + the deferred gate. **Cleanup shipped 2026-07-13** (780/780 green):
      `AgentLoopPrompts.Orchestrator` deleted; the six "the orchestrator will fan out / apply /
      review" mentions in the Planner/Worker/Reconciler prompts reworded to "the runtime"; the
      seeded `orchestrator` pipeline profile relabeled "Default" with an empty system prompt
      (kept — it's the Default-profile credential anchor); parent plan's stale "C2/C3 not
      started" line corrected and AP-6 marked resolved-by-deletion.
      **Deferred gate cleared 2026-07-14 (Bradley, manual):** the claude-cli-Default-profile
      end-to-end (create a goal with a claude-cli Default profile and nothing else → plan → fan-out
      → work → agent review → merge → workspace review → materialized, no API key anywhere) ran
      successfully by hand. This one run satisfied both pending items at once — M3's pending smoke
      and the parent plan's harness-comparison gate — because a claude-cli-only topology *is* the
      comparison's CLI arm. Verified on semantic + process correctness; token/cost counting was
      unavailable in the manual run and wasn't the bar (it only fed the eval-driven routing tables,
      which stay out of scope). codex-cli has run live but not yet a full end-to-end — noted, not
      blocking.

Follow-up to `plans/harness-hosting-architecture.md` (Phases A–D + the Review-seam
follow-up complete as of 2026-07-13). This plan executes the "orchestrator LLM-loop →
pure service" direction that plan reserved (AP-6, "trends toward pure service rather
than LLM loop") and resolves its one remaining asymmetry: after this, **no role requires
an API-based profile** — a profile is just a profile — and the AP-6 orchestrator-CLI
restriction disappears by deletion rather than by putting the orchestrator behind the
executor seam.

## Motivation (verbatim intent from the driving conversation, 2026-07-13)

> "the orchestrator role requiring an API based profile, that doesn't make sense to me,
> a profile should be just a profile, for any role"
> "the next logical step so we can have a full end to end with any profile"

Two facts make this cheap rather than scary:

1. **The LLM's contribution is vestigial.** `OrchestratorAgentLoop.RunAsync`'s
   deterministic tail already does the real convergence (`TryFanOutFromPlanAsync`,
   the child rescue sweep, `TryReconcileAsync`, `TryEnqueueReviewerAsync`, the
   proposal-status-gated completion check), and `WorkSchedulerService` re-runs the same
   three sweeps on every lease release *before* reinvoking the orchestrator. The loop's
   own comment concedes it: "the LLM turn above routinely ends with a 'plan exists,
   fan-out is automatic' NoOp — the real convergence scan is these post-loop calls."
   What the LLM uniquely still does: enqueue the planner at goal start (with injected
   credentials), and write prose into the decision log.
2. **The credential half must survive — under an honest name.** The "orchestrator
   profile" today is doing double duty: powering the loop (dies) and serving as the
   goal's default credentials that every other role inherits (survives). Every
   `GetCredentialsForStage(...) ?? GetOrchestratorCredentials(...)` chain, every
   "— inherit Orchestrator —" option in Agent Topology, is really "fall back to the
   goal's default Model Profile." M1 renames the concept without changing a single
   fallback semantic.

Side benefits: "reinvoke" stops needing credentials at all (kills the
cold-registration-after-restart silent-no-op bug family — `orchestratorStalled`, the
recovery button's profile picker, `ReinvokeOrchestratorAsync`'s UnprocessableEntity
path); one fewer LLM loop to pay for and to flake; and the harness-comparison eval
gate (deferred until "actually run it and compare in full end to end") becomes runnable
with a CLI-only topology, no API simming.

## The replacement: `GoalCoordinator` (pure service)

One new service replacing `OrchestratorAgentLoop` + `StartOrchestratorLoop`:

- **On goal start** (today's `SpawnAsync("orchestrator", ...)` moment): register the
  goal's Default-profile credentials + per-stage overrides (exactly what
  `OrchestratorRegistration`/`OrchestratorRoutingConfig` capture today), then enqueue
  the planner via the scheduler with injected credentials — the same
  `nm_v1_scheduler_enqueue(profileId: "planner")` + `InjectSpawnCredentialsAsync`
  behavior, as plain code. The D2 planner-executor-selection branch
  (`IPlannerSelectionService`, only reachable when the Plan stage assignment is
  auto/unset) moves here verbatim.
- **On wake** (today's `ReinvokeOrchestratorAsync` moments — scheduler lease release,
  manual reinvoke, Continue): run the existing sweeps in the existing order —
  `TryFanOutFromPlanAsync(goal)`, the child rescue sweep (fan-out for Executing/Active/
  Waiting children with their own Plan artifacts), `TryEnqueueReadyDependentsAsync`,
  `TryReconcileAsync`, `TryEnqueueReviewerAsync`, then the reconciled-proposal-status
  completion check. All of these are idempotent today by design; the coordinator is
  just the one named home for the sequence instead of three copies (loop tail,
  scheduler release path, reinvoke).
- **Decision log**: the coordinator records `OrchestrationEvent`s with deterministic
  reasons ("Enqueued planner", "Fanned out N children from plan", "Reconciled into
  {proposalId} — awaiting workspace review", "Waiting on children: X, Y", "Conflict —
  escalated"). The loop's post-loop sweep already writes exactly these; what's lost is
  only the LLM's occasional free-prose NoOp narration. Timeline/Insights rendering of
  the decision log is unchanged.
- **No agent identity**: no `agentId`, no conversation log, no stall detector, no
  compactor, no MaxIterations, no dead-letter entries for orchestrator turns. The
  coordinator either succeeds, records a Conflict/Escalate decision, or throws into the
  caller's existing error handling.

**Stall semantics replacement.** Today `orchestratorStalled` means "root unit not
terminal, no live orchestrator agent, nothing parked." With no agent to be alive, the
detector becomes "root unit not terminal, nothing parked, no queue items, no active
children" — same UI badge, and the Reinvoke button simply runs a convergence sweep
(no credentials, no 422, no profile picker).

## Terminology decision

- **UI + docs**: "Default profile". Agent Topology's mandatory slot is relabeled
  `Orchestrator` → `Default`; the inherit option text "— inherit Orchestrator —"
  → "— inherit Default —".
- **Code**: `GoalDefaultCredentials` (record), `GetGoalDefaultCredentials`,
  `GoalRoutingConfig`, `StudioNodeKind.GoalRoutingV1`. "Orchestration" as a *stage
  name* (`PipelineStage.Orchestrate`, `OrchestrationEvent`, decision log) survives —
  coordination is still a real pipeline concept; it's the *agent* that goes away.
- **REST**: additive. New field names ship alongside the old for one release
  (`orchestratorProfileId` → also `defaultProfileId`; reinvoke body unchanged);
  old names removed after the extension is updated (M3 lands in the same release,
  so the dual period is within this branch).

## Complete touchpoint inventory

Everything the orchestrator and its profile touch, grouped by what happens to it.
(Sources: full-repo sweep 2026-07-13 — 355 hits/62 files in `src/`, 77/8 in the
extension, 273/40+ in `tests/`.)

### A. Credential identity — renamed, semantics frozen (M1)

| Touchpoint | Today | After |
|---|---|---|
| `ServiceContracts.cs:1125` `OrchestratorCredentials` record | currency of the whole credential system | `GoalDefaultCredentials` (pure rename) |
| `IAgentControlService.GetOrchestratorCredentials` (:1212) | goal default lookup | `GetGoalDefaultCredentials` |
| `GetCredentialsForStage` (:1219) | per-stage override lookup | unchanged |
| `GetAutoReviewProfileId`, `GetOrchestratorProfileId`, `GetEnabledDomainAgents` | read registration/routing | unchanged / `GetGoalDefaultProfileId` |
| `OrchestratorRegistration` (in-memory, with ApiKey) + `OrchestratorRoutingConfig` (persisted, no ApiKey) | captured at `SpawnAsync("orchestrator")` | `GoalCredentialRegistration` / `GoalRoutingConfig`, captured at goal start |
| `StudioNodeKind.OrchestratorRoutingV1` + `RehydrateOrchestratorRoutingAsync` | restart survival | **back-compat**: new kind `GoalRoutingV1`; rehydration reads BOTH kinds (old nodes = in-flight goals from before the upgrade) and writes only the new |
| `ResolveAndPersistCredentialsAsync` / `ResupplyCredentialsAsync` | shared resolve+persist; **requires non-blank baseUrl** (predates CLI providers) | rename; **fix the baseUrl gate here** — CLI providers register with null baseUrl (known cosmetic wart from the Review-seam work) |
| `IRuntimeCredentialCache` (CredentialRef flow) | ref-based resupply | unchanged |
| Fallback chains — `InlineReviewerService` (×2 incl. parent walk), `AutomatedReviewGateService` (×4), `FanOutService:124`, `ContinueService:86`, `DomainAgentTriggerService` (×2), `ReconciliationAgentService.ResolveCredentials` (×2), `WorkUnitCommandService.RequeueAsync` (×2), `OrchestratorAgentLoop.InjectSpawnCredentialsAsync` | `stage ?? orchestrator` inheritance | mechanical rename only; the loop's copy moves into `GoalCoordinator` |
| `StudioRestEndpoints.cs` credential DTOs (`ToCredentials()` :155, stage-credential map :1622, `ResolveRetryCredentials` :2999, requeue credential re-registration :2539) | construct/resolve `OrchestratorCredentials` | rename; wire shapes unchanged |
| `DeadLetterEntry` credential-resolution doc/order (creds → cache → "live orchestrator registry") | retry credentials | same order, registry renamed |
| `LlmProfileSelectionService`, `IPlannerSelectionService` signatures taking `OrchestratorCredentials?` | D2 planner routing | rename param type |
| `WorkspaceOptions:154` (per-goal option captured "at orchestrator spawn time the same way AutoReviewProfileId is") | goal-scoped options channel | captured at goal start |

### B. The LLM loop + its life-support — deleted or absorbed (M2)

| Touchpoint | Disposition |
|---|---|
| `OrchestratorAgentLoop.cs` (entire file: loop, stall detector, `ProjectionDelta` consumption, constraint/prompt-guidance folding, `InjectSpawnCredentialsAsync`, `RecordToolDecisionAsync`, post-loop sweeps, 26-tool `BuildAllTools`) | deleted; `InjectSpawnCredentialsAsync` + D2 branch + post-loop sweep sequence + decision recording move to `GoalCoordinator` |
| `InMemoryAgentRuntimeService`: `SpawnAsync`'s `agentType == "orchestrator"` branch (:707), `StartOrchestratorLoop` (:920), `ReinvokeOrchestratorAsync` (:825) | spawn branch → `GoalCoordinator.StartGoalAsync` (old agentType accepted as alias during migration — external callers exist); reinvoke → `GoalCoordinator.ConvergeAsync` (credential-free) |
| `AgentLoopPrompts.Orchestrator` (:11) | deleted. **Keep** the ~7 *mentions* of "the orchestrator" in Planner/Worker/Reconciler prompts but reword to "the runtime"/"the coordinator" (they describe who fans out / applies merges — still true, just not an agent) |
| Default `"orchestrator"` pipeline `AgentProfile` (`AgentProfileService:54`) | retired from defaults; `GET /studio/profiles` keeps serving it if user-customized (harmless). Its MaxIterations/SystemPrompt/AllowedTools have no consumer post-M2 |
| `ConversationCompactor` / `ConversationLogRecorder` "Orchestrator" role usage | no orchestrator turns exist; other loops unchanged |
| Orchestrator dead-letter entries (`InMemoryDeadLetterService`, `FailureKind`, retry endpoints) | no new entries of this shape; retry endpoint keeps handling pre-existing ones |
| `ProviderRetryAttemptedPayload` "orchestrator reliability" events from this loop | gone for orchestrator; other loops still emit |
| `McpToolDispatcher`/`SchedulerTools`/`WorkUnitTools` comments + `nm_v1_agent_spawn`'s "orchestrator" agentType | dispatcher tools unchanged (they're the *internal* surface other loops use); spawn tool stops accepting `orchestrator` (alias → StartGoal during migration) |
| `WorkUnit.cs:161` fan-out-parent / direct-spawn commentary, `StudioTask` "matched by the orchestrator" doc | comment updates |

### C. Wake/reinvoke machinery — converted (M2)

| Touchpoint | Disposition |
|---|---|
| `WorkSchedulerService` lease-release path (:345–404): `ResolveOrchestratorWorkUnitIdAsync`, the planner-item-on-own-unit special case, the three sweeps, `ReinvokeOrchestratorAsync` call | sweeps collapse into one `GoalCoordinator.ConvergeAsync(target)` call; target resolution logic kept as-is |
| `POST /studio/workunits/{id}/reinvoke-orchestrator` (:1647) + `ReinvokeOrchestratorBody` resupply | endpoint kept (URL kept for compat, new alias `/converge`); body's credential-resupply half becomes optional-and-rare (only needed to *change* the stored default profile); 409 already-active / 422 no-credentials responses both disappear |
| `orchestratorStalled` computation (:3946, :3978) + `orchestratorProfileId` in `GET /studio/goals` | stall detector re-specified (see above); `orchestratorProfileId` → `defaultProfileId` (additive) |
| `ContinueService` orchestrator-target handling | calls `ConvergeAsync` |
| `FindingDetectorService`, `GoalControlService:124` ("first profile that isn't orchestrator"), `ProjectionManager` mentions | GoalControlService picks "first non-Default role profile" — same intent, relabeled; others comment-level |

### D. Non-obvious spawn sites — migrated (M2)

| Touchpoint | Disposition |
|---|---|
| `ReconciliationAgentService:121` spawns `"orchestrator"`-type agents for reconciliation work units | reconciliation units become coordinator-driven too: `StartGoalAsync` on the reconciliation unit (its planner/worker children were already normal). Its `ITaskReconciliationTrigger`/`OrchestratorCredentials?` signatures rename per §A |
| `ExternalGoalTools` (external MCP `nms_v1` goal creation, :117–122) hardcodes `["orchestrator", workerProfileId]` + `agentType: "orchestrator"` | calls `StartGoalAsync`; profile list becomes `[defaultProfileId, worker]` |
| `ExperimentService` / `CounterfactualService` / `SteeringService` — multi-model comparison spawns 2 orchestrators with different models (Slice 18a) | semantics clarified to what they always meant: compare two **Default profiles** (two full pipelines with different goal-default credentials). Spawn calls → two `StartGoalAsync` calls |
| `InsightLlmAnalyzerService`, `DomainAgentLoop`, `ReplanService`, `PlannerSelectionService` references | rename/comment-level; ReplanService's orchestrator-target logic follows §C |

### E. Extension (M3)

| Touchpoint | Disposition |
|---|---|
| `modelAgentStudio.js` — topology editor: `t.orchestrator` mandatory slot (:368–400), `profileOptions(..., includeInherit)` with "— inherit Orchestrator —" (:310, :359), templates table column | relabel to **Default**; wire field name `orchestrator` kept in the template JSON for storage compat (display-only change), or migrated with a read-both shim — decide in M3 |
| `AgentConfigService.ts` — template type `{ orchestrator: string; ... }` (:43), inherit-fallback doc (:45–52), built-in `orchestrator` profile (:79), Default template (:84) | same relabel + keep-wire-name decision; built-in profile relabeled "Default" |
| `ArtifactExplorerPanel.ts` — **AP-6 CLI block (:1324–1329) deleted**; goal-start payloads `agentType: 'orchestrator'` (:1271, :1277, :1374) → new start-goal call; profile-readiness checks (:1316–1320) now validate the Default profile (CLI providers now pass); multi-model comparison (:1185–1277) per §D; `orchestratorStalled`/`orchestratorProfileId` session plumbing (:83–84, :435–444) → new names; `handleReinvokeOrchestrator` (:795) loses the profile-picker fallback (credential-free converge) |
| `WorkspaceDashboardPanel.ts` — `resolveOrchestratorCredentials` (:938) + requeue's "no Orchestrator profile configured" error (:530), `resolveReconcilerCredentials` falling back to `template.orchestrator` (:926), quick-spawn placeholders (:395, :454) | rename to Default-profile resolution; reconciler inherit-from-Default unchanged in behavior |
| `goalWorkspace.js` — stalled badge + tooltip (:138–143), Reinvoke button (:174–176, ArtifactExplorer :1930), orchestrator agent-id display (:1248) | badge/button kept, tooltip reworded ("run convergence sweep"); agent-id row shows coordinator decisions instead (decision log is unchanged, so mostly free) |
| `executionTimeline.js` / `InsightsPanel.ts` orchestrator-turn rendering | orchestrator *conversation* turns stop appearing (none exist); decision-log rendering unchanged |

### F. Tests (~273 hits / 40+ files)

Mechanical rename for the §A surface (`CredentialCacheAndRoutingRehydrationTests` is
the big one, 44 hits — gains a read-old-kind back-compat case). Loop-behavior tests
(`InMemoryAgentRuntimeServiceTests` orchestrator sections, `FanOutLlmHandler`'s
orchestrator scripting, `FullAgentCycleTests`, `ControlPlaneIdempotencyTests`) re-target
`GoalCoordinator` — most of what they assert (fan-out fired, reconcile ran, reviewer
enqueued, completion gated on proposal status) is exactly what the coordinator does, so
they get *simpler* (no scripted LLM turns to drive the routing). New tests: goal start
enqueues planner with injected default credentials; converge is idempotent; stalled
detector under the new definition; rehydration reads `OrchestratorRoutingV1` nodes.

## Slices

**M1 — credential concept swap (behavior-neutral).** Everything in §A: rename record/
methods/registration/routing, `GoalRoutingV1` node kind with read-both rehydration,
additive REST field names, fix the baseUrl-required resupply gate. Full suite green
with zero behavioral diffs (the point of doing it first: M2's diff then contains no
renames, only the loop swap).

**M2 — the loop swap.** §B/§C/§D: introduce `GoalCoordinator` (StartGoal + Converge),
port `InjectSpawnCredentialsAsync` + D2 branch + sweep sequence + decision recording,
delete `OrchestratorAgentLoop`/`StartOrchestratorLoop`, convert reinvoke + scheduler
release path, migrate the three non-obvious spawn sites, re-specify the stall detector,
keep `agentType: "orchestrator"` as a StartGoal alias. Suite green; the FullAgentCycle/
FanOut integration flows must pass **without any orchestrator LLM handler scripted**.

**M3 — extension.** §E: relabels, AP-6 gate deletion, start-goal call, credential-free
reinvoke, new REST field names. Manual smoke: create a goal with a **claude-cli Default
profile and nothing else configured** → plan → fan-out → work → agent review → merge →
workspace review → materialized, no API key anywhere.

**M4 — cleanup + the deferred gate.** Prompt rewording, stale-doc sweep (fix
`harness-hosting-architecture.md`'s stale "C2/C3 not started" status line while there;
mark AP-6 resolved-by-deletion), then run `plans/harness-comparison-eval.md` as a true
end-to-end on ≥2 harness topologies — the condition Bradley set for it.

## Open questions — all resolved 2026-07-13 (Bradley)

1. **Wire-name compat**: read-both/write-new confirmed; old names (`t.orchestrator`
   template field, `orchestratorProfileId` REST field, reinvoke URL) dropped by the
   end of this branch since extension and server ship together.
2. **Default profile stays mandatory** — "there's got to be the llm backing before
   starting a goal" — and its out-of-the-box value **defaults to vscode-lm**: a fresh
   install's built-in Default profile is `provider: vscode-lm` so a goal can start with
   zero credential setup inside VS Code. (The extension's built-in `orchestrator`
   profile is already `vscode-lm` — M3 preserves that under the new name; server-side
   goal start validates a resolvable Default profile exists before accepting.)
3. **Decision log**: clear, brief, deterministic reasons are the ideal — no prose
   narrator. Confirmed as specified.
4. **Experiments compare two *profiles*, full stop** — not "Default profiles"
   specifically; a profile is a profile. The §D framing is corrected: the comparison
   feature takes any two Model Profiles and runs the full pipeline under each; the
   Default-profile tie-in was only ever naming to make the topology relationship clear.
5. **`PipelineStage.Orchestrate` kept.** Confirmed.

## Reconciliation note (resolved same session)

Reconciliation work units migrate in M2 alongside goals — no separate slice. Where
reconciliation genuinely needs an LLM (authoring the reconciled result, not the
coordination around it), it uses the **Reconciler profile** (the existing topology
slot, which already inherits from Default when unset — see
`WorkspaceDashboardPanel.resolveReconcilerCredentials`). The coordination half
(spawning the reconciliation unit, sweeping its children, applying the reconciled
proposal) is `GoalCoordinator`, same as goals.
