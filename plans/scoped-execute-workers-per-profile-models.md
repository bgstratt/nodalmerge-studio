# Scoped Execute workers on their own models — bind a Model Profile to a file-scoped Agent Profile

## Status

**SHIPPED (2026-07-19)** — full stack implemented per this design; `domain`-field
retirement (§2) deliberately deferred (kept as-is; may find a use later). What
landed:
- Contract: `AgentProfile.ModelProfileId` (nullable, back-compat).
- Server: `GoalDefaultCredentials`-keyed `ProfileCredentials` threaded through
  `SpawnAsync` → `GoalCredentialRegistration` + persisted `GoalRoutingConfig` +
  `ResolveAndPersistCredentialsAsync`; new `GetCredentialsForProfile` (twin of
  `GetCredentialsForStage`).
- FanOut: matched profile's bound creds win over the Execute-stage default
  (`FanOutService` `effectiveCreds`). Plan: same in `GoalCoordinator` (`profileCreds`).
- REST: `SpawnAgentBody.ProfileCredentials`, `Create/UpdateAgentProfileBody.ModelProfileId`.
- Client: Model Profile dropdown + "Model" column in the Pipeline Profiles editor;
  spawn resolves each bound Execute/Plan profile → `profileCredentials`.
- Tests: `GetCredentialsForProfile` unit tests (green) + two `FileScopeProfileRoutingTests`
  (bound-model routes to that model; unbound inherits stage default).
- Docs: `docs-site/studio/guides/multi-agent-profiles.mdx` (registered in `docs.json`).

Original design of record below.

## Problem

File-scope routing of Execute-stage workers already exists and is fully wired
(`FanOutService.TryMatchFileScopeProfileAsync`, `PlannerSelectionService`), but
it can only vary a worker's **behavior** — system prompt, allowed tools,
executor. It **cannot** vary which **LLM** the worker runs on, because
credentials are resolved by `PipelineStage`, not by the matched profile. Every
Execute worker — frontend, backend, docs — is forced onto the single Model
Profile the Agent Topology assigns to the `worker` (Execute) stage.

The whole point of scoped workers is to let a user say "plan on Opus, do the
frontend on Sonnet, do the backend on Haiku." Today they can't, and the seam is
why the data model feels wrong.

### Root cause (confirmed by investigation)

- **`AgentProfile` (server, `Contracts/Domain/AgentProfile.cs:16-33`) carries no
  credentials.** Fields are `AgentProfileId, Name, Stage, SystemPrompt,
  AllowedTools, MaxIterations, FileScopePatterns, Executor, InjectApiKeyEnv`.
  `Executor` selects a harness kind, not an API key; `InjectApiKeyEnv` only
  toggles whether the *caller-supplied* key is injected.
- **Credentials are joined at the stage level.** The client `TopologyTemplate`
  (`AgentConfigService.ts:46-63`) maps each stage (`orchestrator/planner/worker/
  reviewer/reconciler`) to a *Model Profile id*; at spawn the client resolves
  those to creds and ships a `stageCredentials` map keyed by `PipelineStage`.
  The server resolves per stage in
  `InMemoryAgentRuntimeService.GetCredentialsForStage` (`:1006`).
- **FanOut throws the matched profile's identity away for credential purposes.**
  At `FanOutService.cs:529-539` the enqueue passes `creds?.Model/BaseUrl/ApiKey/
  Provider` (stage creds); the matched profile only contributes
  `selection.ProfileId` (behavior).

### Also in scope: retire the vestigial `domain` field

The client Model Profile has a required free-text `domain` field
(`AgentConfigService.ts:29`) that **never crosses the wire** and drives nothing
except one soft UI default (`goalWorkspace.js:278`, preferring
`domain === 'orchestration'` as the pre-selected fork). It is an abandoned first
attempt at the same "scoped worker" idea that `FileScopePatterns` now does
correctly. Leaving it in place is what makes the model read as having two
half-built scoping mechanisms. This plan removes it (or demotes it to optional,
display-only) so the mental model is: **Model Profile = LLM connection; Agent
Profile = the worker (role + scope + which LLM).**

