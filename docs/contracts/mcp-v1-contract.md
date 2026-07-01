# MCP v1 contract (frozen)

Status: **Frozen for implementation** — updated for Phase 6.5 (command-surface hardening), Phase 6.6 (workspace execution), and Phase 9 workspace execution/profile parity additions

> **Note:** This document predates Phase 6.7+ and Phase 7. It does not cover the `goal`, `decision`,
> `evidence`, `trajectory`, `hypothesis`, `reasoning`, or `model` tool namespaces (all already in
> code), nor the Phase 7 REST-only additions (Experiments, Steering, Counterfactuals, Review Policy,
> Promotion Branches). See [docs/reference/api-reference.md](../reference/api-reference.md) for the
> complete, currently-accurate tool and endpoint catalog, including which tools are dispatched to
> autonomous agents versus reachable only by external MCP clients or REST/the VS Code extension.
> Tool naming and the design principles below remain frozen and accurate.

Canonical C# constants: `NodalMerge.Studio.Contracts.Versioning.McpToolNames`

Architecture context: [v1 architecture spec](../architecture/v1-architecture-spec.md)

---

## Design principles

### MCP-1: Projection first

Agents should rarely need storage-level operations. Prefer `nm_v1_projection_get` over raw DAG queries. The DAG is an implementation detail.

### MCP-2: Intent over topology

Expose `nm_v1_task_create`, `nm_v1_merge_propose`. Do not expose `create_node()` or `append_edge()` — those belong to NodalMerge internals.

### MCP-3: Branch-aware

Operations execute within branch context. Requests SHOULD include `branchId` where applicable.

### MCP-4: Versioned forever

All tools use the `nm_v1_*` namespace. Breaking changes require `nm_v2_*`.

### MCP-5: Transport consolidation (Phase 6.5)

Every tool has exactly one shared command service implementation called by MCP, REST, and the agent-loop dispatcher. No transport can diverge in behavior.

---

## Namespace layout

```text
nm_v1_projection_*
nm_v1_task_*
nm_v1_workunit_*
nm_v1_branch_*
nm_v1_merge_*
nm_v1_replay_*
nm_v1_state_*
nm_v1_snapshot_*
nm_v1_agent_*
nm_v1_workspace_*
nm_v1_scheduler_*
nm_v1_intent_*
nm_v1_artifact_*
```

---

## Tool catalog

### Projections

| Tool | Purpose |
|------|---------|
| `nm_v1_projection_get` | Get projection by type and level |
| `nm_v1_projection_list` | List projection types and levels |

### Work Units

| Tool | Purpose |
|------|---------|
| `nm_v1_workunit_create` | Create work unit (goal + branch) |
| `nm_v1_workunit_get` | Get work unit |
| `nm_v1_workunit_update` | Update status / assignment |
| `nm_v1_workunit_list` | List work units |

### Tasks

| Tool | Purpose |
|------|---------|
| `nm_v1_task_create` | Create task (intent only, no DAG refs) |
| `nm_v1_task_update` | Update task |
| `nm_v1_task_list` | List tasks |
| `nm_v1_task_assign` | Assign task to agent |

### Branches

| Tool | Purpose |
|------|---------|
| `nm_v1_branch_create` | Create branch |
| `nm_v1_branch_checkout` | Checkout branch |
| `nm_v1_branch_list` | List branches |
| `nm_v1_branch_status` | Branch status for agents |

### Merges

| Tool | Purpose |
|------|---------|
| `nm_v1_merge_propose` | Create merge proposal with full diff, artifact lineage, execution event, policy gate (ProposalCreated), and work-unit status transition |
| `nm_v1_merge_validate` | Validate proposal (tests/policy) |
| `nm_v1_merge_review` | Human review metadata |
| `nm_v1_merge_apply` | Apply approved merge (v1 requires approval) |

### Replay

| Tool | Purpose |
|------|---------|
| `nm_v1_replay_range` | Inspect history range |
| `nm_v1_replay_rollback` | Rollback via known good state |
| `nm_v1_replay_inspect` | Human-friendly history summary |

