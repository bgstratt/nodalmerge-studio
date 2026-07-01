# Extending Goals — Programmatic Goal Injection

Goals can be created by humans via the VS Code extension, by autonomous agents via MCP tools, or
by external systems via the REST API. This guide covers the REST API surface, the distinction
between a `GoalNode` and a `WorkUnit`, and patterns for injecting goals from external triggers
(CI systems, monitoring alerts, scheduled tasks, headless peers).

---

## GoalNode vs. WorkUnit — the distinction

Every executing goal has two representations:

| | WorkUnit | GoalNode |
|---|---|---|
| **What it is** | Execution container | Decision-centric metadata |
| **Lifecycle** | `Pending → Active → Proposed → Reviewing → Merged / Abandoned` | `Exploring → Converging → Converged → Blocked → Abandoned` |
| **Owns** | Branch, agent assignments, task tree, artifact chain | Exploration status, parent/child goal hierarchy |
| **ID relationship** | Primary identity | `GoalId == WorkUnitId` |
| **What the extension reads** | Activity Center "Active Goals" reads work units directly | GoalNode records are not yet read by any extension panel |

For most automation purposes, **creating a work unit is sufficient** — it will appear in the
extension and can have an agent spawned against it. Creating a goal node additionally enrolls the
work unit in the `GoalNode` store, which is used by `nm_v1_goal_list` and `GET /studio/goals`,
and will be the backing store for the forthcoming decision-centric audit UI.

---

## REST API

### Create goal + GoalNode (preferred for full metadata)

`POST /studio/goals`

```json
{
  "goal": "Fix authentication timeout on /api/login",
  "owner": "ci-pipeline",
  "repositoryId": "repo-abc123",
  "referenceFiles": ["src/Auth/LoginHandler.cs"]
}
```

Creates a `WorkUnit` first, then records a `GoalNode` with `GoalId == WorkUnitId`. Response:

```json
{
  "goalId": "WU-abc123",
  "workUnitId": "WU-abc123",
  "branchId": "work-abc123",
  "status": "Exploring"
}
```

### Create work unit only

`POST /studio/workunits`

```json
{
  "goal": "Fix authentication timeout on /api/login",
  "branchId": "fix/auth-timeout",
  "owner": "ci-pipeline",
  "reviewPolicy": "AgentApproval"
}
```

The `reviewPolicy` field (`HumanRequired` / `AgentApproval` / `Hybrid`) is only available here.
The MCP tool `nm_v1_workunit_create` always defaults to `HumanRequired`.

If `branchId` is omitted, a stable ID is generated from the work unit ID.

### Spawn an agent after creation

After creating the work unit, start an orchestrator:

`POST /studio/agents/spawn`

```json
{
  "workUnitId": "WU-abc123",
  "agentType": "orchestrator",
  "provider": "anthropic",
  "model": "claude-sonnet-4-6",
  "apiKey": "sk-...",
  "enabledDomainAgents": ["Security", "Test"]
}
```

The `enabledDomainAgents` field overrides the session-wide `Workspace:EnabledDomainAgents` setting
for this work unit's execution.

---

## Review policy

| Policy | Behavior |
|---|---|
| `HumanRequired` (default) | Every proposal waits at the merge-review gate for manual Accept/Reject in the extension. |
| `AgentApproval` | A reviewer agent evaluates the proposal and auto-applies on approval, or rejects with notes. |
| `Hybrid` | Reviewer agent approves, then a countdown (default 5 min) starts. A human can override during the window. |

`AgentApproval` and `Hybrid` are available only when creating via `POST /studio/workunits` (REST).
Goals created via the MCP tool `nm_v1_workunit_create` or the extension's "New Goal" dialog default
to `HumanRequired`. To use agent-controlled review in an automated pipeline, create via REST and
pair with `AllowAgentGitCommits=true` for a fully unattended flow.

---

## Visibility in the extension

Work units created by REST or a headless peer appear in the Activity Center and Goal Workspace
identically to interactively created ones. There is no "external source" badge. The `owner` field
on the work unit records the creator identity (e.g., `"ci-pipeline"`) and is shown in the
Decision Lens metadata.

---

## External trigger patterns

### Pattern 1 — CI failure webhook

A CI system sends a webhook when a build fails. A lightweight listener calls `POST /studio/goals`
with the failure context as the goal text, then calls `POST /studio/agents/spawn`.

```
CI build fails
  → webhook fires to listener
  → POST /studio/goals  { goal: "Investigate build failure: ...", reviewPolicy: "AgentApproval" }
  → POST /studio/agents/spawn  { workUnitId: ..., provider: "anthropic", ... }
  → agent investigates, proposes fix, reviewer agent auto-approves
  → AllowAgentGitPush=true: branch pushed, PR opened externally
```

No headless peer is needed if the Studio host is already running (e.g., via the VS Code extension).

### Pattern 2 — Monitoring alert handler (persistent peer)

A headless peer (`PeerType: "persistent-agent"`) subscribes to a metrics/alerting bus. On alert
receipt, it injects a goal in-process via `IWorkUnitCommandService` and starts an orchestrator via
`IAgentControlService.SpawnAsync` — no HTTP round-trip needed. Because the peer is connected to
the room (`Peer:HostUri` set), the resulting work unit and agents appear in the extension in real
time.

### Pattern 3 — Scheduled maintenance (cron → ephemeral peer)

A cron job launches the peer binary in `--mode peer`. An initialization hook creates a work unit
and spawns an orchestrator at startup. With `AllowAgentGitCommits=true` and `AllowAgentGitPush=true`,
the agent commits and pushes its work. The peer exits when the orchestrator finishes. Set
`AllowAutoRequeue=false` so a failure surfaces as an exit code, not an infinite retry loop.

### Pattern 4 — Human via VS Code (reference)

The Activity Center "New Goal" button calls `POST /studio/workunits` with `reviewPolicy` from the
Review row in the Goal Workspace. Goals created this way are identical in structure to
programmatically created ones. The difference is only the `owner` field and the fact that the
human can set the exploration strategy (multi-fork experiment, etc.) from the extension UI before
starting.

---

## Using `nm_v1_goal_create` (external MCP clients)

`nm_v1_goal_create` is the MCP tool equivalent of `POST /studio/goals`. It is **not dispatched to
in-process agents** — orchestrators and workers cannot create goals for themselves. It is available
to external MCP clients (e.g., Claude Desktop, a custom MCP proxy) and headless peers that are
configured to use the MCP transport.

The tool does not expose `reviewPolicy` — goals created via MCP always default to `HumanRequired`.
Use `POST /studio/workunits` via REST when you need a non-default review policy.

---

## Related

- [headless-peer.md](headless-peer.md) — Running a peer process for CI/CD and background work
- [domain-observers.md](domain-observers.md) — Enabling reactive constraint observers on spawned agents
- [repository-virtualization.md](repository-virtualization.md) — How each branch gets its own working directory
- [docs/reference/api-reference.md](../reference/api-reference.md) — Full REST and MCP tool catalog
