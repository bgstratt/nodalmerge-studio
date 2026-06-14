# MCP v1 contract (frozen)

Status: **Frozen for implementation**

Canonical C# constants: `NodalMerge.Studio.Contracts.Versioning.McpToolNames`

Architecture context: [v1 architecture spec](../architecture/v1-architecture-spec.md)

---

## Design principles

### MCP-1: Projection first

Agents should rarely need storage-level operations. Prefer `nm.v1.projection.get` over raw DAG queries. The DAG is an implementation detail.

### MCP-2: Intent over topology

Expose `nm.v1.task.create`, `nm.v1.merge.propose`. Do not expose `create_node()` or `append_edge()` — those belong to NodalMerge internals.

### MCP-3: Branch-aware

Operations execute within branch context. Requests SHOULD include `branchId` where applicable.

### MCP-4: Versioned forever

All tools use the `nm.v1.*` namespace. Breaking changes require `nm.v2.*`.

---

## Namespace layout

```text
nm.v1.projection.*
nm.v1.task.*
nm.v1.workunit.*
nm.v1.branch.*
nm.v1.merge.*
nm.v1.replay.*
nm.v1.state.*
nm.v1.snapshot.*
nm.v1.agent.*
nm.v1.workspace.*
```

---

## Tool catalog

| Tool | Purpose |
|------|---------|
| `nm.v1.projection.get` | Get projection by type and level |
| `nm.v1.projection.list` | List projection types and levels |
| `nm.v1.workunit.create` | Create work unit (goal + branch) |
| `nm.v1.workunit.get` | Get work unit |
| `nm.v1.workunit.update` | Update status / assignment |
| `nm.v1.workunit.list` | List work units |
| `nm.v1.task.create` | Create task (intent only, no DAG refs) |
| `nm.v1.task.update` | Update task |
| `nm.v1.task.list` | List tasks |
| `nm.v1.task.assign` | Assign task to agent |
| `nm.v1.branch.create` | Create branch |
| `nm.v1.branch.checkout` | Checkout branch |
| `nm.v1.branch.list` | List branches |
| `nm.v1.branch.status` | Branch status for agents |
| `nm.v1.merge.propose` | Create merge proposal |
| `nm.v1.merge.validate` | Validate proposal (tests/policy) |
| `nm.v1.merge.review` | Human review metadata |
| `nm.v1.merge.apply` | Apply approved merge (v1 requires approval) |
| `nm.v1.replay.range` | Inspect history range |
| `nm.v1.replay.rollback` | Rollback via known good state |
| `nm.v1.replay.inspect` | Human-friendly history summary |
| `nm.v1.state.markKnownGood` | Mark known good state |
| `nm.v1.state.findKnownGood` | Find known good states |
| `nm.v1.state.checkoutKnownGood` | Checkout known good state |
| `nm.v1.snapshot.get` | Derived execution snapshot |
| `nm.v1.snapshot.compare` | Compare agent snapshots |
| `nm.v1.agent.spawn` | Spawn agent for work unit |
| `nm.v1.agent.pause` | Pause agent |
| `nm.v1.agent.resume` | Resume agent |
| `nm.v1.agent.status` | Agent status |
| `nm.v1.agent.stop` | Stop agent |
| `nm.v1.workspace.summary` | Control tower workspace summary |

---

## Representative requests

### Projection get

Tool: `nm.v1.projection.get`

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

Tool: `nm.v1.workunit.create`

```json
{
  "goal": "Implement payment validation",
  "branchId": "feature/payment-validation"
}
```

### Merge propose

Tool: `nm.v1.merge.propose`

```json
{
  "sourceBranch": "feature/payment-validation",
  "targetBranch": "main",
  "summary": "Payment validation complete",
  "verificationResults": []
}
```

Response:

```json
{
  "contractVersion": "v1",
  "data": { "proposalId": "MP-100" }
}
```

### Workspace summary

Tool: `nm.v1.workspace.summary`

Provides active work units, agents, pending merges, failures, and known good states. This is the likely VS Code entry surface.

---

## Error envelope

Errors return JSON with:

```json
{
  "contractVersion": "v1",
  "tool": "nm.v1.task.update",
  "status": "error",
  "message": "Task not found"
}
```

---

## Typed DTOs

C# request/response records live under `src/NodalMerge.Studio.Contracts/Mcp/` mirroring this document.

---

## Out of scope for MCP v1

* Raw DAG node CRUD
* Vector DB / agent memory stores
* Agent-controlled merges without human review
* Unversioned tool names
