# Headless Peer

A headless peer runs the full Studio runtime — all agent loops, projections, storage, orchestrator,
domain observers — without an HTTP server or MCP-over-HTTP endpoint. It is the primary integration
point for CI/CD pipelines, autonomous background workers, and non-interactive goal injection.

The embedded Studio host (the server powering the VS Code extension) and a headless peer are built
from the same binary. The difference is a single build path: `StudioWebApplication.BuildPeer(args)`
instead of `StudioWebApplication.Build(args)`.

---

## Two modes

### Standalone (no room presence)

`Peer:HostUri` is null or omitted. The `RoomPeerClient` service logs "running standalone" and
becomes a no-op. All agent loops execute locally. Agent work units, artifacts, and proposals are
stored in the peer's own workspace; nothing is replicated to a remote room.

Use this for: local CI automation, test runners, script-triggered agent work, any scenario where
multi-peer room replication is not needed.

### Connected (room presence)

`Peer:HostUri` is set (e.g., `ws://localhost:5080`). `RoomPeerClient` opens an outbound WebSocket
connection to `{HostUri}/ws/{RoomId}` and sends a `hello` message identifying itself with
`peer_id`, `peer_type`, and an empty `frontier`.

Reconnection: exponential backoff starting at 1 s, capped at 30 s. On a `participant.stop` message
addressed to this peer, the peer calls `StopApplication()` and shuts down cleanly.

> **Current status (2026-07-14): connected mode is presence + remote stop only — Studio
> data does NOT replicate yet.** Work units, artifacts, and proposals created by the
> peer stay in the peer's own workspace store; they are not visible to the host or to
> other peers. Inbound room messages (e.g. `catch-up-pack`) are received but not
> applied to the peer's store. The bidirectional replication plane is planned work —
> see `plans/cas-distribution-and-storage.md`, Phase 6 slice 6.1. Until it lands, do
> not build anything that assumes peer-created data appears anywhere but the peer's
> own database.

**What the VS Code extension sees in connected mode:** the peer itself appears in the
participant list as a connected peer (`kind: "peer"`, with its `peer_type`), and it can
be **stopped** from the extension (a targeted `participant.stop` shuts the whole peer
process down). The peer's agents and work units do *not* appear in the Activity Center,
and per-agent pause/resume from the extension is not possible — the host's agent list
is in-process only (`StudioParticipantService`).

---

## Activation

Three equivalent paths — use whichever fits your deployment:

**CLI argument:**
```powershell
dotnet run --project src/NodalMerge.Studio.Host -- --mode peer
```

**Environment variable:**
```powershell
$env:STUDIO_MODE = "peer"
dotnet run --project src/NodalMerge.Studio.Host
```

**Configuration:**
```json
{
  "Peer": {
    "Enabled": true
  }
}
```
Or via env var: `Peer__Enabled=true`.

---

## Configuration reference

All keys live under the `"Peer"` section in `appsettings.json`:

```json
{
  "Peer": {
    "Enabled": true,
    "HostUri": "ws://localhost:5080",
    "RoomId": "studio",
    "PeerType": "ephemeral-agent",
    "PeerId": null
  }
}
```

| Key | Default | Description |
|---|---|---|
| `Peer:Enabled` | `false` | Must be `true` (or use CLI/env override) to activate peer mode |
| `Peer:HostUri` | `null` | WebSocket base URI of the room host. `null` = standalone mode. |
| `Peer:RoomId` | `"studio"` | The NodalMerge room to join. Must match the room ID on the host. |
| `Peer:PeerType` | `"ephemeral-agent"` | `"ephemeral-agent"` for short-lived runs; `"persistent-agent"` for long-running workers. |
| `Peer:PeerId` | `null` | Stable identity string. When null, a UUID is auto-generated and persisted to `{Workspace:RootPath}/.peer-id` so the same identity is reused on restart. |

### Workspace flags for headless use

Under `"Workspace"` in `appsettings.json`:

| Key | Default | Recommended for CI |
|---|---|---|
| `Workspace:AllowAgentGitCommits` | `false` | `true` — commit materialized files after a proposal is approved |
| `Workspace:AllowAgentGitPush` | `false` | `true` — push branches to `origin` after committing (requires `AllowAgentGitCommits=true`) |
| `Workspace:AllowAutoRequeue` | `false` | `true` — automatically retry a failed work unit instead of landing it in the dead-letter queue |
| `Workspace:EnabledDomainAgents` | `[]` | Add observer names if you want reactive constraint checks in the peer |

See [repository-virtualization.md](repository-virtualization.md) for the full `Workspace` config
reference and details on how branch directories are seeded and isolated.

---

## Injecting goals

