# NodalMerge Studio — agent instructions

## About this project

- NodalMerge Studio is an agent-native collaborative workspace built on NodalMerge.
- All persistent state MUST reside in NodalMerge nodes (no separate memory DB in v1).
- Agents consume projections, not raw DAG history, as their primary context.
- Human approval is required for merge in v1.

## Terminology

- Use **NodalMerge** for the core engine and **NodalMerge Studio** (or **Studio**) for this product.
- Use **room** for sync boundary terminology.
- Use **work unit** for the primary execution abstraction (`Goal + Branch`).
- Use **projection** for agent-consumable derived views (Studio Projection Manager).
- Distinguish **speculative** state from **authoritative** state where relevant.

## Architecture

- **Canonical spec:** [docs/architecture/v1-architecture-spec.md](docs/architecture/v1-architecture-spec.md)
- **MCP v1 contract:** [docs/contracts/mcp-v1-contract.md](docs/contracts/mcp-v1-contract.md)
- **Projection contract:** [docs/contracts/projection-v1-contract.md](docs/contracts/projection-v1-contract.md)
- **CRDT vs cognition layer:** [docs/architecture/crdt-vs-cognition-layer.md](docs/architecture/crdt-vs-cognition-layer.md)
- **Layer 1:** NodalMerge core (Rust) — consumed via NuGet/FFI, never reimplemented here.
- **Layer 2:** Studio services (.NET) — business logic in `src/`.
- **Layer 3:** UX (`clients/`) — presentation only; no authoritative state.

## Build and test

```powershell
pwsh -File .\scripts\restore-local-nodalmerge.ps1   # first time
pwsh -File .\scripts\verify.ps1
pwsh -File .\scripts\dev.ps1
```

## Conventions

- Target framework: `net10.0`
- Package prefix: `NodalMerge.Studio.*`
- MCP contract version: `v1` with frozen tool names in `McpToolNames` (`nm.v1.*`)
- Node schema paths: `studio/{entity}/v1` (see `docs/architecture/node-schemas.md`)
- One capability per PR with a verification checklist in the PR description

## Content boundaries

- Do not add vector DBs, agent memory stores, or non-NodalMerge persistence in v1.
- Do not implement agent-controlled merges or autonomous goal generation in v1.
- Do not put business rules in TypeScript client code.

## Related repos

- `../nodalmerge` — engine, host runtime, protocol
- `../docs` — platform documentation and developer experience apps
