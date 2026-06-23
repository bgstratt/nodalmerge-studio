# Phase 11 — Conversation Transcripts

## Context

Before this phase, the only trace of what an agent actually *did* during a cycle was
`ActivityLabeler.Describe()` overwriting a single `CurrentActivity` string on the in-memory
`AgentRecord` ([InMemoryAgentRuntimeService.cs](../src/NodalMerge.Studio.AgentRuntime/InMemoryAgentRuntimeService.cs))
— deliberately ephemeral, per the comment citing AP-5 ("Immutable History" —
[v1-architecture-spec.md](../docs/architecture/v1-architecture-spec.md)). The actual LLM exchange
(assistant text, tool calls, tool results) lived only in a local `List<NmMessage>` inside each loop's
`RunAsync()` (`OrchestratorAgentLoop`, `PlannerAgentLoop`, `WorkerAgentLoop`, `ReviewerAgentLoop`) and
was discarded the instant the method returned. Durable artifacts (`OrchestrationEvent.Reason`,
`DecisionNode.Rationale`, `MergeProposal.ReviewNotes`) only ever captured the *destination* of a
decision, never the reasoning path that produced it.

That left a user watching a run with three disconnected fragments — workers on Goal Workspace,
one-line "Thinking..." labels in Activity Center, and a Merge Proposal at the end — with no way to
connect them or audit what was tried and discarded along the way. This phase adds a durable,
append-only conversation log per agent cycle, surfaced where a user is already looking at a work
unit's history (Goal Workspace's Decision Lens), plus a live-transcript deep-link from Activity
Center for currently-running agents.

Decisions made along the way:
- **Surfaced in Goal Workspace's Decision Lens, not a new top-level tab.** A work unit outlives the
  agent that acted on it, so tying the transcript to the work unit (not the agent) is what lets a
  user review history after the agent finishes. Reuses the existing `.gw-tab-bar` pattern already
  built for Metadata/Context. Activity Center's running-agent cards get a "View live transcript"
  link instead of a new agent-selector/badge system.
- **2s REST polling, matching every other panel** — not a WebSocket push. The poll hits the local
  Studio Host process (already-durably-written data), not an LLM or remote call, so the existing
  cadence used elsewhere in the extension is sufficient.
- **All four loops instrumented** — Orchestrator, Planner, Worker, Reviewer — for consistent
  coverage rather than leaving Planner/Reviewer as opaque as before.
- **Unbounded, append-only log; truncate only oversized tool results** (any single `ToolResult`
  string over 20KB gets capped with a `"...truncated"` marker). Consistent with AP-5: no
  cycle-count cap, no dropped history.
- No "thinking"/extended-reasoning content is parsed anywhere in `LlmClient.cs` today —
  `NmContent` only has `NmText`/`NmToolUse`/`NmToolResult`. This phase logs exactly those three; it
  does not add new thinking-block capture (separate concern, not attempted here).
- **No deletion/clearing UI.** Explicitly deferred: once this log exists, unbounded append-only
  growth with no purge path means storage only grows, and there's no precedent anywhere in the app
  for deleting a session or its artifacts yet. Worth scoping as its own phase since "delete a
  session" would need to cascade across every node kind tied to it, not just this one.

## 11a — `ConversationLogEntry` domain type + storage — Complete

New [ConversationLogEntry.cs](../src/NodalMerge.Studio.Contracts/Domain/ConversationLogEntry.cs):
`LogId`/`WorkUnitId`/`AgentId`/`AgentRole`/`TaskId`/`CycleNumber`/`AssistantText`/`ToolCalls`
(`ConversationToolCall`: `ToolUseId`/`Name`/`InputJson`)/`ToolResults` (`ConversationToolResult`:
`ToolUseId`/`Result`/`Truncated`)/`StopReason`/`OccurredAt`/`SessionId`. New node kind
`StudioNodeKind.ConversationLogV1` ([StudioNodeStore.cs](../src/NodalMerge.Studio.Storage/StudioNodeStore.cs)).
`IConversationLogService` declared in
[ServiceContracts.cs](../src/NodalMerge.Studio.Core/Services/ServiceContracts.cs) (cross-project,
same placement as `IOrchestrationDecisionLogService`/`IFindingService` since `AgentRuntime` loops
take it as a constructor parameter). `ConversationLogService`
([ConversationLogService.cs](../src/NodalMerge.Studio.Storage/ConversationLogService.cs)) — same
dual in-memory-index + node-store-write + `IRehydratable` shape as `DecisionNodeService`; truncation
of oversized `ToolResult` strings happens once, inside `RecordAsync`, not duplicated per caller.
Registered in [ServiceCollectionExtensions.cs](../src/NodalMerge.Studio.Storage/ServiceCollectionExtensions.cs)
alongside `DecisionNodeService`.

## 11b — Wiring into the four agent loops — Complete

