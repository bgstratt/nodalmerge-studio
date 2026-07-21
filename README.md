# NodalMerge Studio — VS Code Extension

**Branch ideas. Review reasoning. Materialize software.**

NodalMerge Studio is a persistent collaborative runtime for human and AI development work, built on [NodalMerge](https://github.com/nodalmerge/nodalmerge). It provides the execution environment, decision history, artifact lineage, and coordination layer for a shared, evolving workspace — designed so that any agent harness, tool, or human collaborator can contribute, with every contribution reviewed, promoted, and replayable.

Every step an agent takes produces a durable artifact in a DAG. Every artifact can be inspected, branched, replayed, merged, and audited. The durable graph is the product; agents are features of it. The harness is just one participant.

---

## How It Works

### The Artifact Pipeline

```
Goal
  ↓
Plan               ← inspectable, reviewable, branchable
  ↓
Work Units         ← inspectable, has lineage, has scope
  ↓
Branch Changes     ← auditable, attributable, comparable
  ↓
Merge Proposal     ← reviewable, approvable, supersedable, replayable
  ↓
Approved State     ← committed, traceable back to the goal
```

1. **You define a goal.** A human describes what needs to be accomplished.
2. **An orchestrator agent plans it.** The orchestrator breaks the goal into work units and tasks.
3. **Worker agents execute tasks.** Each worker operates on its own branch, producing changes.
4. **Merge proposals are generated.** Workers submit structured proposals with full lineage.
5. **A proposal is reviewed and approved.** By default a human approves it; a goal can opt into `Agent Approval` or `Hybrid` review policy so a reviewer agent (and a timeout) handles it instead — see [Trust, Autonomy & Exploration](#trust-autonomy--exploration) below.
6. **State converges.** Approved changes land in the authoritative branch.

### Key Concepts

| Concept | Description |
|---|---|
| **Work Unit** | The primary execution abstraction: `Goal + Branch`. Every agent session is scoped to exactly one work unit. |
| **Projection** | Agent-consumable derived views of the DAG. Agents never consume raw history — they request projections at configurable compression levels (Full / Normal / Compact / Emergency). |
| **Branch** | Isolated workspace for speculative work. Changes are made on branches, not directly on authoritative state. |
| **Merge Proposal** | Structured intent to reconcile branch changes into the authoritative branch. Includes diff, artifact lineage, execution event, and verification results. |
| **Known Good State** | A verified branch checkpoint used for rollback and recovery. |
| **Room** | Sync boundary for multi-peer NodalMerge replication. |
| **Review Policy** | Per-work-unit setting (`Human Required` / `Agent Approval` / `Hybrid`) controlling who approves a proposal before it applies. |
| **Candidate Branch** | Optional safety layer: when promotion branches are enabled, applies land here instead of `main` until a human explicitly promotes. |
| **Experiment** | A parent work unit fanned out into 2+ sibling forks (different model, architecture, library, or product framing) that run in parallel for comparison. |
| **Steering** | Pausing a running work unit and injecting a constraint, which forks a sibling that resumes with it — the original's decision log is never rewritten. |
| **Counterfactual** | A sibling work unit branched from a completed proposal's base state and re-run under a different model/profile for comparison. |

### Agent Execution Loop

```
Observe → Read projections
   ↓
Think   → Determine next action
   ↓
Act     → Perform workspace operation
   ↓
Verify  → Validate outcome
   ↓
Propose → Submit merge proposal
```

The loop ends at proposal submission. Merge authority remains external — agents propose, by default humans approve.

---

## Trust, Autonomy & Exploration

Beyond the core pipeline above, NodalMerge Studio lets you configure *how much* human attention each goal actually needs, *where* speculative work is allowed to land, and *how many* alternatives get explored before you commit to one. These are independent, composable settings — each goal can use a different combination.

### Review Policy — who approves a proposal

Set per goal in the Goal Workspace (or as a session default in Model & Agent Studio):

| Policy | Behavior |
|---|---|
| `Human Required` (default) | Every proposal waits at the merge-review gate for manual Accept/Reject. No behavior change from v1. |
| `Agent Approval` | A reviewer agent evaluates the proposal (build/test evidence, goal satisfaction) and auto-applies on approval, or rejects with notes for a human to see. |
| `Hybrid` | The reviewer agent approves immediately, then a countdown (default 5 minutes) starts. A human can override (reject) during the window; otherwise it auto-applies at expiry. |

### Optional execution gates — verify before proposing

Independent of review policy, a work unit's branch can be required to **build** and/or **test** clean before it's even allowed to submit a proposal (toggled in Exploration Settings). Failing evidence is attached to the proposal either way, so reviewers (human or agent) always see it.

### Promotion Branches — a safety layer above "main"

When enabled (session-wide, with a per-goal override), proposals never apply directly to `main`. They land on a shared `candidate` branch instead:

```
Agent Work Branches  →  Candidate Branch  →  Main
 (per work unit,         (auto-applies         (explicit human
  fully sandboxed)        land here)            "Promote to Main")
```

A goal can opt out of the candidate layer with the **Direct** target override, bypassing promotion even when it's on session-wide.

### Experiments — explore several approaches in parallel

A goal can fan out into 2+ sibling work units that run concurrently and converge into a side-by-side comparison:

| Strategy | What differs between forks |
|---|---|
| **Multi-Model Comparison** | Same goal, different LLM/profile per fork |
| **Architecture Fork** | Same goal, a different structural constraint injected per fork (e.g. "use CQRS" vs. "use a simple service layer") |
| **Library Comparison** | Same goal, a different dependency constraint per fork |
| **Product Strategy Fork** | Same goal, a different product-framing constraint per fork |

Each fork runs to its own proposal; the Decision Tree shows a fork-count badge and a **Compare Results** view. **Pick Winner** accepts the chosen fork's proposal and rejects the others — all recorded in the decision log.

### Steering — redirect a running agent without losing its history

Instead of stopping and re-prompting an agent from scratch, you can **pause** a running work unit, inject a constraint or correction ("use Redis instead of SQLite"), and the system forks a sibling work unit that resumes with that constraint in its plan context. The original work unit's decision log is untouched — steering never rewrites history, it branches from it. You can also fork from any specific node in Trajectory Replay, not just the live edge.

### Counterfactual Replay — "what would a different model do here?"

From any completed work unit, **Run with different model** branches from that proposal's base state and re-runs the same goal under a different profile. The result is a new sibling work unit; selecting it shows a **Compare with Original** view (proposals, confidence, file coverage side by side) without disturbing the original.

### Putting it together

A typical autonomous run: you describe a goal, pick `Agent Approval` (or `Hybrid`) so it doesn't need you at the merge gate, turn on the candidate branch so nothing touches `main` directly, optionally require build+test evidence before any proposal is even accepted, and — if you're unsure which approach is best — launch it as a Multi-Model or Architecture experiment instead of a single run. You can walk away; when you come back, either a completed merge is waiting on `candidate` for you to promote, or a decision (a rejected proposal, a paused agent awaiting your steering input, or a set of forks awaiting **Pick Winner**) is waiting in the Decision Tree.

See [UI reference](https://docs.nodalmerge.com/studio/reference/ui-reference) for every control these features expose in the extension, and [API reference](https://docs.nodalmerge.com/studio/reference/api-reference) for the full MCP/REST surface behind them.

---

## Benefits

### Durable Artifact Graph

Every plan, task, proposal, and decision is a persistent DAG node. Nothing is ephemeral. You can trace any approved change back through its entire lineage to the original goal.

### Inspectability

Answer questions that ephemeral agent frameworks cannot:
- What did the agent produce? Why? From what input state?
- Which model produced this proposal? Under what constraints?
- What was the full artifact lineage for this change?

### Branching and Replay

- **Branch** from any artifact checkpoint to spawn alternate executions.
- **Replay** a proposal with a different model or agent profile to compare outcomes.
- **Roll back** to any Known Good State and recover from bad applies.

### Human-Governed

Agents propose; humans decide. The merge review workflow enforces human approval before any speculative work lands in the authoritative branch.

### Token Efficiency Through State Compression

Agents receive compact projections — not full DAG history. Projections compress relevant state (active work units, tasks, proposals, artifact chain) into a token-efficient view. Knowledge artifacts (constraints, conventions, decisions) persist across runs so agents don't rediscover the same information on every execution.

### MCP-Native

Full MCP v1 tool surface with 117 frozen tool names across 21 namespaces under `nm_v1_*`, plus a 14-tool `nms_v1_*` external-caller surface. Integrate any MCP-compatible client with the Studio host. See [API reference](https://docs.nodalmerge.com/studio/reference/api-reference) for the verified catalog.

---

## Limitations (v1 Scope)

The following are explicitly deferred from v1:

- Autonomous self-directed agents (agents cannot set their own goals)
- Long-term memory databases or vector stores
- "Dreaming" / distillation pipelines
- Cross-workspace reasoning
- Autonomous goal generation
- Enterprise RBAC systems
- Multi-tenant SaaS architecture
- Per-work-unit cross-repository execution (a single `SeedRepositoryPath` provides the source repository; agents cannot switch repositories mid-session)

**No longer fully deferred (Phase 7):** Agent-controlled merges without a human in the loop are now possible, but only when a goal explicitly opts into `Agent Approval` or `Hybrid` review policy — `Human Required` remains the default and unchanged behavior. There is still no agent-to-agent approval chain beyond the single configured reviewer agent.

**Architectural invariant:** All persistent state lives in NodalMerge nodes. No separate memory DB. No vector DB. No agent-specific memory stores.

---

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download)
- [Node.js 22+](https://nodejs.org/) (for VS Code extension)
- A sibling checkout of the [`nodalmerge`](https://github.com/nodalmerge/nodalmerge) repo (for embedded host and local NuGet packages)
- [VS Code 1.90+](https://code.visualstudio.com/)

---

## First-Time Setup

### 1. Restore NodalMerge dependencies

Pack local NodalMerge NuGet artifacts from the sibling repo:

```powershell
pwsh -File .\scripts\restore-local-nodalmerge.ps1
```

### 2. Restore and build

```powershell
dotnet restore NodalMerge.Studio.slnx --configfile NuGet.config
dotnet build NodalMerge.Studio.slnx
```

### 3. Build the VS Code extension

```powershell
cd clients\vscode-extension
npm install
npm run compile
```

---

## Running Locally

### Studio Host (backend)

```powershell
pwsh -File .\scripts\dev.ps1
```

Endpoints:

| Endpoint | Purpose |
|---|---|
| `GET http://127.0.0.1:5080/health` | Process health |
| `GET http://127.0.0.1:5080/studio/health` | Studio service health |
| `POST http://127.0.0.1:5080/studio/merges` | REST merge proposal (rich response) |

NodalMerge runtime routes from the embedded host (`/ws/runtime`, `/sync/token`, etc.).

### VS Code Extension

Press `F5` from the `clients/vscode-extension` directory to launch the extension in a VS Code Extension Development Host.

### Verify

```powershell
pwsh -File .\scripts\verify.ps1
```

Unit tests run by default. Integration tests require native NodalMerge packages and a built sibling `nodalmerge` repo.

---

## VS Code Extension Usage

NodalMerge Studio serves as the **Control Tower** for human operators. The extension provides:

### Commands

| Command | Description |
|---|---|
| `NodalMerge: Open Studio` | Open the Studio shell (tabbed: Goal Workspace, Activity Center, Model & Agent Studio, Decision Convergence, Pathways) |
| `NodalMerge: Open Review` | Open Decision Convergence for a specific merge proposal |
| `NodalMerge: Open Decision Conflict` | Open Decision Convergence in conflict-resolution mode |
| `NodalMerge: Restart Studio Host` | Restart the embedded Studio host |
| `NodalMerge: Show Output` | Show Studio output channel |

### Control Tower Capabilities

- Create goals with a chosen review policy, target branch, and exploration strategy (single agent, multi-agent fanout, or multi-fork experiment)
- View active work units, agents, and their status; spawn, pause, resume, and stop agents
- Inspect projections, decision context, and reasoning chains at any compression level
- Review and approve/reject/apply merge proposals, including converged multi-candidate decisions
- Steer a running agent (pause + inject constraint) or fork from any point in its history
- Compare experiment forks or counterfactual re-runs side by side and pick a winner
- Browse the artifact DAG and scrub the replay timeline; branch from any checkpoint
- Mark a checkpoint as Known Good, restore a branch to one, or fork a new exploration from one
- View workspace summary (active work units, pending merges, failures, dead-letter queue)

See [UI reference](https://docs.nodalmerge.com/studio/reference/ui-reference) for the complete control-by-control inventory of every panel.

---

## Configuration

### VS Code Settings

| Setting | Type | Default | Description |
|---|---|---|---|
| `nodalmerge.hostPort` | number | `5080` | Port the Studio Host listens on |
| `nodalmerge.agentProfiles` | array | `[]` | Named agent profiles (see below) |
| `nodalmerge.topologyTemplates` | array | `[]` | Orchestrator + worker role assignments |
| `nodalmerge.defaultTopology` | string | `""` | Default topology for new workspaces |

### Agent Profiles

Define named profiles that map agent types to LLM providers and models:

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
      "baseUrl": "https://api.anthropic.com",
      "apiKeyRef": "anthropic-key",
      "systemPromptHint": "You are an orchestrator agent. Plan work, assign tasks, review results."
    },
    {
      "id": "code-worker",
      "label": "Code Worker (GPT-4o)",
      "domain": "code",
      "provider": "openai",
      "model": "gpt-4o",
      "apiKeyRef": "openai-key"
    }
  ]
}
```

Provider options:
- `anthropic` — Anthropic API (Claude models)
- `openai` — OpenAI API (GPT models)
- `vscode-lm` — VS Code Language Model API (built-in, no API key required)

When using `vscode-lm`, the `baseUrl` and `apiKeyRef` fields are ignored. Store API keys via VS Code's `SecretStorage` (set with **Ctrl+Shift+P → "Preferences: Configure Runtime Arguments"** or via the extension).

### Topology Templates

Define reusable orchestrator + worker team compositions:

```jsonc
{
  "nodalmerge.topologyTemplates": [
    {
      "name": "code-review-pair",
      "orchestrator": "orchestrator",
      "workers": [
        { "profile": "code-worker", "branch": "feature/impl" },
        { "profile": "review-worker", "branch": "feature/review" }
      ]
    }
  ],
  "nodalmerge.defaultTopology": "code-review-pair"
}
```

### Environment Variables

| Variable | Description |
|---|---|
| `STUDIO_ROOM_ID` | Default NodalMerge room for Studio operations |
| `NODALMERGE_PACKAGE_VERSION` | Override NodalMerge NuGet package version (default: `0.1.0-local`) |
| `NODALMERGE_NATIVE_AVAILABLE` | Set to `true` to run integration tests in CI |

---

## Headless Peer

A headless peer runs all Studio agent services (orchestrator, workers, projections, storage) without
an HTTP server. It is the primary integration point for CI/CD pipelines, autonomous background
workers, and programmatic goal injection from external systems (monitoring alerts, scheduled tasks,
webhook triggers). Activate with `--mode peer`, `STUDIO_MODE=peer`, or `Peer:Enabled=true` in
config. In connected mode (set `Peer:HostUri`), the peer joins a NodalMerge room and its agents
appear in the extension's Activity Center in real time.

See [headless peer guide](https://docs.nodalmerge.com/studio/guides/headless-peer) for configuration, use-case
patterns, and the goal injection API. See [extending goals guide](https://docs.nodalmerge.com/studio/guides/extending-goals)
for the full goal creation surface and external trigger patterns.

---

## Repository Virtualization

Each work unit branch gets a physically isolated working directory under `Workspace:RootPath`.
Branch directories are seeded from `Workspace:SeedRepositoryPath` (via CAS snapshot or directory
copy) so agents start with a clean, full-fidelity workspace. Agents operating in parallel never
share a working directory or compete for file locks — the CAS blob store is the shared read layer;
per-branch directories are the write-isolated layer. The `nm_v1_workspace_path` MCP tool returns
the effective filesystem path for any branch.

See [repository virtualization guide](https://docs.nodalmerge.com/studio/guides/repository-virtualization) for
seeding strategies, scoped materialization (Phase 11), and the full `Workspace` config reference.

---

## MCP Integration

NodalMerge Studio exposes an MCP v1 tool surface with **117 `nm_v1_*` tools** (canonical constants in `McpToolNames`), **72 of them** dispatched in-process to autonomous agents; the rest are available to external MCP clients and the VS Code extension only. A separate, smaller **`nms_v1_*` surface (14 tools, `McpServerToolNames`)** is the recommended entry point for external MCP clients (Claude Code, Cursor, scripts) — goal-centric, without requiring knowledge of work units or branches. See [API reference](https://docs.nodalmerge.com/studio/reference/api-reference) for the full, verified catalog and coverage breakdown.

Core namespaces: `nm_v1_projection_*`, `nm_v1_workunit_*`, `nm_v1_task_*`, `nm_v1_branch_*`, `nm_v1_merge_*`, `nm_v1_workspace_*` (file I/O, build/test/run, semantic navigation, profile), `nm_v1_scheduler_*`, `nm_v1_artifact_*`. Phase 6.7+ adds `nm_v1_goal_*`, `nm_v1_decision_*`, `nm_v1_evidence_*`, `nm_v1_trajectory_*`, `nm_v1_hypothesis_*`, `nm_v1_reasoning_*`, `nm_v1_model_*` (none dispatched to agents yet). Phase 7 capabilities (Experiments, Steering, Counterfactuals, Review Policy, Promotion Branches) are REST-only.

Full documentation:
- [API reference](https://docs.nodalmerge.com/studio/reference/api-reference) — complete 117-tool catalog with dispatch status, REST endpoint parity, and extension coverage analysis
- [docs/contracts/mcp-v1-contract.md](docs/contracts/mcp-v1-contract.md) — frozen design principles, request/response schemas, error envelope format, and Phase 6.7+/Phase 7 addendum

---

## Architecture

### Three-Layer Design

```
Layer 3: UX + Peers               — VS Code Extension (Control Tower)
         (TypeScript / .NET)        Web Dashboard
                                    Headless Peer (no HTTP, optional room presence)
              ↕ REST / in-process call / WebSocket room
Layer 2: Studio Services (.NET)   — AgentRuntime, Orchestrator, Projections,
                                    MCP Server, Tasks, Merge, Storage
              ↕ NodalMerge APIs
Layer 1: NodalMerge Core (Rust)   — DAG storage, CRDT convergence, replication,
                                    replay, branching, promotion, sync
```

The VS Code extension and headless peers both sit at Layer 3 — they use Studio services (Layer 2),
not NodalMerge APIs directly. The extension exposes an HTTP server and MCP-over-HTTP; a headless
peer does not. See [headless peer guide](https://docs.nodalmerge.com/studio/guides/headless-peer).

### Key Architectural Principles

1. **NodalMerge is the source of truth.** All persistent state resides in NodalMerge nodes.
2. **Agents reason over projections.** Agents never consume raw DAG history.
3. **Work unit-centric execution.** Every agent session is scoped to exactly one work unit.
4. **Human-governed promotion by default.** Agents propose; a human approves merges unless a goal explicitly opts into agent-approved review, and even then a promotion branch can keep automated applies off `main`.
5. **Immutable history.** DAG nodes are append-only; updates create new nodes.

See [docs/architecture/v1-architecture-spec.md](docs/architecture/v1-architecture-spec.md) for the full specification.

---

## Repository Layout

| Path | Purpose |
|---|---|
| `src/NodalMerge.Studio.Contracts` | Frozen domain, projection, and MCP DTOs + `McpToolNames` |
| `src/NodalMerge.Studio.Core` | Service interfaces |
| `src/NodalMerge.Studio.Storage` | NodalMerge node persistence adapters |
| `src/NodalMerge.Studio.Projections` | Projection Manager |
| `src/NodalMerge.Studio.Tasks` | Task services |
| `src/NodalMerge.Studio.Merge` | Merge proposal and review workflow |
| `src/NodalMerge.Studio.AgentRuntime` | Agent execution loop |
| `src/NodalMerge.Studio.Orchestrator` | Work unit and orchestration |
| `src/NodalMerge.Studio.McpServer` | MCP v1 tool surface |
| `src/NodalMerge.Studio.Host` | ASP.NET composition root; `HeadlessPeerOptions.cs` + `RoomPeerClient.cs` for peer mode |
| `clients/vscode-extension` | VS Code extension (Control Tower) |
| `clients/web-dashboard` | Web dashboard (placeholder) |
| `tests/` | Unit and integration tests |
| `docs/architecture/` | Architecture spec, CRDT vs. cognition layer, node schemas, ADRs |
| `docs/contracts/` | MCP v1 contract (frozen + Phase 6.7+ addendum), projection contract |
| `docs/guides/` | Dev/smoke guides (e.g. the multi-user smoke test). User guides & references live at [docs.nodalmerge.com/studio](https://docs.nodalmerge.com/studio) |
| `plans/` | Slice-based execution plans |
| `scripts/` | Build, dev, and verify scripts |

---

## Development

### Build and Test

```powershell
pwsh -File .\scripts\verify.ps1     # full build + unit tests
pwsh -File .\scripts\dev.ps1        # run Studio Host

# VS Code extension
cd clients\vscode-extension
npm run compile                      # build
npm run typecheck                    # type-check only
npm run package                      # production build + .vsix
```

### Conventions

- Target framework: `net10.0`
- Package prefix: `NodalMerge.Studio.*`
- MCP contract version: `v1` with frozen tool names in `McpToolNames`
- Node schema paths: `studio/{entity}/v1`
- One capability per PR with verification checklist in `plans/`

---

## Related Repositories

- [nodalmerge](https://github.com/nodalmerge/nodalmerge) — Core engine, host runtime, DAG storage, CRDT replication
- [nodalmerge/docs](https://github.com/nodalmerge/docs) — Platform documentation

---

## License

[MIT](LICENSE)