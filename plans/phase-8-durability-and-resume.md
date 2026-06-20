# Phase 8 — Durable Storage Anchoring & Gated Resume

## Context

Investigating "what happens if VS Code/the Host crashes" surfaced two real gaps, confirmed by
reading the actual code (not assumptions):

1. **Disk storage isn't anchored to the project.** The Sqlite DAG db (`data/nodalmerge-nodes.db`),
   file blob store (`data/blobs`), and branch workspace files (`%TEMP%\studio-workspace` by
   default) resolve relative to whatever the Host process's CWD happens to be — `HostManager.ts`'s
   `cp.spawn` never sets `cwd` or overrides these paths. It's real disk storage (not in-memory),
   but not pinned next to the project you opened.
2. **Worker-level resume has no gate; orchestrator-level does.** `InMemoryAgentRuntimeService`
   already does the right thing for orchestrator-level work (Slice 19d): on Host restart, work
   units that were Active/Executing get flagged "Interrupted" in the dashboard and require a
   manual "↺ Resume" click — no silent auto-continue. But `WorkSchedulerService.RehydrateAsync`
   does the opposite for scheduler-queued *worker* tasks: it clears the stale lease and the poll
   loop immediately re-acquires and respawns a fresh worker, fully automatically. Separately,
   `ExecutionSessionService.SetStatusAsync(Paused)` already exists as a REST-reachable session
   status flag, but nothing in `WorkSchedulerService.TryAcquireAsync` ever checks it — pausing a
   session today does nothing.

User decisions locked in for this phase:
- Orchestrator-level Interrupted + manual Resume is correct as-is — keep it, don't auto-resume on
  startup or on selecting a session.
- Worker-level scheduler items should get the **same** treatment: no silent auto-pickup on
  restart, plus a "Resume All" bulk action (a busy fan-out could leave many children interrupted).
- Session Pause should actually block new scheduler dispatch, not just record a flag.
- Disk storage should live near the opened project, not OS temp / process CWD.
- Resume design is confirmed to be the right model already: `WorkUnit.BranchId` is set once at
  creation and never reassigned — every (re)spawn for a work unit, including a resume, operates on
  the *same* branch and whatever partial files/Task status are already there. No "fresh branch,
  redo everything" risk. The only gap is the kickoff prompt doesn't currently say "this may be a
  resume, check existing state first."

## 8a — Anchor disk storage to the opened workspace folder

**File:** `clients/vscode-extension/src/HostManager.ts` (`spawnProcess`/`resolveHostCommand`).

- Read `vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath` — the same accessor
  `WorkspaceDashboardPanel.ts:200` already uses for per-goal `repositoryPath`.
- When a folder is open: set `cwd` on the `cp.spawn(...)` options to that folder, and add env vars
  (standard ASP.NET Core double-underscore config binding, same convention as the existing
  `Studio__Urls` env var already set here) pointing storage at a `.nodalmerge/` subfolder:
  - `Workspace__RootPath` → `<wsRoot>/.nodalmerge/workspace`
  - `NodalMerge__Storage__Sqlite__DbPath` → `<wsRoot>/.nodalmerge/data/nodalmerge-nodes.db`
  - `NodalMerge__Storage__FileBlobs__RootPath` → `<wsRoot>/.nodalmerge/data/blobs`
- No folder open (single-file mode): leave current temp-dir fallback behavior untouched.
- No Host-side code changes needed — config binding for all three already exists and reads env
  vars the same way `ProductionStorageIntegrationTests`/`RuntimeSettingsRehydrationTests` already
  prove via `configureConfiguration`.
- Note for the user (not blocking implementation): `.nodalmerge/` should go in the project's
  `.gitignore`; worth a follow-up prompt-on-first-run but out of scope here.

## 8b — Session pause actually gates dispatch

**File:** `src/NodalMerge.Studio.Storage/WorkSchedulerService.cs`.

- Constructor-inject `IExecutionSessionService` (check for a DI cycle first — `WorkSchedulerService`
  already uses a documented lazy-`IServiceProvider` pattern for deps that *do* cycle back through
  `IAgentControlService`/`IWorkUnitService`; `IExecutionSessionService` has no such dependency back
  on the scheduler, so a direct constructor dependency should be safe, but verify before wiring).
