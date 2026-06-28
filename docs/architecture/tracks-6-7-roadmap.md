# Tracks 6 & 7 — Architecture Roadmap

## Guiding Principle

> **The runtime owns engineering state and behavior. Clients own experience. Participants own specialization.**

When unsure where a feature belongs: engineering model and coordination → runtime. User visibility and configuration → client (extension or other). Domain knowledge, credentials, external systems → participant package.

---

## Track 6 — Participant Definition + Event Model

### Goal

Replace the current implicit "agent = spawned process managed by the extension" model with an explicit **Participant** abstraction. Participants declare what they observe, what they publish, and what configuration they need. The runtime becomes the coordination layer; participants connect, observe, and react without needing to know about each other.

### Participant Definition Schema

Two kinds of participants:

```yaml
# Generic domain agent — ships with Studio, configurable via extension UI
kind: reasoning
name: Reviewer
subscribes:
  - ProposalCreated
publishes:
  - ReviewCompleted
configuration:
  - model
  - systemPrompt
  - tools           # allowlist of tool names this agent may call
```

```yaml
# Integration participant — external package, not configured in the extension
kind: observer
name: Datadog
publishes:
  - Benchmark
  - Observation
  - Incident
configuration:
  - apiKey           # secrets managed outside Studio
  - endpoint
  - pollInterval
```

Key distinction: `reasoning` participants belong in the extension UI (system prompt, model, tool allowlist — no secrets). `observer` participants belong in standalone packages with their own secret/config harness. Don't force integration participants into the extension settings.

### Event Model

Current tools are request/response. Track 6 introduces a declarative subscription model:

| Event | Published by | Consumed by |
|---|---|---|
| `WorkUnitCreated` | Runtime | Planner, domain agents |
| `ArtifactPublished` | Agent | Reviewer, CI participant |
| `ProposalCreated` | Runtime | Reviewer |
| `ReviewCompleted` | Reviewer | Runtime (merge gate) |
| `ProjectionMaterialized` | Runtime | CI participant, observers |
| `ExperimentCompleted` | Runtime | Observers |
| `MergeAccepted` | Runtime | Deployment observers |
| `BuildCompleted` | CI participant | Runtime |
| `BenchmarkAvailable` | CI/observer | Runtime |

Participants declare `subscribes` in their definition; the runtime routes events to them. The Planner doesn't need to know the Reviewer exists — the Reviewer subscribes to `ProposalCreated` and reacts.

### Runtime API Surface

Add to the REST/MCP/WS surface:

```
GET  /participants                  → list all participants with status
POST /participants/{id}/stop        → stop a participant by ID
GET  /participants/{id}/events      → event history for a participant
POST /events                        → publish an event (for participant packages)
GET  /event-types                   → list all registered event types
```

The extension renders participant status from `GET /participants` — it doesn't know or care whether participants are processes, containers, remote machines, or services:

```
Planner         Connected
Reviewer        Disconnected
CI Runner       Busy
Datadog         Healthy
```

### Agent Stopping Gap (fix here)

Currently the Activity Center can stop in-process agents but not headless/remote ones. Track 6 resolves this: `POST /participants/{id}/stop` sends a lifecycle signal through the room (WebSocket broadcast `{"type":"participant.stop","peer_id":"..."}`) that any connected peer can act on. The peer's `RoomPeerClient` receives the broadcast and calls `IHostApplicationLifetime.StopApplication()`.

### Extension UI

The extension gains a **Participant configuration panel** (or extends Model & Agent Studio) for `reasoning`-kind participants:
- Add/remove domain agents
- Configure system prompt, model, tool allowlist
- These map directly to `AgentProfile` entries in settings but with a guided UI rather than raw JSON

`observer`-kind participants are not configured here — they appear in the participant list as externally-managed entries once they connect.

### Implementation Sketch