### State

| Tool | Purpose |
|------|---------|
| `nm_v1_state_markKnownGood` | Mark known good state |
| `nm_v1_state_findKnownGood` | Find known good states |
| `nm_v1_state_checkoutKnownGood` | Checkout known good state |

### Snapshots

| Tool | Purpose |
|------|---------|
| `nm_v1_snapshot_get` | Derived execution snapshot |
| `nm_v1_snapshot_compare` | Compare agent snapshots |

### Agents

| Tool | Purpose |
|------|---------|
| `nm_v1_agent_spawn` | Spawn agent for work unit |
| `nm_v1_agent_pause` | Pause agent |
| `nm_v1_agent_resume` | Resume agent |
| `nm_v1_agent_status` | Agent status |
| `nm_v1_agent_stop` | Stop agent |

### Scheduler

| Tool | Purpose |
|------|---------|
| `nm_v1_scheduler_enqueue` | Enqueue work unit for agent execution (supports `model`/`baseUrl`/`apiKey`/`provider` overrides) |
| `nm_v1_scheduler_pending` | List pending scheduled items |
| `nm_v1_clarification_request` | Record a human clarification question; with `blocking=true`, pause scheduler execution until resume |

### Intents

| Tool | Purpose |
|------|---------|
| `nm_v1_intent_record` | Record a change intent for conflict detection |

### Artifacts

| Tool | Purpose |
|------|---------|
| `nm_v1_artifact_record` | Record a knowledge artifact |
| `nm_v1_artifact_query` | Search artifacts by type/keywords |
| `nm_v1_artifact_list` | List artifacts for a work unit (with ancestor chain option) |

### Workspace (MCP-3: branch-aware)

| Tool | Purpose |
|------|---------|
| `nm_v1_workspace_summary` | Control tower workspace summary (active work units, agents, merges, failures) |
| `nm_v1_workspace_read` | Read file from branch workspace |
| `nm_v1_workspace_write` | Write file to branch workspace |
| `nm_v1_workspace_delete` | Delete file from branch workspace |
| `nm_v1_workspace_list` | List files in branch workspace |
| `nm_v1_workspace_diff` | Diff between two branches |
| `nm_v1_workspace_exists` | Check if file exists in branch workspace |
| `nm_v1_workspace_symbol_definition` | Compiler-backed symbol definition lookup |
| `nm_v1_workspace_symbol_references` | Compiler-backed symbol reference/call-site lookup |
| `nm_v1_workspace_symbol_implementation` | Compiler-backed symbol implementation lookup |

Routing guidance: when semantic tools are enabled in the active profile, they are authoritative for
definition/reference/implementation questions. `nm_v1_workspace_search` remains for text/content
search (comments, literals, config keys, docs).

### Workspace Execution (Phase 6.6)

| Tool | Purpose |
|------|---------|
| `nm_v1_workspace_build` | Run build on a branch (auto-detect build system or explicit command) |
| `nm_v1_workspace_test` | Run tests on a branch (parses dotnet/cargo/pytest/go output) |
| `nm_v1_workspace_exec` | Run build + test + lint on a branch with full `WorkspaceExecutionRequest` |
| `nm_v1_workspace_run` | Run the application in the branch (e.g., `dotnet run`) |
| `nm_v1_workspace_run_stop` | Stop a process started by `nm_v1_workspace_run` |
| `nm_v1_workspace_exec_status` | Query latest persisted execution result for a branch |
| `nm_v1_workspace_path` | Get branch working directory filesystem path |
| `nm_v1_workspace_profile_get` | Get detected workspace profile (roots, stacks, resolved commands) for a branch |
| `nm_v1_workspace_profile_rescan` | Re-scan and refresh detected workspace profile for a branch |

---

## Representative requests

### Projection get

Tool: `nm_v1_projection_get`

```json
{
  "projectionType": "WorkUnit",
  "projectionLevel": "Normal",
  "workUnitId": "WU-123",
  "branchId": "feature/payment-validation"
}
```

Response:

