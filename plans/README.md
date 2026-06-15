# Execution plans

Slice-based delivery for NodalMerge Studio. Each slice should land as a focused PR with a verification checklist.

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

## Phase 2 — Real execution (next)

See [phase-2-real-execution.md](./phase-2-real-execution.md) for the full stub inventory, architectural decisions, and rationale.

| Slice | Focus | Status |
|-------|-------|--------|
| 8a | LLM API config — model, baseUrl, apiKey through VS Code secrets → spawn body → AgentRecord | Planned |
| 8b | Real DAG storage — `NodalMergeStudioNodeStore` + `NodalMergeBranchService` replace in-memory impls | Planned |
| 8c | Orchestrator agent loop — `SpawnAsync` starts a real background task; calls LLM; uses `McpToolDispatcher` | Planned |
| 8d | Worker agent loop — orchestrator spawns worker; worker executes task; creates merge proposal for AP-4 gate | Planned |
| 8e | End-to-end integration test — full loop from work unit creation to merged proposal, automated | Planned |

## Slice document template

Each slice file should include:

1. Problem and scope
2. Files/projects touched
3. Success criteria (testable)
4. Verification checklist
5. Out of scope for the slice

Add new plans as `plans/slice-N-short-name.md`.
