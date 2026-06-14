# VS Code extension (deferred)

The VS Code extension is **not implemented in the infrastructure scaffold**.

## Planned role

Studio Control Tower for humans:

- View branches and replay timeline
- Spawn, pause, and resume agents
- Inspect projections
- Review and approve merge proposals
- Rollback to KnownGoodState

## Integration surface

- **Primary:** MCP v1 tools (`nm.v1.*`) per [MCP contract](../../docs/contracts/mcp-v1-contract.md)
- **Secondary:** Studio Host HTTP endpoints for rich UI flows

## Constraints

Layer 3 presentation only. No business rules or authoritative state in TypeScript.

See [v1 architecture spec](../../docs/architecture/v1-architecture-spec.md) for MCP namespaces and VS Code extension scope.
