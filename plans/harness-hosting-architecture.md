# Harness hosting — external CLI harnesses as pluggable executors

## Status

- [x] Phase A — Engineering State projection + `.workspace/` contract (no harness dependency) —
      implemented 2026-07-12 (A1–A5 all landed; full solution test suite green, 707/707)
- [x] Phase B — `IHarnessExecutor` seam + Claude Code CLI adapter — B1/B2/B3 implemented
      2026-07-12 (722/722 tests green); both items originally deferred out of B3
      (`--add-dir` for cross-repo work units, `--resume`/session-id persistence for steering) were
      closed the same day, 725/725 tests green — see the Phase B3 implementation notes below
- [ ] **Gate: run the harness-comparison eval** (`plans/harness-comparison-eval.md` — checkouts
      prepped, never executed) before committing past Phase B
- [x] **UI hook: provider-driven executor selection** — closed 2026-07-12, out-of-band
      B follow-up (not scoped in any phase text — see "Phase B UI gap" note below). A
      `claude-cli` Model Profile provider, assigned per role via Agent Topology, routes the
      role to `ClaudeCodeExecutor` — one selection, no separate executor picker
- [ ] Phase C — Transcript ingestion + capability flags + second adapter — C1 (transcript
      ingestion + capability flags) shipped 2026-07-12, 729/729 tests green; see
      `plans/phase-c-implementation.md`'s C1 implementation notes; C2/C3 not started
- [ ] Phase D — Plan ingestion; scheduler shifts from decomposing to coordinating
- [ ] Phase E (opportunistic) — hooks-based leasing, file watching, Agent SDK sidecar

Phase A is done. Phase B carries an implementation-ready slice breakdown
(see "Slice breakdown"); C has a slice mapping; D is deliberately design-gated. This
plan captures the direction decided 2026-07-11 (external design discussion + code
analysis) plus all design decisions resolved in the follow-up passes the same day.

## Decision: host the harness, don't replace it

Studio's hand-rolled agent loops (`OrchestratorAgentLoop`, `WorkerAgentLoop`,
`ConversationCompactor`, `ContinueService`, retry/iteration budgets, prompt engineering)
are implicitly competing with vendor harness teams (Anthropic, OpenAI, Cursor, …) that
iterate weekly. That race is unwinnable and, more importantly, unnecessary — the parts of
the stack those vendors are *not* trying to own are exactly the parts Studio is built on:
durable, collaborative execution state over a persistent workspace.

**The split:**

| Runtime (Studio) owns | Harness owns |
|---|---|
| Goals, work units, scheduling, dependencies | Prompting, system prompts |
| Projections + materialization | Planning / local decomposition |
| File leases, fan-out coordination | Context management, compaction |
| Artifact graph, decisions, engineering state | Tool usage, model interaction |
| AP-4 review, promotion, replay, counterfactuals | Retries within a run, subagents |
| Cross-harness / cross-goal coordination | |

The native loop becomes a **reference implementation** behind an executor seam, not the
product. The runtime never talks to Claude directly — it talks to an adapter. If a vendor
ships a better harness, swap the adapter; the runtime is unchanged. Harness improvements
make the runtime *more* valuable instead of making our loop obsolete.

**Cleanest phrasing of the boundary: the harness plans, the runtime coordinates.**
Planning asks "how should I solve this goal?" Coordination asks "given everything
happening across this repository and every other active goal, how do multiple planners
execute safely in parallel?" Claude cannot answer the second question — it has no
visibility into sibling work units, other harnesses, blocked reviews, or leases. That's
runtime state.

**Adopt this as a core design principle — AP-6: the harness plans, the runtime
coordinates.** With a boundary test applied to every future feature: *is this about
making a planner smarter?* → it belongs in a harness (any harness — including ours).
*Is this about letting multiple planners collaborate safely over persistent state?* →
it belongs in the runtime. The long-term position only holds if the native harness
doesn't quietly grow back into the center of the system.

A corollary that raises AP-4's value: the review gate is now reviewing **harnesses**,
not just agents. Claude, Codex, native loop, human — every change enters through the
same promotion gate, which is what makes the executors genuinely interchangeable.

The native loop is the **reference implementation and the fallback**: it exercises the
same runtime surfaces external adapters do, provides the headless/automation path, and
keeps Studio fully functional if a vendor's licensing, pricing, or availability shifts.

## The end-to-end flow (orientation)

```
Goal (user)
  ↓
PLAN      Phase B: native planner/orchestrator (API, as today)
          Phase D: harness in Plan mode → plan.json → Studio folds it
  ↓
WORKUNITS durable runtime state — dependencies, FileScope, advisory claims,
          scheduling. Always Studio, every phase; never delegates.
  ↓
EXECUTE   per work unit, executor by AgentProfile:
          NativeHarnessExecutor (API loop) | ClaudeCodeExecutor (claude -p in
          branch workdir, .workspace/ contract materialized)
  ↓
HARVEST   diff → build/test gate → merge proposal; decisions/ → artifacts;
          usage → telemetry. Always Studio.
  ↓
AP-4      reviewer (native agent or human per ReviewPolicy) → apply /
          reconcile (MergeReconciliationService). Always Studio.
  ↓
          Engineering State fold updates → next work unit spawns with
          better context.
```

Per-role executor assignment (planner = claude-code, worker = claude-code,
reviewer = native, …) is free-mix via `AgentProfile.executor`. One asymmetry: the
orchestrator **role splits** — its *planning* half is delegatable (Phase D,
`plan.json`); its *coordination* half (spawn/schedule/sequence/hold) is code, never
delegates, and trends toward pure service rather than LLM loop. "Setting Claude Code
as orchestrator" means choosing who *authors the decomposition*, not who runs the show.

**The scoping rule:** pass the harness a *scoped* goal and let it do its thing inside
boundaries — constrain the edges (directory, allowlist, constraints, decisions channel,
harvest gate, AP-4), never the middle (its planning, tool use, verification habits).
Prompt posture follows: Studio exits the prompt-*craft* business (loop prompts,
compaction, tool coaching — the harness owns those) but stays in the prompt-*contract*
business — the kickoff contract is a versioned interface document, evolved like the MCP
contract, not tuned like a prompt.

## Why this is cheaper than it sounds

Two existing properties do most of the heavy lifting:

1. **Branches already materialize to real working directories.**
   `FileSystemWorkspaceService` / `WorkspaceExecutionService` run real `dotnet
   build`/`test` in a per-branch directory on disk. An external harness doesn't need
   `nm_v1_workspace_*` at all — point `claude -p` at the branch workdir, let it use its
   native Read/Edit/Bash, harvest the diff afterward. The projection/materialization
   layer *is* the compatibility layer: every coding harness fundamentally wants "a
   directory, git, shell, files" — don't fight that, serve it.

2. **The eval harness already debugged the adapter plumbing.** `eval-harness/run-one.ps1`
   does clone-at-ref → invoke `claude -p --output-format json` → parse usage/cost →
   grade, and caught the real-world traps (path resolution after Push-Location, MSBuild
   directory-props walk-up contaminating nested checkouts, prompt-leak extraction).
   Phase B is largely porting known-working logic into C#, not discovery.

The one genuine refactor: the current abstraction is at the wrong altitude.
`IAgentToolClient` abstracts the **LLM call inside** the loop; the seam this plan needs is
one level up — abstract the **entire `RunAsync`**. `InMemoryAgentRuntimeService`
constructs `WorkerAgentLoop` directly in three places (lines 374, 884, 979 — verified
2026-07-11) — that's the refactor site.

## Phase A — Engineering State projection + `.workspace/` contract

*5 slices — see slice breakdown. Do first: no external-harness dependency, and it
immediately improves the native loop's kickoff context too.*

### A.1 The "living timeline" gap

The DAG holds the **historical timeline** (goal → plan → decisions → artifacts → reviews →
promotion — replayable, immutable, auditable). What doesn't exist is the **living
timeline**: given all *promoted* work, what is currently true? Architecture choices,
active constraints, invariants, superseded decisions, known risks. These are facts, not
events — Git-vs-Kubernetes: one stores what happened, the other stores what should
currently exist.

