# Projection v1 contract

Status: **Frozen for implementation**

C# types: `NodalMerge.Studio.Contracts.Projections`

---

## Purpose

Projection Manager transforms NodalMerge state into agent-consumable context. Agents never read raw DAG structures as their primary reasoning path.

```text
projection = f(DAG state, task state, optional agent scope)
```

Projections are **read-only**. No reverse writes from projections into storage.

---

## Projection types

| Type | Primary consumers | Contains |
|------|-------------------|----------|
| `WorkUnit` | Worker agents | Goal, status, active tasks, dependencies, success criteria, assigned agents |
| `AuthoritativeState` | Orchestrator, humans | Merged authoritative state only (no branch-local speculative changes) |
| `Task` | Orchestrator, workers | Open/blocked/completed tasks, assignments |
| `MergeProposal` | Humans, orchestrator | Pending proposals, review status, verification results |
| `ExecutionSnapshot` | Worker agents | Reasoning state, failure history, recovery hints |

List via MCP: `nm.v1.projection.list`

Fetch via MCP: `nm.v1.projection.get`

---

## Compression levels

All projection types support:

| Level | Use |
|-------|-----|
| `Full` | Maximum detail |
| `Normal` | Default agent context |
| `Compact` | Token-efficient |
| `Emergency` | Minimal operational state |

API shape:

```text
nm.v1.projection.get(projectionType, projectionLevel, scope...)
```

Internal service: `IProjectionManager.GetAsync(ProjectionRequest)`

---

## Payload shapes (Normal level)

Typed records in `ProjectionContracts.cs`:

* `WorkUnitProjectionPayload`
* `AuthoritativeStateProjectionPayload`
* `TaskProjectionPayload`
* `MergeProposalProjectionPayload`
* `ExecutionSnapshotProjectionPayload`

These are contract shapes — implementations may omit fields at lower compression levels.

---

## Scope parameters

`ProjectionRequest` accepts optional scope:

* `WorkUnitId` — work-unit-scoped projections
* `BranchId` — branch context (MCP-3)
* `AgentId` — agent-scoped execution views

---

## Relationship to NodalMerge query projections

NodalMerge core exposes low-level `projection.build/read` over the DAG. Studio Projection Manager is a **higher-level cognition layer** that may use those primitives internally but exposes only Studio projection types to agents and MCP clients.

See [crdt-vs-cognition-layer.md](../architecture/crdt-vs-cognition-layer.md).

---

## Rules

1. Tasks in projections represent **intent**, never DAG node IDs.
2. `ExecutionSnapshot` is derived and not authoritative storage.
3. Projections invalidate when underlying DAG or task state changes.
4. Replay is for debugging/recovery — not the primary agent reasoning model (AP-2).
