# Execution plans

Slice-based delivery for NodalMerge Studio. Each slice should land as a focused PR with a verification checklist.

**Strategic context:**
* [VISION.md](./VISION.md) — the "why" and differentiator test: every feature decision should pass "Can I inspect, branch, replay, review, and audit this artifact?"

**Canonical references:**
* [v1 architecture spec](../docs/architecture/v1-architecture-spec.md) — the "what"
* [MCP v1 contract](../docs/contracts/mcp-v1-contract.md) — the operating-system API
* [Projection v1 contract](../docs/contracts/projection-v1-contract.md)

## Recommended slice order

| Slice | Focus | Status |
|-------|-------|--------|
| 0 | Repo scaffold — projects compile, Host health, MCP stubs | Complete |
| 1 | Contracts + MCP v1 freeze (`nm.v1.*`, DTOs, docs) | Complete — [slice-1-contracts-mcp-v1.md](./slice-1-contracts-mcp-v1.md) |
| 2 | Core + Storage — in-memory branch, KGS, workspace summary | Complete — [slice-2-storage-in-memory.md](./slice-2-storage-in-memory.md) |
| 3 | Projection Manager — real materialization from in-memory state | Complete — [slice-3-projection-manager.md](./slice-3-projection-manager.md) |
| 4 | Tasks + Work units — AP-3 execution model | Complete — [slice-4-tasks-workunit-ap3.md](./slice-4-tasks-workunit-ap3.md) |
| 5 | Merge workflow — human review states (AP-4) | Complete — [slice-5-merge-workflow-ap4.md](./slice-5-merge-workflow-ap4.md) |
| 6 | Agent Runtime + Orchestrator | Complete — [slice-6-agent-runtime-orchestrator.md](./slice-6-agent-runtime-orchestrator.md) |
| 7a | Extension scaffold — sidecar spawn, health, status bar | Complete — [slice-7a-extension-scaffold.md](./slice-7a-extension-scaffold.md) |
| 7b | Workspace dashboard panel — WUs, agents, merges, failures | Complete — [slice-7b-workspace-dashboard.md](./slice-7b-workspace-dashboard.md) |
| 7c | Merge review panel — AP-4 human gate UI | Complete — [slice-7c-merge-review-panel.md](./slice-7c-merge-review-panel.md) |
| 7d | DAG replay panel — live branch visualization via /ws/runtime | Complete — [slice-7d-dag-replay-panel.md](./slice-7d-dag-replay-panel.md) |
| 7e | Historical scrubbing — cursor, branch-from-cursor, known good | Complete — [slice-7e-historical-scrubbing.md](./slice-7e-historical-scrubbing.md) |
| 7f | Agent config — profiles, domain routing, topology templates | Complete — [slice-7f-agent-config.md](./slice-7f-agent-config.md) |
| 7g | Studio write-through — domain events land in the DAG for true time-travel | Complete — [slice-7g-studio-writethrough.md](./slice-7g-studio-writethrough.md) |

## Phase 2 — Real execution

See [phase-2-real-execution.md](./phase-2-real-execution.md) for the full stub inventory, architectural decisions, and rationale.

| Slice | Focus | Status |
|-------|-------|--------|
| 8a | LLM API config — model, baseUrl, apiKey through VS Code secrets → spawn body → AgentRecord | Complete |
| 8b | Real DAG storage — `NodalMergeStudioNodeStore` + `NodalMergeBranchService` replace in-memory impls | Complete |
| 8c | Orchestrator agent loop — `SpawnAsync` starts a real background task; calls LLM; uses `McpToolDispatcher` | Complete |
| 8d | Worker agent loop — orchestrator spawns worker; worker executes task; creates merge proposal for AP-4 gate | Complete |
| 8e | End-to-end integration test — full loop from work unit creation to merged proposal, automated | Complete |

## Phase 2.5 — Pipeline stages & artifact-centered execution

