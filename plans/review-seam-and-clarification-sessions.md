# Review mode through the executor seam + clarification session robustness

## Status

- [x] S1 — clarification sessions: synthetic-session fallback + real sessionIds threaded —
      shipped 2026-07-13 (commit 8b52cf9), 775/775 tests green. Fallback chain landed exactly as
      specified below; `ClarificationSessionFallbackTests` covers both the synthetic and
      goal-node tiers.
- [x] S2 — `HarnessMode.Review` through the seam — shipped 2026-07-13, full solution 780/780
      green (5 new tests in `HarnessReviewModeSeamTests`: claude Approved/Rejected/missing-verdict,
      codex Approved, and the inline-site claude-cli routing test). Landed as specified below,
      plus two things discovered while wiring:
      - `InlineReviewerService` now resolves `GetCredentialsForStage(…, Review)` BEFORE the
        orchestrator-credentials fallback (both on the work unit and on the parent walk) — the
        user's per-role Review Model Profile was previously ignored at the inline site even for
        API providers; only the enqueue site (`AutomatedReviewGateService`) honored it.
      - `RunScheduledWorkerAsync` no longer constructs `DefaultAgentToolClient`/LlmClient at all —
        with Review behind the seam, all three stage branches build a `HarnessRunRequest` and the
        native executor constructs its own client. The scheduled-site loud-fail gate for CLI
        providers on Review roles is deleted.
      Known scope note: `ResupplyCredentialsAsync`/`ResolveAndPersistCredentialsAsync` still
      requires a non-blank baseUrl to register anything (predates CLI providers) — a claude-cli
      resupply needs a placeholder baseUrl. Cosmetic for now (the CLI adapter ignores it); worth
      folding into any future credential-model cleanup.

Follow-up to plans/harness-hosting-architecture.md (Phases A–D complete, real-CLI smokes passed
2026-07-13). Motivation, verbatim from the driving conversation: a claude-cli-only Agent Topology
currently stalls at every review gate — "we'd create a goal, plan, fanout slices, work them, say
we're done and... then not review them?" Review should route to whatever harness the user
configured, exactly like Plan (D1) and Execute (B1) already do. With S2 in place, a goal can go
from creation to materialized-back-into-the-user's-repo with either human or agent review at both
gates (task-level and workspace-level), on any registered executor.

## S1 — Clarification session robustness

**Bug (found by the C3 real-CLI smoke, 2026-07-13):** `ClarificationCommandService.RequestAsync`
only appends the `ClarificationRequested` execution event when a sessionId resolves — and that
event is the *sole* source `ListActiveRequestsAsync`/`RespondAsync`/`ClarificationTimerService`
read from. A blocking request with no resolvable session still parks the work unit
(`MarkAwaitingResumeAsync` + Waiting) but is invisible in the Clarification Inbox and unanswerable
— a stuck goal with no visible cause. Known session-less enqueue paths: `ContinueService`'s
park-re-enqueue (`sessionId: null` explicitly), `WorkUnitCommandService.RequeueAsync`'s member
re-enqueues (omits it), and any direct REST/MCP scheduler caller.

**Fix (three parts):**
1. **Central guarantee** — `ResolveSessionIdAsync` gains two more fallbacks after the existing
   pending-scheduled-item lookup: (a) the owning goal's `GoalNode.SessionId` (walk
   `ParentWorkUnitId` to the root, match `IGoalNodeService.ListAsync` on `WorkUnitId`); (b) a
   synthetic `wu-{workUnitId}` session as the never-null last resort. The event append becomes
   unconditional. The synthetic id is a real, valid ExecutionEventStream key — everything that
   reads these events already queries by kind across sessions.
2. **`ContinueService`** — resolve the goal session the same way for its park-re-enqueue instead
   of hardcoding `sessionId: null`.
3. **`WorkUnitCommandService.RequeueAsync`** — resolve the root goal's session once and thread it
   through the member re-enqueues (and status updates).

Acceptance: integration test proving a `RequestAsync` with no explicit session, no pending
session, and no goal node still lands in `ListActiveRequestsAsync` and round-trips
`RespondAsync`; a second test proving the goal-node fallback wins over the synthetic one.

## S2 — Review mode through the executor seam

