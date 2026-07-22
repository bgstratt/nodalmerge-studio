# Recursive planning — 2-level spike

Prove that a planner can decompose a slice into a *sub-plan* (rather than a leaf task),
that the sub-plan fans out into grandchildren, and — the load-bearing part — that the
grandchildren's changes **reconcile back up through the interior node to the root**. Gated
behind a `MaxPlanDepth` setting that defaults to `1` (today's flat-and-wide behavior,
unchanged).

Written 2026-07-21 after code recon (file:line refs verified that day).

## Status

- [ ] S1 — `PlanSlice.Kind` (`leaf | compound`) contract + planner emits it
- [ ] S2 — `MaxPlanDepth` setting + plan-depth computation + cap enforcement
- [ ] S3 — route branch in FanOut: compound slice under the cap → sub-planner, not worker
- [ ] S4 — depth-aware planner prompt (a sub-planner knows it *may* re-slice)
- [ ] S5 — **the load-bearing test**: 2-level tree reconciles bottom-up to the root
- [ ] S6 — **explicit peer contracts** (parent-authored interfaces shared by producer + consumer)
- [ ] S7 — **blocking integration gate** on the reconciled branch *(may defer — captured so we don't lose it)*

## What this spike proves — and what it deliberately doesn't

This is the question worth answering before writing a line: does 2 levels put us "in the
race," or is there a lot more after?

**What green S5 proves (the architecture bet is won):**

1. The **route decision** works — a planner marking a slice `compound` causes a
   sub-planner to run instead of a worker.
2. **Recursion mechanically closes** — the sub-planner's `plan.json` gets fanned out into
   grandchildren by machinery that *already exists* (the rescue sweep, S3 recon below). No
   new orchestration loop.
3. **Bottom-up reconciliation composes across one interior node** — grandchildren merge
   into the interior node, and the interior node (now a completed child) merges into the
   root. This is the one thing the whole feature rests on and the one thing we can't
   assume. If it's green at 2 levels, **3+ is config**, because nothing in the DAG,
   coordinator, or credential resolution is depth-bound (verified: the 32-hop walks and
   the rescue sweep are already N-level).
4. **The depth cap forces leaf at the boundary** — a `MaxPlanDepth=2` run never produces a
   third planning layer.

If S5 is green, the answer to "are we in the race" is: **the architecture is** — the
mechanism runs end-to-end and the hard risk is retired.

**What the spike does NOT prove (the "lot more to go" — all hardening, not new architecture):**

- **Plan quality.** Whether a planner makes *good* leaf/compound calls on real workloads is
  a prompt-eval problem, not a plumbing one. S4 ships a plausible prompt; it does not
  validate the judgment. This is the largest open item and it's evaluative, not structural.
- **Conflict roll-up.** S5 uses non-overlapping grandchild fileScopes (clean merge). What
  happens when two grandchildren conflict and the interior node has to surface a
  `merge-conflict-report.md` *at the interior level* is untested here (see Open questions).
- **Failure containment.** A wedged grandchild stalling its interior node stalling the root
  — dead-letter/replan behavior at interior nodes (`ReplanService` creates sibling slices;
  does that compose one level down?) is out of scope.
- **Latency / critical path.** A deep branch does plan→plan→…→execute sequentially before
  its first worker starts. Not measured here.
- **Observability.** Artifact Explorer rendering a nested tree, decision-log readability at
  depth. Out of scope.

Net: the spike wins the architecture bet and retires the reconciliation risk. Reaching
Cursor-swarm scale (hundreds of workers, coherent reassembly under real conflict) is a
meaningful amount more — but it's *hardening of known machinery plus plan-quality evals*,
not another architecture. That's the honest shape of "how much further."

**One correctness gap is promoted *into* the spike, not deferred: peer interface coherence
(S6).** Reconciliation only coordinates slices that share files or a `dependsOn` edge; two
peers across disjoint fileScopes (backend API ↔ frontend that calls it) share neither, so
today they merge green whether or not their interface agrees. That's a self-consistent-but-
wrong failure that *ships*, and it exists at flat depth 1 already — recursion only makes it
worse. It is the thing that most needs an explicit contract, so S6/S5.b fold it in rather
than leaving it to "hardening."

## Recon — what already exists (so the spike stays small)

The recursion machinery is ~70% present. Verified 2026-07-21:

- **Addressing is already the DAG, not filenames.** `plan.json` is written *into a branch*
  (`FileSystemWorkspaceService.cs:832` — "internal planning artifact"), and every planning
  work unit has its own branch (`WorkUnit = Goal + Branch`). The Plan artifact is keyed by
  `workUnitId` (`ArtifactCommandService.cs:62-64`, `PLAN-{Guid}`). A sub-plan is just
  `plan.json` on the sub-plan unit's branch — **no `plan.001.json` numbering needed, and we
  must not add one** (it would flatten a tree into a filename).
- **The rescue sweep already re-fans-out children that hold their own plan.**
  `GoalCoordinator.ConvergeAsync` (`GoalCoordinator.cs:57-70`) walks children and calls
  `IFanOutService.TryFanOutFromPlanAsync` on any in a live state. Today only reconciliation
  units exercise it; it is depth-agnostic. **This is the recursion step — S3 just gives it
  a producer.**
- **`EnsurePlannerAsync` already plans non-root units** (`GoalCoordinator.cs:134`, comment
  at :173 names "a reconciliation or sub-plan unit"). Not root-only.
- **Credentials already resolve to the root across depth.** `ResolveCredentialRootAsync`
  (`FanOutService.cs:589`, 32-hop bound) exists *because* deeper fan-outs already happen
  (comment :578-588). Depth-2 credential keying is already correct.
- **The DAG is depth-unbounded.** `WorkUnit` carries `ParentWorkUnitId` / `DependsOn` /
  `FanOutInfo`; anti-cycle guards (`WouldCreateCycleAsync`) are in place.

What's genuinely missing is the **producer** for the rescue sweep: today every slice is
enqueued as a worker in `FanOutService.EnqueueChildWorkerAsync` (`FanOutService.cs:448-576`,
unconditional `_scheduler.EnqueueAsync` at :540). Nothing ever enqueues a *child planner*.
S3 is that branch.

## S1 — `PlanSlice.Kind` contract

- Add `Kind` to `PlanSlice` (`Contracts/Domain/PlanDocument.cs`) as an enum
  `PlanSliceKind { Leaf, Compound }`, **defaulting to `Leaf`** on deserialization so every
  existing plan and every external harness `plan.json` reads back as today's flat behavior
  (back-compat, same discipline as `AgentProfile.ModelProfileId`).
- Mirror the field in the `WorkspaceContractPlan` DTO (`NodalMerge.Studio.Contracts`) so
  external harness plans can express it; document it in the plan.json contract doc.
- `FanOutInfo` already carries `SliceId`; add a `Kind` (or a `PlanDepth`) alongside it on
  the child `WorkUnit` at creation in `EnsureChildWorkUnitsAsync` so the route decision in
  S3 doesn't have to re-read the parent plan. (Prefer storing `Kind` on the child; depth is
  computed in S2.)

