# NodalMerge Studio

**A collaborative AI development runtime for VS Code.**

NodalMerge Studio is where humans, AI agents, headless peers, MCP clients, and automated development workflows converge on a shared workspace. Every goal, decision, conversation, code change, review, and execution becomes part of a persistent graph that can be inspected, replayed, branched, and evolved over time.

Powered by the NodalMerge runtime, Studio enables collaborative software development that spans interactive editing, autonomous agents, background workers, and external tooling—all working against the same durable workspace.

---

## Features

### Goal Workspace
Create and manage goals across your team of agents. Set the review policy, target branch, and exploration strategy — single agent, multi-agent fanout, or a parallel experiment across models or approaches. Monitor active sessions and inject clarifications without stopping running agents.

### Activity Center
Live view of all running work units and agents: status, current task, file leases, and the dead-letter queue. Spawn, pause, resume, or stop agents directly from the panel. Links to live transcripts and deep-link to Decision Convergence when a proposal is waiting. File leases are scoped per goal (an unrelated goal touching the same path never blocks) and a wait-for cycle between two work units is detected and resolved automatically instead of deadlocking. Retrying, continuing, or re-planning a dead-lettered work unit can resupply credentials on the spot — useful after a Host restart clears the in-memory credential registry.

### Model & Agent Studio
Configure named agent profiles — provider (Anthropic, OpenAI, or VS Code built-in), model, system prompt, tool allowlist, and deployment mode (inline or headless peer). Build topology templates that define orchestrator + worker compositions you can reuse across goals.

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
- **NodalMerge Studio Host** running locally or on a reachable host  
  The extension auto-manages a local host process on startup. Point it at a remote host via `nodalmerge.runtimeUri`.
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

| Setting | Default | Description |
|---|---|---|
| `nodalmerge.runtimeUri` | `""` | Base URI of the Studio runtime. Empty = local host on `127.0.0.1:5080`. Set to a remote address to connect to a standalone or shared host. |
| `nodalmerge.workspaceDataPath` | `""` | Where Studio stores branch files and the node-store database. Empty = VS Code's per-workspace extension storage (outside your repo). Set a relative path (e.g. `.nodalmerge`) to store inside the repo. |
| `nodalmerge.repositoryPath` | `""` | Absolute path to the repository agents operate against. Empty = first folder open in the window. |
| `nodalmerge.agentProfiles` | `[]` | Named agent profiles. Each maps an agent type to a provider, model, system prompt, and tool allowlist. |
| `nodalmerge.topologyTemplates` | `[]` | Reusable orchestrator + worker compositions. |
| `nodalmerge.defaultTopology` | `""` | Topology template applied when spawning a new workspace. |
| `nodalmerge.defaultReviewPolicy` | `HumanRequired` | Default review policy for new goals: `HumanRequired`, `AgentApproval`, or `Hybrid`. |
| `nodalmerge.requireBuildBeforeProposal` | `false` | Require a passing build before a proposal can be submitted. |
| `nodalmerge.requireTestBeforeProposal` | `false` | Require passing tests before a proposal can be submitted. |
| `nodalmerge.buildCommand` | `""` | Global build command. Empty = auto-detect per branch. |
| `nodalmerge.testCommand` | `""` | Global test command. Empty = auto-detect per branch. |
| `nodalmerge.postMergeExecutionMode` | `Disabled` | Post-merge execution: `Disabled`, `Async` (background), or `Blocking` (synchronous with rollback on failure). |

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
    }
  ]
}
```

---

## Headless Peers

Agents don't have to run inside VS Code. A **headless peer** runs the full Studio agent runtime as a standalone process — no IDE required. Headless peers join a NodalMerge room and appear in the Activity Center in real time alongside inline agents.

Useful for CI/CD pipelines, always-on background workers, and external triggers (webhooks, alerts, scheduled tasks).

See the [headless peer guide](../../docs/guides/headless-peer.md) for configuration and usage patterns.

---

## MCP Integration

The Studio host exposes two MCP tool surfaces over the same connection (`http://127.0.0.1:5080` by default):

**`nms_v1_*` — External caller surface (14 tools)**  
High-level tools designed for external MCP clients — Claude Code, Cursor, scripts, CI agents — to orchestrate the workspace without knowing its internals. These tools cover the full human-in-the-loop lifecycle: register a repo, start a goal, respond to clarifications, inspect results, apply proposals.

| Namespace | Tools |
|---|---|
| `nms_v1_goal_*` | `goal_run`, `goal_list`, `goal_status`, `goal_cancel`, `goal_pause`, `goal_resume`, `goal_recover` |
| `nms_v1_clarification_*` | `clarification_respond` |
| `nms_v1_results_*` | `results_get`, `results_apply` |
| `nms_v1_repo_*` | `repo_register`, `repo_list` |
| `nms_v1_workspace_*` | `workspace_status`, `feedback_record` |

**`nm_v1_*` — Full internal surface (117 tools)**  
The complete tool catalog used by autonomous agents in-process. Available to external clients that need low-level workspace access (file I/O, branch ops, artifact records, execution). Frozen by the MCP v1 contract.

- [MCP v1 contract](../../docs/contracts/mcp-v1-contract.md) — frozen tool names, schemas, and error envelope
- [API reference](../../docs/reference/api-reference.md) — complete tool catalog including external surface, dispatch status, and REST parity

---

## Documentation

- [UI reference](../../docs/reference/ui-reference.md) — every control in every panel
- [API reference](../../docs/reference/api-reference.md) — MCP tools and REST endpoints
- [Headless peer guide](../../docs/guides/headless-peer.md)
- [Repository virtualization](../../docs/guides/repository-virtualization.md)
- [Extending goals](../../docs/guides/extending-goals.md)
- [Source repository](https://github.com/bgstratt/nodalmerge-studio)

---

## License

[MIT](LICENSE)