Today a harness (ours or anyone's) has to *infer* this from source + history. The fix is
deterministic, not probabilistic: **a new canonical projection** computed by folding
promoted Decision/Constraint artifacts. No LLM, no summarization, no embeddings — the
same move as projecting files, applied to engineering state. "Why is MediatR not used?" →
Decision #418, promoted 2026-07-02, reason, superseded-by: none.

Existing substrate: `ArtifactRecord` already persists Decision/Constraint/Research nodes;
`ProjectionType.DecisionContext` and `ProjectionManager`'s constraint-artifact knowledge
inheritance already exist. Missing pieces:

- **Supersession semantics** — so the fold yields *current* truth, not all-time truth.
  This is the load-bearing schema change. Resolved design: forward `Supersedes` list
  written on the new artifact at creation; `SupersededBy` derived in reverse by the fold
  (see Open items for full rationale — no rewrite of promoted records).
- **The fold** — `ProjectionType.EngineeringState`: walk promoted decisions/constraints,
  emit the current-truth document deterministically.
- **Query surface** — REST + an `nm_v1_workspace_state`-style tool, and inclusion in the
  `.workspace/` contract below.
- Domain observers (see `docs/guides/domain-observers.md`) get a better role here:
  instead of prompt injectors, each observer contributes **facts** to this projection.

Note the backlog is *not* execution state — 5,000 open tickets don't describe the
software. Only promoted work feeds the fold.

### A.1.1 Three orthogonal projection families (vocabulary, decided)

Naming this explicitly because the runtime now has three independent state concepts that
must never share a name:

| Family | Fold | Character |
|---|---|---|
| **Repository projection** | operations → files | The existing canonical projection |
| **Engineering State** | promoted decisions/constraints → current facts | Persistent, derived, changes only on promotion |
| **Execution State** | sessions/events → current runtime (`SessionStateSnapshot` via `StateReconstructionService`) | Ephemeral, changes constantly — leases, workers, queues, work-unit progress |

"Engineering State" is the settled name for the A.1 projection (was an open item —
resolved): "Runtime State" collides with the third family, which already exists in code.

### A.2 The `.workspace/` contract — bidirectional, file-based, JSON-canonical

Every harness can read and write files; nothing else is universally true. So the runtime
contract is a directory materialized into the branch workdir at spawn.

**Format rule: JSON is canonical, markdown is a derived rendering.** The runtime reads
and writes structured JSON; alongside it, materialize `.md` renderings of the
runtime→harness files because LLM harnesses genuinely consume markdown better. Not every
future harness is an LLM, and structured data is what supersession/versioning/tooling
need — but never hand-maintain both: markdown is generated from the JSON, one direction
only. Harness→runtime files are accepted in either form (an LLM told to "append a
decision entry" will produce markdown; the harvest parser normalizes to JSON).

```
.workspace/
  manifest.json           # REQUIRED, first file any adapter reads. Contains:
                          #   contractVersion, runtimeVersion,
                          #   projectionId, goalId, workUnitId,
                          #   capabilities: ["engineering-state", "review-policy",
                          #                  "inbox", "decisions", ...]
                          # capabilities are RUNTIME capabilities (which contract
                          # surfaces this runtime materialized) — an older adapter
                          # ignores entries it doesn't know; a newer adapter detects
                          # a runtime that doesn't yet expose a surface and degrades.
                          # The mirror of the C.2 harness capability flags.
  goal.json / goal.md     # runtime → harness
  workunit.json           # runtime → harness (ids, FileScope, branch info)
  state.json / state.md   # runtime → harness (the Phase A.1 projection)
  constraints.json / .md  # runtime → harness
  review-policy.json /.md # runtime → harness (what the AP-4 gate will check)
  decisions/              # harness → runtime — one file per entry, numbered
                          # (0001.md …), each carrying a type field
                          # (Research | Decision | Constraint); parsed +
                          # normalized at harvest
  plan.json               # harness → runtime (Phase D — planning-mode output)
  inbox/                  # harness → runtime questions, one numbered file each;
  outbox/                 # runtime → harness answers, matched by number — lets Studio
                          # answer asynchronously and inject on --resume respawn
```

Per-entry numbered files (decisions/, inbox/) rather than single append files: no
append-ordering ambiguity at harvest, and the async answer flow falls out naturally —
Studio writes `outbox/0001.md`, the respawned/resumed harness is pointed at it. The
numbers are also **stable identities**: `decisions/0007.md` maps to one deterministic
domain key, so harvest plugs straight into the 10b.3 idempotent control plane —
re-harvesting after a crash or retry can't double-record an artifact, and deletion
semantics are obvious.

### A.2.1 The contract outlives the directory — schema in Contracts, transport pluggable

The durable asset here is the **Workspace Contract**, not any adapter — Claude happens
to consume it; Codex, the native harness, and future integrations consume the same
thing. The directory is merely the v1 *transport*, chosen because every harness
understands files. The same contract could later travel over MCP, an SDK call, or a
socket — so the schema must be designed transport-agnostically from day one.

Studio already has the exact discipline for this: **the contract DTOs live in
`NodalMerge.Studio.Contracts`** (alongside the frozen MCP/projection DTOs), versioned
and frozen the same way, with a `docs/contracts/workspace-contract-v1.md` companion to
`mcp-v1-contract.md`. Directory materialization is then just one serializer over those
types. This is where to spend disproportionate design care — more than on any specific
adapter.

Treat it as a **specification, not a schema** — closer in kind to HTTP or the OCI image
format than to an internal DTO set. The class of abstraction is different: the Rust FFI,
the .NET host, MCP tools, and REST are *implementation* APIs; the Workspace Contract is
the **execution API** — the thing independent parties implement against. The layering
follows: the schema owns the service (`Contracts` → `WorkspaceContractService` →
materialization → harness), never the reverse.

**Design principles for the contract** (write these into
`workspace-contract-v1.md` *before* the first DTO; every future version is evaluated
against them):

1. Transport-independent — files are one serialization of the contract, not the contract.
2. Deterministic — the same runtime state materializes byte-identical contract content.
3. Unknown fields must be ignored; producers may add optional fields (additive-only
   within a major version).
4. Consumers must tolerate partial capability sets (the manifest declares what's present).
5. The runtime is authoritative; harness output is advisory until promotion (AP-4).
6. The contract exposes **everything a harness needs, not everything Studio knows** —
   the runtime always knows vastly more than the contract carries.
7. Independent-implementation test: a new adapter (any language, any vendor) must be
   writable from the spec document alone, without reading Studio source.
8. Minimalism test per field: *could two independent harnesses produce equivalent
   results without it?* If yes, leave it out.
9. No central-server assumption — every field must be interpretable by a disconnected
   local-first replica (see the future-state sections). IDs are content/domain
   identities, never server-local handles or URLs that require a live host to resolve.

### A.2.2 Assembly is a service, not a projection (decided)

`.workspace/` materialization gets its own **`WorkspaceContractService`** — *not*
`ProjectionManager`. The concern split: `ProjectionManager` answers "produce a
deterministic projection"; `WorkspaceContractService` answers "assemble a harness
execution environment" — it *consumes* projections (Engineering State, DecisionContext,
repository files) plus work-unit and review-policy state, and emits the contract. If
`ProjectionManager` learned about review policies, manifests, and harness contracts, it
would stop being a projection engine and become a harness integration layer.

The harness→runtime half is the underrated part: it solves "how do decisions flow back
from a black-box harness" with zero integration code, for *any* harness — the prompt
contract just instructs it to write entries. MCP mounting and transcript mining (Phase C)
become **enrichment** for harnesses that support them, not requirements.

`.workspace/` is excluded from diff harvest / merge proposals (and from CAS snapshot
paths — see the resolved decisions).

## Phase B — Executor seam + Claude Code CLI adapter

*3 slices — see slice breakdown.*

- **B.1 `IHarnessExecutor`** — extract the loop-run from the three construction sites in
  `InMemoryAgentRuntimeService`; current loop becomes `NativeHarnessExecutor`.
  Shape: **one method, mode-carrying request** — `RunAsync(HarnessRunRequest) →
  HarnessRunResult`, where the request carries a `Mode` (v1: `Execute` only). This is
  deliberate: `PlannerAgentLoop` and `ReviewerAgentLoop` already exist natively, so
  `Plan` (Phase D) and `Review` modes are known future values, not speculation — but a
  mode enum grows without breaking adapters, whereas a four-method interface
  (`Plan/Execute/Review/Reconcile`) forces every adapter to answer questions it can't
  yet. `AgentProfile` gains an `executor` field (it already carries provider/model/
  systemPrompt — natural home). Scheduler, leases, sessions, events all unchanged above
  the seam.
- **B.2 `ClaudeCodeExecutor`** — materialize `.workspace/`, spawn `claude -p` in the
  branch workdir (`--output-format stream-json`, generated `--settings` allowlist —
  see resolved decisions, cost/wall-clock caps), await exit, harvest: diff →
  `merge.propose` → existing AP-4 gate; `decisions/` → artifact records; usage json →
  cost fields. Kill-and-respawn with `--resume` is the steering story for v1.
- **B.3 Failure modes** — timeout/cap-hit/crash mapped onto existing
  `AgentLoopCompletion` semantics + execution events; `isResume` path re-materializes
  and re-spawns.

**Orchestrator/planner stays native in this phase.** Only the worker/executor — the
commoditized part where harness maturity actually wins — becomes pluggable. That's also
exactly the cell the comparison eval measures (R1/R2 vs C/D), which is why the eval gates
Phase C/D investment.

## Slice breakdown — Phases A–B (implementation-ready)

### Phase A

| Slice | Content | Acceptance |
|---|---|---|
| A1 | `Supersedes: [artifactId,…]` on the artifact record (Contracts DTO + `nm_v1_artifact_record` param + REST), plus the standalone Supersession artifact for retirement-without-replacement | Record + query round-trips; promoted records never rewritten |
| A2 | `ProjectionType.EngineeringState` fold in `ProjectionManager`: **applied-proposal filter** (see decisions below), supersession resolution, deterministic ordering (promotion timestamp, then artifact ID — required for byte-identical output, principle 2) | Tests: supersession chain resolves to current truth; abandoned/in-flight work-unit artifacts excluded; two runs → identical bytes. Note: per the Pathways slice-1 precedent, new `ProjectionType`s serve through the existing generic `/studio/projections/{type}` route + MCP surface with **no transport changes** — A2 likely includes the query surface for free |
| A3 | Workspace Contract v1: DTOs in `NodalMerge.Studio.Contracts` + `docs/contracts/workspace-contract-v1.md` (the 9 principles go in first, then schemas: manifest, goal, workunit, state, constraints, review-policy, decision/plan/inbox entries — decision entries carry a `type` field) | Contract doc reviewed/frozen before A4 starts |
| A4 | `WorkspaceContractService`: assemble from projections + work-unit + review-policy state; materialize JSON + derived markdown into the branch workdir | Byte-deterministic materialization; consumed by A5 |
| A5 | Integration: `.workspace/` exclusion in both places (see decisions), harvest parser (decisions/ + inbox normalization, idempotent by numbered-file domain key), native loop consumes the contract **by kickoff injection of the rendered markdown** (native workers shouldn't need file reads to get context) | Native worker kickoff includes engineering state; harvest re-run doesn't double-record |

#### Phase A implementation notes (shipped 2026-07-12) — gaps folded into Phase B

All 5 slices landed; full solution test suite green (707/707; 3 of these 4 items landed as part
of B1/B2 — see the Phase B implementation notes below for what actually happened to each).
Verified against the shipped code, four items were cut or left incomplete relative to this table
and were carried forward rather than back-patched in isolation, since each is naturally
B-scoped work anyway:

- **Inbox/outbox harvesting was not built.** A5's `decisions/` harvest parser
  (`WorkspaceContractService.HarvestDecisionsAsync`) shipped; `inbox/` did not — only the
  `WorkspaceContractInboxEntry`/`OutboxEntry` DTOs exist (A3). Folds into **B3**, which is
  where "pause-and-wait" `AwaitingClarification` wiring already lives per the decisions below
  — inbox harvesting has no independent value until B3 can act on it.
- **`WorkspaceContractReviewPolicy` is missing `SelfVerifyBuildRequired`/`SelfVerifyTestRequired`.**
  Confirmed 2026-07-12: these map to `WorkspaceOptions.RequireBuildBeforeProposal`/
  `RequireTestBeforeProposal` — session-wide checkboxes in the extension's goal-workspace
  webview (`goalWorkspace.js`), today consumed only as a `WorkerAgentLoop` kickoff-prompt hint
  (`selfVerifyBuild`/`selfVerifyTest` params), with no gate behind them. Adding the two bool
  fields to the DTO (sourced from `WorkspaceOptions`, not `WorkUnit` — they aren't per-work-unit
  today) is a **B3** prerequisite: B3's own text already says these "become harvest-side
  enforcement for external executors," so the DTO field is B3 work, not a missed A3 field.
- **`WorkspaceProfileService.IgnoredDirNames`** (`WorkspaceProfileService.cs:16-17`) is a
  separate, hand-duplicated list that never actually mirrored `WorkspacePathFilter
  .IgnoredDirNames` (predates this plan — already missing `.git`/`.nodalmerge`) and does not
  exclude `.workspace` either, so sub-project root/build-command detection still walks into
  `.workspace/`. Low-risk (nothing project-marker-shaped lives there today); fold into **B1**
  as a one-line fix alongside the executor-seam refactor, since B1 already touches kickoff
  plumbing in the same area.
- **Docs drift**: `docs/reference/api-reference.md` line ~234 still describes
  `nm_v1_artifact_record` as Research/Decision/Constraint only (missing Supersession/
  `supersedes`), and its projection catalog (~line 449) doesn't list `EngineeringState`. Fix
  as part of **B1** (touches the same tool-catalog area when `AgentProfile.executor` lands).

Not carried forward (accepted as-is): `WorkspaceContractGoal` derives "the goal" by walking
`WorkUnit.ParentWorkUnitId` to the root rather than querying `IGoalNodeService` — reasonable
proxy, revisit only if `GoalNodeService` proves to be the more authoritative source once B
exercises the contract for real. `RuntimeVersion()` is a placeholder
(`Assembly.GetName().Version`) — cosmetic, not blocking.

### Phase B

| Slice | Content | Acceptance |
|---|---|---|
| B1 | `IHarnessExecutor` + `HarnessRunRequest/Result` (Mode enum, `Execute` only); `NativeHarnessExecutor` wraps existing loops; refactor the 3 construction sites; `AgentProfile.executor` field. Bundled cleanup (see Phase A notes above): add `.workspace` to `WorkspaceProfileService.IgnoredDirNames`; refresh `docs/reference/api-reference.md`'s artifact-record/projection-catalog entries | Full existing test suite green with zero behavior change |
| B2 | `ClaudeCodeExecutor`: spawn `claude` (cwd = branch workdir, `--print --output-format stream-json`, generated `--settings` allowlist, ambient auth + optional env-key injection), wall-clock/cost caps, exit-code handling | Integration test against a **fake `claude` stub** — reuse the eval-harness stub technique (`RUNBOOK.md`); no API calls in CI |
| B3 | Harvest: diff → `merge.propose` (existing services), `decisions/` → artifact records, run-level usage → conversation log/cost; failure modes → `AgentLoopCompletion`; `isResume` → re-materialize + `--resume`. **`selfVerifyBuild/Test` becomes harvest-side enforcement for external executors**: Studio runs build/test on the branch at harvest and failure blocks the proposal — the contract *mentions* the requirement (efficiency: agent fixes before exiting), but the guarantee is the gate, not prompt compliance. Same principle as advisory leases: prompt = hint, runtime mechanism = correctness. Add `SelfVerifyBuildRequired`/`SelfVerifyTestRequired` to `WorkspaceContractReviewPolicy` (sourced from `WorkspaceOptions`, not `WorkUnit`). Complete the `inbox/` harvest parser (mirror `HarvestDecisionsAsync`) and wire detected questions into `AwaitingClarification` | Full cycle test: spawn(stub) → edit → harvest → gate → proposal → AP-4 approve → apply (mirror of `FullAgentCycleTests`); a build-breaking stub edit is blocked at the gate; an `inbox/0001.md` question pauses the work unit and an `outbox/0001.md` answer resumes it |

#### Phase B1/B2 implementation notes (shipped 2026-07-12) — gaps folded into B3

Full solution suite green (717/717; up from 707 after Phase A). B1 matched this plan closely,
with one correction found during its own bundled cleanup: **`WorkspaceProfileService
.IgnoredDirNames` was never actually a functional gap** — its sole caller (`IsIgnored`) already
treats any dot-prefixed path segment as hidden, independent of that array, so build/test-command
detection already skipped `.workspace` before the "fix." The array was still synced for parity
(the two lists drift on non-dot-prefixed entries otherwise), but the original claim that this was
a real bug was wrong.

B1 also closed a real gap in its own first pass: the initial seam-routing test only exercised the
direct-spawn Worker construction site (`StartWorkerLoop`, reached via `SpawnAsync` with a non-null
`taskId` — e.g. the orchestrator's own `nm_v1_agent_spawn` tool call), which discards
`HarnessRunResult` entirely and never calls `VerifyWorkerProgressAsync`. A second test
(`Queue_driven_worker_spawn_...`) was added driving a real `ScheduledItem` through
`IWorkScheduler.EnqueueAsync` + `PollSchedulerAsync`'s background loop to cover the
queue-driven site (`RunScheduledWorkerAsync`) the plan's acceptance text actually specified.

B2 shipped `ClaudeCodeExecutor`, grounded in one real `claude -p --output-format stream-json`
invocation (captured 2026-07-12 against claude 2.1.177 — cost $0.044 for a one-word reply,
confirming automated tests must never call the real binary). Real findings from that capture:
stream-json is multi-line (`system`/`rate_limit_event`/`assistant`/`result` event types, not a
single blocking JSON object); `total_cost_usd`/`num_turns`/`usage.input_tokens`/
`usage.output_tokens` on the terminal `result` event matched `run-one.ps1`'s unverified guesses;
there is no structured "question" event — confirming the inbox/-file-based clarification design
was the right call, not something stream-json itself would ever surface.

Four items from this plan's literal B2 text were not implemented in the first pass. Env-key
injection was closed immediately (below) since it wasn't naturally B3-scoped work; the other
three are carried forward into **B3**, since the harvest step is where they become actionable:

- **`--add-dir` for multi-root work units was not implemented.** The generated `--settings`
  allowlist covers Bash/Edit/Write/Read for the single branch workdir only; a multi-root work
  unit's additional registered-repo roots are not passed via `--add-dir` at all.
- **Denial → execution-event wiring was not implemented.** The real captured `result` event
  includes a `permission_denials` array (empty in the trivial probe, unverified non-empty shape)
  that `ClaudeCodeExecutor`'s parser does not read or act on — a misconfigured allowlist is not
  yet diagnosable from the dashboard as the plan intended.
- **`--resume`/session-id threading is unbuilt end-to-end.** `ClaudeCodeExecutor` never sends
  `--resume`; `HarnessRunResult.HarnessSessionId` captures the session id from stream-json output
  but nothing persists or reuses it yet (this is explicitly B3's "Resume identity" bullet above,
  so the gap is intentional, not an oversight — noted here for completeness).
- ~~**Env-key-injection opt-in has no mechanism.**~~ **Closed 2026-07-12.** Added
  `AgentProfile.InjectApiKeyEnv` (mirrors `Executor`'s trailing-optional-field pattern, threaded
  through the REST create/update bodies); `ClaudeCodeExecutor` sets `ANTHROPIC_API_KEY` from
  `HarnessRunRequest.ApiKey` only when the resolved profile opts in — ambient auth stays the
  default. 719/719 tests green (2 new: default-no-injection, opt-in-injects).

One deliberate, undiscussed design deviation from this plan's stub description: the test stub is
a `.cmd` batch file, and `ClaudeCodeExecutor` wraps **every** executable path (stub or real
`claude`) via `cmd.exe /c` uniformly on Windows, rather than the plan's "directly invocable, no
cmd.exe wrapping needed in tests" design. This means the wrapping logic itself is exercised by
the stub-backed tests too (arguably better coverage), but it's a real, un-approved departure from
what was written, made unilaterally during implementation.

#### Phase B3 implementation notes (shipped 2026-07-12)

722/722 tests green (up from 719 after the B2 env-key-injection follow-up; 3 new test files:
`ClaudeCodeExecutorHarvestTests` full-cycle/gate/inbox tests). Denial→execution-event wiring and
the self-verify DTO fields (both carried forward from B1/B2) landed as designed:
`ExecutionEventKind.HarnessPermissionDenied` + `HarnessPermissionDeniedPayload` (best-effort
`ToolName`/`Reason` extraction, `RawJson` always preserved since the real shape of a non-empty
`permission_denials` entry is still unverified); `WorkspaceContractReviewPolicy
.SelfVerifyBuildRequired`/`TestRequired` sourced from `WorkspaceOptions`.

The harvest pipeline itself lives inside `ClaudeCodeExecutor.RunAsync` (not the caller) —
`IMergeCommandService.ProposeAsync` + `ValidateAsync` on success, `IWorkspaceContractService
.HarvestDecisionsAsync`/`HarvestInboxAsync` always, `IClarificationCommandService.RequestAsync`
per inbox question (reusing the exact same `MarkAwaitingResumeAsync`/`WorkUnitStatus.Waiting`
parking mechanism a native worker's own `nm_v1_clarification_request` tool call triggers — no new
pause plumbing needed), one `ConversationLogEntry` per run regardless of outcome. This keeps
`IHarnessExecutor.RunAsync`'s contract simple ("run the harness AND reach the standard outcome")
and means `VerifyWorkerProgressAsync` (in the caller) needs zero changes — a real `MergeProposal`
already exists by the time `RunAsync` returns `Succeeded`, satisfying its existing check for free.
`targetBranch` defaults to `"main"`; `MergeCommandService.ProposeAsync`'s own fan-out redirect
(`merge/{parentWorkUnitId}`) overrides this internally for fanned-out children regardless of what's
passed, same as the native worker's own tool call relies on.

**Real end-to-end smoke test run 2026-07-12** (3 real `claude` invocations, ~$0.24 total, throwaway
seed repo, temporary test file deleted afterward — not part of the checked-in suite). This is the
first time any code from Phases A/B ran against the real CLI rather than a stub, and it resolved
the biggest previously-unverified risk: **the generated `--settings` permission schema is correct**
— zero `permission_denials` across all 3 runs, confirming the `Edit(//c/...//**)`/`Write(...)`/
`Read(...)` pattern syntax (reverse-engineered from a real settings.json on the dev machine, per
B2's own notes) actually works against real enforcement, not just plausible-looking JSON. The
kickoff prompt also worked as designed — Claude read `.workspace/goal.md`/`workunit.md`/`state.md`
unprompted and correctly followed a "preserve existing content" instruction on the second attempt.
The full pipeline (spawn → contract materialization → real edit → harvest → `ProposeAsync` →
`ValidateAsync`) produced a correctly-attributed `ReadyForReview` proposal.

The first attempt surfaced a real bug — **in the smoke test's own setup, not in Phase A/B code**:
`IOrchestratorService.CreateWorkUnitAsync(repositoryPath: ...)` alone does not seed branch content
— it only registers a `RepositoryId` for write-back (`InMemoryWorkUnitService.cs:362-372`). Actual
seeding needs `WorkspaceOptions.SeedRepositoryPath` set explicitly, `InitBranchAsync("main")`
called first, and `seedFromBranchId: "main"` passed — exactly the pattern
`FileSystemWorkspaceServiceSeedingTests` already documents. Worth knowing for anyone else calling
this API directly rather than through the REST-facing `WorkUnitCommandService`, which has its own
first-goal seeding logic this bypassed.

**Both previously-deferred items closed 2026-07-12** (725/725 tests green, up from 722 — 3 new
tests: cross-repo `--add-dir`, resume persists/reuses the CLI session id, a fresh non-resume
attempt does not resume a stale session; plus one new assertion added to the existing native-path
`ClarificationWorkflowTests` test for the outbox file):

- **`--add-dir` for multi-root/cross-repo work units.** `WriteSettingsFileAsync` now resolves
  `WorkUnit.ReferenceFiles`' distinct `RepositoryId`s via `IRepositoryRegistryService.GetAsync`
  and, per distinct repository, adds a `--add-dir <path>` CLI arg plus a **Read-only** `--settings`
  allow entry (`ReferenceFiles` is documented as "not write-gating like FileScope; just where to
  look" — confirmed no Edit/Write entry is generated for these roots, only for the branch's own
  workdir). `ProjectRoot.RelativePath` (sub-projects *within* one branch, e.g. "frontend") was
  never actually part of this gap — those already live inside the workdir's own fully-allowed
  `**` pattern and their Bash allow entries were already wired in B2; the real gap was purely
  `IRepositoryRegistryService`'s cross-repo resolution, which wasn't researched until now.
- **`--resume`/session-id persistence + outbox-answer-triggers-respawn.** Closed the
  `IWorkUnitService.SetMetadataAsync` gap the plan called out: added a generic single-key
  read-merge-write setter (null value removes the key) — a **default interface method** (using the
  interface's own `GetAsync`+`CreateAsync`) so all ~15 existing `IWorkUnitService` test fakes kept
  compiling without a mechanical per-file edit, with `InMemoryWorkUnitService` overriding it with
  the same direct-dictionary read-merge-write every other setter there uses (avoiding the
  upsert-race class `IncrementReviewRejectionCountAsync`'s own comment already warns about).
  `ClaudeCodeExecutor` persists `claude`'s own session id onto `WorkUnit.Metadata["claudeCodeSessionId"]`
  after every run (success, failure, or timeout — `session_id` appears on every stream-json line
  per B2's real capture, so even a killed run's session is resumable) and passes `--resume <id>`
  only when `HarnessRunRequest.IsResume` is true (the same `ScheduledItem.AttemptCount > 0` signal
  B1 already wired) *and* a prior session id is on record — a fresh first attempt never
  accidentally resumes an unrelated stale session. On the **respawn** trigger itself: no new
  plumbing was needed at all — B1's own executor-seam wiring means the scheduler's normal
  `RunScheduledWorkerAsync` poll (which `ClarificationCommandService.RespondAsync`'s existing
  `scheduler.ApproveResumeAsync` + `WorkUnitStatus.Queued` already re-triggers for *every* work
  unit, native or external) already re-resolves `IHarnessExecutorResolver` and re-invokes
  `ClaudeCodeExecutor.RunAsync` — that was true before this session, just previously undiscovered
  as the actual resume mechanism. What was missing was purely the CLI-session continuity: the
  outbox half (`RespondAsync(resume: true)` now writes the human's answer to
  `.workspace/outbox/NNNN.md`, numbered the same way `.workspace/inbox`/`.workspace/decisions`
  are — unconditional, not executor-specific, so it's inert-but-harmless for a native worker) and
  the kickoff prompt now tells a resuming run to check `.workspace/outbox/` before re-asking.

#### Phase B UI gap — discovered and closed 2026-07-12 (provider-driven design)

Auditing this plan against the shipped extension found that **no phase (B, C, or D) ever
scoped UI work for selecting `AgentProfile.executor`.** B1 added the field to the DTO and
REST bodies; nothing downstream ever surfaced it. A first fix (an Executor `<select>` on
the Pipeline Agent Profile form) was built and then **rejected the same day** on UX
review: it forced users to make two disconnected selections (a provider on the Model
Profile *and* an executor on the pipeline profile) to know what would actually run.

**The shipped design is provider-driven — one selection.** The extension's Model Profiles
(the "which LLM and whose credentials" objects assigned per role by Agent Topology)
gained a fourth provider, `claude-cli`, alongside `anthropic`/`openai`/`vscode-lm`. The
provider value already travels the per-stage credential channel end-to-end (Topology →
`stageCredentials` → server-side `OrchestratorRoutingConfig` persistence → `ScheduledItem`
→ the worker construction sites), so the server just derives the executor from it:
`HarnessProviders.ResolveExecutorName` (`HarnessExecutorContracts.cs`) maps
`claude-cli` → `"claude-code"`, falling back to `AgentProfile.Executor` (which remains as
the REST-level override for headless callers, and survives UI PUT round-trips untouched).

Semantics of a `claude-cli` Model Profile:

- **No base URL; API key optional.** Blank key = the machine's ambient `claude` login
  (the resolved "ambient auth, key opt-in" decision); a stored key = injected as
  `ANTHROPIC_API_KEY` — storing the key *is* the opt-in gesture, equivalent to
  `InjectApiKeyEnv` on the REST path.
- **Model optional.** Blank = the CLI's own default; otherwise passed via `--model`
  (aliases like `sonnet`/`opus` or full model ids).
- **Worker/Execute roles only, enforced at two layers**: the extension's goal-run path
  rejects a `claude-cli` profile assigned to the orchestrator or Plan/Review stages with
  an actionable error, and the server's scheduled-run path fails the same combination
  with a clear reason (Plan/Review still construct native loop classes directly — B1
  scope; lifted when Phase C/D modes land). The orchestrator restriction is permanent by
  AP-6 for its coordination half.
- Credential gates that required baseUrl/apiKey (`canRun` in `RunScheduledWorkerAsync`,
  `canStartLoop` in `SpawnAsync`) now pass `claude-cli` without them.

### Decisions resolved for implementation (2026-07-11)

- **Fold input — applied-proposal rule:** an artifact enters the Engineering State fold
  iff its owning work unit has an applied (promoted) merge proposal. Artifacts on
  in-flight/abandoned work units stay visible in DecisionContext but are not "current
  truth."
- **Credentials — ambient auth, key opt-in:** ClaudeCodeExecutor defaults to the
  machine's existing CLI auth (the dev's subscription seat; Studio handles no secret).
  `AgentProfile` may optionally specify env-key injection for headless/CI profiles.
- **Permissions — generated allowlist `--settings`:** per-run settings permitting
  Edit/Write/Read within the branch workdir and Bash only for the detected build/test
  commands; everything else denied (headless denials fail the tool; the agent adapts).
  Trust escalation (bypass) is a per-profile opt-in later, not the default.
  **Multi-root work units:** generate the surface from the *full materialized root set*,
  not a single cwd — `--add-dir` (verified present in 2.1.177) for each additional
  registered-repo root, Bash allowlist built per-root from `WorkspaceProfile`'s detected
  commands. **Denials must be emitted as execution events** so a misconfigured allowlist
  degrading a run is diagnosable from the dashboard, not buried in the transcript.
- **Review gate posture:** hard gates are mechanical only — build/test at
  harvest, plus whatever `ReviewPolicy` requires for approval. Everything judgment-shaped
  is a **soft signal for the reviewer, never an auto-block** — chiefly scope delta:
  FileScope stays a suggestion (consistent with advisory leases), and the kickoff
  contract instructs the harness to record a decision entry justifying any out-of-scope
  file it touches; the reviewer sees delta + justification and judges goal-attainment.
  Unjustified excess flags for closer review, never rejects. **Review criteria live in
  `review-policy.json` as data, not prose in the reviewer prompt** — the same policy
  drives a native reviewer, a human checklist, and a future Review-mode harness
  identically, and "reviewer too strict" becomes a policy knob, not prompt re-tuning.
  The reviewer role itself stays native/human in v1 (the harness never reviews its own
  work); its system prompt is prompt-craft Studio deliberately retains.
- **Pause-and-wait semantics per executor:** native loop keeps true mid-turn pause
  (blocking clarification → `AwaitingClarification` → extension UI → resume). External
  executor v1 pauses at **run granularity**: `inbox/` question (or question in final
  output) → detected at harvest → same `AwaitingClarification` state and UI → answer to
  `outbox/` → respawn `--resume`. Headless permission prompts don't exist
  (`--permission-prompt-tool` absent in 2.1.177) — permissions are pre-declared only.
  **C.4 upgrade path:** once the slim MCP is mounted, Studio holds the
  `clarification_request` tool call open until the human answers — true mid-turn pause
  with no respawn (requires raising the MCP tool timeout); this is a second reason C.4
  exists beyond artifact recording.
- **`.workspace/` exclusion applies in TWO places:** the diff harvest (already noted in
  A.2) *and* the CAS snapshot paths — `RepositoryImportService.ForceSyncAsync` walks
  branch/repo disk content post-merge; without an exclusion there, contract files leak
  into `RepositorySnapshot` and thence into future branch seeds.
- **CLI flags verified** against installed `claude` 2.1.177 (2026-07-11):
  `--output-format stream-json`, `--resume`, `--permission-mode`, `--mcp-config`,
  `--settings` all present. Remaining: exact JSON field shape needs one real
  `claude -p` run before B3's usage parsing is written.
- **Worker-tool parity audit:** the native worker's 26 tools partition
  cleanly — file/search/build tools → harness-native (better); context tools
  (WorkUnitGet, Summary/Status, ProfileGet) → the `.workspace/` contract; coordination
  tools (TaskUpdate, MergePropose/Validate, WorkspaceDiff) → runtime-absorbed at
  spawn/harvest per AP-6. Only three genuine v1 losses: symbol nav, DocFetch, live
  ArtifactQuery — all assigned to C.4 (see its confirmed list). Two consequences:
  (1) **`decisions/` entry schema must carry a `type` field**
  (Research | Decision | Constraint) or external harnesses can record only one artifact
  kind; (2) DocFetch stays denied in v1, with per-profile `WebFetch(domain:…)` opt-in
  as the interim if research-heavy tasks need it before C.4. The orchestrator's 26
  tools are near-pure coordination (WorkUnit/Task CRUD, spawn, scheduler, apply,
  projections — zero file tools) and by AP-6 never transfer to any harness in any
  phase; the planner (read-only worker + ArtifactRecordPlan) maps 1:1 onto the future
  `Plan` mode.

## Phase C — Telemetry, capabilities, second adapter

*3 slices: C1 = transcript ingestion + capability flags (C.1 + C.2 below — the flag enum
is small enough to ride along); C2 = second adapter (C.3); C3 = slim MCP mount (C.4).*

**Status (2026-07-12, see `plans/phase-c-implementation.md` for full detail):** Phase C is fully
shipped. C1 (transcript ingestion + capability flags, 729/729 green), C3 (slim `/mcp-harness` MCP
mount + per-run bearer token + held-open clarification, 743/743 green — landed independently of C2,
per the child plan's own "C3 gates on C1's capability flags" ordering note), and C2 (Codex CLI second
adapter, 754/754 green) all shipped 2026-07-12. C2 was initially parked pending the Codex CLI being
installed; it became available on this machine later the same day, real `codex exec --json` captures
(codex-cli 0.144.1) were taken before any parser code was written (the child plan's own ground rule),
and the adapter landed with a shared `HarnessHarvestPipeline` extracted from `ClaudeCodeExecutor` —
confirming the seam genuinely isn't Claude-shaped (see the child plan's "C2 implementation notes").
`GET /studio/executors` (C1) needed no code change to describe the third-and-now-second registered
executor; the extension's Model Profile provider dropdown is now data-driven from that endpoint
(static fallback list if the Host isn't reachable), retiring the hardcoded-per-adapter `<option>`
this section anticipated needing "for a second entry."

- **C.1 Transcript ingestion** — parse Claude Code stream-json / session `.jsonl` into
  `ConversationLogEntry` rows + execution events so Decision Lens, cost tracking, and
  replay keep working (granularity: per-harness-turn, not per-Studio-turn).
  **Isolate the parsers as versioned components** (`ClaudeTranscriptParser.V1`, `.V2`, …)
  selected by detected CLI/format version — never scatter format knowledge through the
  adapter. A format bump then means adding a parser version, and an unrecognized format
  degrades to run-level telemetry instead of failing the run.
- **C.2 Capability flags** — don't normalize harness features; declare them:
  `SupportsTurnTelemetry`, `SupportsResume`, `SupportsHooks`, `SupportsSubagents`,
  `SupportsMcp`, `SupportsPlanningMode`. Runtime takes advantage when available,
  degrades cleanly when not. **UI consequence:** once flags exist server-side, the Model
  Profile form's CLI-provider note (see the Phase B UI gap section) can become
  capability-aware — e.g. surface "supports resume/steering" per provider instead of a
  static description, and the extension's role-validation (worker-only today) can key
  off `SupportsPlanningMode` instead of a hardcoded stage check.
- **C.3 Second adapter** (Codex CLI or Aider) — primarily to prove the seam isn't
  Claude-shaped. Expect its transcript parser to be the bulk of the work. **UI
  consequence — this is the forcing function, not C.2:** under the provider-driven
  design (Phase B UI gap section), each CLI adapter surfaces as one more provider option
  in the Model Profile form (`codex-cli`, `copilot-cli`, …) plus one more mapping in
  `HarnessProviders.ResolveExecutorName`. That stays hand-maintained honestly for a
  second entry, but C.3 is the point to add a `GET /studio/executors` endpoint (list of
  `{ name, providerKey, displayName, capabilities }` from every registered
  `IHarnessExecutor`) so the extension's provider dropdown, the CLI-note copy, and the
  server-side mapping are all driven from one registration instead of three hardcoded
  lists. Per-harness auth quirks (e.g. a Copilot CLI login flow) then live on that
  provider's own form section, not as ad hoc shared fields.
- **C.4 Slim MCP mount (optional per-harness)** — a *knowledge/coordination* subset
  mounted into the harness via `.mcp.json`. Confirmed list from the worker-tool parity
  audit (2026-07-11): `workspace_symbol_definition/references/implementation` (Roslyn
  semantic nav — no harness equivalent exists; a Studio value-add to the harness, not a
  parity patch), `doc_fetch` (traceable allowlisted web research — denied by the B2
  settings allowlist otherwise), `artifact_record/query` (live ancestor-knowledge access
  mid-run; spawn-time materialization only covers the static case), and
  `clarification_request` (held-open call = true mid-turn pause, see pause semantics).
  Not the file tools — the harness brings its own. Longer-term the runtime's external
  surface trends toward coordination primitives (AcquireLease, PublishDecision,
  QueryEngineeringState, WaitForDependency), not file I/O.

**Honest cost note:** "the adapter can be surprisingly dumb" is only half true. Dumb
(spawn → diff) buys correctness; the telemetry that feeds Decision Lens and enriches the
DAG requires per-harness parsers, which are real, version-fragile code. Still a far
better trade than owning a whole harness — but it's the recurring maintenance line item
of this whole plan. Budget for it; gate it behind C.2 flags.

## Phase D — Plan ingestion; scheduler role shift

*Tentatively 3 slices + design time. Highest conceptual risk — gated on the eval;
revisit slice shape after B/C land. Tentative sketch: D1 = `Plan` mode on
`HarnessRunRequest` + `plan.json` schema + fold into WorkUnits; D2 = executor routing
("who plans this goal") via the Slice 9d selector machinery; D3 = plan-staleness /
replan policy (grows from `ReplanService`).*

**UI consequence for D2:** `IAgentProfileSelectorService`-driven routing is *automatic*,
policy-based selection — a different mechanism from the manual per-role assignment in
Agent Topology (which, under the provider-driven design, is also what picks the executor
via the assigned Model Profile's provider). D2 should not replace that manual assignment;
keep it as the explicit override (a role with a concrete Model Profile assigned skips the
selector; one left on `auto` is routed by policy). Losing the manual path would remove
the only way to force a specific harness for debugging, comparison-eval runs, or a role
the selector gets wrong.

Instead of Studio decomposing goals into N work units, the flow becomes:

1. Spawn the harness in **planning mode** against the projection (contract: write
   `plan.json`, implement nothing).
2. Studio folds `plan.json` into runtime WorkUnits — now durable state with dependency
   edges, FileScope, and advisory claims, executable later by *any* harness (or several
   in parallel).
3. The scheduler's job narrows to coordination: which harness, which machine, when
   dependencies are satisfied, claim acquisition, retries, holding a work unit because a
   *sibling goal* is touching the same files. Not "split this into 7 pieces" — the
   harness is better at that.

**Don't assume the external harness plans better.** Decomposition quality likely splits
by task shape — architectural/greenfield work may favor a vendor harness, while large
migrations or 400-file epics may favor Studio's global view. The comparison eval (and
task-type-sliced results — it already labels S/M/L and task type) decides *routing*, not
just go/no-go. The natural home for "who plans this goal" routing is the Slice 9d
selector machinery (`IAgentProfileSelectorService`), extended to select an executor, not
just a profile.

**The scheduler is actually two schedulers, and Phase D is where they separate:**

- **Execution scheduler** (exists today — `WorkSchedulerService`): leases, retries,
  dependencies, concurrency, which machine/harness.
- **Planning scheduler** (embryo exists as `ReplanService`): who plans, when to replan,
  whether a plan is stale, invalidating work units a stale plan produced.

Don't build the second one early — but recognize plan-staleness/invalidation as a
distinct concern now, so Phase B/C code doesn't accidentally couple "a plan exists" to
"the native orchestrator produced it."

A likely **third** scheduler — promotion — is noted here so nobody builds it into either
of the other two: "N completed work units + dependency graph satisfied → batch promotion
candidate → AP-4." Its embryo already exists as `ReviewTimerService` + `ReviewPolicy` /
`AgentApproval` (Phase 7.0 autonomous completion) — what doesn't exist is batching
across a dependency graph. Don't build it; just don't block it.

Open design questions (deliberately unresolved here):

- Plan quality varies by harness — does the AP-4 gate extend to plan review, or do plans
  auto-promote below a size threshold?
- How does a harness-authored plan express FileScope well enough for claim
  pre-acquisition?
- Does `PlannerAgentLoop` survive as the fallback planner, or become
  `NativeHarnessExecutor` running in `Plan` mode?

## Phase E — Granularity recovery (opportunistic, unscheduled)

- **Hooks-based leasing** — Claude Code PreToolUse hooks calling back into Studio before
  each Edit/Write restores park-before-write for that one harness.
- **FileSystemWatcher on the branch workdir** — incremental artifact/checkpoint events
  during a run instead of one harvest at the end.
- **Claude Agent SDK sidecar** — a small Node process (precedent: `LmApiProxy.ts`)
  wrapping the SDK: programmatic hooks, `canUseTool`, in-process MCP, session control.
  The better long-term Claude adapter once the CLI adapter proves the approach.

## Accepted degradations (do NOT chase parity)

Several Studio differentiators are loop-introspection features that assume Studio owns
every turn. For external executors they degrade **by design**:

| Feature | Native loop | External executor |
|---|---|---|
| File leasing | Park-before-write (`awaitingFileLease`) — unchanged for local connected mode | Advisory claims pre-acquired on declared FileScope at spawn (leases are advisory by design — see multi-dev future state); conflict detection at harvest/merge; hooks later (E) |
| Steering / pause-redirect | Mid-turn | Kill-and-respawn with `--resume` |
| Compaction / iteration budgets | `ConversationCompactor`, `ContinueService` | Harness's problem — deleting this responsibility is the point |
| Conversation log | Per Studio turn | Per harness turn via transcript (C.1), or run-level only |
| Clarifications | Blocking mid-loop | `inbox/`/`outbox/` + async answer on resume, or MCP tool where mounted |
| Counterfactual replay | Turn-level | Work-unit level (re-run with different harness/model) |

**"Cattle not pets" is real only at work-unit granularity.** Swapping harness or provider
mid-work-unit works via re-materialized files + engineering state + `decisions/` — the
runtime owns the durable execution context, the harness is ephemeral compute. But
conversation-level resume across vendors is not a thing; don't design for it.

## Sizing summary

| Phase | Slices | Ships alone? |
|---|---|---|
| A | 5 | Yes — improves native loop immediately |
| B | 3 | Yes — the core unlock |
| C | 3 | Yes — telemetry + proves the seam |
| D | ~3 + design (eval-gated) | Yes — full vision |
| **Total** | **~14** | Phase-7-sized overall |

## Integration priority (which harnesses, in order)

1. **CLI harnesses** — Claude Code CLI, then Codex CLI / Gemini CLI / Aider. Spawnable,
   workspace-oriented, capture stdout, no awareness of NodalMerge required.
2. **Open APIs/SDKs** — already covered by the native loop (which is the reference
   harness); Agent SDK sidecar in Phase E.
3. **IDE extensions** — opportunistically, only where they expose stable surfaces
   (Continue, Cline). Not drivable externally in general.
4. **Closed IDEs** (Cursor, Windsurf) — not an integration target. Users choose them;
   we don't automate them.

## Out of scope / not doing

- Normalizing harness-specific features (slash commands, memories, hooks) into one
  abstraction — capability flags only.
- Making external executors reach feature parity with the native loop (see degradation
  table — the asymmetry is intentional).
- Replacing the orchestrator with an external harness (Phase D changes *who plans*, not
  who coordinates).
- Cross-vendor conversation-level resume.
- Any change to `nm_v1_*` tool behavior for the sake of external harnesses — the
  `.workspace/` contract and slim MCP subset are additive surfaces.

## North star (context, not scheduled): local-first agent pods

This plan is not a pivot — it's a return to the original NodalMerge Studio intent. The
harness was always incidental: the hand-rolled loop was simply the easiest way to get an
LLM doing work whose state, reasoning, decisions, and lineage we wanted to persist and
learn from. The unique value was never the loop; it's *institutional memory* — not
"the user prefers PostgreSQL" (retrieval) but "PostgreSQL was selected in Decision #418
after evaluating MySQL/DynamoDB, reason: transaction semantics, reviewed under AP-4,
still active" (provenance). NodalMerge is the engine; Studio is its first compelling
application (the SQLite/Git/Kubernetes pattern — a great engine disappears).

The eventual expression of that intent is **local-first pods**: a laptop running a human
+ Claude, another running a worker agent, a CI machine running a build/test agent, an
offline node — each a full NodalMerge replica (local projection, local CAS, local
history, local execution), converging via CRDT replication with the server doing
coordination/discovery (the existing **Room** concept is the sync boundary for exactly
this). Under CRDT semantics the agent is *just another replica* — the same convergence
machinery serves agents, humans, services, and offline nodes without centralized
ownership.

A pod decomposes into exactly what Phases A–C build: **replica + materialized workspace
+ Workspace Contract + whichever harness is installed there.** So this plan is the pod
enabler, and it imposes one design constraint *now*: **the Workspace Contract must be
interpretable by a disconnected replica** — no field may require a live central server
to resolve (see contract principle 9). Beyond that constraint, pods stay out of scope:
they're gated on NodalMerge peer-replication maturity, not on anything in Phases A–E.

## Future state 2 (context, not scheduled): multi-developer topology

The pods vision, one level more concrete: multiple developers on separate machines, a
central Studio server between them. Half of this already exists — headless peers in
connected mode (`docs/guides/headless-peer.md`) do CRDT room replication of work units,
artifacts, and proposals over WebSocket today, and a peer's agents already appear in
another machine's extension.

**The organizing principle: state converges without authority; coordination requires
authority.** The distributed state machine separates into three planes:

| Plane | Content | Mechanism | Consistency need |
|---|---|---|---|
| Coordination | Promotion ordering (the one true serialization point) + advisory claims/scheduling hints | Server-authoritative over the room protocol | Ordering for promotion only; claims are best-effort |
| Metadata / DAG | Ops, work units, artifacts, decisions, snapshot maps (path → BLAKE3) | Room fan-out (WS/WebRTC) — largely exists | CRDT convergence |
| Content (CAS blobs) | File bytes | Resolve(hash) → source, fetch, verify, cache | None — immutable + hash-verified, pure availability/caching |

**Leases are advisory, never correctness (decided 2026-07-11).** Offline-first
convergence and file locks are contradictory, and locks lose: an offline pod, a human
in an editor, or an uncooperative harness cannot be prevented from writing — and must
not be, or the system blocks work, which betrays the availability-first premise. The
two convergence problems resolve at different layers: the **DAG converges via CRDT**
(append-only facts merge automatically), but **file contents cannot** — RGA/Lamport/LWW
are semantically unsound for code (a character-level auto-merge of two edits to one
function yields syntactically plausible garbage). Code converges only at intent level:
branch isolation → proposal → reconciliation (mechanical, LLM-assisted via
`LlmMergeProvider`, or human) → build/test → AP-4. `MergeReconciliationService` and its
`SupersededBy` proposal chains are that mechanism and already exist. So:

- **Reconciliation/merge** = the correctness mechanism. Universal — connected agents,
  offline pods, humans, crashed peers. Never optional.
- **Leases/claims** = collision-*probability* reducers that save wasted spend (two
  agents burning tokens on doomed parallel work). Available only where actors are
  connected and cooperative; when absent, nothing breaks — the merge gate absorbs it.
  The native loop's park-and-resume was always an efficiency choice, not safety.
  Humans can be *informed* by claims ("an agent is active in src/Payments/"), never
  constrained. Git's philosophy: allow work always, pay reconciliation occasionally.
- **Promotion ordering** = the only genuine authority requirement — two peers cannot
  both advance the authoritative branch concurrently without an ordering decision.

Chosen topology: **hybrid** — every dev machine is a connected peer materializing only
the branches it works on, locally, from its local replica; each dev's harness runs
locally with their own secrets (their own API keys / Claude Code seat — credentials
never leave the machine); the server is just another peer with more uptime *plus* the
coordination-authority role. Two things fall out for free: a human editing their locally
materialized branch is just another executor (diff harvest → proposal → AP-4, no
human-specific machinery), and per-seat harness economics beat central org-key metering.

**Enumerated gaps** (none of them touch Phases A–E):

1. **CAS blob distribution** — the open technical hurdle. Today blobs live on the local
   filesystem, seeded from a local repo; nothing sends content over the wire. The
   likely hook already exists in the engine: `BlobObjectStore` /
   `IBlobStoreProvider` / `IBlobUrlResolverProvider` and the delegated-storage + GC
   design (`nodalmerge/docs/delegated-storage-gc.md`) — i.e. blobs referenced by hash,
   resolved to a URL/source (server relay, delegated S3, eventually peer fetch),
   fetched lazily, verified by BLAKE3, cached. The Studio-side work is routing
   `RepositoryImportService` (push new blobs on import/merge-writeback) and scoped
   materialization (resolve-fetch instead of local-copy) through that provider seam.
   Design deliberately unresolved here.
2. **Promotion ordering over the room protocol + advisory claims** — promotion is the
   one primitive needing server authority; leases downgrade to best-effort claims
   surfaced to connected peers (and displayed to humans as information, not
   enforcement). Reconciliation remains the correctness path for all conflicts.
3. **Per-peer identity/attribution** — who did what, from which machine, under which
   review authority.

Offline behavior: a peer can't materialize blobs it never cached — accepted;
local-first ≠ serverless. `FileScope`-driven prefetch bounds the exposure (a work unit
declares what it needs; prefetch it while connected).

### CAS distribution — design notes (discussion, 2026-07-11)

Confirmed against current code/docs: **nothing sends blobs over the wire today.** Room
replication covers domain nodes only; the CAS snapshot derives from a local walk of
`Workspace:SeedRepositoryPath`; branch seeding is local copy/extract. A remote peer
receives the *map* (path → BLAKE3) and none of the *content*.

**Why this is the easy kind of hard:** the three planes have different consistency
requirements, and content's is *none*. Blobs are immutable and self-verifying, so
distribution is a pure caching problem — no ordering, no conflicts, no consensus. Every
genuinely hard consistency problem lives in layers already built or already decided
(op log convergence, promotion-as-authority). "Where a blob comes from" is freely
swappable over time because the hash guarantees integrity regardless of source.

**The engine already encodes the solution** (same delegated-storage pattern as
SpeechSlate): `BlobObjectStore` (`core/crdt/src/storage.rs`), `IBlobStoreProvider` /
`IBlobUrlResolverProvider` (.NET host abstractions — the presigned-URL resolution seam),
and `nodalmerge/docs/delegated-storage-gc.md` with GC contracts. The BlobStorageParity
refactor's "and future servers" was this seam. Missing is Studio-layer wiring only:

1. **Import (dev A):** bootstrap walk produces snapshot map + local blobs as today,
   *plus* a push of blobs to the blob store. The map is small JSON — replicates via
   room like any node.
2. **Materialize (dev B):** map arrives via room; scoped materialization walks
   `FileScope`, resolves each hash through the URL provider → fetch → verify BLAKE3 →
   cache → write. No git, no full-repo download — only what the work touches.
3. **Merge writeback (any peer):** new blobs push; new snapshot generation replicates.
   `ForceSyncAsync` keeps its role, gains a push step.

**Rollout order** (low-stakes because sources are swappable): server-relay HTTP first
(`GET /blobs/{hash}` on the coordination server that's required anyway) → delegated
S3/R2 with presigned URLs second (offloads bandwidth) → peer-to-peer fetch last and
maybe never (NAT + availability pain for a niche offline-LAN win).

**Relationship to git:** this is git's dumb content-addressed fetch re-derived — a
feature, not an embarrassment. The justifying differences are **granularity**
(blob-level, `FileScope`-scoped: fetch 40 files of a 5,000-file repo) and
**addressability**: git materializes branch heads/commits; the map-in-DAG materializes
*any projection point* — "the workspace as it stood when Decision #418 promoted," "work
unit X's branch at pathway cursor N," a counterfactual's base state, a pulled-down
session. Git has no equivalent because its history doesn't know what a decision or a
session is.

**The genuinely hard long-term problem is deletion, not distribution.** GC in a
distributed CAS with offline peers ("does a laptop closed for two weeks still need this
blob?") = reachability-from-live-branches + pin semantics — exactly what
`AdminPinStore`/`AssetInventoryStore` anticipate. Fine to defer, with one standing
design rule: **never build anything that assumes a pushed blob can be synchronously
deleted.**

### Offline divergence — catch-up merge, not rebase (discussion, 2026-07-11)

Scenario: a peer (human or agent) works offline while canonical advances; on reconnect
its work must land against a moved target. A three-way merge needs (base, ours, theirs)
— and unlike git, which *computes* the merge base, Studio **records** it: every branch
is seeded from a known CAS snapshot generation. On reconnect all three states are
materializable, so "merge canonical into local before proposing" is absolutely possible.

- **Not a rebase** — rebasing rewrites history and violates AP-5. The append-only form:
  apply diff(base → new canonical) into the working branch as a new "synced with
  canonical @ gen N" node. Divergence stays recorded; work continues from merged state.
- **Same reconciliation machinery, two trigger times:**
  - *Eager (on reconnect, pre-propose — preferred when the actor is present):* catch-up
    merge conflicts become work for the agent (a natural future `HarnessRunRequest`
    conflict-resolution context) or the human; build/test verifies the *merged* result
    before proposing, so AP-4 reviews something coherent against current canonical.
  - *Lazy (at the gate — exists today, must remain):* `MergeReconciliationService`
    absorbs stale-based proposals when the actor is gone (e.g. a pod queued its
    proposal while offline and disconnected).
  - Policy: sync early when present — divergence compounds; fall back to gate-side
    reconciliation otherwise.
- **Studio's reconciliation has more to work with than git's**: the work unit's goal,
  both sides' decision logs, and constraints are available as input to LLM-assisted
  resolution (`LlmMergeProvider`) — semantic merge guidance rather than hunk
  arithmetic, with the human gate behind it either way.

Related clarification (same discussion): file leasing remains fully active for local
connected mode — multiple worker agents in one runtime keep park-before-write exactly
as built. The advisory reclassification governs the distributed future and unleasable
actors; it does not change current behavior.

## Open items before starting

Resolved in design discussion (2026-07-11):

- ~~Naming for the A.1 projection~~ — **Engineering State**, kept. "Runtime State"
  collides with existing execution state (`SessionStateSnapshot` /
  `StateReconstructionService`); "Workspace State" collides with
  `nm_v1_workspace_status`/`workspace_summary`. See A.1.1 for the three-family
  vocabulary.
- ~~Where `.workspace/` materialization lives~~ — **`WorkspaceContractService`**, not
  `ProjectionManager`. See A.2.2.
- ~~`SupersededBy` representation~~ — **forward `Supersedes: [artifactId, …]` written on
  the *new* artifact at creation; `SupersededBy` is derived in reverse by the
  Engineering State fold.** Rationale: the studio layer has no first-class edge
  primitive — every relationship today (`ParentArtifactId`, WorkUnit `DependsOn`,
  `MergeProposal.SupersededBy`) is an ID reference in a node payload. A back-pointer
  *field* on the old decision (the `MergeProposal.SupersededBy` precedent) requires
  rewriting an already-promoted record (`existing with { … }` → new node version) —
  legal under AP-5 but it means the reviewed/promoted record's content changes after
  the fact. The forward link needs no such rewrite: it's known at creation time of the
  superseding artifact, append-only clean, and lives on the *common* artifact record so
  it generalizes to Constraint→Constraint or Decision→Research without new schema.
  Edge-in-spirit, field-in-mechanism; the graph stays the source of truth because both
  directions are queryable via the fold. For retirement-without-replacement (a human
  marks a decision obsolete with no successor), append a small standalone Supersession
  artifact carrying only `Supersedes` — still no mutation of the promoted record.

  **Refinements (2026-07-11, second pass):**
  - *Derived means computed, never stored*: the fold builds an in-memory reverse index
    as a side effect of its walk — there is no persisted/maintained reverse graph.
    "What superseded #418" is answered by the projection.
  - *The branch argument proves it*: if branch A promotes S1 superseding D and branch B
    promotes S2 superseding D, a stored back-pointer on D is incoherent (both? tagged?
    merged how?). The reverse relation is **branch-relative — a fact about a timeline,
    not about the record** — and branch-relative facts can only be derived from a chosen
    history. Same reasoning that makes Engineering State a projection.
  - *Fact vs. workflow records taxonomy* (resolves the `MergeProposal.SupersededBy`
    question — earlier "smell" retracted): **fact records** (Decision/Constraint/
    Evidence) are assertions — never rewritten once promoted, forward links only,
    futures derived. **Workflow records** (Proposal/Task/WorkUnit/Lease) are lifecycle
    entities — each state transition is `WriteNodeAsync` on the same entityId = a *new
    DAG node version* with the old retained (verified in `IStudioNodeStore`), so
    proposal `SupersededBy` is lifecycle status like `Status=Approved`, append-only at
    the storage layer, and stays where it is. Only residue is the name collision
    between the two relations — document, don't migrate.

Also resolved (2026-07-11, second pass — see "Decisions resolved for implementation"):
fold input = applied-proposal rule; credentials = ambient auth with per-profile key
opt-in; permissions = generated allowlist `--settings` over the full multi-root set;
review gate posture (mechanical hard gates, judgment as soft signals, criteria as
`review-policy.json` data); pause-and-wait per executor; `.workspace/` exclusion needed
in CAS snapshot paths too; CLI flags verified against `claude` 2.1.177; construction
sites in `InMemoryAgentRuntimeService` verified at lines 374/884/979 by grep;
worker/orchestrator/planner tool-parity audit (three v1 losses → C.4).

Still open (both are single actions, not design work):

- **One real `claude -p` run** to capture the exact stream-json/usage field shape before
  writing B3's parser (same open item the eval plan has — one run serves both).
- **Run the comparison eval** (gate above) — its result sizes how far past Phase B to go.
