# Phase 6 — Policy Gates, Worker Routing & Merge Intelligence

Phase 5 made the durable artifact DAG visible and steerable; Phase 5.5 made every layer beneath it
actually durable (a restart loses nothing) and replaced the last no-op stubs with real
implementations. Phase 6 closes the remaining correctness gap inherited from Phase 3/4 — conflict
handling between parallel workers is advisory-only, detected after the fact, not prevented — and
turns two single-purpose hacks (the hardcoded `"worker"` profile, the line-level `FilesTouched`
overlap check) into general-purpose primitives: a pluggable policy gate, and declarative
file-scope-based worker routing.

This phase was scoped from a fresh top-to-bottom gap audit of the repo (post-5.5), not just the
old Phase 6 pointer in `phase-5-control-plane-ui.md` — see "Already resolved" and the new "Phase 7
pointer" below for what changed as a result.

---

## What Phase 6 adds

| Phase 5.5 | Phase 6 |
|-----------|---------|
| Overlapping `fileScope` between sibling slices is advisory only (`ConflictWarning`), real conflicts only ever caught by the merger after both workers finish | Overlapping `fileScope` can be blocked **before enqueue**, via a general-purpose policy gate — opt-in, not a forced behavior change |
| No cross-stage validation primitive — each service enforces its own rules ad hoc, nowhere to plug in a new rule without editing that service | `IPolicyGateService`: pluggable rules checked at defined pipeline checkpoints, violations visible in the existing decision log |
| Every fanned-out child gets `"worker"` (heuristic) or an LLM guesses among **every** registered profile, including non-Execute ones (the bug fixed earlier this session) | Worker profiles can declare file-scope glob patterns; routing is deterministic first (free, instant), LLM/heuristic only as fallback |
| Merge conflict detection is whole-file: two proposals "conflict" if they both touched the same file at all, even at opposite ends of it | Line-range-aware overlap: two proposals only conflict if their *changed line ranges* in a shared file actually intersect |
| `MaxConcurrentWorkers`/`SchedulerPollIntervalMs` are config-file-only (this session's addition) | Runtime-mutable via `/studio/options`, same pattern as `UseLlmProfileSelection` |
| Some MCP tools return `{ data: null }` for a not-found id — indistinguishable from "found, genuinely empty" | Explicit `error` response for not-found, matching every other tool's contract |

---

## Slice 14a — Policy/Validator Pipeline Primitive

Foundational slice — 14b is the first real rule built on top of this, but the seam itself ships
empty (zero registered rules) and must be a no-op against every existing behavior.

### Design

New interfaces in `NodalMerge.Studio.Core/Services/ServiceContracts.cs`:

```csharp
public enum PolicyCheckpoint
{
    BeforeEnqueue,    // FanOutService.EnqueueChildWorkerAsync, before scheduler.EnqueueAsync
    ProposalCreated,  // IMergeService.ProposeAsync
    BeforeMerge,       // MergeReconciliationService, before producing a reconciled candidate
}

public sealed record PolicyViolation(string RuleId, string Message);
public sealed record PolicyResult(bool Allowed, IReadOnlyList<PolicyViolation> Violations);

public interface IPolicyRule
{
    string RuleId { get; }
    PolicyCheckpoint Checkpoint { get; }
    Task<PolicyResult> EvaluateAsync(IReadOnlyDictionary<string, object?> context, CancellationToken ct = default);
}

public interface IPolicyGateService
{
    Task<PolicyResult> EvaluateAsync(PolicyCheckpoint checkpoint, IReadOnlyDictionary<string, object?> context, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListRuleIdsAsync(CancellationToken ct = default);
}
```

- `PolicyGateService` (new, `NodalMerge.Studio.Storage`) runs every registered `IPolicyRule` whose
  `Checkpoint` matches, aggregates violations. Rules are resolved via DI (`IEnumerable<IPolicyRule>`)
  so adding a new rule later is "register one more class," not "edit the gate."
- `context` is a loose bag (`workUnitId`, `parentWorkUnitId`, `fileScope`, etc.) rather than a
  typed-per-checkpoint payload — different checkpoints have genuinely different available data, and
  a shared closed type would force every rule to handle fields it doesn't need. (Revisit if this
  proves too loose in practice — a discriminated union per checkpoint is the natural escape hatch.)
