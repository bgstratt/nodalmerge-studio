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
| 2 | Core + Storage — node schema read/write on test branch | Planned |
| 3 | Projection Manager — real materialization from DAG + task state | Planned |
| 4 | Tasks + Work units — AP-3 execution model | Planned |
| 5 | Merge workflow — human review states (AP-4) | Planned |
| 6 | Agent Runtime + Orchestrator | Planned |
| 7 | VS Code extension | Planned |

## Slice document template

Each slice file should include:

1. Problem and scope
2. Files/projects touched
3. Success criteria (testable)
4. Verification checklist
5. Out of scope for the slice

Add new plans as `plans/slice-N-short-name.md`.
