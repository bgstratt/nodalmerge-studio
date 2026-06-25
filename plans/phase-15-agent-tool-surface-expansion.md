# Phase 15 — Agent Tool Surface Expansion (Semantic Navigation, Clarification, Doc Fetch, Workspace Status)

This phase adds the highest-leverage missing tools for Orchestrator/Planner/Worker loops while preserving
NodalMerge Studio's core safety model: deterministic branch-scoped execution, reproducible decisions, and
human-gated merge.

The focus is intentionally narrow:

- Category 1 (high-value, low-risk):
  - compiler-backed semantic navigation (LSP-like tools)
  - explicit clarification/human-question primitive
- Category 2 (useful, design-sensitive):
  - external documentation fetch with evidence capture
  - lightweight workspace status/diff helper for "what changed"

Explicitly deferred in this phase:

- generic unrestricted terminal/runner tool
- generic "approve any irreversible action" gate outside existing merge/review controls

## Why this phase now

Current agent workflows are strong on branch-scoped file operations (`workspace_read`, `workspace_search`,
`workspace_write`, `workspace_replace`) and execution (`workspace_build/test/exec`). The biggest remaining
accuracy gap for coding agents is semantic code understanding.

Today (text-first):

```text
search symbol -> read files -> infer definition/usages
```

Target (compiler-backed):

```text
find_definition -> find_implementations -> find_references
```

For large .NET codebases, this reduces hallucinated relationships and improves plan slicing, worker edit
precision, and review confidence.

## Design constraints

- Preserve MCP v1 naming/versioning discipline (`nm_v1_*`, additive only).
- Preserve transport consolidation (MCP/REST/dispatcher share command services).
- Preserve branch/work-unit authority model (workUnitId-resolved branch context where applicable).
- Preserve reproducibility: any external content used in reasoning must be snapshotted and attributable.
- Preserve existing human gate for merge as the canonical irreversible gate in v1.

## Scope and slices (dependency-value order)

| Slice | Focus | Status |
|---|---|---|
| 15a | Semantic navigation service foundation (`definition`, `references`, `implementations`) | Done |
| 15b | Semantic MCP/REST/dispatcher integration + profile gating | Done |
| 15c | Semantic adoption rules in prompts/profiles (semantic-authoritative routing) + safe read-only semantic extras | Done |
| 15d | Clarification workflow as orchestration state transition (not in-loop wait) | Done |
| 15e | Clarification inbox/respond/resume UX + clarification metrics | Done |
| 15f | Lean workspace status helper (`workspace_status`) for changed-files/proposal summary | Done |
| 15g | External documentation fetch with provenance snapshot + artifact lineage | Planned |

## Slice 15a — Semantic navigation service foundation

Add a language-service-backed semantic query surface for repositories in a branch workspace.

### Tools (proposed)

- `nm_v1_workspace_symbol_definition`
- `nm_v1_workspace_symbol_references`
- `nm_v1_workspace_symbol_implementations`

First cut remains read-only. Symbol mutation is out of scope in this phase.

### Behavior

- Input supports either:
  - file + position/line context + symbol, or
  - symbol + optional path hint.
- Output returns canonical locations (`path`, line, column) and optional containing symbol/type.
- Empty result is valid; malformed/ambiguous query returns structured error.

### Service architecture

- New command service interface in Core Services (transport-agnostic).
- Implementations:
  - Primary: language-server-backed resolver (Roslyn-first for .NET).
  - Fallback: explicit unsupported/partial result per workspace stack.
- Keep branch-scoped path mapping (branch workspace path -> symbol locations relative to branch root).

### Safety

- Read-only capability.
- Max results cap and payload truncation to avoid context bloat.
- No cross-branch leakage.
- Explicitly no `rename symbol` capability in the first cut.

## Slice 15b — Semantic MCP/REST/dispatcher integration and profile controls

Wire 15a into all three surfaces consistently:

- MCP tool constants and schemas.
- Dispatcher routing (Orchestrator/Planner/Worker tool loop).
- REST parity endpoints.

Add profile-level allow/deny controls so teams can roll out semantic tools incrementally.

### Success criteria

- If enabled in profile: tool callable by loop and external MCP client.
- If not enabled: structured "tool not permitted" response.
- Same service code path for MCP/REST/dispatcher.

## Slice 15c — Semantic adoption rules and safe semantic read surface

Prevent fallback to old habits once semantic tools exist.

### Prompt/profile rule (required)

When semantic tools are available, they are authoritative for symbol-relationship questions.

- Definition/reference/implementation questions -> semantic tools.
- Text/content questions -> `workspace_search`.

This rule is added to Planner/Worker/Reviewer prompt guidance and profile documentation so agents do
not continue overusing plain search for semantic queries.

### Safe semantic read extras (optional in this slice)

- `hover/type info`
- `diagnostics`

These remain read-only and can ship now if low effort, or immediately after 15a/15b behind the same
feature flag.

## Slice 15d — Clarification workflow as orchestration state transition

Introduce a structured, auditable clarification request instead of forcing guesses.

### Core design choice

Treat clarification as a workflow transition, not an in-loop blocking wait.

```text
Agent action
  -> ClarificationRequested
  -> Paused (AwaitingClarification)
  -> Human response captured
  -> Resume
```

### Tool/event trigger (proposed)

- `nm_v1_clarification_request`

### Request shape