- A new `OrchestrationAction.PolicyBlocked` enum value (`ExecutionEventPayloads.cs`) — `Escalate`
  already means something specific (11e's dead-letter path) and reusing it would conflate two
  different kinds of "something stopped this." Violations get recorded via the existing
  `IOrchestrationDecisionLogService.RecordAsync` — no new store, reuses 10e's decision log so
  violations show up in the Artifact Explorer for free.
- `GET /studio/policies` — lists registered rule ids (visibility only; no per-rule toggle in this
  slice, that's 14b's `BlockOverlappingFileScope`-style toggle, scoped per rule, not generic here).

### Success criteria
- Zero registered rules → every checkpoint call returns `Allowed = true` with no violations — full
  existing test suite passes completely unchanged (this slice changes no behavior by itself).
- A trivial test-only rule (e.g. "goal must not be empty") registered at `BeforeEnqueue`, evaluated
  against a real enqueue call, rejects it and the rejection is visible via
  `IOrchestrationDecisionLogService.GetEventsAsync`.

---

## Slice 14b — Built-in Policy: Non-Overlapping FileScope Gate

The first real rule, and what actually answers the 10f.5/11b deferred decision (region locking vs.
"existing advisory warning is sufficient") — not with region-level locking, but at the coarser
granularity the planner already expresses things in: whole-file `fileScope` lists.

### Design

- `NonOverlappingFileScopeRule` (new, implements `IPolicyRule`, `Checkpoint = BeforeEnqueue`):
  given the plan's full slice set and the children already created (same data
  `FanOutService.BuildSliceMapAsync` already has), reject enqueueing a slice whose `fileScope`
  intersects a *sibling* slice that's already enqueued or running.
- **Opt-in, default off** — new `WorkspaceOptions.BlockOverlappingFileScope` (default `false`).
  When `false`, behavior is byte-for-byte what it is today: `ConflictWarning` still fires, nothing
  is blocked. When `true`, the second of two overlapping slices is rejected at `BeforeEnqueue`
  instead of being enqueued — `FanOutService` needs a real disposition for a rejected slice (not
  silently dropped): mark the child work unit with a new status or `FanOutInfo` flag that the
  Artifact Explorer surfaces as "blocked — overlaps with {other slice}," since something has to
  let a human (or a future re-planning step) actually resolve it.
- Runtime-mutable via `/studio/options`, same mechanism as `UseLlmProfileSelection` (12d) and the
  concurrency settings (14e below) — one settings panel, one persistence path.

### Success criteria
- Toggle off (default): two slices with overlapping `fileScope` both enqueue; `ConflictWarning`
  fires as it does today — unchanged behavior, full existing suite (including
  `FanOutConcurrencyTests.cs`) passes with no modification.
- Toggle on: the second overlapping slice is rejected at enqueue, visible as a `PolicyBlocked`
  decision-log entry; the first slice proceeds and runs normally.

---

## Slice 14c — Declarative File-Scope/Domain Worker Routing

Closes the gap surfaced this session: today, routing a child to a specific worker profile is
either hardcoded (`"worker"`, always) or fully LLM-judgment-based (infers a fit from a 160-char
system-prompt excerpt). Adds a deterministic middle tier that costs nothing and needs no LLM call.

### Design

- `AgentProfile` (`Contracts/Domain/AgentProfile.cs`) gains `IReadOnlyList<string> FileScopePatterns`
  (glob patterns, e.g. `src/**/*.tsx`; empty list = "no declared specialty," unchanged from today).
- New step in `FanOutService.EnqueueChildWorkerAsync`, **before** calling
  `IProfileSelectionService.SelectProfileAsync`: if exactly one `Execute`-stage profile's
  `FileScopePatterns` match every path in the child's `fileScope`, select it directly — no LLM
  call, works even with `UseLlmProfileSelection` off. Zero matches or more than one match: fall
  through to the existing `IProfileSelectionService` path completely unchanged (heuristic or LLM,
  per today's logic — including this session's stage filter).
- `AgentConfigPanel.ts`'s profile editor gains a "File scope patterns" text field (comma-separated
  globs) alongside the existing Stage/AllowedTools/MaxIterations fields; `PipelineProfile` (the TS
  mirror of the backend `AgentProfile`) gains the same field.
- Decision-log entry (already recorded for every child per 12d) gains a `matchedPattern: true/false`
  field alongside the existing `usedLlm` field, so it's auditable from the Artifact Explorer which
  of the three paths (pattern match / LLM / heuristic) actually selected the profile.

### Success criteria
- Two `Execute`-stage profiles registered with non-overlapping glob patterns (e.g. `**/*.tsx` vs.
  `**/*.cs`); a plan slice whose `fileScope` is entirely `.tsx` deterministically routes to the
  matching profile — confirmed via the decision log's `matchedPattern: true`, zero LLM calls, even
  with the toggle off.
- A slice whose `fileScope` matches zero or more than one profile's patterns behaves exactly as it
  does today (heuristic default, or LLM selection if the toggle is on).

---

## Slice 14d — Line-Range-Aware Merge Conflict Detection + Real Diff in Merge Review

