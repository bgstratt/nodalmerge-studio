# API Reference — MCP Tools, REST Endpoints, and Coverage

This is the current, verified catalog of every MCP tool and REST endpoint exposed by the Studio
Host, plus a breakdown of which surface (the VS Code extension, autonomous agents, or neither)
actually reaches each one. It supersedes the tool counts in
[mcp-v1-contract.md](../contracts/mcp-v1-contract.md) for anything added since Phase 6.7, but that
document remains the source of truth for tool-naming design principles and the error envelope
format.

Ground truth as of this writing: **63 MCP tools** (`McpToolNames.All`), **39 of them** dispatched
in-process to autonomous agents (`McpToolDispatcher.cs`), and **103 REST routes**
(`StudioRestEndpoints.cs`).

---

## How the transports relate

```
VS Code Extension  ──HTTP──▶  REST endpoints  ──┐
                                                  ├──▶  Shared command services  ──▶  NodalMerge DAG
External MCP client ──MCP───▶  MCP tools  ───────┘
Orchestrator/Worker loops ──in-process call──▶  McpToolDispatcher  ──▶  same MCP tool handlers
```

Per MCP-5 (transport consolidation), a capability that has both a REST endpoint and an MCP tool
calls the *same* command service either way — they can't drift in behavior. But agents never make
HTTP calls to their own host's REST API; they call `McpToolDispatcher.DispatchAsync` in-process
with an MCP tool name. So "is this reachable by agents" is really "is this tool name in the
dispatcher's switch statement," not "does a REST route exist." A REST endpoint with no frontend
caller can still be heavily used — just by agents, via the MCP tool it shares a command service with.

---

## MCP Tool Catalog (63 tools)

✅ = dispatched in-process to orchestrator/worker agents · — = MCP-client or REST/extension only

### Projection (2)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_projection_get` | ✅ | Get a projection by type and compression level |
| `nm_v1_projection_list` | — | List available projection types and levels |

### Work Unit (4)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_workunit_create` | ✅ | Create a work unit (goal + branch, optional parent/scope/deps/reviewPolicy) |
| `nm_v1_workunit_get` | ✅ | Get a work unit by id |
| `nm_v1_workunit_update` | ✅ | Update status or assigned agent |
| `nm_v1_workunit_list` | ✅ | List work units, optionally filtered by branch |

### Task (4)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_task_create` | ✅ | Create a task (intent only) under a work unit |
| `nm_v1_task_update` | ✅ | Update task title/description/status/priority |
| `nm_v1_task_list` | ✅ | List tasks, optionally by work unit |
| `nm_v1_task_assign` | ✅ | Assign a task to an agent |

### Branch (4)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_branch_create` | ✅ | Create a branch |
| `nm_v1_branch_checkout` | — | Check out a branch |
| `nm_v1_branch_list` | ✅ | List branches |
| `nm_v1_branch_status` | ✅ | Branch status for agents |

### Merge (4)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_merge_propose` | ✅ | Submit a proposal — diff, lineage, execution event, status transition, `ProposalCreated` policy gate |
| `nm_v1_merge_validate` | ✅ | Validate a draft proposal, moves it to ReadyForReview |
| `nm_v1_merge_review` | ✅ | Approve/reject (supports `automated=true` for the reviewer-agent path) |
| `nm_v1_merge_apply` | ✅ | Apply an approved proposal — runs the `BeforeMerge` policy gate (review-policy/promotion-branch logic) |

### Replay (3)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_replay_range` | — | Inspect a history range for a branch |
| `nm_v1_replay_rollback` | — | Roll back a branch to a known-good state |
| `nm_v1_replay_inspect` | — | Human-friendly replay summary |

### State / Known Good (3)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_state_markKnownGood` | — | Mark current branch state as known-good |
| `nm_v1_state_findKnownGood` | — | Find known-good states for a branch |
| `nm_v1_state_checkoutKnownGood` | — | Restore a branch to a known-good state |