```json
{
  "contractVersion": "v1",
  "projectionType": "WorkUnit",
  "level": "Normal",
  "data": {}
}
```

### Work unit create

Tool: `nm_v1_workunit_create`

```json
{
  "goal": "Implement payment validation",
  "branchId": "feature/payment-validation"
}
```

### Merge propose

Tool: `nm_v1_merge_propose`

Submits a merge proposal with the full agent-loop logic previously reserved for
the in-process dispatcher: workspace diff generation, `filesTouched` parsing
(with fallback to a `ListAsync` listing), artifact lineage record,
`ArtifactProposed` execution event, and a best-effort work-unit status
transition to `Proposed` + current-stage advance to `Review`.

Policy gate at `ProposalCreated` checkpoint fires before diff: if
`RequireBuildBeforeProposal` / `RequireTestBeforeProposal` are enabled,
`WorkspaceExecutionRule` runs build/test in the source branch directory.
Results (pass or fail) are attached to the proposal's `VerificationResults`.

All three transports (MCP, REST, agent-loop dispatcher) now execute the same
`IMergeCommandService.ProposeAsync`; idempotency is handled via an optional
`commandId` parameter / `X-Command-Id` header.

```json
{
  "sourceBranch": "feature/payment-validation",
  "targetBranch": "main",
  "summary": "Payment validation complete",
  "goal": "Implement payment validation",
  "changeDescription": "Added validation middleware and tests",
  "workUnitId": "WU-789",
  "agentId": "agent-12",
  "model": "gpt-4",
  "provider": "openai",
  "commandId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
}
```

Response (MCP thin-adapter wrapper):

```json
{
  "contractVersion": "v1",
  "data": {
    "proposalId": "MP-100",
    "status": "Draft"
  }
}
```

The REST transport (`POST /studio/merges`) returns the full `MergeProposal`
record including `workspaceChanges`, `filesTouched`, `diffGeneratedAt`,
`agentId`, `model`, `provider`, `sessionId`, and `workUnitId` populated.

### Workspace summary

Tool: `nm_v1_workspace_summary`

Provides active work units, agents, pending merges, failures, and known good states. This is the likely VS Code entry surface.

### Workspace build

Tool: `nm_v1_workspace_build`

```json
{
  "branchId": "work-abc123",
  "buildCommand": "dotnet build -c Release",
  "timeoutSeconds": 600
}
```

Response:

```json
{
  "branchId": "work-abc123",
  "builds": [
    {
      "success": true,
      "exitCode": 0,
      "stdOut": "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n",
      "stdErr": "",
      "buildSystem": "dotnet",
      "command": "dotnet build -c Release",
      "startedAt": "2026-06-18T12:00:00Z",
      "completedAt": "2026-06-18T12:00:05Z",
      "truncated": false
    }
  ],
  "tests": [],
  "lintResults": [],
  "allSucceeded": true,
  "executedAt": "2026-06-18T12:00:05Z",
  "nodeId": "exec/work-abc123/20260618120000"
}
```

### Workspace test

Tool: `nm_v1_workspace_test`

```json
{
  "branchId": "work-abc123",
  "testCommand": "dotnet test --no-build"
}
```

Response:

```json
{
  "branchId": "work-abc123",
  "builds": [],
  "tests": [
    {
      "success": true,
      "exitCode": 0,
      "totalTests": 47,
      "passed": 47,
      "failed": 0,
      "skipped": 0,
      "stdOut": "Passed! - Failed: 0, Passed: 47, Skipped: 0, Total: 47",
      "buildSystem": "dotnet",
      "command": "dotnet test --no-build",
      "startedAt": "2026-06-18T12:00:05Z",
      "completedAt": "2026-06-18T12:00:12Z",
      "truncated": false
    }
  ],
  "lintResults": [],
  "allSucceeded": true,
  "executedAt": "2026-06-18T12:00:12Z",
  "nodeId": "exec/work-abc123/20260618120005"
}
```

### Workspace exec status

Tool: `nm_v1_workspace_exec_status`

```json
{
  "branchId": "work-abc123"
}
```

Returns the latest `BranchExecutionResult` for the branch, or 404 if none exists.