A headless peer has no MCP-over-HTTP endpoint of its own. Goal injection uses the REST API of the
Studio host (either the embedded host the extension talks to, or the peer's own internal services
if embedded as a library).

### Option A — REST: create goal + GoalNode

`POST http://<studio-host>:5080/studio/goals`

```json
{
  "goal": "Fix authentication timeout on /api/login",
  "owner": "ci-pipeline",
  "repositoryId": "repo-abc123"
}
```

Creates a `WorkUnit` and a `GoalNode` (where `GoalId == WorkUnitId`) in one call. The work unit
appears in the Activity Center immediately. Use this when you want the decision-centric GoalNode
metadata recorded (consulted by `nm_v1_goal_list` and the forthcoming decision audit UI).

### Option B — REST: create work unit only

`POST http://<studio-host>:5080/studio/workunits`

```json
{
  "goal": "Fix authentication timeout",
  "branchId": "fix/auth-timeout",
  "owner": "ci-pipeline",
  "reviewPolicy": "AgentApproval"
}
```

Creates just the work unit. The `reviewPolicy` field (`HumanRequired` / `AgentApproval` / `Hybrid`)
is only available here — the MCP tool `nm_v1_workunit_create` always defaults to `HumanRequired`.

### Option C — Programmatic (embedded library)

If the peer is embedded as a library in a custom host, inject `IWorkUnitCommandService` and call
`CreateAsync` directly. This bypasses HTTP entirely and is the lowest-latency path.

### Spawning an agent after creation

After creating the work unit, start an orchestrator:

`POST http://<studio-host>:5080/studio/agents/spawn`

```json
{
  "workUnitId": "WU-abc123",
  "agentType": "orchestrator",
  "provider": "anthropic",
  "model": "claude-sonnet-4-6",
  "apiKey": "sk-..."
}
```

See [extending-goals.md](extending-goals.md) for more detail on the goal creation API surface and
patterns for different trigger scenarios.

---

## What the peer cannot do

- **No MCP-over-HTTP endpoint.** External MCP clients must connect to the embedded Studio host
  (the full HTTP server). The peer's room participation is presence + remote stop today (see
  the connected-mode status note above).
- **No VS Code extension UI of its own.** All UI interaction happens through the extension
  connected to the embedded host. In connected mode, the *peer* surfaces in the extension's
  participant list (not its agents or work units); in standalone mode, there is no UI
  visibility at all.
- **No agent-to-agent room discovery.** The peer connects to a known `HostUri`; it does not
  discover other peers dynamically.

---

## Use case patterns

### CI failure trigger (webhook → REST)

A CI system sends a webhook to a lightweight listener. The listener calls
`POST /studio/goals` on the Studio host with the failure details as the goal text, then calls
`POST /studio/agents/spawn`. No headless peer process is needed if the Studio host is already
running; the peer is only needed when you want a fully self-contained, no-HTTP process.

### Monitoring alert handler (persistent-agent peer)

A long-running peer (`PeerType: "persistent-agent"`) subscribes to an alert bus. On alert
receipt, it calls `IWorkUnitCommandService.CreateAsync` and
`IAgentControlService.SpawnAsync` in-process. With `AllowAgentGitCommits=true` and
`ReviewPolicy=AgentApproval`, the agent can investigate, propose, and merge a fix autonomously.

### Scheduled maintenance (cron → ephemeral peer)

A cron job launches the peer binary (`--mode peer`). The peer creates a work unit and spawns an
orchestrator. With `AllowAgentGitPush=true`, the agent pushes a branch when done. The peer exits
when the orchestrator finishes (`AllowAutoRequeue=false` is appropriate here to avoid retrying
indefinitely on a cron schedule).

### Background observer (persistent peer, no goal injection) — future state

A `persistent-agent` peer connects to the room (`HostUri` set) but does not inject goals — it
participates in replication with domain observers enabled, so any artifact recorded by
interactive agents triggers the observer pipeline in the peer as well, effectively doubling
observer coverage when the embedded host has observers disabled. **This pattern requires the
replication plane (cas-distribution-and-storage.md Phase 6 slice 6.1) and does not work today**
— artifacts recorded on the host currently never reach a connected peer's store, so its
observers have nothing to react to.

---

## Minimal `appsettings.json` for a CI peer

```json
{
  "Peer": {
    "Enabled": true,
    "HostUri": "ws://studio-host:5080",
    "RoomId": "studio",
    "PeerType": "ephemeral-agent"
  },
  "Workspace": {
    "RootPath": "/tmp/studio-ci",
    "SeedRepositoryPath": "/repo",
    "AllowAgentGitCommits": true,
    "AllowAgentGitPush": true,
    "AllowAutoRequeue": false,
    "EnabledDomainAgents": ["Security", "Test"]
  }
}
```
