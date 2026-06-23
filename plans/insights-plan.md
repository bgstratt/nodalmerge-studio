# Phase 10 — Insights: Knowledge Promotion & Prompt Improvements

## Context

The Insights tab shipped with a single analytics dashboard (`RunRetrospective` projection —
session/work-unit counts, model acceptance rates, fork win rates, failure-cause counts, review
outcomes), computed live and only ever triggered by a manual "Run Analysis" button. That covered the
*data* half of "Retrospectives" but none of the other sections from the original design discussion:
**Knowledge Promotion**, **Prompt & Process Improvements**, and **Workspace Intelligence**. This
phase turns it from a read-only dashboard into something that changes how future runs behave —
gated by human approval at every step, never automatic.

Two decisions shape every slice below:
- **Heuristics before LLM analysis.** Pattern detection starts as deterministic rules over fields
  that already exist in the DAG (rejection/evidence/fork-outcome counts), not open-ended LLM
  summarization — added later as a second, independent detector, never a replacement for the first.
- **One governance pipeline, reused twice.** Knowledge Promotion and Prompt Improvements reduce to
  the same shape (pattern detected → recommendation → human Promote/Dismiss/Investigate → durable
  effect). Built once against Knowledge Promotion (10c), then reused for Prompt Improvements (10e)
  instead of two review systems.

## 10a — Retrospectives polish + Workspace Intelligence — Complete

Extended `RunRetrospectiveProjectionPayload`/`BuildRunRetrospectiveAsync`
([ProjectionManager.cs](../src/NodalMerge.Studio.Projections/ProjectionManager.cs)) — analytics only,
no new domain concepts. Optional `Since`/`Until` on `ProjectionRequest` (default null = all-time);
derived highlight fields (`AverageReworkCycles`, `TopFailureCause`, `MostSuccessfulModel`,
`MostSuccessfulStrategy`, each with a minimum-sample-size floor so one lucky run doesn't get
crowned); fork win rates broken out by `ForkType` and sub-bucketed by structured fork-constraint
metadata (`WorkUnit.Metadata["architectureConstraint"]` etc. — exact grouping, not keyword-fuzzy, a
better signal than originally planned); model acceptance rates grouped by `(Model, Stage)` in
addition to `(Model, Provider)`. UI: period selector, highlight-card row, Workspace Intelligence
section in [InsightsPanel.ts](../clients/vscode-extension/src/panels/InsightsPanel.ts).

## 10b — `Finding` domain + review pipeline — Complete

New [Finding.cs](../src/NodalMerge.Studio.Contracts/Domain/Finding.cs), modeled on `MergeProposal`
but simpler (no build/apply step): `FindingStatus` (`Open` → `Promoted`/`Dismissed`/`Investigating`),
`FindingKind` (`KnowledgeGuideline`, `PromptImprovement`), `FindingSource`
(`Deterministic`/`LlmScan`/`Imported`), and the `Finding` record itself (`Title`/`Summary` double as
both the review-UI narrative and the literal text injected into prompts on promotion;
`SupportingDataJson` carries the triggering stat(s); `TargetStage: PipelineStage?` scopes
`PromptImprovement` findings to one pipeline stage). `IFindingService`/`FindingService`
([FindingService.cs](../src/NodalMerge.Studio.Storage/FindingService.cs)) — `ProposeAsync`,
`GetAsync`, `ListAsync`, `ReviewAsync`, `ListPromotedPromptGuidanceAsync` — persisted via the same
node-store pattern as every other entity (`StudioNodeKind.FindingV1`). REST:
`GET/POST /studio/findings`, `POST /studio/findings/{id}/review`
([StudioRestEndpoints.cs](../src/NodalMerge.Studio.Host/StudioRestEndpoints.cs)).

## 10c — Knowledge Promotion: deterministic scan + LLM scan — Complete