```json
{
  "workUnitId": "WU-123",
  "question": "Should we enforce unique email at API or DB layer?",
  "context": "Current schema has no unique index; both options affect migration order.",
  "blocking": true,
  "options": ["API only", "DB unique index", "Both"]
}
```

### Behavior

- `blocking: true` emits `ClarificationRequested`, transitions the run to an explicit
  awaiting-clarification state, and exits the loop turn cleanly.
- `blocking: false` records question for human response while run may continue.
- Human response is appended as a structured event/decision and becomes agent-visible context on resume.
- No `ask_human(...); wait ...` behavior inside an active loop.

### Integration points

- Scheduler state (mirrors existing awaiting-resume patterns).
- Session/event stream for auditability.
- Work unit timeline visibility.
- Resume path uses existing pause/resume style controls instead of creating a new hidden wait primitive.

### Safety

- No freeform hidden channel; all requests/responses are persisted.
- Optional timeout/escalation policy to avoid indefinite stalls.

## Slice 15e — Extension UX for clarification workflow + metrics

Add a first-class clarification inbox in the VS Code extension.

### UX behaviors

- List active clarification requests by session/work unit.
- Show question, context, options, age, and blocking status.
- Respond with constrained answer and optional note.
- Resume blocked items after response (explicit action for safety/traceability).

### Metrics (required)

Track and expose:

- clarification requests per goal
- clarification requests answered
- clarification requests abandoned

Goal: keep clarification as an ambiguity breaker, not a default escape hatch.

### Safety and operator ergonomics

- Confirm before bulk-resume actions.
- Clear lineage: who answered, when, and to which run.

## Slice 15f — Lean workspace status helper

Add a lightweight helper for "what changed" without requiring multiple calls.

### Tool (proposed)

- `nm_v1_workspace_status`

### Behavior

- Branch-scoped summary:
  - added/modified/deleted file paths
  - proposal status summary for the current work unit where available
  - optional short diff stats (line counts)
  - optional limit/paging

This is intentionally not a second diff engine.

### Safety

- Read-only capability.
- Bounded result size.
- Deterministic from branch state.

## Slice 15g — External documentation fetch with reproducibility

Add constrained external fetch designed for traceability, not open browsing.

### Tool (proposed)

- `nm_v1_doc_fetch`

### Request shape

```json
{
  "url": "https://learn.microsoft.com/...",
  "reason": "Confirm API behavior for X before changing Y",
  "workUnitId": "WU-123"
}
```

### Required outputs

- normalized URL
- fetch timestamp
- content hash
- captured content snapshot (size-limited)
- optional extracted summary

### Artifact policy

- Automatically record a source artifact linked to work unit/proposal lineage.
- If fetch content influences a merge proposal, proposal references artifact ids.

### Safety

- allowlist/denylist policy support (domains, schemes).
- content size caps and timeout.
- no credential forwarding, no authenticated browsing in v1.

## Verification (whole phase)

### Unit tests

- Semantic service query parsing, result normalization, truncation.
- Semantic-authoritative prompt routing tests (symbol queries route to semantic tools when enabled).
- Clarification lifecycle transitions (requested -> awaiting -> answered -> resumed).
- Doc fetch policy enforcement, hashing/snapshot metadata.
- Workspace status result correctness (adds/modifies/deletes + proposal status summary).
- Clarification metrics aggregation correctness (per-goal requested/answered/abandoned).

### Integration tests

- Planner/Worker use semantic tool to locate references and produce correct file targets.
- Blocking clarification pauses loop with no extra LLM churn; response resumes correctly.
- Doc fetch records lineage artifact and can be inspected in projections/events.
- Workspace status short-circuits multi-call diff discovery path.

### End-to-end checks

1. Build + full tests green.
2. A worker using semantic references edits the correct implementation path in a multi-project solution.
3. A blocking clarification request appears in UI, receives answer, resumes run, and records audit trail.
4. Workspace status output matches proposal `filesTouched` expectations before merge proposal.
5. A doc fetch influences a change and leaves attributable source artifact evidence.
6. Clarification metrics are populated and visible for at least one goal with multiple clarification events.

## Rollout strategy

- Feature flags per capability (`SemanticTools`, `ClarificationTools`, `DocFetchTools`, `WorkspaceStatusTool`).
- Profile-based opt-in (default off for existing profiles).
- Start in non-production sessions; promote gradually.
- Track usage and error metrics before enabling by default.

## Explicitly deferred (this phase)

- **Generic unrestricted runner tool.** Keep `workspace_build/test/exec/run` as the bounded execution surface.
- **Generic irreversible-action approval gate.** Merge/review remains the canonical human gate. Revisit
  only if future tools add non-merge destructive actions that cannot be safely modeled through current
  session/scheduler controls.
- **Open-ended web browsing/search.** Only constrained URL fetch with artifact capture is in scope.

## Risks and mitigations

- **Semantic provider drift across stacks.**
  Mitigation: Roslyn-first scope in this phase; explicit unsupported responses for non-.NET stacks.
- **Clarification overuse causing throughput loss.**
  Mitigation: profile guidance; require concise context and optional choices; track request-rate metrics.
- **External content nondeterminism.**
  Mitigation: mandatory snapshot/hash/timestamp + artifact linkage.
- **Payload blowups (references/diffs/fetch content).**
  Mitigation: strict caps, truncation markers, and pagination.

## Completion bar

This phase is complete when all planned slices are shipped with tests, all new tools are auditable via
existing event/projection surfaces, and rollout can be safely enabled by profile/flag without changing
merge-gate behavior.