Scoped down from the old Phase 6 pointer's "AST-level conflict detection" after checking what
exists today: `FileSystemWorkspaceService.DiffAsync` (the only diff primitive in the codebase)
compares whole-file text equality — `if (sourceText != targetText) modified.Add(file)` — it has no
concept of *which lines* changed, let alone syntax structure. True AST-aware diffing needs a
parser per supported language; that's a bigger investment than the rest of this phase. This slice
ships the realistic, broadly-useful step — line-range overlap — and leaves syntax-aware diffing as
a stretch goal (see Phase 7 pointer).

Widened mid-design (while investigating an unrelated DAG Replay bug) after noticing
`MergeReviewPanel.ts` doesn't actually show a diff today: its "file changes" section dumps full
before/after file content side-by-side with no line highlighting at all, and its "Combined diff
summary" section *looks* like it renders unified-diff hunks (it has `+`/`-`/`@@` CSS classes) but
is fed by `DiffAsync`'s whole-file `+++ ADDED:`/`~~~ MODIFIED:`/`--- DELETED:` lines — never real
hunks, so that section has been decorative dead weight. Since this slice is already building the
first real line-diff primitive the codebase has ever had, building it once and using it for both
conflict detection *and* the review UI avoids standing up two diff engines (a C# one for
conflict-range comparison, a separate one — likely duplicated in TS — for display) and the
throwaway UI work that would mean if the UI version were built first and ad hoc.

### Design

**Shared diff primitive** (new — `NodalMerge.Studio.Merge`):
- `DiffLine`/`DiffHunk` records (new, `NodalMerge.Studio.Contracts/Domain/`) — a hunk has
  `BeforeStart`/`BeforeCount`/`AfterStart`/`AfterCount` plus its ordered `DiffLine`s, each tagged
  `Context`/`Added`/`Removed` with the relevant before/after line number(s). Same shape `git
  diff`/unified-diff hunks use, since both target render modes (below) need to derive from it.
- `LineDiffer` (new, static, `NodalMerge.Studio.Merge`) — a small Myers-diff (or an existing NuGet
  package) computing `IReadOnlyList<DiffHunk> Diff(string beforeText, string afterText, int
  contextLines = 3)`. Stateless/pure, so a plain static method, not a DI-registered service —
  there's exactly one implementation and no seam anything needs to swap.

**Conflict detection** (`MergeReconciliationService`):
- The overlap check currently short-circuits on `FilesTouched` *set intersection* — two proposals
  "conflict" if they both appear in each other's touched-file list, regardless of where in the
  file. Add a second pass for files in that intersection: diff each proposal's branch against the
  common `base/{proposalId}` snapshot (13e's real snapshot primitive — `ProposalReviewService`
  already reads from this exact branch, see below) via `LineDiffer`, take the `Added`/`Removed`
  line ranges from each proposal's hunks, and only escalate to a conflict report if a file's two
  range-sets actually intersect.
- Two proposals each adding a different, non-overlapping function to the same file: no longer
  falsely flagged — this is the common case for parallel fan-out today, and currently always
  produces a conflict report purely because both proposals touched the file.

**Merge Review diff display** (`ProposalReviewService` + `MergeReviewPanel.ts`):
- `ProposalReviewService.GetFileChangesAsync` already loads full `before`/`after` text per file
  from `base/{proposalId}` vs. the proposal's source branch (`ProposalReviewService.cs:28-29`) —
  it just discards the structure and hands `ProposalFileChange` the raw strings. Run that same pair
  through `LineDiffer` and add the resulting `IReadOnlyList<DiffHunk> Hunks` to `ProposalFileChange`
  (kept alongside the existing `beforeContent`/`afterContent` fields — "Open Diff in Editor" still
  needs the full text for `vscode.diff`).
- `MergeReviewPanel.ts`'s `renderFileChanges` renders from `Hunks` instead of dumping full content:
  a per-file toggle (inline-unified ↔ side-by-side-split) switches which of two render functions
  consumes the *same* hunk data — no second backend call, no second diff computation. Inline shows
  one pane with `+`/`-`/context lines and `@@` hunk headers; split keeps today's two-column layout
  but only highlights/aligns the actually-changed lines instead of dumping whole files. Remember
  the last-chosen mode in webview state (`vscode.setState`) so it persists across proposals in the
  same session.
- The now-genuinely-real "Combined diff summary" section either gets the same hunk-based treatment
  (rendering the reconciled proposal's aggregate hunks) or is removed if `section-files`' per-file
  view already covers it fully — decide once the per-file view is in place and it's clear whether
  the aggregate section adds anything `section-files` doesn't already show.

### Success criteria
- Two proposals each append a distinct function at different points in the same file: merger
  combines them without a conflict report (today this incorrectly flags as a conflict).
- Two proposals editing literally the same lines: conflict report still fires, same as today.
- Opening a modified file's change in Merge Review shows real `+`/`-` line-level diff content (not
  a full-file dump) in both inline and side-by-side modes, toggleable without a page/data reload.