Two independent, manually-triggered detectors, both producing only `KnowledgeGuideline` findings:
- **Deterministic** (`FindingDetectorService.DetectDeterministicAsync`,
  [FindingDetectorService.cs](../src/NodalMerge.Studio.Host/FindingDetectorService.cs)): re-runs the
  10a heuristics against current `RunRetrospective` stats — a `ForkWinRateStat`/
  `ForkConstraintWinRateStat` with win rate ≥70% over ≥5 samples becomes a Finding. Deduped by title
  against existing Open findings so re-clicking doesn't spam the queue.
- **LLM scan** (`IInsightLlmAnalyzerService`/`InsightLlmAnalyzerService`,
  [InsightLlmAnalyzerService.cs](../src/NodalMerge.Studio.AgentRuntime/InsightLlmAnalyzerService.cs)):
  calls a real model, selected via a profile `<select>` reusing
  `AgentConfigService.resolveSpawnLlmConfig` (the same call Multi-Model Comparison already makes).
  Lives in `AgentRuntime` (not Host/Storage) because `LlmClient` is `internal` to that assembly —
  public contract in `Core.Services`, concrete implementation in the owning project, same split as
  every other cross-project service here. Gets structured output via a forced `report_findings` tool
  call, never free-text parsing; an empty/missing tool call degrades to zero findings, not an error.
  Context assembled by `ProjectionManager.BuildInsightScanContextAsync` — current stats plus a capped
  sample of rejection rationales/steering notes/review comments (bounded deliberately: a real paid
  API call per click, not free heuristics). REST: `POST /studio/insights/llm-scan`.

**Promotion** (`FindingService.PromoteKnowledgeGuidelineAsync`): records a new
`ArtifactRef(Type=Constraint, OwnedByWorkUnitId=null)` — a *global* constraint. Required adding
`IArtifactLineageService.GetGlobalConstraintsAsync` (artifacts with no owning work unit weren't
queryable before) and folding it into `BuildAgentWorkspaceAsync`'s `InheritedConstraints`. That field
had been computed since Phase 3 foundations but **never read by any agent loop** — this slice finally
wired it into the Orchestrator, Planner, *and* Worker loops' outgoing prompts (Worker initially
missed in the first pass since only Orchestrator/Planner have system-prompt construction hooks
already; closed in 10g below once Worker turned out to be the loop that most needed it — it's the
one writing the code).

UI: Findings queue in [InsightsPanel.ts](../clients/vscode-extension/src/panels/InsightsPanel.ts)
(list + Promote/Dismiss/Investigate, `Source` badge), "Detect Findings" button, profile `<select>` +
"Run LLM Scan" button — independently triggered, neither chained to the other or to anything
automatic. `NotificationManager.update()`
([NotificationManager.ts](../clients/vscode-extension/src/NotificationManager.ts)) extended to fire a
"Finding ready for review" notification.

## 10d — Insights tab structure — Complete

Folded into 10a/10c's UI work above rather than a separate slice: period selector, Retrospective
highlights, Workspace Intelligence, and the Findings queue all live in one
[InsightsPanel.ts](../clients/vscode-extension/src/panels/InsightsPanel.ts), gated entirely by manual
buttons (no scheduling, no auto-refresh-and-act).

## 10e — Prompt & Process Improvements — Complete

Adds `FindingKind.PromptImprovement`. **Redesigned mid-implementation**: the original sketch promoted
a `PromptImprovement` finding by editing the target `AgentProfile.SystemPrompt` directly. Rejected
once it became clear that a profile with an empty `SystemPrompt` falls back to a hardcoded, per-loop
`DefaultSystemPrompt` invisible anywhere in the UI — appending a suggestion to that empty field would
silently replace an agent's entire built-in instructions with one line, not improve them.

Redesigned to match how Knowledge Promotion already works: inject as appended context at call time,
never mutate the persisted profile. The only new concept is *scoping* — `Finding.TargetStage`
(`PipelineStage?`) limits a `PromptImprovement` to the one stage it's actually about, instead of
applying everywhere like a `KnowledgeGuideline` constraint does. `PromoteAsync` for this kind creates
**no artifact at all** — the durable effect is just the Finding's own `Status=Promoted` +
`TargetStage`, read directly by the matching stage via the new
`IFindingService.ListPromotedPromptGuidanceAsync(stage)`.

