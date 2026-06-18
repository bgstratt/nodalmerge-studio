# NodalMerge Studio — VS Code Extension

**Git for agent reasoning and execution** — not Git for files.

NodalMerge Studio is an agent-native collaborative workspace platform built on [NodalMerge](https://github.com/nodalmerge/nodalmerge). It provides agent orchestration, task management, projection-based context, MCP integration, human review workflows, and a VS Code Control Tower — all backed by a persistent, branchable, replayable artifact graph.

Every step an agent takes produces a durable artifact in a DAG. Every artifact can be inspected, branched, replayed, merged, and audited. The durable graph is the product; agents are features of it.

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
5. **You review and approve.** Human approval is mandatory for all merges in v1.
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

The loop ends at proposal submission. Merge authority remains external — agents propose, humans approve.

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

Full MCP v1 tool surface with 30+ frozen tool names under `nm.v1.*` namespaces. Integrate any MCP-compatible client with the Studio host.

---

## Limitations (v1 Scope)

The following are explicitly deferred from v1:

- Autonomous self-directed agents (agents cannot set their own goals)
- Agent-to-agent approval chains
- Agent-controlled merges without human review
- Long-term memory databases or vector stores
- "Dreaming" / distillation pipelines
- Cross-workspace reasoning
- Autonomous goal generation
- Enterprise RBAC systems
- Multi-tenant SaaS architecture

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
dotnet restore NodalMerge.Studio.slnx --configfile NuGet.config -p:NodalMergePackageVersion=0.1.0-local
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
| `NodalMerge: Open Studio` | Open the main workspace dashboard |
| `NodalMerge: Open Merge Review` | Review and approve/reject merge proposals |
| `NodalMerge: Open Merge Conflict` | Resolve merge conflicts |
| `NodalMerge: Restart Studio Host` | Restart the embedded Studio host |
| `NodalMerge: Show Output` | Show Studio output channel |

### Control Tower Capabilities

- View active work units, agents, and their status
- Spawn, pause, resume, and stop agents
- Inspect projections at any compression level
- Review and approve/reject merge proposals
- Browse the artifact DAG and replay timeline
- Rollback to Known Good State
- View workspace summary (active work units, pending merges, failures)

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

## MCP Integration

NodalMerge Studio exposes a frozen MCP v1 tool surface with 30+ tools. All tools use the `nm.v1.*` namespace (unchanged since plan slice 1; canonical constants in `McpToolNames`).

### Tool Namespaces

| Namespace | Purpose | Key Tools |
|---|---|---|
| `nm.v1.projection.*` | Context generation for agents | `get`, `list` |
| `nm.v1.workunit.*` | Work unit lifecycle | `create`, `get`, `update`, `list` |
| `nm.v1.task.*` | Task management (intent only) | `create`, `update`, `list`, `assign` |
| `nm.v1.branch.*` | Branch management | `create`, `checkout`, `list`, `status` |
| `nm.v1.merge.*` | Merge proposal workflow | `propose`, `validate`, `review`, `apply` |
| `nm.v1.replay.*` | History inspection | `range`, `rollback`, `inspect` |
| `nm.v1.state.*` | Known good state | `markKnownGood`, `findKnownGood`, `checkoutKnownGood` |
| `nm.v1.snapshot.*` | Execution snapshots | `get`, `compare` |
| `nm.v1.agent.*` | Agent lifecycle | `spawn`, `pause`, `resume`, `status`, `stop` |
| `nm.v1.workspace.*` | Control tower summary | `summary` |

See [docs/contracts/mcp-v1-contract.md](docs/contracts/mcp-v1-contract.md) for the complete catalog, request/response schemas, and error envelope format.

---

## Architecture

### Three-Layer Design

```
Layer 3: UX (TypeScript)          — VS Code Extension, Web Dashboard
        Presentation only. No business rules. No authoritative state.
            ↕ MCP + REST
Layer 2: Studio Services (.NET)   — AgentRuntime, Orchestrator, Projections,
        MCP Server, Tasks, Merge, Storage
            ↕ NodalMerge APIs
Layer 1: NodalMerge Core (Rust)   — DAG storage, CRDT convergence, replication,
        replay, branching, promotion, sync
```

### Key Architectural Principles

1. **NodalMerge is the source of truth.** All persistent state resides in NodalMerge nodes.
2. **Agents reason over projections.** Agents never consume raw DAG history.
3. **Work unit-centric execution.** Every agent session is scoped to exactly one work unit.
4. **Human-governed promotion.** Agents propose; humans approve merges.
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
| `src/NodalMerge.Studio.Host` | ASP.NET composition root |
| `clients/vscode-extension` | VS Code extension (Control Tower) |
| `clients/web-dashboard` | Web dashboard (placeholder) |
| `tests/` | Unit and integration tests |
| `docs/` | Architecture spec, MCP v1 contract, ADRs |
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