- `ProposalFileChange`'s new `Hunks` field and `MergeReconciliationService`'s overlap pass are
  backed by the exact same `LineDiffer.Diff` call — no second diff implementation anywhere.

---

## Slice 14e — Runtime-Mutable Scheduler Concurrency Settings

Small, independent of everything else in this phase.

### Design

- `MaxConcurrentWorkers`/`SchedulerPollIntervalMs` (added to `WorkspaceOptions` this session,
  currently config-file-only / restart-required) become mutable via the existing
  `/studio/options` GET/POST endpoint, persisted via `RuntimeSettingsService` (13d) — same pattern
  `UseLlmProfileSelection` already uses, just two more fields on the same settings payload.
- Artifact Explorer settings gear gains two number inputs next to the existing checkbox.

### Success criteria
- `POST /studio/options { maxConcurrentWorkers: 5 }` takes effect on the scheduler's next poll
  iteration without a restart, and the new value survives a restart (per 13d's rehydration pattern).

---

## Slice 14f — MCP Tool Not-Found Semantics

Small, independent. Found while cleaning up nullability warnings earlier this session.

### Design

- `StateTools.CheckoutKnownGoodAsync` and `WorkUnitTools.UpdateAsync` (and any other MCP tool
  found via a full grep for the same `McpJson.Ok(<possibly-null lookup result>)` shape — the audit
  this session only spot-checked the two that triggered a compiler warning, there may be others
  that happen not to trigger CS8604 but have the same behavior) return `McpJson.Error(tool, "...")`
  when the underlying lookup returns null, instead of `Ok` wrapping `data: null`.

### Success criteria
- Calling `nm_v1_state_checkoutKnownGood` with a nonexistent id returns an explicit
  `{ status: "error", message: "..." }`, not `{ data: null }`.
- The CS8604 nullable-reference warnings on these two call sites are gone, and a full repo grep for
  the same pattern turns up no other instances (or any found are fixed too).

---

## Slice ordering

14a → 14b → 14c → 14d, with 14e/14f independent and doable anytime (including in parallel with the
rest, or first, since they're the cheapest).

- **14a before 14b**: 14b is a rule registered against 14a's framework — there's nothing to build
  it on top of otherwise.
- **14b before 14c**: not a hard dependency, but both touch `FanOutService.EnqueueChildWorkerAsync`;
  sequencing avoids rebasing one slice's changes against the other mid-phase.
- **14d** has no dependency on 14a–14c — it's entirely inside the merge/review stage (Merge service
  + Merge Review panel, no fan-out/routing changes) — ordered last among the "real" slices only
  because it's the largest single piece of new surface (a real line-diff algorithm doesn't exist
  anywhere in the codebase yet, and it now also touches the review webview) and benefits from being
  tackled once, not interleaved with smaller changes.
- **14e/14f** are both small, independent, and low-risk — good candidates to do first if you want
  early wins while 14a–14d are still being designed/reviewed, or last as cleanup. No reason to fix
  their position in the sequence.

---

## Already resolved (removed from the old Phase 6 pointer)

- **"Persistent branch history: branches survive server restart (real blob store, not `WsOnly`)"**
  — this was written before Phase 5.5 existed. Closed by 5.5's slice 13a: `NodalMergeBranchService`
  now seeds child branches via `IFileWorkspaceService.InitBranchAsync` in production, and
  `ProductionStorageIntegrationTests.cs`'s restart test proves both branch metadata and file content
  survive a second `StudioWebApplication` instance built against the same SQLite/file-blob paths.
  Nothing left to do here.

---

## Phase 7 pointer (future)

Carried forward from the old Phase 6 pointer, plus one addition surfaced by 14d's scoping:

- **Cross-repo work units**: `WorkspaceOptions.SeedRepositoryPath` is a single nullable string
  today, mutated as a shared singleton field (`InMemoryWorkUnitService.cs`) rather than per-work-unit
  state — real multi-repo support needs this to become a real per-work-unit array, not a process-wide
  setting that the next work unit created could clobber.
- **Collaborative steering**: multiple humans editing the work unit DAG simultaneously (CRDT/OT).
- **True syntax-aware (AST-level) merge diffing**: only worth it once 14d's line-range-aware
  approach has been used in practice and line-range overlap (vs. real syntactic non-conflict, e.g.
  two edits on adjacent lines that don't actually interact) turns out to still produce too many
  false-positive conflict reports.
- **Region-level (not whole-file) conflict prevention**: 14b deliberately operates at the same
  whole-file granularity the planner's `fileScope` already uses. If two workers need to safely
  co-edit *different regions* of the same file as a common case, that needs the finer-grained
  intent-graph region locking that 10f.5/11b explicitly deferred — revisit only if 14b's coarser
  gate turns out to block work that should have been allowed to proceed.