- `RuntimeProtocolMapper.cs`: add `participant.stop` inbound message type → `StopParticipant` command
- `RuntimeRoomBroker.cs`: `GET /participants` endpoint that returns `_peers` plus status
- New `IParticipantEventBus` service (in-process pub/sub for `reasoning` participants in server mode)
- `ParticipantDefinition` record (kind, name, subscribes, publishes, configurationSchema)
- `RoomPeerClient.cs`: handle `participant.stop` broadcast → `IHostApplicationLifetime.StopApplication()`
- Extension: participant panel, `POST /participants/{id}/stop` wired to a stop button

---

## Track 7 — Projection Materialization

### Goal

Every write to the filesystem goes through a **Projection** first, then a **Materialization** step. The direct `Workspace → Filesystem` path becomes a fallback/legacy path, eventually removed.

### Current Path (legacy)

```
WorkUnit goal
  → Agent writes files directly to branch workspace
  → MergeProposal captures workspace diff
  → Merge applies diff to target branch
```

### Target Path

```
WorkUnit goal
  → Agent publishes Artifacts (structured data, not raw files)
  → Runtime builds a Projection (canonical view of artifact state)
  → Materialization step: Projection → Filesystem
  → Build / Test / Deploy runs against materialized filesystem
  → MergeProposal captures materialized diff
```

### Why

- **Reproducibility**: given the same Projection, you always get the same filesystem. No "it worked on my machine" from workspace drift.
- **Auditability**: the Projection is a named, versioned, queryable snapshot. You can ask "what did the filesystem look like at checkpoint N?" without digging through diffs.
- **Multiple materialization targets**: the same Projection can materialize to a local filesystem, a container image, or a remote workspace — transport becomes configurable, not hardcoded.
- **CRDT convergence**: Projections already live in the causal graph (Track 4 wired `PromoteCheckpointToGraph`). Materialization becomes the act of making a CRDT-resolved state concrete on disk.

### Materialization Pipeline

```
Projection (CRDT-resolved canonical map)
  ↓
MaterializationPlan (which files change, add, delete)
  ↓
MaterializationExecutor (writes to a target)
  ↓
MaterializationResult (hash, file list, errors)
  ↓
Build / Test / Deploy
```

`MaterializationTarget` is an interface: `LocalFilesystem`, `ContainerLayer`, `RemoteWorkspace`. Start with `LocalFilesystem` only.

### Events emitted

- `ProjectionMaterialized { projectionId, targetKind, fileCount, durationMs }` — consumed by CI participants (Track 6) to trigger build/test

### Implementation Sketch

- `IProjectionMaterializer` service in `NodalMerge.Studio.Orchestrator`
- `MaterializeProjectionAsync(projectionId, targetPath)` → `MaterializationResult`
- Called at the end of `WorkUnitStatus.Proposed` transition (agent finished writing, now materialize before review)
- `ProjectionMaterialized` event published to `IParticipantEventBus` (Track 6)
- REST endpoint: `POST /studio/projections/{id}/materialize`
- MCP tool: `materialize_projection` (replaces direct file-write tools in agent loops long-term)
- Remove direct workspace-file-write path from `WorkerAgentLoop` once materialization is stable

### Deferred / Out of Scope for Track 7

- Container image materialization targets
- Remote workspace materialization (needs participant auth from Track 6)
- Replacing MCP file-write tools in agent loops (Track 8 / separate)
- Rollback of materialized state (can derive from CRDT history — future)

---

## Dependency Order

```
Track 6 (events + participants) must precede Track 7 (materialization)
because ProjectionMaterialized is an event that CI/observer participants consume.
Participants need to exist before events are worth publishing.
```

Track 6 can be split into two phases if needed:
- **6a**: `GET /participants`, stop endpoint, participant status in extension
- **6b**: Event model, subscriptions, `IParticipantEventBus`

Track 7 depends on 6b (the event bus) but not on 6a.

---

## CORS Note (from Track 5)

Current: `AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()` — works for remote access since the actual access boundary is network/firewall, not CORS. The stale comment "safe for local-only access" should be updated. When Track 6 introduces participant auth (token/keypair credentials), add `AllowSpecificOrigins` alongside credential checking. For now, leave CORS permissive.
