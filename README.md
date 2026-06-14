# NodalMerge Studio

Agent-native collaborative workspace platform built on [NodalMerge](https://github.com/nodalmerge/nodalmerge).

NodalMerge Studio provides agent orchestration, task management, projection generation, MCP integration, human review workflows, and VS Code integration. All persistent state lives in NodalMerge nodes.

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- Sibling checkout of the `nodalmerge` repo (for embedded host and local NuGet packages)

## First-time setup

Pack local NodalMerge NuGet artifacts from the sibling repo:

```powershell
pwsh -File .\scripts\restore-local-nodalmerge.ps1
```

Restore and build:

```powershell
dotnet restore NodalMerge.Studio.slnx --configfile NuGet.config -p:NodalMergePackageVersion=0.1.0-local
dotnet build NodalMerge.Studio.slnx
```

## Run locally

```powershell
pwsh -File .\scripts\dev.ps1
```

Endpoints:

- `GET http://127.0.0.1:5080/health` — process health
- `GET http://127.0.0.1:5080/studio/health` — Studio service health
- NodalMerge runtime routes from embedded host (`/ws/runtime`, `/sync/token`, etc.)

## Verify

```powershell
pwsh -File .\scripts\verify.ps1
```

Unit tests run by default. Integration tests require native NodalMerge packages and a built sibling `nodalmerge` repo.

## Repository layout

| Path | Purpose |
|------|---------|
| `src/NodalMerge.Studio.Contracts` | Frozen domain, projection, and MCP DTOs + `McpToolNames` |
| `src/NodalMerge.Studio.Core` | Service interfaces |
| `src/NodalMerge.Studio.Storage` | NodalMerge node persistence adapters |
| `src/NodalMerge.Studio.Projections` | Projection Manager |
| `src/NodalMerge.Studio.Tasks` | Task services |
| `src/NodalMerge.Studio.Merge` | Merge proposal and review workflow |
| `src/NodalMerge.Studio.AgentRuntime` | Agent execution loop |
| `src/NodalMerge.Studio.Orchestrator` | Work unit and orchestration |
| `src/NodalMerge.Studio.McpServer` | MCP v1 tool surface |
| `src/NodalMerge.Studio.Host` | ASP.NET composition root |
| `clients/` | Placeholder folders for VS Code extension and web dashboard |
| `docs/` | Architecture spec, [MCP v1 contract](docs/contracts/mcp-v1-contract.md), ADRs |
| `plans/` | Slice-based execution plans |

## Configuration

| Variable | Description |
|----------|-------------|
| `STUDIO_ROOM_ID` | Default NodalMerge room for Studio operations |
| `NODALMERGE_PACKAGE_VERSION` | Override NodalMerge NuGet package version (default `0.1.0-local`) |
| `NODALMERGE_NATIVE_AVAILABLE` | Set to `true` to run integration tests in CI |

## Status

Infrastructure scaffold only. Business logic is implemented in iterative slices documented under `plans/`.