New internal `ConversationLogRecorder.RecordTurnAsync`
([ConversationLogRecorder.cs](../src/NodalMerge.Studio.AgentRuntime/ConversationLogRecorder.cs)) —
shared `NmContent` → `ConversationLogEntry` mapping called once per cycle from each of
`OrchestratorAgentLoop`/`PlannerAgentLoop`/`WorkerAgentLoop`/`ReviewerAgentLoop`'s `RunAsync()`, at
the same point each loop already calls `onActivity?.Invoke(ActivityLabeler.Describe(...))` — both
on the `end_turn` path (empty tool-results) and after the tool-dispatch loop builds `toolResults`.
Each loop gained an optional trailing `IConversationLogService? conversationLog = null` constructor
parameter (appended rather than inserted positionally, to avoid reordering existing callers).
`IConversationLogService` is resolved via `_serviceProvider.GetRequiredService<>()` at all six
construction sites — `InMemoryAgentRuntimeService`'s `RunScheduledWorkerAsync` (Planner/Reviewer/
Worker branches), `StartOrchestratorLoop`, the legacy `StartWorkerLoop`, and
`InlineReviewerService.ReviewAsync` — matching how every other cross-cutting service (e.g.
`IFindingService`) is already obtained at those same sites.

## 11c — REST endpoint — Complete

`GET /studio/workunits/{workUnitId}/conversation-log`
([StudioRestEndpoints.cs](../src/NodalMerge.Studio.Host/StudioRestEndpoints.cs)), placed directly
beside the existing `orchestration-events` endpoint and following its exact shape (404 if the work
unit doesn't exist, otherwise the ordered entry list).

## 11d — Goal Workspace Decision Lens "Conversation" tab — Complete

[ArtifactExplorerPanel.ts](../clients/vscode-extension/src/panels/ArtifactExplorerPanel.ts) —
third button in the existing `.gw-tab-bar` (alongside Metadata/Context), lazy-loaded on first click
via a new `explorerSelectConversationTab` round trip, mirroring the Context tab's pattern. Renders
each cycle's assistant text plus collapsible tool call/result pairs (`<details>`), newest cycle
first. While the selected work unit is in a running status, the webview runs its own 2s
`setInterval` (`startConversationPoll`/`stopConversationPoll`) that re-fetches and patches just the
`#gw-panel-conversation` div in place — deliberately not a full `renderDecisionInspector()` rebuild,
so polling doesn't reset whichever tab the user has open the way the existing Context-tab message
handler does. The poll self-stops once the work unit's status is no longer running or the selection
changes.

## 11e — Activity Center "View live transcript" deep-link — Complete

[WorkspaceDashboardPanel.ts](../clients/vscode-extension/src/panels/WorkspaceDashboardPanel.ts) —
every agent card (regardless of pause/interrupted/active state, since the transcript is durable)
gets a "View live transcript" button posting `{ type: 'activityViewTranscript', workUnitId }`.
[StudioShellPanel.ts](../clients/vscode-extension/src/panels/StudioShellPanel.ts) special-cases
that message type ahead of its normal broadcast-to-all-panels handling (same pattern
`extension.ts`'s notification/dead-letter deep links already use via `showTab` + a direct panel
method) — switches to the Goal Workspace tab and calls a new
`GoalWorkspacePanel.openConversationStandalone(workUnitId)`, which fetches the single work unit by
ID (mirroring `DecisionConvergencePanel.loadProposal`/`loadConflict` — no dependency on that work
unit belonging to whichever session Goal Workspace currently has selected), renders it standalone
into the inspector, and force-activates the Conversation tab. Known limitation: if Goal Workspace's
regular 2s tree poll is also running for a *different* session, it will overwrite the injected
standalone node within ~2s; the Conversation tab's own content is unaffected (it isn't driven by
tree membership), only the Metadata tab's re-render would degrade gracefully back to "select a
node." Acceptable for a secondary deep-link; not worth the session-resolution machinery a fully
robust fix would need.

## Slices

| Slice | Scope | Status |
|---|---|---|
| 11a | `ConversationLogEntry` domain type, `ConversationLogService`, node-store persistence | Complete |
| 11b | Shared recorder helper wired into all four agent loops + their construction sites | Complete |
| 11c | `GET /studio/workunits/{workUnitId}/conversation-log` REST endpoint | Complete |
| 11d | Goal Workspace Decision Lens "Conversation" tab, with live 2s polling while running | Complete |
| 11e | Activity Center "View live transcript" deep-link into Goal Workspace | Complete |

## Non-goals

- No capture of model "thinking"/extended-reasoning blocks — `LlmClient.cs` doesn't parse any
  today; adding that is a separate, larger change to the LLM client itself.
- No WebSocket push for live updates — 2s polling against the local Studio Host is sufficient.
- No cycle-count retention cap — only individual oversized tool-result strings are truncated.
- No retroactive backfill — only work units created after this shipped have a conversation log.
- No deletion/clearing UI for conversation logs or sessions in general (see Context above).

## Verification

1. `dotnet build NodalMerge.Studio.slnx` / `dotnet test` — 0 errors. Extension `tsc --noEmit` /
   `npm run compile` — 0 errors.
2. Create a work unit, let it fan out through Orchestrator → Planner → Worker → (Reviewer if
   enabled). Open Goal Workspace, select the work unit's node, switch to the Conversation tab,
   confirm entries appear for each loop with assistant text, tool calls, and tool results, ordered
   by cycle, newest first.
3. While the work unit is still running with the Conversation tab open, confirm it updates roughly
   every 2s without manual refresh, and stops polling once the work unit reaches a terminal status.
4. From Activity Center, click "View live transcript" on a running agent's card; confirm Goal
   Workspace opens with the Conversation tab pre-selected and populated for that work unit.
5. Trigger a large tool result (e.g. read a file over 20KB) and confirm the persisted entry shows
   the `"...truncated"` marker rather than the full text.
6. Restart the Studio Host mid-run and confirm conversation log entries survive —
   `ConversationLogService.RehydrateAsync` repopulates from the node store.
