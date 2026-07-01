# Domain Observers

Domain observers are six reactive agents built into the Studio runtime that watch the artifact
stream and propose constraints when they detect a gap relevant to their domain. They run
in-process, fire-and-forget, and produce only artifacts — they cannot create goals, spawn work
units, schedule tasks, or write files.

---

## The six observers

| Observer | Triggered by keywords (sample) |
|---|---|
| **Security** | auth, authn, authz, login, password, token, jwt, oauth, session, credential, secret, permission, role, acl, encrypt, csrf, xss, sql injection, cors, cookie |
| **Architecture** | architecture, framework, library, module boundary, service boundary, coupling, dependency graph, design pattern, microservice, monolith, scalability, migration |
| **Performance** | performance, latency, throughput, n+1, cache, caching, benchmark, memory leak, allocation, slow query, timeout, concurrency, lock contention, rate limit |
| **Test** | test coverage, unit test, integration test, flaky, test gap, regression, mock, test fixture, e2e, untested |
| **Documentation** | documentation, docs, readme, api doc, changelog, undocumented, doc gap |
| **UX** | ux, usability, accessibility, a11y, user flow, ui, design system, user experience, onboarding, error message, confusing |

---

## Enabling observers

Observers are **disabled by default** (empty list). Enable them by adding agent names to
`Workspace:EnabledDomainAgents` in `appsettings.json`:

```json
{
  "Workspace": {
    "EnabledDomainAgents": ["Security", "Architecture", "Test"]
  }
}
```

Valid names: `Security`, `Architecture`, `Performance`, `Test`, `Documentation`, `UX`.
The value is case-sensitive. Changes require a host restart.

### Per-work-unit override

When spawning an orchestrator via `POST /studio/agents/spawn`, pass `enabledDomainAgents` to
override the session default for that specific work unit's execution:

```json
{
  "workUnitId": "WU-abc123",
  "agentType": "orchestrator",
  "enabledDomainAgents": ["Security"]
}
```

This is captured at spawn time and does not change if the session default is later updated.

### No UI control today

There is no panel in the VS Code extension to toggle observers per goal. Configuration is
via `appsettings.json` only (global) or the `enabledDomainAgents` field on agent spawn (per
work unit). A UI toggle is a planned addition.

---

## How observers trigger

After any `Research`, `Decision`, or `Constraint` artifact is recorded against a work unit,
`DomainAgentTriggerService` runs the following pipeline for each enabled observer:

1. **Type filter** — only `Research`, `Decision`, `Constraint` artifacts are considered. Other
   artifact types (Plan, Task, MergeProposal, etc.) do not trigger observers.
2. **Loop prevention** — artifacts whose title starts with an observer's title prefix (e.g.,
   `[SecurityAgent] `) do not re-trigger that observer. This prevents an observer's own output
   from cascading back into itself or cross-triggering peers.
3. **Keyword heuristic** — the artifact's title and body are scanned for the observer's keywords.
   If no keyword matches, the observer is skipped for that artifact.
4. **Spawn** — a `DomainAgentLoop` is started fire-and-forget. Failures do not affect the
   artifact that triggered them.

---

## What an observer can do

Each observer receives exactly four MCP tools:

| Tool | Purpose |
|---|---|
| `nm_v1_projection_get` | Read the work unit's artifact chain + inherited constraints |
| `nm_v1_artifact_query` | Search for existing constraints to avoid duplicates |
| `nm_v1_workspace_search` | Grep workspace files to corroborate a finding |
| `nm_v1_artifact_record` | Record a `Constraint` or `Research` artifact |

The observer's LLM loop (max 8 iterations) uses these tools to decide whether a concrete gap
exists. If yes, it records a `Constraint`; if the gap is already covered or doesn't apply, it
does nothing. Observers never call write, build, test, or merge tools.

---

## What observers produce

Observers emit artifacts of two types:

- **`Constraint`** — a concrete, specific gap the observer identified (e.g., "Missing CSRF
  protection on the login endpoint"). This is the common output.
- **`Research`** — an informational note that doesn't rise to the level of a constraint (e.g.,
  "The current implementation uses session cookies; be aware of SameSite attribute requirements").

Observers never emit `Decision` artifacts. Titles always carry the observer's prefix
(e.g., `[SecurityAgent] Missing rate-limit on /api/auth`), making them identifiable in the
artifact chain.

---

## How constraints flow through the system

Constraints produced by observers enter the same inheritance model as manually recorded
constraints:

```
Global constraints (promoted, no owning work unit)
  └── Ancestor work unit constraints (root → ... → parent)
        └── Self constraints (this work unit's own)
```

All three layers are merged into `InheritedConstraints` on the `AgentWorkspaceProjection`.

**Orchestrator:** Receives inherited constraints in the kickoff message at cycle 0 (once per
run). They shape everything the orchestrator plans for that execution.

**Reviewer:** Queries constraints before approving a proposal. A change that violates a
recorded constraint is grounds for rejection.

**Decision Lens (VS Code extension):** Observer-proposed constraints appear in the Artifacts
chain visible in the Context tab, identifiable by their `[AgentName] ` title prefix.

---

## Advanced: promoting a constraint to global scope

A constraint recorded against a specific work unit is scoped to that work unit and its
descendants. To make a constraint apply workspace-wide (e.g., a security rule that should
govern all future work), promote it by removing the `OwnedByWorkUnitId` link — this marks it
as a global constraint inherited by every work unit. Global promotion is a manual operation
today (no UI button); it requires updating the artifact node directly via the node store.