> Not in scope: `EnabledDomainAgents` / `DomainAgentRegistry`. Despite the
> shared "domain" word, those are reactive artifact observers (Security, Perf,
> …), a different concept. Untouched here.

## Design

The coherent unit is the **Agent Profile as the worker**: it already owns stage
+ file scope + prompt + tools + executor. The single missing attribute is *which
LLM*. So we **add a Model Profile binding to the Agent Profile** and let
FanOut/Planner honor it when a scope match occurs. The Agent Topology's per-stage
Model Profile stays exactly as-is and becomes the **default/fallback** for
unmatched or unscoped work.

Mental model after this change:

| Entity | Is | Role in scoping |
|---|---|---|
| **Model Profile** (client) | an LLM *connection* (provider/model/creds), reusable | the "which LLM" a worker points at |
| **Agent Profile** (server) | a *worker/role*: stage + file scope + prompt + tools + executor + **bound Model Profile** | the scoped worker itself |
| **Agent Topology** (client) | per-stage *default* Model Profile lineup | the fallback when nothing scoped matches |

A "scoped worker" = **one Execute-stage Agent Profile** with `FileScopePatterns`
set and a `ModelProfileId` bound. FanOut already routes to it by scope; this plan
makes it also run on its own LLM. Same mechanism generalizes to the **Plan**
stage for free (`PlannerSelectionService` uses the identical match).

### Back-compat invariant

`ModelProfileId` is nullable. **Null ⇒ inherit the stage credentials exactly as
today.** Every existing profile (including the seeded empty-scope `worker`)
behaves identically until a user opts in by binding a model. No migration.

## Implementation

The server never sees Model Profiles — the client resolves Model Profile → creds
and ships them at spawn. So the new path mirrors the existing `stageCredentials`
plumbing, keyed by **profile id** instead of stage.

### 1. Contract: bind a model to the profile
- Add `string? ModelProfileId` to `AgentProfile`
  (`Contracts/Domain/AgentProfile.cs:16-33`). Nullable, default null.
- Thread it through `CreateAgentProfileBody` / `UpdateAgentProfileBody`
  (`StudioRestEndpoints.cs:119,129`) and `AgentProfileService` CRUD/persistence
  (`StudioNodeKind.AgentProfileV1`).

### 2. Client: author the binding
- Add a **Model Profile** dropdown to the Pipeline Profiles editor next to the
  existing `pp-filescope` input (`modelAgentStudio.js:544`), populated from the
  configured Model Profiles. Persist the selected id on the Agent Profile.
- Remove the required `domain` field from the Model Profile form
  (`modelAgentStudio.js:289,296,304`) and settings schema
  (`package.json:135,146-148`); drop the `domain === 'orchestration'` default
  in `goalWorkspace.js:278-280` (fall back to "any profile"). Keep `domain`
  readable as optional/deprecated if removing it outright is too invasive for
  one pass — but it must no longer be *required*.

### 3. Spawn: ship profile-keyed credentials
- At goal start, after resolving `stageCredentials`, also resolve every
  Execute-/Plan-stage Agent Profile that has a `ModelProfileId` → creds, and add
  a `profileCredentials` map keyed by `AgentProfileId` to the spawn payload
  (`ArtifactExplorerPanel.ts:1301-1391`, parallel to the existing
  `stageCredentials` assembly).
- Extend the spawn REST body + `StageCredentialDto` sibling in
  `StudioRestEndpoints.cs` to accept `profileCredentials`.

### 4. Server: register + resolve by profile
- Register `profileCredentials` alongside `StageCredentials` in
  `InMemoryAgentRuntimeService` registration (`:82,863,877`).
- Add `GoalDefaultCredentials? GetCredentialsForProfile(string workUnitId,
  string profileId)` — a twin of `GetCredentialsForStage` (`:1006`), including
  the same CLI-provider reconstruction fallback.

### 5. FanOut / Planner: use the matched profile's creds
- In `FanOutService.cs:529-539`: when `TryMatchFileScopeProfileAsync` returns a
  profile whose id has an entry in `profileCredentials`, enqueue with **those**
  creds instead of the stage `creds`. Fall back to stage `creds` when the match
  has no bound model (null `ModelProfileId`).
