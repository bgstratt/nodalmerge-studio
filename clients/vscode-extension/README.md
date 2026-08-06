# NodalMerge Studio

**A collaborative AI development runtime for VS Code.**

NodalMerge Studio is where humans, AI agents, headless peers, MCP clients, and automated development workflows converge on a shared workspace. Every goal, decision, conversation, code change, review, and execution becomes part of a persistent graph that can be inspected, replayed, branched, and evolved over time.

Powered by the NodalMerge runtime, Studio enables collaborative software development that spans interactive editing, autonomous agents, background workers, and external tooling—all working against the same durable workspace.

---

## Features

### Goal Workspace
Create and manage goals across your team of agents. Set the review policy, target branch, and exploration strategy — single agent, multi-agent fanout, or a parallel experiment across models or approaches. Goals that span several subsystems can plan **recursively**: raise the plan depth (globally or per goal) and a planner can mark a slice *compound* so a sub-planner re-slices it, with the pieces reconciling bottom-up. Tune how long the unattended loop retries before escalating with **Max auto-retries**. Monitor active sessions and inject clarifications without stopping running agents.

### Activity Center
Live view of all running work units and agents: status, current task, file leases, and the dead-letter queue. Spawn, pause, resume, or stop agents directly from the panel. Links to live transcripts and deep-link to Decision Convergence when a proposal is waiting. File leases are scoped per goal (an unrelated goal touching the same path never blocks) and a wait-for cycle between two work units is detected and resolved automatically instead of deadlocking. Retrying, continuing, or re-planning a dead-lettered work unit can resupply credentials on the spot — useful after a Host restart clears the in-memory credential registry.

### Model & Agent Studio
Configure named agent profiles — provider, model, system prompt, tool allowlist, and deployment mode (inline or headless peer). Build topology templates that define orchestrator + worker compositions you can reuse across goals.