Two new deterministic heuristics in `FindingDetectorService.DetectPromptImprovementsAsync`: missing
test evidence on rejected proposals (→ `TargetStage=Execute`) and recurring steering-note keywords
across distinct work units (→ `TargetStage=Orchestrate`, approximate/keyword-derived, flagged as
such). The LLM scan's `report_findings` tool gained `kind`/`targetStage` per item, with defensive
parsing (bad/missing values fall back to `KnowledgeGuideline`/`null` rather than failing the scan).

## 10f — Wiring promoted guidance into outgoing prompts — Complete

Mirrors 10c's `InheritedConstraints` wiring, stage-filtered instead of universal:
`OrchestratorAgentLoop` takes `IFindingService` directly (it already self-fetches its projection
every cycle); `PlannerAgentLoop`/`WorkerAgentLoop` get it as a precomputed string combined with the
existing constraints helper at their construction sites in
[InMemoryAgentRuntimeService.cs](../src/NodalMerge.Studio.AgentRuntime/InMemoryAgentRuntimeService.cs)
(`BuildPromptGuidanceContextAsync`, new). `ReviewerAgentLoop` deliberately left out — no
context-injection mechanism exists there at all, and neither heuristic targets `Review` stage.

## 10g — Worker into global Knowledge constraints — Complete

Follow-up gap closed after 10c: Worker — the loop that actually writes code and proposes merges —
never received promoted `KnowledgeGuideline` constraints, only the new (10e) stage-scoped
`PromptImprovement` guidance. Both of Worker's construction sites
(`InMemoryAgentRuntimeService.RunScheduledWorkerAsync`'s Worker branch, and the legacy
`StartWorkerLoop` path) now also call `BuildConstraintsContextAsync`, combined with
`BuildPromptGuidanceContextAsync(Execute)` into one block appended to the kickoff message.

## 10h — Active-findings visualization + export/import ("spread the wealth") — In progress

Two real gaps remained after 10g:
1. The Findings queue only ever showed `Open` findings — once reviewed, a finding vanished from the
   UI with no way to see what's currently active.
2. Each repo/workspace runs a fully separate Studio Host with its own local storage (VS Code's
   per-workspace `storageUri`) — nothing detected or promoted in one repo is visible in another.

A live, always-on cross-repo aggregator and automatic similarity-grouping across repos' findings were
both considered and explicitly deferred — bigger scope (a new always-on process or shared store) and
real risk (false-positive grouping silently applying the wrong fix to an unrelated repo) for unproven
benefit. This slice ships the smaller, lower-risk piece: a human-reviewed, file-based export/import,
plus the visualization that's a prerequisite for it (can't export what you can't see).