- Mirror in `PlannerSelectionService` for the Plan stage.
- Everything downstream (`WorkSchedulerService`, `StartWorkerLoop` at
  `:1047-1056`) already takes provider/model/baseUrl/apiKey as loose params — no
  change needed there.

### 6. Tests
- Extend `FileScopeProfileRoutingTests.cs` (and/or a new test) to assert that a
  scope-matched profile with a bound model enqueues with **that** model's
  provider/model, and that a match with no bound model still inherits stage
  creds (back-compat).
- Cover the fall-through cases already tested (zero/partial/multiple matches)
  still land on the stage-default worker with stage creds.

### 7. Docs — user guide for multi-agent profiles (docs repo)

Add a new guide so users can set this up themselves. **This is a required
deliverable, not optional.**

- New file: `docs-site/studio/guides/multi-agent-profiles.mdx` in the **docs
  repo** (`c:/Users/bgstr/source/repos/docs`), matching the frontmatter/style of
  the sibling `domain-observers.mdx` (title + description frontmatter, H1, task
  tables).
- Register it in `docs-site/docs.json` in the studio guides array (after line
  248, alongside `studio/guides/build-from-source`).
- Content must walk a real end-to-end example — e.g. **Opus for Plan, Sonnet for
  frontend, Haiku for backend**:
  1. Create one Model Profile per LLM (provider/model/creds) — e.g.
     `opus-plan`, `sonnet-frontend`, `haiku-backend`.
  2. Create the scoped Pipeline (Agent) Profiles: a Plan-stage profile bound to
     `opus-plan`; a `frontend-worker` (Execute, scope `src/**/*.tsx,
     src/**/*.css`, bound to `sonnet-frontend`); a `backend-worker` (Execute,
     scope `src/**/*.cs`, bound to `haiku-backend`).
  3. Keep the seeded empty-scope `worker` as the catch-all; explain the topology
     `worker` slot is its default LLM.
  4. Explain routing semantics: a profile matches only when **every** file in the
     slice is covered by its patterns and **exactly one** profile matches; mixed
     or unmatched slices fall to the default worker. So slice granularity
     controls whether domain routing fires.
  5. Note null-binding back-compat: a profile with no bound Model Profile inherits
     the stage default.
- If a screenshot is added, drop it under `docs-site/images/` (there is already
  `quickstart-model-agent-studio-profiles.png` to pattern-match against).

## Open questions / decisions

- **Remove `domain` outright vs. soft-deprecate?** Removing a `required` settings
  field can trip existing user configs on load. Safer first pass: make it
  optional and stop reading it; delete in a follow-up. Decide during
  implementation.
- **Should the empty-scope default `worker` also be bindable?** Yes for free —
  it's just an Agent Profile; if it gets a `ModelProfileId` that wins over the
  topology `worker` slot for unmatched slices. Worth calling out in the guide.
- **Review/Merge/Reconcile scoping** stays out of scope — those stages don't
  fan out per-file. Only Execute and Plan consult `FileScopePatterns`.

## Key references (for the implementer)

- `Contracts/Domain/AgentProfile.cs:16-33` — record to extend
- `Orchestrator/FanOutService.cs:529-539` (enqueue), `:570-583` (match) — the
  Execute credential swap
- `AgentRuntime/PlannerSelectionService.cs:52-66` — Plan-stage twin
- `AgentRuntime/InMemoryAgentRuntimeService.cs:1006` (`GetCredentialsForStage`) —
  clone into `GetCredentialsForProfile`; `:82,863,877` registration
- `clients/vscode-extension/src/AgentConfigService.ts:29` (`domain`), `:46-63`
  (`TopologyTemplate`)
- `clients/vscode-extension/src/panels/ArtifactExplorerPanel.ts:1301-1391` —
  spawn payload assembly
- `clients/vscode-extension/src/webviews/views/modelAgentStudio.js:544`
  (`pp-filescope`), `:289-304` (`domain` form)
- `tests/NodalMerge.Studio.Integration.Tests/FileScopeProfileRoutingTests.cs` —
  test bed
- Docs: `docs-site/studio/guides/domain-observers.mdx` (style ref),
  `docs-site/docs.json:241-248` (nav)
