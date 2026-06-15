# Slice 5 — Merge workflow (AP-4)

Status: **Complete**

## Problem

The human review gate (AP-4) had two defects in `InMemoryMergeService`:

1. `ValidateAsync` did not check the current proposal status — it would blindly transition any proposal to `ReadyForReview`, even one already in `Approved` or `Merged` state.
2. `ReviewAsync` had a redundant double-check: `MergeProposalTransitions.CanTransition` already encodes the `ReadyForReview` requirement, but a second explicit status check followed. One was removed.

Additionally, MCP tool methods (`ValidateAsync`, `ReviewAsync`, `ApplyAsync`) propagated raw service exceptions to MCP clients instead of returning structured error envelopes.

## Changes

### `IMergeService.GetAsync` (Core)

Added `GetAsync(proposalId)` for O(1) single-proposal lookup — consistent with `ITaskService.GetAsync` added in Slice 4. Used by projections and workspace summary internally.

### `InMemoryMergeService` fixes

| Method | Fix |
|--------|-----|
| `ValidateAsync` | Now calls `CanTransition(current, ReadyForReview)` before transitioning — rejects anything not in Draft |
| `ReviewAsync` | Removed redundant second status check; `CanTransition` is the single authority |
| `ApplyAsync` | Replaced hard-coded `!= Approved` check with `CanTransition(current, Merged)` — consistent with other methods |

### `MergeTools` (McpServer)

`ValidateAsync`, `ReviewAsync`, and `ApplyAsync` now catch `KeyNotFoundException` and `InvalidOperationException` and return structured `McpJson.Error(...)` responses instead of propagating exceptions. `ProposeAsync` response now includes `status` alongside `proposalId`.

### New test project: `NodalMerge.Studio.Merge.Tests`

18 tests covering:

| Area | Tests |
|------|-------|
| `ProposeAsync` | Always stores as Draft regardless of caller-supplied status |
| `GetAsync` | Returns stored proposal / null for unknown |
| `ValidateAsync` | Draft→ReadyForReview / rejects re-validation / throws on unknown |
| `ReviewAsync` | Approve / Reject / bypass-validate blocked / throws on unknown |
| `ApplyAsync` | Applies Approved / blocks ReadyForReview / blocks Draft / throws on unknown |
| `ListAsync` | Filters by source branch / returns all without filter |
| AP-4 happy path | Full Draft→ReadyForReview→Approved→Merged sequence |
| AP-4 rejection path | Draft→ReadyForReview→Rejected; Apply on Rejected blocked |

## AP-4 gate summary

The human merge gate cannot be bypassed:

```
Draft  ──[validate]──▶  ReadyForReview  ──[human review]──▶  Approved  ──[apply]──▶  Merged
                                         └──────────────────▶  Rejected
```

- `Draft → Merged` directly: ❌ blocked
- `ReadyForReview → Merged` without Approved: ❌ blocked
- `Rejected → Merged`: ❌ blocked

## Out of scope

- Reviewer identity / audit log (who approved — deferred)
- `nm.v1.merge.list` MCP tool (no agent-facing list surface in v1 contract; workspace summary aggregates it)
- Merge proposal timestamps (no `CreatedAt`/`ReviewedAt` fields in v1 domain model)

## Success criteria

- [x] `ValidateAsync` rejects non-Draft proposals
- [x] `ApplyAsync` blocked on anything other than Approved
- [x] MCP merge tools return structured errors, not raw exceptions
- [x] 67/67 tests pass

## Next slice

**Slice 6 — Agent Runtime + Orchestrator:** Wire the agent execution loop — spawn/pause/resume/stop lifecycle, `ExecutionSnapshot` recording via `IAgentRuntimeService`, and `IOrchestratorService` coordinating work unit assignment to spawned agents.