**Current state:** two Reviewer construction sites bypass the seam — the scheduled Review-stage
branch in `InMemoryAgentRuntimeService.RunScheduledWorkerAsync` (fed by
`AutomatedReviewGateService.TryEnqueueReviewerAsync` + dead-letter retries) and
`InlineReviewerService` (fed by `AutoReviewRule` at the BeforeMerge checkpoint, which is how BOTH
`TaskReviewPolicy` and `WorkspaceReviewPolicy` AgentApproval/Hybrid gates run). Both construct
`ReviewerAgentLoop` + `DefaultAgentToolClient` directly; a CLI provider either hits the explicit
loud-fail gate (scheduled) or produces garbage HTTP calls (inline — pre-empted by the creds check).

**Design — mirrors D1 (Plan mode) exactly:**

- `HarnessMode.Review` added to the enum; `HarnessCapabilities.SupportsReviewMode` added as a
  trailing optional flag (default false — additive, no adapter/test churn). True for native,
  claude-code, codex.
- `HarnessRunRequest.TaskId` carries the proposalId for Review runs — the same convention the
  scheduled branch and dead-letter entries already use.
- **NativeHarnessExecutor** gains a Review branch wrapping `ReviewerAgentLoop` (fetches the
  proposal itself for filesTouched/noFileChangesJustification via `IMergeService`), so native
  remains the reference implementation and the capability-miss degrade target.
- **Scheduled site**: the Review branch resolves via `ResolveForProvider(provider,
  profile?.Executor)`, degrading to native on a capability miss — and the "claude-cli cannot run
  Review-stage roles" loud-fail gate is deleted.
- **Inline site**: `InlineReviewerService` resolves the same way and runs the executor inside the
  existing `TrackInlineAgentAsync` wrapper; all its post-run bookkeeping (approved check,
  inconclusive-review dead-lettering, evidence nodes) is executor-agnostic and stays put.
- **Contract**: before spawn, a Review-mode CLI run materializes `.workspace/review-request.json`
  (proposalId, goal, summary, changeDescription, filesTouched, noFileChangesJustification,
  targetBranch, diff) — shared helper on `HarnessHarvestPipeline`. The harness works in the
  proposal's source branch workdir, so the actual changed files are just *there*; the request file
  is the metadata + diff framing.
- **Verdict channel**: `.workspace/review.json` — `{"decision":"Approved"|"Rejected",
  "verificationResults":"...","consideredArtifactIds":[]}` (file-based like plan.json, so codex
  gets it without MCP; a future MCP verdict tool is an optional upgrade, not required).
  `HarnessHarvestPipeline.HarvestAsync` grows a Review branch: parse/validate the verdict
  (decision must parse; verificationResults required — same rule the native `nm_v1_merge_review`
  dispatcher enforces) and call `IMergeService.AutomatedReviewAsync(proposalId, decision,
  verificationResults, reviewerAgentId, consideredArtifactIds)` — the exact call the native tool
  makes, so all downstream behavior (rejection retry cycles, evidence, timers) is identical by
  construction. Missing/invalid verdict → Stalled (dead-letter/Continue handle it like any
  inconclusive review). No plan-mode-style diff discard: the branch under review legitimately
  differs from main.
- **claude-code allowlist (Review)**: `Read({workDir}/**)` + `Edit({workDir}/.workspace/review.json)`
  + the detected build/test `Bash(...)` entries (a reviewer verifies) + the `mcp__nodalmerge-harness`
  entry when mounted. Codex: prompt-only enforcement, same posture as its Plan mode.
- **Kickoff prompts**: Review-mode variants on both adapters — read the contract files +
  review-request.json, investigate/verify, write the verdict file, modify nothing else.

Acceptance:
- Stub-CLI Review-mode tests (both adapters): verdict file → proposal Approved (and a Rejected
  variant carrying verificationResults); missing verdict → Stalled.
- Inline path: `InlineReviewerService` with claude-cli creds routes to the stub CLI executor and
  an approved verdict unblocks `AutoReviewRule` (AgentApproval policy applies the proposal).
- Native regression: scheduled + inline review paths behave exactly as before for API providers.
- Full suite green; parent plan + this file's status updated.

## Explicitly out of scope

- Orchestrator role behind the seam (separate, pre-existing "orchestrator LLM-loop → pure
  service" direction).
- An MCP verdict tool for claude (`nm_v1_merge_review` on the harness mount) — file-based verdict
  ships first; the mount upgrade is a follow-up if verdict fidelity needs it.
- Clarification "agent-first" response-policy tiers (agent answers, then human, then timeout) —
  the three responder paths (human REST/UI, external-MCP agent, timer) all exist; a policy knob
  combining them is future work.