### Snapshot (2)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_snapshot_get` | ✅ | Agent execution snapshot (goal, task, failure count, next action) |
| `nm_v1_snapshot_compare` | — | Compare two agents' snapshots |

### Agent (5)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_agent_spawn` | ✅ | Spawn an agent for a work unit |
| `nm_v1_agent_pause` | — | Pause an agent |
| `nm_v1_agent_resume` | — | Resume a paused agent |
| `nm_v1_agent_status` | ✅ | Get agent status |
| `nm_v1_agent_stop` | ✅ | Stop an agent |

*(Agents cannot pause/resume themselves or each other — those are human/extension-only actions today, which is the right default but worth knowing if you're designing an agent-to-agent steering flow.)*

### Workspace — file I/O (6) and execution (6)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_workspace_read` / `_write` / `_delete` / `_list` / `_diff` / `_exists` | ✅ all | Branch-scoped file operations (write/delete respect `fileScope`) |
| `nm_v1_workspace_summary` | ✅ | Control-tower summary (active work units, agents, merges, failures) |
| `nm_v1_workspace_build` / `_test` / `_exec` / `_run` | ✅ all | Run build / test / build+test+lint / the app on a branch |
| `nm_v1_workspace_exec_status` | ✅ | Latest persisted execution result for a branch |
| `nm_v1_workspace_path` | ✅ | Branch working-directory filesystem path |

### Scheduler (2)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_scheduler_enqueue` | ✅ | Queue a work unit for a profile (parallel execution, used by Experiments) |
| `nm_v1_scheduler_pending` | ✅ | List pending scheduler queue items |

### Intent (1)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_intent_record` | ✅ | Record a change intent for conflict detection |

### Artifact (3)
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_artifact_record` | ✅ | Record a knowledge note (Research/Decision/Constraint) |
| `nm_v1_artifact_query` | ✅ | Search artifacts by type/keyword across a work unit's ancestry |
| `nm_v1_artifact_list` | ✅ | Full artifact chain for a work unit, ancestors included by default |

### Goal (2) — *not in original frozen contract*
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_goal_create` | — | Create a decision-centric goal node |
| `nm_v1_goal_list` | — | List goals (falls back to work units if goal store empty) |

### Decision (2) — *not in original frozen contract*
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_decision_record` | — | Record Accepted/Rejected/Deferred/Superseded against a proposal |
| `nm_v1_decision_list` | — | List decisions, optionally by work unit |

### Evidence (2) — *not in original frozen contract*
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_evidence_attach` | — | Attach build/test evidence from the latest execution result |
| `nm_v1_evidence_list` | — | List evidence entries for a work unit |

### Trajectory (2) — *not in original frozen contract*
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_trajectory_create` | — | Record a lifecycle phase (GoalDefined…Converged/Forked/Abandoned) |
| `nm_v1_trajectory_replay` | — | Replay decisions in Linear / BranchExplorer / **Counterfactual** mode |

### Hypothesis (2) — *not in original frozen contract*
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_hypothesis_fork` | — | Fork a work unit/proposal with a typed fork (Code/Reasoning/Model/Research/Architecture/Library/Product) |
| `nm_v1_hypothesis_list` | — | List forks, optionally by parent |

### Reasoning (1) — *not in original frozen contract*
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_reasoning_record` | — | Record a reasoning commit from an orchestration step |

### Model (2) — *not in original frozen contract*
| Tool | Dispatched | Purpose |
|---|---|---|
| `nm_v1_model_compare` | — | Compare two proposals' diverged files/overlap |
| `nm_v1_model_replay` | — | List all proposals for a work unit, grouped for model comparison |

### Phase 7 — REST-only, no MCP tool at all
| Capability | REST endpoint | Why it matters |
|---|---|---|
| Experiments (create/list/get) | `POST/GET /studio/experiments`, `GET /studio/experiments/{id}` | An MCP client (or an agent) cannot launch a multi-fork experiment on its own |
| Steering (redirect, fork-from-node) | `POST /studio/steering/redirect`, `POST /studio/steering/fork-from-node` | An agent cannot steer itself or another agent — human/extension only |
| Counterfactuals (create) | `POST /studio/counterfactuals` | Read-only counterfactual *viewing* exists via `nm_v1_trajectory_replay` (mode=Counterfactual); *creating* one does not have a tool |
| Review Policy on create | `reviewPolicy` field on `POST /studio/workunits` | `nm_v1_workunit_create` has no `reviewPolicy` parameter — MCP-created work units always default to `HumanRequired` |
| Promotion branches | `usePromotionBranch`/`candidateBranchId` on `POST /studio/options`, `POST /studio/branches/candidate/promote` | No tool to toggle or promote |
| Fork from Known Good | `POST /studio/state/{stateId}/fork` | An MCP client/agent can checkout (mutate) a known-good state via `nm_v1_state_checkoutKnownGood`, but can't fork a *new* work unit from one without writing its own `seedFromBranchId`-style call |

---

## REST Endpoint Catalog (103 routes)

Grouped by resource area; method + path + one-line purpose. `StudioRestEndpoints.cs` is the single
file that registers all of these.

### Workspace files & execution
branchId is a query parameter on every one of these, not a route segment — branch ids like
`merge/{workUnitId}` and `base/{proposalId}` contain a literal `/`, which a `{branchId}` route
segment can never match.
- `GET /studio/workspace-summary` — control-tower summary
- `POST /studio/workspace/build|test|exec|run?branchId=...` — trigger build/test/build+test+lint/run on a branch
- `GET /studio/workspace/exec/latest?branchId=...` — latest execution result
- `GET /studio/workspace/exec/output?branchId=...&resultId=...` — cached stdout/stderr for a past result
- `GET /studio/workspace/path?branchId=...` — branch working directory path

### Work units
- `GET /studio/workunits` — list (filter by branch/session)
- `POST /studio/workunits` — create
- `GET /studio/workunits/{id}` — get
- `GET /studio/workunits/{id}/children` — child work units
- `GET /studio/workunits/{id}/artifacts` — artifact lineage chain
- `GET /studio/workunits/{id}/orchestration-events` — orchestrator decision events
- `GET /studio/workunits/{id}/intents` — intent graph
- `GET /studio/workunits/{id}/conflict-report` — merge conflict report (Reviewing status)
- `GET /studio/workunits/{id}/proposal-dag` — proposal/branch/reconciliation DAG

### Tasks
- `GET /studio/tasks` (list) · `GET /studio/tasks/{id}` (get) · `POST /studio/tasks` (create) · `PUT /studio/tasks/{id}` (update) · `POST /studio/tasks/{id}/assign` (assign)

### Agents
- `GET /studio/agents` — list (active by default, `?all=true` for all)
- `POST /studio/agents/spawn` — spawn
- `GET /studio/agents/{id}/status` — status
- `POST /studio/agents/{id}/pause|resume|stop` — lifecycle control

### Merges / proposals
- `GET /studio/merges` — list (filter by branch/session)
- `POST /studio/merges` — create proposal
- `GET /studio/merges/{id}` — get
- `GET /studio/merges/{id}/constituents` — resolve a reconciled proposal's source proposals
- `GET /studio/merges/{id}/file-changes` — diffs
- `POST /studio/merges/{id}/validate|review|apply` — workflow transitions (`review` body: `{ decision: "Approved"|"Rejected" }` — there is **no** separate `/accept` or `/reject` route)
- `GET /studio/merges/compare?ids=` — compare two proposals
- `POST /studio/merges/{id}/branch` — fork a new work unit from a proposal's base state
- `POST /studio/merges/{id}/restore-workspace` — restore workspace to pre-change state

### Branches
- `GET /studio/branches` (list) · `POST /studio/branches` (create) · `POST /studio/branches/{id}/checkout` (checkout) · `GET /studio/branches/{id}/status` (status)
- `POST /studio/branches/candidate/promote` — promote candidate → main

### Nodes (versioned entities)
- `GET /studio/nodes` — raw node lookup by kind + entityId

### Known-good states
- `POST /studio/state/markKnownGood` · `GET /studio/state/knownGood/{branchId}` · `POST /studio/state/checkoutKnownGood`
- `POST /studio/state/{stateId}/fork` — fork a new work unit seeded from a checkpoint's snapshot branch, instead of restoring in place (the extension's "Fork from Known Good")

### Agent profiles
- `GET /studio/agent-profiles` (list) · `GET /studio/agent-profiles/{id}` (get) · `POST` (create) · `PUT /studio/agent-profiles/{id}` (update)

### Scheduler
- `GET /studio/scheduler/pending` · `POST /studio/scheduler/enqueue`

### Sessions
- `GET/POST /studio/sessions` · `GET /studio/sessions/{id}` · `POST /studio/sessions/{id}/pause|resume|abandon` · `GET /studio/sessions/{id}/workunits` · `POST /studio/sessions/{id}/branch`

### Events & session state
- `GET /studio/sessions/{id}/events` · `GET /studio/events/{id}` · `GET /studio/sessions/{id}/state` (point-in-time reconstruction)

### Artifacts (generic knowledge store)
- `GET/POST /studio/artifacts` (list/query, record) · `GET /studio/artifacts/{id}` (get) · `GET /studio/artifacts/{id}/children`

### Dead-letter queue
- `GET /studio/dead-letter` (list) · `GET /studio/dead-letter/{id}` (get) · `POST /studio/dead-letter/{id}/retry`

### Options / settings
- `GET/POST /studio/options` — concurrency, polling, build/test gates, promotion branch

### Policies
- `GET /studio/policies` — registered policy rule IDs

### Projections
- `GET /studio/projections` — types/levels catalog
- `GET /studio/projections/{type}` — get a projection (`?workUnitId=&branchId=&agentId=&level=`)

### Snapshots
- `GET /studio/snapshots/{agentId}` · `GET /studio/snapshots/{agentId}/compare/{otherAgentId}`

### Replay
- `GET /studio/replay/timeline` (+ `/{branchId}` variant) · `GET /studio/replay/range/{branchId}` · `POST /studio/replay/rollback/{branchId}` · `GET /studio/replay/inspect/{branchId}`

### Goals / Decisions / Evidence / Trajectory / Hypotheses / Reasoning / Models
- `GET/POST /studio/goals`
- `GET/POST /studio/decisions`
- `GET /studio/evidence` · `POST /studio/evidence/attach`
- `GET /studio/trajectory/replay` · `POST /studio/trajectory`
- `GET /studio/hypotheses` · `POST /studio/hypotheses/fork`
- `POST /studio/reasoning`
- `GET /studio/models/compare` · `GET /studio/models/replay/{workUnitId}`

### Phase 7: Experiments, Steering, Counterfactuals
- `GET/POST /studio/experiments` · `GET /studio/experiments/{id}`
- `POST /studio/steering/redirect` · `POST /studio/steering/fork-from-node`
- `POST /studio/counterfactuals`

---

## Coverage: Extension UI vs. Agents vs. Neither

This answers "is anything dead, and if not, who's using it?" Verified by grepping every literal
REST path string the VS Code extension calls, cross-referenced against the MCP dispatcher's switch
statement (which is what makes a capability *agent*-reachable, since agents call MCP tools
in-process — they don't call their own REST API).

### Used by both the extension UI and agents (via the matching MCP tool)
Work units (create/get/list/children/artifacts), tasks are agent-only (see below), branches
(create/list/status via MCP — the *extension* only ever touches branches indirectly through work
units and the explicit `candidate/promote` route), merges (propose/validate/review/apply/branch),
workspace file read/write (agents via MCP; the extension reads files only through diff/restore
views), `scheduler/enqueue` (extension's "Re-explore" button **and** `ExperimentService`'s
auto-enqueue both call this).

### Used by the extension UI only (no agent path — human/system actions by design)
`agents/{id}/pause|resume` (agents cannot pause themselves), `branches/{id}/checkout`,
`state/markKnownGood`, `state/knownGood/{branchId}`, `state/{stateId}/fork`,
`replay/rollback/{branchId}`, `agent-profiles` CRUD, `options`, `sessions` CRUD,
`dead-letter/{id}/retry`, `merges/compare`, `merges/{id}/constituents`,
`merges/{id}/restore-workspace`, `hypotheses/fork`, `experiments` (create/list/get),
`steering/redirect`, `steering/fork-from-node`, `counterfactuals` (create),
`projections/{DecisionContext,CounterfactualComparison,ReasoningCommitGraph}`,
`evidence` (list), `replay/timeline`, `workunits/{id}/orchestration-events`,
`workunits/{id}/conflict-report`, `workunits/{id}/artifacts`.

### Used by agents only (via MCP, in-process) — no extension UI button calls these directly
`workspace/{branchId}/build|test|exec|run` (the extension's "Require build/test before proposal"
checkboxes configure a *server-side policy gate* that runs these automatically when a proposal is
created — there's no manual "run build now" button), `artifact` record/query/list (generic),
`intent_record`, `snapshot_get`, `task_*` (the extension never does direct task CRUD — tasks are an
agent-internal intent-tracking primitive), `branch_create`/`branch_list` (the extension manages
branches implicitly; it never lists raw branches).

### Reachable only via raw HTTP or an external MCP client — neither the extension nor in-process agents call these today
- **`POST /studio/state/checkoutKnownGood`** — restores in place like `replay/rollback/{branchId}`,
  but without validating that the state belongs to the branch being restored. The extension's
  "↩ Restore Known Good" (Pathways) and "Fork from Known Good" (Goal Workspace) use the validated
  `replay/rollback/{branchId}` and the new `state/{stateId}/fork` routes instead — this unvalidated
  one is left for external MCP clients that already know the state→branch pairing.
- **`GET /studio/workunits/{id}/children|intents|proposal-dag`** — read-only inspection endpoints
  with no current caller.
- **`GET /studio/scheduler/pending`** — the extension's dead-letter/blocked view doesn't surface the
  raw scheduler queue.
- **`GET /studio/snapshots/{agentId}` (+ compare)** — `nm_v1_snapshot_compare` isn't dispatched to
  agents either, so this is unused on both sides.
- **`GET/POST /studio/decisions`, `GET/POST /studio/goals`, `POST /studio/reasoning`,
  `POST /studio/trajectory`, `GET /studio/models/compare`, `GET /studio/models/replay/{id}`** — these
  node stores exist and have working REST + (mostly undispatched) MCP tools, but neither the
  extension nor the in-process agent loops write to them today. They look like scaffolding for a
  decision-centric UI layer (the `nm_v1_goal_*`/`nm_v1_decision_*` naming suggests a "Goal" surface
  that doesn't exist yet — the extension's "Active Goals" section is actually built on plain work
  units, not these goal nodes).
- **`GET /studio/replay/range/{branchId}`, `GET /studio/replay/inspect/{branchId}`,
  `GET /studio/replay/timeline/{branchId}`** — the Trajectory Replay panel only calls the bare
  (no-branchId) `GET /studio/replay/timeline`; these per-branch read variants are unused by the UI
  (and `replay_range`/`replay_inspect` MCP tools aren't dispatched to agents either).
  `POST /studio/replay/rollback/{branchId}` is now used — see above.
- **`GET /studio/nodes`, `GET /studio/policies`** — debug/introspection endpoints.

**Net read:** nothing here is *broken* — these are either deliberate human-only safety boundaries
(pause/resume, promote, known-good checkout *should* require a deliberate action), or capabilities
that were built ahead of their consumer (the Goal/Decision/Reasoning node stores). Known-good
checkout/list/fork is no longer one of these — it's wired into both Pathways and Goal Workspace now.