Five providers, in two shapes. **HTTP API** — `anthropic`, `openai`, and `vscode-lm` (VS Code's built-in Language Model API, no API key required). **Local CLI harness** — `claude-cli` and `codex-cli` route the role to a harness executor that spawns your local `claude` / `codex` binary in the work unit's branch directory, rather than calling an API. CLI providers take no `baseUrl`, and `apiKeyRef` is optional: leave it blank to use the machine's ambient CLI login, or store a key to have it injected as `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` for headless use. A blank `model` means the CLI's own default.

Every provider enters through the same review gate, which is what makes them interchangeable.

### Decision Convergence (Review)
Human merge-review gate. Inspect a proposal's diff, artifact lineage, execution timeline, build/test evidence, and confidence score. Accept, reject, or request changes. For multi-fork experiments, compare all candidates side by side and pick a winner — the rest are auto-rejected and recorded.

### Pathways (Trajectory Replay)
Scrub the full artifact timeline for any work unit. Inspect projections at any point in execution, branch from any checkpoint, steer a completed run with a new constraint, or launch a counterfactual re-run under a different model for comparison.

### Insights
Cross-session observability: goal completion rates, proposal acceptance trends, model performance comparison, and token usage by profile. Filter by time range or agent profile.

### Projection Snapshots
Side-by-side diff of two projection snapshots — useful for auditing what an agent saw at different compression levels or comparing base state before and after a merge.

---

## Requirements

- **VS Code** 1.90 or later
- **NodalMerge Studio Host** — the extension always spawns/adopts its own local host process on
  startup; it never talks to a remote server directly (`nodalmerge.runtimeUri` only tunes the
  local bind address/port). To join a shared room hosted elsewhere, set `nodalmerge.room.hostUri`
  instead — the local host connects out to it.
- **.NET SDK 10.0** (for the local host process)

---

## Getting Started

1. Install the extension.
2. Open a repository in VS Code.
3. Run **NodalMerge: Open Studio** from the Command Palette (`Ctrl+Shift+P`).
4. The Studio Host starts automatically on `http://127.0.0.1:5080`.
5. Add at least one agent profile in **Model & Agent Studio** and store your API key when prompted.
6. Create a goal in **Goal Workspace**.

---

## Commands

| Command | Description |
|---|---|
| `NodalMerge: Open Studio` | Open the Studio shell |
| `NodalMerge: Open Review` | Open Decision Convergence for a specific proposal |
| `NodalMerge: Open Decision Conflict` | Open Decision Convergence in conflict-resolution mode |
| `NodalMerge: Restart Studio Host` | Restart the embedded Studio host |
| `NodalMerge: Show Output` | Show the Studio output channel |
| `NodalMerge: Start Local Runtime` | Start the local NodalMerge runtime |

---

## Extension Settings

**Runtime & connection**

| Setting | Default | Description |
|---|---|---|
| `nodalmerge.runtimeUri` | `""` | Base URI the extension's own locally-managed runtime binds to. Empty = `127.0.0.1:5080`. Always local — see `nodalmerge.room.hostUri` to join a shared room. |
| `nodalmerge.hostPort` | `5080` | Port the Studio Host listens on. |
| `nodalmerge.room.hostUri` | `""` | WebSocket URI of a room server the local runtime should connect to (e.g. `wss://team.example.com`). Empty = standalone, no room connection attempted. |
| `nodalmerge.room.workgroup` | `""` | Names the workgroup room this workspace's repository/goal state joins. Only meaningful once `nodalmerge.room.hostUri` is set. |

**Storage**

| Setting | Default | Description |
|---|---|---|
| `nodalmerge.workspaceDataPath` | `""` | Where Studio stores branch files and the node-store database. Empty = VS Code's per-workspace extension storage (outside your repo). Set a relative path (e.g. `.nodalmerge`) to store inside the repo. |
| `nodalmerge.repositoryPath` | `""` | Absolute path to the repository agents operate against. Empty = first folder open in the window. |
| `nodalmerge.blobOrigin.uri` | `""` | Base URL of a blob origin server the local runtime chains in front of, e.g. `https://blobs.example.com`. Empty (default) is local-only — the runtime's blob store never reaches out to the network. |
| `nodalmerge.blobOrigin.token` | `""` | Optional bearer token sent with every request to `nodalmerge.blobOrigin.uri`, when that server requires auth. |
| `nodalmerge.blobOrigin.s3Direct.enabled` | `false` | Adds an s3-direct chain link (peer ↔ bucket, resolved via `nodalmerge.blobOrigin.uri`'s presign endpoints) on top of the server-relay origin. Only meaningful when `nodalmerge.blobOrigin.uri` is also set. |

**Agents & topology**

| Setting | Default | Description |
|---|---|---|
| `nodalmerge.agentProfiles` | `[]` | Named agent profiles. Each maps an agent type to a provider, model, system prompt, and tool allowlist. |
| `nodalmerge.topologyTemplates` | `[]` | Reusable orchestrator + worker compositions. |
| `nodalmerge.defaultTopology` | `""` | Topology template applied when spawning a new workspace. |

**Review & gates**

| Setting | Default | Description |
|---|---|---|
| `nodalmerge.defaultTaskReviewPolicy` | `HumanRequired` | Whether worker proposals are automatically integrated into the agent session. Seeds the Task Review setting for new goals. One of `HumanRequired`, `AgentApproval`, `Hybrid`. |
| `nodalmerge.defaultWorkspaceReviewPolicy` | `HumanRequired` | Whether session changes are automatically applied to your workspace. Seeds the Workspace Review setting for new goals. One of `HumanRequired`, `AgentApproval`, `Hybrid`. |
| `nodalmerge.requireBuildBeforeProposal` | `false` | Require a passing build before a proposal can be submitted. |
| `nodalmerge.requireTestBeforeProposal` | `false` | Require passing tests before a proposal can be submitted. |
| `nodalmerge.buildCommand` | `""` | Global build command. Empty = auto-detect per branch. |
| `nodalmerge.testCommand` | `""` | Global test command. Empty = auto-detect per branch. |
| `nodalmerge.postMergeExecutionMode` | `Disabled` | Post-merge execution: `Disabled`, `Async` (background fire-and-forget), or `Blocking` (synchronous with rollback on failure). |

### Agent Profile example

```jsonc
// settings.json
{
  "nodalmerge.agentProfiles": [
    {
      "id": "orchestrator",
      "label": "Orchestrator (Claude)",
      "domain": "orchestration",
      "provider": "anthropic",
      "model": "claude-sonnet-4-6",
      "apiKeyRef": "anthropic-key"
    },
    {
      "id": "code-worker",
      "label": "Code Worker (GPT-4o)",
      "domain": "code",
      "provider": "openai",
      "model": "gpt-4o",
      "apiKeyRef": "openai-key"
    },
    {
      "id": "fast-worker",
      "label": "Fast Worker (built-in)",
      "domain": "code",
      "provider": "vscode-lm",
      "deploymentMode": "inline"
    },
    {
      "id": "claude-code-worker",
      "label": "Worker (local Claude Code CLI)",
      "domain": "code",
      "provider": "claude-cli"
    },
    {
      "id": "codex-worker",
      "label": "Worker (local Codex CLI)",
      "domain": "code",
      "provider": "codex-cli"
    }
  ]
}
```

The last two profiles have no `model`, `baseUrl`, or `apiKeyRef`. That's deliberate: they spawn the
`claude` / `codex` binary already installed and logged in on this machine, using the CLI's own
default model. If `claude` works in your terminal, that profile works.

---

## Headless Peers

Agents don't have to run inside VS Code. A **headless peer** runs the full Studio agent runtime as a standalone process — no IDE required. Headless peers join a NodalMerge room and appear in the Activity Center in real time alongside inline agents.

Useful for CI/CD pipelines, always-on background workers, and external triggers (webhooks, alerts, scheduled tasks).

See the [headless peer guide](https://docs.nodalmerge.com/studio/guides/headless-peer) for configuration and usage patterns.

---

## MCP Integration

The Studio host exposes two MCP tool surfaces over the same connection (`http://127.0.0.1:5080` by default):

**`nms_v1_*` — External caller surface (15 tools)**  
High-level tools designed for external MCP clients — Claude Code, Cursor, scripts, CI agents — to orchestrate the workspace without knowing its internals. These tools cover the full human-in-the-loop lifecycle: register a repo, start a goal, respond to clarifications, inspect results, apply proposals.

| Namespace | Tools |
|---|---|
| `nms_v1_goal_*` | `goal_run`, `goal_list`, `goal_status`, `goal_cancel`, `goal_requeue`, `goal_pause`, `goal_resume`, `goal_recover` |
| `nms_v1_clarification_*` | `clarification_respond` |
| `nms_v1_results_*` | `results_get`, `results_apply` |
| `nms_v1_repo_*` | `repo_register`, `repo_list` |
| `nms_v1_workspace_*` | `workspace_status`, `feedback_record` |

**`nm_v1_*` — Full internal surface (117 tools)**  
The complete tool catalog used by autonomous agents in-process. Available to external clients that need low-level workspace access (file I/O, branch ops, artifact records, execution). Frozen by the MCP v1 contract.

- [MCP v1 contract](https://github.com/bgstratt/nodalmerge-studio/blob/master/docs/contracts/mcp-v1-contract.md) — frozen tool names, schemas, and error envelope
- [API reference](https://docs.nodalmerge.com/studio/reference/api-reference) — complete tool catalog including external surface, dispatch status, and REST parity

---

## Documentation

- [UI reference](https://docs.nodalmerge.com/studio/reference/ui-reference) — every control in every panel
- [API reference](https://docs.nodalmerge.com/studio/reference/api-reference) — MCP tools and REST endpoints
- [Headless peer guide](https://docs.nodalmerge.com/studio/guides/headless-peer)
- [Repository virtualization](https://docs.nodalmerge.com/studio/guides/repository-virtualization)
- [Extending goals](https://docs.nodalmerge.com/studio/guides/extending-goals)
- [Source repository](https://github.com/bgstratt/nodalmerge-studio)

---

## License

[MIT](LICENSE)