- **Status filter.** `GET /studio/findings?status=` already filters server-side — the gap was
  UI-only: `sendFindings()` always fetched unfiltered and `renderFindings()` hardcoded
  `status === 'Open'`. New `<select>` (Open default / Promoted / Dismissed / Investigating / All)
  re-fetches with that status as the query param. `Promoted`/`Dismissed` cards render **read-only**
  (no Promote/Dismiss/Investigate buttons — `ReviewAsync` has no revert path, so showing those would
  imply an undo that doesn't exist); `Open`/`Investigating` stay actionable. Promoted cards also
  surface `ReviewedAt`/`ReviewNotes`/`PromotedArtifactId` (already on the record, just unrendered).
- **Export** (no new REST endpoint — the webview already holds the fetched finding objects): when
  the filter is `Promoted`, each card gets a checkbox plus "Select All"/"Export Selected" in the
  toolbar. Exports the minimal portable shape per finding (`kind`/`title`/`summary`/`targetStage`),
  deliberately excluding `findingId`/`createdAt`/`status`/`source`/`promotedArtifactId` — meaningless
  or actively wrong in a different repo. Written via `vscode.window.showSaveDialog` +
  `vscode.workspace.fs.writeFile`.
- **Import** (one new endpoint, since it creates real `Finding`s via `IFindingService`):
  `FindingSource` gains `Imported`. `POST /studio/findings/import` validates each entry (`kind` must
  parse; `title`/`summary` non-empty; `targetStage` required and must parse when
  `kind=PromptImprovement`) and **skips invalid entries rather than rejecting the whole batch** — one
  malformed entry in a hand-edited file shouldn't sink the rest. Valid entries land as new `Open`
  findings, `Source=Imported`, fresh `FindingId`/`CreatedAt` — reviewed in the destination repo like
  any other finding, never pre-promoted. `InsightsPanel.ts` gets an "Import Findings…" button next to
  "Detect Findings"/"Run LLM Scan" — a third way findings enter the queue.

## Slices

| Slice | Scope | Status |
|---|---|---|
| 10a | Retrospective highlights, date-range filtering, Workspace Intelligence sub-bucketing | Complete |
| 10b | `Finding` domain + `IFindingService` review pipeline + REST | Complete |
| 10c | Deterministic + LLM-scan detectors; global Constraint promotion; `InheritedConstraints` wired into Orchestrator/Planner | Complete |
| 10d | Insights tab UI structure (period selector, highlights, Workspace Intelligence, Findings queue) | Complete |
| 10e | `PromptImprovement` findings, stage-scoped context-injection promotion (redesigned from profile-editing) | Complete |
| 10f | Promoted guidance wired into Orchestrator/Planner/Worker outgoing prompts | Complete |
| 10g | Worker wired into global `KnowledgeGuideline` constraints (gap closed post-10c) | Complete |
| 10h | Status filter + read-only history view; export Promoted findings to file; import findings from file | In progress |

## Non-goals (every slice)

- No scheduling or background triggers — "Run Analysis", "Detect Findings", "Run LLM Scan", and
  import are all manual actions. The LLM scan is never chained automatically after the deterministic
  one or after a regular run.
- Findings never auto-apply, regardless of source. Promote/Dismiss/Investigate is always a human
  action; import always lands as `Open`, never pre-promoted.
- No `AgentProfile.SystemPrompt` mutation, ever, for Finding promotion (10e's redesign).
- No revert/un-promote action.
- No live cross-repo selector/aggregator, no automatic similarity grouping across findings — both
  considered for 10h and deferred (see 10h above).

## Verification

1. `dotnet build NodalMerge.Studio.slnx` / `dotnet test` — 0 errors, full pass. Extension
   `tsc --noEmit` / `npm run compile` — 0 errors.
2. Manual (10c): click "Detect Findings", confirm expected heuristic findings appear; separately run
   "Run LLM Scan" with a configured profile and confirm it produces zero or more findings tagged
   `Source=LlmScan`, never crashes on a plain-text (non-tool-call) model reply, and never fires
   unless the button is clicked.
3. Manual (10c/10f/10g): promote a `KnowledgeGuideline` finding; confirm a global `ArtifactRef` is
   recorded, a fresh work unit's `AgentWorkspace` projection includes it in `InheritedConstraints`,
   and a fresh Orchestrator/Planner/Worker run's outgoing prompt actually contains the text.
4. Manual (10e/10f): promote a `PromptImprovement` finding targeting `Execute`; confirm a fresh
   Worker run's kickoff message contains the guidance text and `AgentProfile.SystemPrompt` is
   untouched. Repeat for an `Orchestrate`-targeted one against a fresh Orchestrator run.
5. Manual (10h): switch the status filter through all five options; confirm action buttons appear
   only for `Open`/`Investigating`. Export 1-2 Promoted findings, inspect the JSON (only
   `kind`/`title`/`summary`/`targetStage` present), import that file back in, confirm new `Open`
   findings appear tagged `Source=Imported`. Hand-edit the file to add one malformed entry and
   confirm import still succeeds for the valid entries, reporting the malformed one as skipped.