- In `TryAcquireAsync`, before acquiring an item with a non-null `SessionId`, look up the session
  and `continue` (skip, don't acquire) if `session.Status == ExecutionSessionStatus.Paused`.
- This affects all dispatch, not just resumed items — pausing a session genuinely stops new agent
  work from starting under it.

## 8c — Worker-level Interrupted + Resume + Resume All

**Files:** `ServiceContracts.cs` (`ScheduledItem`, `IWorkScheduler`), `WorkSchedulerService.cs`,
`StudioRestEndpoints.cs`, `WorkspaceDashboardPanel.ts`, `WorkerAgentLoop.cs` +
`InMemoryAgentRuntimeService.cs` (resume-aware kickoff).

- Add `bool AwaitingResume = false` to `ScheduledItem` (`ServiceContracts.cs:488`).
- `WorkSchedulerService.RehydrateAsync` (`WorkSchedulerService.cs:400`): currently clears
  `LeasedBy`/`LeasedAt` unconditionally. Change: if an item *had* a lease at rehydrate time (i.e.
  it was actively being worked when the Host died), also set `AwaitingResume = true` instead of
  leaving it freely acquirable. Items that were sitting unleased in the queue (never started)
  don't get this flag — nothing was interrupted for those.
- `TryAcquireAsync`: skip items where `AwaitingResume == true`.
- New `IWorkScheduler` methods:
  - `Task<IReadOnlyList<ScheduledItem>> ListAwaitingResumeAsync(CancellationToken ct = default)`
  - `Task ApproveResumeAsync(string workUnitId, CancellationToken ct = default)` — clears the flag,
    persists, so the next poll tick acquires it normally.
  - `Task<int> ApproveResumeAllAsync(CancellationToken ct = default)` — same, for every flagged
    item; returns the count resumed.
- New REST endpoints in `StudioRestEndpoints.cs` (mirror the existing `/studio/scheduler/pending`
  and `/studio/agents/{agentId}/resume` conventions):
  - `GET /studio/scheduler/awaiting-resume`
  - `POST /studio/scheduler/{workUnitId}/resume`
  - `POST /studio/scheduler/resume-all`
- Resume-aware kickoff: thread a small `wasInterrupted`/`attemptCount > 0`-style flag from
  `RunScheduledWorkerAsync` into `WorkerAgentLoop`'s kickoff message (same additive-optional-param
  pattern used for `onActivity` earlier this session) so a resumed worker's first message adds:
  "This work was previously interrupted — check existing files and task status before starting
  from scratch," rather than the plain fresh-start kickoff text.
- Dashboard: new "Awaiting Resume" section in `WorkspaceDashboardPanel.ts`, parallel to the
  existing Interrupted-orchestrator card style, fed by `GET /studio/scheduler/awaiting-resume`.
  Each row gets a "↺ Resume" button (`POST .../resume`); section header gets one "Resume All"
  button (`POST .../resume-all`).

## 8d — Tests

- Extend `tests/NodalMerge.Studio.Integration.Tests/WorkSchedulerRehydrationTests.cs` (existing
  file) rather than adding a new one: cover rehydrate marking a previously-leased item
  `AwaitingResume`, `TryAcquireAsync` skipping flagged items, `ApproveResumeAsync`/
  `ApproveResumeAllAsync` clearing the flag and making items acquirable again.
- New/extended test for 8b: a paused session's items are skipped by `TryAcquireAsync` even when
  otherwise acquirable.
- 8a has no .NET unit test surface (pure extension-host process wiring) — covered by the manual
  verification step below instead.

## Deferred / known related gap (not in scope)

Orchestrator-level "Resume" today goes through the generic `spawnAgent` + profile-picker UI flow
because `_orchestratorRegistrations` (the captured spawn credentials) is in-memory only and doesn't
survive a real Host restart — only worker-level credentials are durable (inside the persisted
`ScheduledItem`). The user confirmed the orchestrator-level *gating* behavior (manual click) is
fine as-is, so this asymmetry is noted but not addressed here.

## Verification

1. `dotnet build NodalMerge.Studio.slnx` — 0 errors.
2. `dotnet test` — full pass (per this session's established repeat-run convention, run 3-5x to
   rule out new flakiness).
3. Manual: open the extension against a real workspace folder; confirm
   `<folder>/.nodalmerge/data/nodalmerge-nodes.db` and `<folder>/.nodalmerge/workspace/` get
   created there (not `%TEMP%`). Kill the Host process mid-task, restart, confirm the in-flight
   item appears under "Awaiting Resume" (not silently re-picked-up) and that a paused session's
   queued items don't dispatch; click Resume and Resume All and confirm dispatch resumes.