### Workspace path

Tool: `nm_v1_workspace_path`

```json
{
  "branchId": "work-abc123"
}
```

Response:

```json
{
  "branchId": "work-abc123",
  "workingDirectory": "C:\\Users\\...\\studio-workspace\\work-abc123",
  "exists": true
}
```

### Artifact record

Tool: `nm_v1_artifact_record`

```json
{
  "workUnitId": "WU-789",
  "type": "DecisionLog",
  "title": "Orchestrator fan-out decision",
  "body": "Fanning out payment validation into 3 child work units: validation, tests, docs.",
  "parentArtifactId": "WU-789"
}
```

### Artifact query

Tool: `nm_v1_artifact_query`

```json
{
  "workUnitId": "WU-789",
  "type": "DecisionLog",
  "keywords": "fan-out payment"
}
```

### Workspace read/write/delete/list/diff/exists

These are branch-scoped filesystem operations consumed by agent loops and the VS Code extension. Examples:

**Read:**

```json
{
  "branchId": "work-abc123",
  "relativePath": "src/main.cs"
}
```

**Write:**

```json
{
  "branchId": "work-abc123",
  "relativePath": "src/main.cs",
  "content": "// updated content"
}
```

**Diff:**

```json
{
  "sourceBranchId": "work-abc123",
  "targetBranchId": "main"
}
```

---

## Error envelope

Errors return JSON with:

```json
{
  "contractVersion": "v1",
  "tool": "nm_v1_task_update",
  "status": "error",
  "message": "Task not found"
}
```

---

## Typed DTOs

C# request/response records live under `src/NodalMerge.Studio.Contracts/Mcp/` mirroring this document.

---

## REST Endpoint Parity (Phase 6.5 + 6.6)

Every MCP tool has a corresponding REST endpoint via the Studio Host. The mapping is maintained in `StudioRestEndpoints.cs`. Key endpoints:

| MCP Tool | REST Endpoint |
|----------|--------------|
| `nm_v1_workspace_build` | `POST /studio/workspace/build?branchId=...` |
| `nm_v1_workspace_test` | `POST /studio/workspace/test?branchId=...` |
| `nm_v1_workspace_exec` | `POST /studio/workspace/exec?branchId=...` |
| `nm_v1_workspace_run` | `POST /studio/workspace/run?branchId=...` |
| `nm_v1_workspace_run_stop` | `POST /studio/workspace/run/stop?branchId=...` |
| `nm_v1_workspace_exec_status` | `GET /studio/workspace/exec/latest?branchId=...` |
| `nm_v1_workspace_path` | `GET /studio/workspace/path?branchId=...` |
| `nm_v1_workspace_profile_get` | `GET /studio/workspace/profile?branchId=...` |
| `nm_v1_workspace_profile_rescan` | `POST /studio/workspace/profile/rescan?branchId=...` |
| Output download (16m) | `GET /studio/workspace/exec/output?branchId=...&resultId=...` |

---

---

## Phase 6.7+ Addendum

> **Note:** The design principles (MCP-1 through MCP-5), tool naming conventions, error envelope
> format, and all tool namespaces through `nm_v1_artifact_*` above remain frozen and accurate.
> The sections below document tool namespaces and REST-only capabilities added after Phase 6.6.
> For the complete, authoritative catalog — including dispatch status, REST endpoint parity, and
> coverage analysis — see [docs/reference/api-reference.md](../reference/api-reference.md).

### New tool namespaces (Phase 6.7+)

None of these are dispatched to in-process orchestrator/worker agents. They are available to
external MCP clients and headless peers calling via REST.