See [phase-2.5-agent-profiles.md](./phase-2.5-agent-profiles.md). Key shift: from persona-based worker roles (Implementer, Reviewer) to a **pipeline of stages over shared artifacts**. Reviewer is the human gate in 2.5; automated reviewer is Phase 3.

| Slice | Focus | Status |
|-------|-------|--------|
| 9a | Diff in Merge Review panel — render `workspaceChanges` with +/- coloring | Planned |
| 9b | `AgentProfile` DAG entity with `PipelineStage` enum; default profiles; REST endpoints | Planned |
| 9c | Profile-driven loop config — system prompt, maxIterations, tool filtering loaded from profile | Planned |
| 9d | `AgentWorkspaceProjection` with artifact chain (plan → changes → proposals) | Planned |
| 9e | Artifact-state-driven routing in orchestrator — routes by what artifacts exist, not by persona | Planned |

## Phase 3 — Multi-agent foundations

See [phase-3-foundations.md](./phase-3-foundations.md). Five foundational systems that make parallelism safe and deterministic before any fan-out is added: Work Unit DAG, Scheduler/Queue, Branch Isolation, Artifact Lineage, and Orchestration Decision Log.

| Slice | Focus | Status |
|-------|-------|--------|
| 10a | Work Unit DAG — parent/child, dependency edges, file scope boundaries | Planned |
| 10b | Scheduler / Work Queue — assignment queue, concurrency control, leasing | Planned |
| 10c | Branch Isolation Model — per-WorkUnit execution branch, no cross-worker writes | Planned |
| 10d | Artifact Ownership + Lineage — proposal attribution, `FilesTouched`, conflict pre-detection | Planned |
| 10e | Orchestration Decision Log — every routing decision persisted with input projection + reason | Planned |
| 10f | Proposal DAG & Artifact Branching — "checkout S0, apply A only"; branch/compare/replay from any proposal | Planned |
| 10g | Knowledge Artifacts — `Research`, `Decision`, `Constraint` types; `artifact.record/query`; inherited constraints in projection | Planned |

## Phase 4 — Fan-out & merge reduction

See [phase-4-fanout-merger.md](./phase-4-fanout-merger.md). Built on Phase 3 foundations: N workers in parallel, artifact lifecycle state machine, Merger/Reducer stage, optional automated reviewer, dead-letter escalation.

| Slice | Focus | Status |
|-------|-------|--------|
| 11a | Artifact Lifecycle State Machine — formal states per WorkUnit and MergeProposal | Planned |
| 11b | Fan-out — planner decomposes goal into N child work units; scheduler executes in parallel | Planned |
| 11c | Merger/Reducer — N proposals reconciled into one candidate; conflict reporting | Planned |
| 11d | Automated Reviewer agent (`Stage = Review`) as optional pre-gate before human | Planned |
| 11e | Dead-letter & failure escalation — failed agents with retry from dashboard | Planned |

## Phase 5 — Control-plane UI

See [phase-5-control-plane-ui.md](./phase-5-control-plane-ui.md). Makes the pipeline visible and steerable: Plan Breakdown panel (live WorkUnit DAG editor), projection diffing for incremental orchestration, real-time stage streaming, LLM-driven profile selection.

| Slice | Focus | Status |
|-------|-------|--------|
| 12a | Artifact Explorer — inspect/branch/replay every artifact; work unit DAG + timeline + inspector | Planned |
| 12b | Projection Diffing — incremental orchestration reasoning; stall detection | Planned |
| 12c | Pipeline Stage Streaming — real-time stage badges on work unit nodes | Planned |
| 12d | LLM-driven profile selection — orchestrator asks LLM which profile fits a task | Planned |

## Slice document template

Each slice file should include:

1. Problem and scope
2. Files/projects touched
3. Success criteria (testable)
4. Verification checklist
5. Out of scope for the slice

Add new plans as `plans/slice-N-short-name.md`.
