# CRDT substrate vs cognition layer

This document resolves the architectural split between NodalMerge core (peer replication, CRDT, DAG) and NodalMerge Studio (agents, MCP, projections).

---

## Two systems layered together

### Layer A — NodalMerge core (CRDT / DAG / replication)

* Peer replication
* Immutable DAG nodes
* Branching, replay, promotion
* Offline edits and convergence

This is the **data fabric**.

### Layer B — Studio agent convergence (MCP / projections / tasks)

* MCP control plane
* Projections (derived views)
* Task and work unit orchestration
* VS Code control tower

This is the **cognition layer**.

---

## What CRDT is doing

CRDT is **not for agents**. CRDT is for:

```text
multiple hosts / peers / users / processes
writing into the same DAG without coordination
```

Examples:

* VS Code instance A edits
* VS Code instance B edits
* Headless agent writes
* Server writes
* CLI replay inspects

All converge structurally through NodalMerge.

---

## Where agents fit

Agents are **peers writing into the CRDT-backed DAG**, restricted by:

```text
CRDT      = how data converges structurally
MCP       = how agents are allowed to mutate intent/workflow
Projections = what agents are allowed to perceive
```

---

## Work unit model

```text
WorkUnit = (goal, branch)
```

* **Goal** — semantic intent
* **Branch** — execution isolation boundary
* Agents operate inside work units
* Merges only occur branch → authoritative target with human approval (v1)

---

## Scratch vs committed state

Agents may use **ephemeral scratch** (runtime reasoning buffer). Scratch is:

* local to agent runtime
* not persisted in the DAG
* not part of convergence

Committed truth lives in NodalMerge nodes only.

---

## Projections are not writable

Projections are pure functions over DAG + task state (+ optional agent scope).

No "fill gaps and merge projections back" — no reverse flow.

---

## Merge semantics

* Branch anywhere; merge into authoritative target (typically `main`) only via proposal + human approval in v1
* CRDT eliminates **structural** conflicts, not **semantic** conflicts
* Validation and merge gatekeeping remain required

---

## Replay

Replay is diagnostic and recovery tooling — not a deterministic guarantee for agent reasoning. Agents prefer projections; humans use replay for inspection and rollback.

---

## Agent isolation

Agents can:

* read shared projections
* submit merge proposals
* update task intent

Agents cannot:

* mutate another agent's scratch
* write directly to authoritative target without review (v1)
* bypass MCP for workflow mutations

---

## Related docs

* [v1 architecture spec](./v1-architecture-spec.md)
* [MCP v1 contract](../contracts/mcp-v1-contract.md)
* [Projection v1 contract](../contracts/projection-v1-contract.md)