| Namespace | Tools | Purpose |
|---|---|---|
| `nm_v1_goal_*` | `goal_create`, `goal_list` | Create and list decision-centric `GoalNode` records. `goal_create` also creates the underlying work unit in one call (`GoalId == WorkUnitId`). `goal_list` falls back to work units when the goal store is empty. |
| `nm_v1_decision_*` | `decision_record`, `decision_list` | Record and query Accepted/Rejected/Deferred/Superseded decisions against a proposal. |
| `nm_v1_evidence_*` | `evidence_attach`, `evidence_list` | Attach build/test evidence from the latest execution result to a work unit; list evidence entries. |
| `nm_v1_trajectory_*` | `trajectory_create`, `trajectory_replay` | Record lifecycle phase transitions (GoalDefined → Converged/Forked/Abandoned); replay decisions in Linear, BranchExplorer, or Counterfactual mode. |
| `nm_v1_hypothesis_*` | `hypothesis_fork`, `hypothesis_list` | Fork a work unit or proposal with a typed fork strategy (Code/Reasoning/Model/Research/Architecture/Library/Product); list forks by parent. |
| `nm_v1_reasoning_*` | `reasoning_record` | Record a reasoning commit from an orchestration step. |
| `nm_v1_model_*` | `model_compare`, `model_replay` | Compare diverged files between two proposals; list all proposals for a work unit grouped for model comparison. |

### Phase 7 REST-only capabilities

These capabilities have no `nm_v1_*` MCP tool. They are accessible only via REST (from the
VS Code extension or direct HTTP clients). Autonomous agents cannot trigger them.

| Capability | REST endpoint(s) | Notes |
|---|---|---|
| Experiments (create/list/get) | `POST /studio/experiments`, `GET /studio/experiments`, `GET /studio/experiments/{id}` | Multi-fork parallel exploration. An agent or MCP client cannot launch an experiment on its own. |
| Steering (redirect, fork-from-node) | `POST /studio/steering/redirect`, `POST /studio/steering/fork-from-node` | Pause a running agent and inject a constraint into a new sibling. Human/extension only. |
| Counterfactuals (create) | `POST /studio/counterfactuals` | Re-run a completed work unit under a different profile. Viewing via `nm_v1_trajectory_replay` (mode=Counterfactual) is available; creating is REST-only. |
| Review policy on work-unit create | `reviewPolicy` field on `POST /studio/workunits` | `nm_v1_workunit_create` always defaults to `HumanRequired`. To use `AgentApproval` or `Hybrid`, create via REST. |
| Promotion branches | `usePromotionBranch` on `POST /studio/options`, `POST /studio/branches/candidate/promote` | No MCP tool to toggle or promote. |
| Fork from Known Good | `POST /studio/state/{stateId}/fork` | Fork a new work unit seeded from a checkpoint. `nm_v1_state_checkoutKnownGood` restores in place; forking requires REST. |

---

## External Caller Surface (`nms_v1_*`)

A separate, higher-level MCP tool surface exists under the `nms_v1_*` namespace
(NodalMerge **Studio** v1, note the `s`). These 13 tools are designed for external MCP clients
— Claude Code, Cursor, scripts, CI — to orchestrate the workspace at a goal-centric level
without needing to know work units, branches, or the internal DAG.

The design principles (MCP-1 through MCP-5) and error envelope format apply to `nms_v1_*` as
well. C# constants live in `McpServerToolNames` (separate from `McpToolNames`).

| Namespace | Tools | Purpose |
|---|---|---|
| `nms_v1_goal_*` | `goal_run`, `goal_list`, `goal_status`, `goal_cancel`, `goal_pause`, `goal_resume` | Full goal lifecycle |
| `nms_v1_clarification_*` | `clarification_respond` | Answer pending agent questions |
| `nms_v1_results_*` | `results_get`, `results_apply` | Inspect and apply merge proposals |
| `nms_v1_repo_*` | `repo_register`, `repo_list` | Register repositories for agents to work in |
| `nms_v1_workspace_*` | `workspace_status`, `feedback_record` | Workspace snapshot and durable feedback injection |

None of these tools are dispatched to in-process orchestrator/worker agents — they are external-caller
only. See [docs/reference/api-reference.md](../reference/api-reference.md) for full descriptions,
parameter details, and the recommended call sequence.

---

## Out of scope for MCP v1

* Raw DAG node CRUD
* Vector DB / agent memory stores
* Agent-controlled merges without explicit per-goal review-policy opt-in
* Unversioned tool names