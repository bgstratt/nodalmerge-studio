# Slice 7c — Merge Review Panel (AP-4 Gate)

Status: **Complete**

## Problem

The AP-4 human merge gate is fully enforced on the server (`InMemoryMergeService`) but there is no UI for a developer to review and act on merge proposals. Approvals, rejections, and applies currently require calling MCP tools directly.

## Architecture

A dedicated WebView panel for proposal review. Opens from the dashboard ("Review →" link) or from a VS Code notification when a proposal reaches `ReadyForReview`.

```
Extension host (TS)
  MergeReviewPanel
    ├─ load proposal detail  GET /studio/merges/{proposalId}
    ├─ postMessage({ type: 'proposal', data })
    └─ handle action messages:
         validate → POST /studio/merges/{id}/validate
         approve  → POST /studio/merges/{id}/review  { decision: 'Approved' }
         reject   → POST /studio/merges/{id}/review  { decision: 'Rejected' }
         apply    → POST /studio/merges/{id}/apply
```

## New REST endpoints on Studio Host

```
GET  /studio/merges                         — IMergeService.ListAsync()
GET  /studio/merges/{proposalId}            — IMergeService.GetAsync()
POST /studio/merges                         — IMergeService.ProposeAsync()
POST /studio/merges/{proposalId}/validate   — IMergeService.ValidateAsync()
POST /studio/merges/{proposalId}/review     — IMergeService.ReviewAsync()  body: { decision }
POST /studio/merges/{proposalId}/apply      — IMergeService.ApplyAsync()
```

## Files touched

### Updated: `src/NodalMerge.Studio.Host/StudioWebApplication.cs`

Add merge REST endpoints.

### New: `extension/src/panels/MergeReviewPanel.ts`

- Accepts a `proposalId` on open
- Loads proposal detail and populates WebView
- Handles action messages and calls REST endpoints
- On success (Merged / Rejected): shows VS Code information/warning message and closes panel or refreshes to "closed" state

### New: `extension/src/webviews/merge-review.html`

Layout:

```
Merge Proposal: mp-1
Source branch:  feat/docs
Target branch:  main
Goal:           Add API reference documentation
Summary:        Adds 12 pages of API docs generated from inline comments
Status:         ReadyForReview

[ Validate ]   [ Approve ]   [ Reject ]   [ Apply ]

Action buttons are conditionally enabled by status:
  Draft         → [Validate] only
  ReadyForReview→ [Approve] [Reject]
  Approved      → [Apply]
  Merged/Rejected → all disabled, status shown
```

### New: `extension/src/NotificationManager.ts`

- On each workspace summary poll, diff the `pendingMerges` list against previously seen proposals
- If a new `ReadyForReview` proposal appears → `vscode.window.showInformationMessage('Merge proposal ready for review', 'Open Review')` → opens `MergeReviewPanel`

## VS Code notification integration

When a merge proposal transitions to `ReadyForReview` (detected by the dashboard poll loop), the extension shows a notification:

```
NodalMerge: "feat/docs" is ready for review.   [Open Review]  [Dismiss]
```

Clicking "Open Review" opens `MergeReviewPanel` for that proposal.

## AP-4 button state rules

The WebView enforces the same transitions the server does — buttons are enabled/disabled by status. The server also enforces; the UI rule is cosmetic redundancy for clarity.

| Status | Validate | Approve | Reject | Apply |
|--------|----------|---------|--------|-------|
| Draft | ✅ | ❌ | ❌ | ❌ |
| ReadyForReview | ❌ | ✅ | ✅ | ❌ |
| Approved | ❌ | ❌ | ❌ | ✅ |
| Merged | ❌ | ❌ | ❌ | ❌ |
| Rejected | ❌ | ❌ | ❌ | ❌ |

## Out of scope

- Diff rendering (no line-by-line diff in v1 — just goal/summary/description text)
- Reviewer identity / audit log (who approved, when — deferred)
- Bulk review (multiple proposals at once)

## Success criteria

- [ ] Panel opens from dashboard "Review →" link and from notification
- [ ] Proposal detail (goal, summary, description, source/target branch) renders correctly
- [ ] Buttons are enabled/disabled per current proposal status
- [ ] Validate, Approve, Reject, Apply each call the correct REST endpoint
- [ ] Server-side errors (invalid transition) shown as VS Code warning messages
- [ ] Notification fires when a new proposal reaches ReadyForReview

## Next slice

**Slice 7d — DAG Replay Panel:** WebView connecting to `/ws/runtime` for live branch visualization using the `branchReplay.ts` state machine from the docs demo.