## S2 — `MaxPlanDepth` setting + depth computation

- Add `MaxPlanDepth` to `WorkspaceOptions`, **default `1`**. `1` = only the root plans
  (today, exactly). The default path must be byte-for-byte unchanged — no compound routing
  fires when `MaxPlanDepth <= 1`.
- Define `planDepth(workUnit) = count of ancestor work units` (root goal = 0, a root
  slice's unit = 1, a grandchild = 2). Compute by the same bounded parent-walk as
  `ResolveCredentialRootAsync` (reuse the 32-hop pattern; do not write a new unbounded
  walk).
- **The cap rule (S3 consumes it):** a child work unit may be enqueued as a *sub-planner*
  iff `slice.Kind == Compound && planDepth(child) < MaxPlanDepth`. With `MaxPlanDepth=2`: a
  root slice (depth 1) may sub-plan; its grandchildren (depth 2) fail `2 < 2` and are forced
  to `Leaf` → worker. Exactly two planning layers, no runaway.

## S3 — route branch in FanOut (the producer for the rescue sweep)

- In the enqueue path (`FanOutService`, around `EnqueueChildWorkerAsync`), branch on the
  cap rule from S2. When it holds, call a **new sibling method `EnqueueChildPlannerAsync`**
  instead of `EnqueueChildWorkerAsync`. Keep them separate — the credential and profile
  resolution differ and mixing them into one method with flags will rot.
- `EnqueueChildPlannerAsync` mirrors the worker path but:
  - selects the **`"planner"` profile** (not a file-scope-matched worker profile),
  - resolves **Plan-stage** credentials via `GetCredentialsForStage(credRootId,
    PipelineStage.Plan)` with the same root-keyed lookup and the same `defaultCreds`
    fallback `EnsurePlannerAsync` uses (`GoalCoordinator.cs:177-200`) — factor that
    resolution so both call sites share it rather than duplicating the fallback ladder,
  - runs the same `PolicyCheckpoint.BeforeEnqueue` gate and writes the same
    `OrchestrationAction.Enqueue` decision-log entry (with `stage: Plan`) so a nested plan
    is auditable exactly like a fan-out.
- **Nothing else is needed for recursion to close**: once the sub-planner runs and records
  its Plan artifact, `ConvergeAsync`'s existing rescue sweep (recon above) fans it out into
  grandchildren on the next release. Confirm this in S5 rather than adding a second
  trigger.

## S4 — depth-aware planner prompt

- `AgentLoopPrompts.Planner` currently *discourages* re-slicing (step 6a: "re-slicing …
  is almost never right"). That guard exists to stop a leaf being pointlessly re-planned; it
  must not fire for a legitimately compound sub-slice.
- Give the planner two pieces of context in its kickoff: (a) its own `planDepth` and the
  `MaxPlanDepth` ceiling, and (b) that it *may* mark slices `Compound` **only if
  `planDepth < MaxPlanDepth`** — at the ceiling it must emit all `Leaf`. Seed a
  count-based hint ("if this would exceed ~N atomic slices or spans clearly separable
  subsystems, prefer Compound"), but frame the real test as *"can one worker hold this in
  context and land it as one coherent change within its fileScope?"* — count is a proxy,
  not the rule.
- This is the slice most likely to need iteration; the spike ships a plausible prompt and
  flags eval as follow-up (see "what this doesn't prove").

## S5 — the load-bearing test: bottom-up reconciliation across an interior node

An integration test in `NodalMerge.Studio.Integration.Tests` (alongside
`MergeReconciliationServiceTests`), `MaxPlanDepth=2`:

1. Root goal → root planner emits a plan with **one `Compound` slice** (plus optionally one
   `Leaf` sibling to prove mixed trees).
2. Assert the compound slice's child unit is enqueued as a **planner** (S3), not a worker —
   check the scheduler item's profile / decision-log `stage: Plan`.
3. The interior (sub-planner) unit emits a plan with **two `Leaf` grandchildren over
   non-overlapping fileScopes** (clean merge — conflict roll-up is explicitly out of scope,
   see Open questions).
4. Assert grandchildren are forced to worker/execute (depth-2 cap), run, and produce merge
   proposals.
5. **Assert the roll-up**: grandchildren reconcile into the interior node's branch, the
   interior node reaches a completed/merged state, and *then* reconciles up into the root —
   i.e. the interior node is correctly both a merge **target** (for its children) and a
   merge **source** (for its parent). Assert the root's final branch contains **both
   grandchildren's changes**.
6. Assert the goal completes (no stranded interior node, no duplicate planners — the
   `_parentGates` semaphore and rescue-sweep idempotency hold at depth).

Green S5 is the milestone. It exercises the route decision, the existing rescue sweep, the
depth cap, and — the point — one full interior-node roll-up.

**Warning about S5's own blind spot:** S5 uses *non-overlapping* fileScopes to get a clean
merge — which is the exact shape of the peer-contract gap S6 addresses. Two disjoint-scope
slices merge green whether or not their interface actually agrees, so S5 alone gives *false
confidence*: it proves the roll-up plumbing while silently passing an interface mismatch.
S6 + S5.b are what stop that.

## S6 — explicit peer contracts (the interface-coherence gap)

**The problem (verified 2026-07-21).** `PlanSlice` is `{sliceId, goal, fileScope, dependsOn,
steps}` (`PlanDocument.cs:11-16`) — **no interface field.** The planner can only coordinate
peers via `dependsOn` (ordering) + branch seeding (the dependent inherits the dependency's
files and *reads the code* to infer the interface, `FanOutService.cs:407-425`). So a contract
today is **implicit — "read what my dependency produced" — and exists only along a dependsOn
edge.** Worse: `AutoSequenceOverlappingSiblingsAsync` only inserts a dependency when sibling
fileScopes **overlap** (`FanOutService.cs:295-296`). A backend API slice and a frontend slice
that calls it touch *disjoint* files → no overlap → no auto-dependency → they run as
uncoordinated peers with no shared contract, and because they never touch the same files
their proposals **merge with zero conflict**. Reconciliation catches nothing. UI expects
`/api/foo → {x}`, backend serves `/api/bar → {y}`, both green, both merge, integration broken.
This is the self-consistent-but-wrong failure, and it *ships* rather than stalling. It is the
single most important correctness gap for anything with a client/server or producer/consumer
seam — which is most real work.

**Note on scope independence.** This gap exists **today at flat depth 1** — it is not caused
by recursion. Recursion only makes it *more* acute (more seams, across more layers). So S6 is
independently valuable and could ship *before or alongside* the recursion spike; it also
de-risks the harder tree case. Recommend sequencing S6 first or in parallel with S1–S3.

**Design.**

- Add a parent-level `contracts` collection to `PlanDocument`: each contract is an explicit
  interface the parent planner authors (endpoint list / typed schema / IDL — start with a
  simple structured shape, not a full OpenAPI parser). Each `PlanSlice` gains `provides` and
  `consumes` naming the contract ids it implements or depends on.
- Materialize each contract as an **artifact injected into *both* the producer's and
  consumer's context** (and seeded into their branches), so both build against the *same
  declared interface* instead of each inventing one. This is contract-first: producer and
  consumer can now run **concurrently** against the shared contract rather than serializing
  through `dependsOn`.
- **Reviewer teeth.** The review/gate checks each slice's output *conforms to the contracts
  it declared* (`provides` matched against the artifact; `consumes` calls checked against it).
  This is the "conformance to the slice/projection" idea from the design thread — now with an
  explicit object to conform to, instead of hoping two workers guessed the same shape.
- **Recursion — this is the "contract coherence across the tree" mechanism.** A sub-planner
  **inherits** the contracts its parent slice declared (as fixed `provides`/`consumes`
  boundaries it must honor) and **may author new internal contracts** among its own children.
  Contracts flow *down* as immutable boundaries; new ones are authored *locally*. This is what
  keeps a deep tree from having each sub-planner reinvent a shared interface — the failure mode
  that makes deep recursion collapse at integration.

**S5.b — peer-contract test (add to S5).** Root plan declares one contract + a producer slice
(`provides`) and a *peer* consumer slice (`consumes`) over **disjoint** fileScopes. Assert:
(1) the contract artifact is injected into both workers; (2) a deliberately non-conformant
consumer (calls a field the contract doesn't declare) is **flagged by review**, not merged
green; (3) a conformant producer/consumer pair integrates and the goal completes. This is the
case S5's clean-merge would otherwise pass while hiding — S5.b is what gives the "coherent
reassembly" claim teeth for the peer seam, not just the dependency seam.

## S7 — blocking integration gate on the reconciled branch (may defer)

**The gap (verified 2026-07-21).** Merge/reconciliation is textual file-copy. The only
composite build/test — `ExecuteCompositeAsync` (`MergeReconciliationService.cs:295-315`) — is
opt-in *and* wrapped in `try { } catch { /* best-effort */ }`, so it **never blocks
acceptance**. Cross-slice conflict detection is same-file line-range only
(`MergeReconciliationService.cs:502-518`). So a rename in slice A's `Foo.cs` with a stale
caller in slice B's `Bar.cs` has zero file overlap, zero line conflict, each branch built
green *in isolation*, and the combined tree that won't compile gets accepted and (under
AgentApproval/Hybrid) auto-applied. This is the contract gap's twin, generalized to *any*
cross-file semantic dependency — not just declared interfaces.

**Why this hasn't bitten us yet — and why it's about to.** Until recently every worker ran the
**same model** against a **shared context cache**, so workers *implicitly agreed* on interfaces
and symbol names — the shared model was doing cross-slice coordination for free, masking the
missing gate. The file-scoped multi-profile work (`scoped-execute-workers-per-profile-models.md`)
deliberately breaks that: frontend on one model, backend on another. Different models make
different naming/interface choices, so cross-slice incoherence moves from *rare* to *likely*
exactly as we adopt the feature this spike is built on. The gate stops being optional the moment
workers stop sharing a brain.

**Design.**

- Make the reconciled-branch build/test **blocking, and on by default for real work** — a
  reconciled proposal that fails to compile/test must not reach Approved/Applied. The machinery
  largely exists (`ExecuteCompositeAsync`); the change is *promote best-effort → hard gate* and
  flip the default, not build from scratch.
- Failure surfaces like a conflict does today (a report on the parent branch + a task), so the
  human/rework path is the one that already exists — don't invent a new escalation channel.
- This is the **universal downstream net**: it catches the non-interface half of the
  missing-dependency gap (internal symbol deps a contract can't express), anything the planner
  missed entirely, and contract violations that slipped past review. Contracts (S6) *prevent*
  upstream and cheaply; S7 *verifies* downstream and catches everything. Want both.

**S5 amendment (do this even if S7's full default-flip is deferred).** Add an assertion that a
deliberately-broken combination — slice A renames a symbol, peer slice B keeps calling the old
name, disjoint fileScopes — is **rejected by the gate**, not accepted green. Without this, S5
proves only that files got *copied together*, not that the combined result *builds* — i.e. the
spike's "coherent reassembly" claim is hollow. A minimal blocking gate scoped to the spike's
test goal is enough to make the assertion real, even if productionizing the default is a
follow-up.

## Related gaps — captured, not yet scheduled (so we don't lose them)

Same textual-vs-semantic signature as S6/S7; noted here rather than slotted:

- **Completion is self-declared.** A worker "succeeds" on `StopReason == "end_turn"`
  (`WorkerAgentLoop.cs:138-213`) with no evidence — with defaults it can succeed having written
  nothing. Green means "the agent stopped," not "it works." Fix direction: require evidence of a
  passing check (ties to S7) before a slice counts as done.
- **`fileScope` is trusted but never enforced.** The hard block was removed
  (`McpToolDispatcher.cs:821`); actual writes are never reconciled against the declaration, and
  `AutoSequenceOverlappingSiblingsAsync` sequences only on *declared* overlap — so an
  under-declared scope silently defeats the one automated coordination mechanism. Fix direction:
  detect out-of-scope writes and feed them back into sequencing/conflict detection.
- **No independent oracle.** Worker and reviewer both reason from the same goal text and run the
  *same existing* tests (`AgentLoopPrompts.cs:344-354`); nothing derives expected behavior
  independently. This is the deepest one and only partially fixable — contract-conformance (S6)
  and the integration gate (S7) are the closest thing to an external check we can manufacture.

## Out of scope for the spike (named so they're not silently assumed done)

Conflict roll-up at interior nodes · dead-letter/replan at interior nodes · latency/critical-path
measurement · Artifact Explorer nested-tree rendering · plan-quality evals · depth 3+ (config
only, but untested here) · per-subsystem scoped test gates before roll-up.

## Open questions

- **Conflict roll-up.** When two grandchildren conflict, `MergeReconciliationService` writes
  `merge-conflict-report.md` to the parent branch and opens conflict tasks
  (`MergeReconciliationService.cs:168,236`). At an *interior* node this report lands on the
  interior branch, not the goal branch — does the human-facing surface (REST
  `StudioRestEndpoints.cs:1352` reads the report off `wu.BranchId`) find it, and does an
  unresolved interior conflict correctly block the root roll-up? Spike sidesteps this with
  non-overlapping scopes; it's the first hardening item after.
- **Depth field vs computed walk.** Store `PlanDepth` on `FanOutInfo` at creation, or
  compute by parent-walk each time? Storing is O(1) and survives cheaply; computing avoids a
  schema change. Spike computes (smaller diff); revisit if the walk shows up hot.
- **Should the root goal count as depth 0 or 1?** Pick one in S2 and assert it in S5 so the
  `< MaxPlanDepth` boundary is unambiguous; this doc assumes root goal = 0.
