# Phase 16 — Workspace as a First-Class Aggregate

Studio's domain model had no `Workspace` entity. What played that role was
`WorkspaceOptions` (`src/NodalMerge.Studio.Storage/WorkspaceOptions.cs`) — a process-wide
singleton bag of runtime config (root path, seed repo path, build/test flags, tool gates).
Ownership of everything else was implicit: a `WorkUnit` knew its `BranchId` and optional
`RepositoryId`; an `ArtifactRef` only knew its owning `WorkUnitId`; a `RepositoryV1` was a bare
registry row with no owner at all.

This phase introduces `WorkspaceV1` as that owning aggregate — **without** introducing
multi-workspace support. Cardinality stays at one workspace per Studio instance
(`WorkspaceId` is always `"workspace-default"`). That's a deliberate scope cut: none of
Studio's actual use cases (architecture forks, library comparisons, multi-repo work) need
*concurrent independent* workspaces, they need one workspace that owns several repositories.
The win is structural — every persisted entity now carries an explicit, non-singleton
`WorkspaceId`, so a second workspace later is additive, not a rewrite.

## Decisions

- **Ownership is explicit.** `WorkspaceV1` owns `Repositories` (via `RepositoryIds`) and, via
  `WorkUnit.WorkspaceId`, every `WorkUnit`. `ArtifactRef` and `ExperimentNode` get no new
  field — they're owned by `WorkUnit` (via `OwnedByWorkUnitId`/`ParentWorkUnitId`), which is
  itself owned by the workspace. Looking up "everything in this workspace" means joining
  through that existing chain, not a new index.
- **Identity is persisted, capabilities are resolved.** `WorkspaceV1` is a minimal persisted
  record (`WorkspaceId`, `Name`, `CreatedAt`, `RepositoryIds`) — no settings, no capability
  flags. `WorkspaceCapabilityResolver.Resolve(WorkspaceOptions)` produces a `CapabilitiesV1`
  snapshot fresh on every call, the same determinism contract as a Projection. Nothing about
  `WorkspaceOptions`' existing flags changed or moved.
- **Repository is a resource, not identity.** `RepositoryV1` gained no new field. Ownership is
  recorded the other direction — `IWorkspaceRegistryService.AttachRepositoryAsync` is called
  from inside `RepositoryRegistryService.RegisterAsync` (and therefore `CreateAsync`/
  `CloneAsync`, which both delegate to it), so every registered repository is automatically
  attached to the default workspace.
- **`WorkspaceId` is resolved server-side, never caller-supplied.** Since cardinality is 1,
  there's nothing for a caller to choose. `WorkUnitCommandService.CreateAsync` resolves it via
  `IWorkspaceRegistryService.GetOrCreateDefaultAsync` and passes it straight through to
  `IOrchestratorService.CreateWorkUnitAsync`. No REST/MCP request body gained a new field.

## Implementation

- `src/NodalMerge.Studio.Contracts/Domain/Workspace.cs` — `WorkspaceV1` record.
- `StudioNodeKind.WorkspaceV1` added to `StudioNodeStore.cs` (same flat node store every other
  kind uses — no new persistence mechanism).
- `src/NodalMerge.Studio.Storage/WorkspaceRegistryService.cs` — `IWorkspaceRegistryService` +
  implementation, same shape/idiom as `RepositoryRegistryService`
  (`GetOrCreateDefaultAsync`, `AttachRepositoryAsync`, `GetAsync`, `IRehydratable`). Named
  `WorkspaceRegistryService` rather than `WorkspaceService` — `IWorkspaceService` was already
  taken (the dashboard summary/status service, `ServiceContracts.cs:1085`).
- `WorkUnit.WorkspaceId` (default `"workspace-default"`) threaded through
  `IOrchestratorService.CreateWorkUnitAsync` → `InMemoryWorkUnitService` →
  `WorkUnitCommandService.CreateAsync`, mirroring exactly how `RepositoryId` was threaded in
  the multi-repo phase.
- `src/NodalMerge.Studio.Storage/WorkspaceCapabilityResolver.cs` — `CapabilitiesV1` +
  `Resolve(WorkspaceOptions)`. Build/Test/Run/Git/CreateRepository/ImportRepository resolve to
  `true` unconditionally: their MCP tools are registered with no gating flag in this codebase
  today. `DocFetch`/`EnabledDomainAgents` reflect the one pair of flags that actually toggle.
- Exposed via `GET /studio/workspace/capabilities` (`StudioRestEndpoints.MapWorkspaceEndpoints`)
  and MCP `nm_v1_workspace_capabilities` (`WorkspaceTools.Capabilities`,
  `McpToolDispatcher.WorkspaceCapabilities`).
- DI: `WorkspaceRegistryService` registered ahead of `RepositoryRegistryService` in
  `ServiceCollectionExtensions.AddRehydratableServices` (the latter now depends on the former).
- Tests: `tests/NodalMerge.Studio.Integration.Tests/WorkspaceAggregateTests.cs`.

## Explicitly deferred (v2 and beyond)

- **Multi-workspace cardinality.** `GetOrCreateDefaultAsync` always resolves
  `"workspace-default"`. Supporting a second workspace means letting that resolution take a
  chosen id, plus REST/MCP create/list/switch endpoints and extension UI for picking one — none
  of that exists, deliberately. `WorkspaceV1`'s shape (a real id, an explicit `RepositoryIds`
  list) was chosen so this is additive later, not a migration.
- **Structured capability *storage*.** `WorkspaceCapabilityResolver` only resolves a read-only
  view from `WorkspaceOptions`' existing ad-hoc booleans — it doesn't replace them. Promoting
  `RequireBuildBeforeProposal`/`DocFetchTools`/etc. into a single structured, persisted
  capability model is a larger, separable refactor.
- **Per-workspace settings overrides.** `WorkspaceOptions` stays a single process-wide config
  object; nothing here makes it per-`WorkspaceId`.
- **Workspace-scoped write surface.** No REST/MCP route to create, rename, or delete a
  workspace — there's exactly one, implicitly created on first use.
