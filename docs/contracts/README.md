# Studio contracts

Frozen v1 contracts for NodalMerge Studio. Components build against these surfaces independently.

## Mental model

```text
NodalMerge Studio
    |
    +-- Domain Model          (NodalMerge.Studio.Contracts.Domain)
    +-- Projection Manager    (NodalMerge.Studio.Contracts.Projections)
    +-- Agent Runtime         (NodalMerge.Studio.Core.Services)
    +-- MCP Contract          (NodalMerge.Studio.Contracts.Mcp + Versioning)
```

The MCP contract is the stable operating-system API for Studio:

* VS Code extension
* Agents
* Future CLI tools
* Future web UI
* Future automation

## Documents

| Document | Purpose |
|----------|---------|
| [mcp-v1-contract.md](./mcp-v1-contract.md) | Frozen MCP tool surface (`nm_v1_*`) |
| [projection-v1-contract.md](./projection-v1-contract.md) | Projection types, levels, payload shapes |
| [../architecture/crdt-vs-cognition-layer.md](../architecture/crdt-vs-cognition-layer.md) | CRDT substrate vs agent cognition layer |
| [../architecture/v1-architecture-spec.md](../architecture/v1-architecture-spec.md) | Product architecture (the "what") |

## Code location

| Area | Project path |
|------|----------------|
| Domain models | `src/NodalMerge.Studio.Contracts/Domain/` |
| Projection contracts | `src/NodalMerge.Studio.Contracts/Projections/` |
| MCP request/response DTOs | `src/NodalMerge.Studio.Contracts/Mcp/` |
| Tool name constants | `src/NodalMerge.Studio.Contracts/Versioning/McpToolNames.cs` |
| Service interfaces | `src/NodalMerge.Studio.Core/Services/` |
| MCP tool implementations | `src/NodalMerge.Studio.McpServer/Tools/` |

## Versioning rules

1. MCP tool names use the `nm_v1_*` prefix and are frozen in `McpToolNames`.
2. Breaking changes require `nm_v2_*` — do not rename v1 tools.
3. Non-breaking additions may extend v1 with new tools only when backward compatible.
4. Typed MCP DTOs live under `NodalMerge.Studio.Contracts.Mcp.*` and should match the markdown contract.

## Implementation status

Contracts and MCP stubs are in place. Persistence-backed behavior lands in subsequent implementation phases.
