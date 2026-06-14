# ADR-001: Embedded NodalMerge .NET host

## Status

Accepted

## Context

NodalMerge Studio requires authoritative DAG storage, replay, branching, and replication without reimplementing core engine logic. The `nodalmerge` repo provides a .NET host (`NodalMerge.DotNetHost`) that embeds Rust FFI in-process.

## Decision

Studio.Host embeds NodalMerge via a project reference to `NodalMerge.DotNetHost` (local dev) and NuGet packages for abstractions, composition, and native RID artifacts (`NodalMergeUseNuGetPackages=true` by default).

Studio services register through `HostApplication.Build(..., configureServices: ...)` and expose Studio endpoints alongside NodalMerge runtime routes (`/ws/runtime`, `/sync/token`, etc.).

## Consequences

- Single-process local deployment (SpeechSlate pattern)
- Requires sibling `nodalmerge` checkout or published NuGet packages for build
- Integration tests validate service registration against embedded host composition
- Sidecar mode remains a future option if operational needs require it
