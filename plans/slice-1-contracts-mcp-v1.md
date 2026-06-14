# Slice 1 — Contracts and MCP v1 foundation

Status: **Complete (contract freeze)**

## Problem

Before implementing persistence, agent runtime, or VS Code, Studio needs a frozen contract surface so components can evolve independently.

## Scope

* `NodalMerge.Studio.Contracts` project
* Frozen `nm.v1.*` MCP tool names and DTOs
* Projection type/level/payload contracts
* MCP tool stubs wired to service interfaces
* Contract documentation under `docs/contracts/`

## Out of scope

* NodalMerge node persistence (Slice 2)
* Real projection materialization from DAG
* LLM agent runtime
* VS Code extension

## Success criteria

- [x] `McpToolNames.All` lists every v1 tool with `nm.v1.` prefix
- [x] Domain models live in Contracts, referenced by Core services
- [x] MCP server exposes all v1 namespaces (stub implementations OK)
- [x] `docs/contracts/mcp-v1-contract.md` matches `McpToolNames`
- [x] Solution builds and contract/unit tests pass

## Verification

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

## Next slice

**Slice 2 — Core + Storage:** persist WorkUnit/Task/MergeProposal as NodalMerge nodes on work branches using `StudioNodeKind` paths.
