# Workspace Contract v1 (frozen)

Status: **Frozen for implementation** — Phase A (`plans/harness-hosting-architecture.md`)

The Workspace Contract is the versioned, transport-agnostic surface a harness (the native loop,
Claude Code, a future adapter) reads and writes to participate in a NodalMerge Studio work unit.
The directory materialized under `.workspace/` in a branch's working directory (see
[WorkspaceContractService](../../src/NodalMerge.Studio.Storage/WorkspaceContractService.cs),
Phase A.4) is one serializer over this contract — files are a transport, not the contract itself.
Treat this document the way you'd treat HTTP or the OCI image format: a specification independent
parties implement against, not an internal DTO set.

Canonical C# types: `NodalMerge.Studio.Contracts.Domain` (`WorkspaceContract.cs`) and
`NodalMerge.Studio.Contracts.Projections` (`EngineeringStateProjectionPayload`, reused directly
for `state.json`/`constraints.json`).

Architecture context: [harness-hosting-architecture.md](../../plans/harness-hosting-architecture.md)
(nodalmerge-studio repo)

---

## Design principles

These are evaluated against every future addition to this contract, additive or otherwise.

### WC-1: Transport-independent
Files are one serialization of the contract, not the contract. The same schema could later travel
over MCP, an SDK call, or a socket.

### WC-2: Deterministic
The same runtime state materializes byte-identical contract content (stable JSON key ordering,
sorted collections, no wall-clock-dependent fields inside a single assembly beyond an explicit
`GeneratedAt`).

### WC-3: Tolerant of unknown/missing fields
Unknown fields must be ignored by consumers; producers may add optional fields (additive-only
within a major version, matching the `nm_v1_*` → `nm_v2_*` MCP convention). Consumers must
tolerate partial capability sets — the manifest declares what's present.

### WC-4: Runtime is authoritative
Harness-written files (`decisions/`, `inbox/`, `plan.json`) are advisory until promotion through
the AP-4 review gate — never applied directly.

### WC-5: Minimal surface
The contract exposes everything a harness needs, not everything Studio knows. Minimalism test per
field: could two independent harnesses produce equivalent results without it? If yes, leave it
out.

### WC-6: No central-server assumption
Every field must be interpretable by a disconnected, local-first replica. IDs are content/domain
identities, never server-local handles or URLs that require a live host to resolve.

### WC-7: Independent-implementation test
A new adapter (any language, any vendor) must be writable from this document alone, without
reading Studio source.

### WC-8: JSON canonical, markdown derived
The runtime reads and writes structured JSON; `.md` renderings of runtime→harness files are
materialized alongside because LLM harnesses consume markdown better — generated from the JSON,
one direction only, never hand-maintained separately. Harness→runtime files are accepted in either
form; the harvest parser normalizes markdown to JSON.

### WC-9: Excluded from diff and CAS
`.workspace/` is not source content — excluded from diff harvest (`merge.propose`) and from CAS
snapshot paths (`WorkspacePathFilter.IgnoredDirNames`). It never appears in a merge proposal or a
`RepositorySnapshot`.

---

## Directory layout

```text
.workspace/
  manifest.json            # REQUIRED, first file any adapter reads.
  goal.json / goal.md      # runtime → harness
  workunit.json            # runtime → harness
  state.json / state.md    # runtime → harness — the EngineeringState projection (Phase A.1)
  constraints.json / .md   # runtime → harness — state.json's facts filtered to Type == Constraint
  review-policy.json / .md # runtime → harness — what the AP-4 gate will check
  decisions/               # harness → runtime — one file per entry, numbered (0001.md, 0002.md, …)
  inbox/                   # harness → runtime — blocking questions, one numbered file each
  outbox/                  # runtime → harness — answers, matched by number
  plan.json                # harness → runtime — Phase D (planning mode), not present in Phase A/B
```

Per-entry numbered files (`decisions/`, `inbox/`) avoid append-ordering ambiguity at harvest, and
give each entry a stable identity (`decisions/0007.md` maps to one deterministic domain key) —
re-harvesting after a crash or retry cannot double-record an artifact.

---

## Manifest capabilities

`manifest.json`'s `capabilities` array declares which contract surfaces *this runtime*
materialized for this run (`NodalMerge.Studio.Contracts.Domain.WorkspaceContractCapabilities`):

| Capability | Meaning |
|---|---|
| `engineering-state` | `state.json`/`state.md` were materialized |
| `review-policy` | `review-policy.json`/`.md` were materialized |
| `inbox` | the runtime will poll/respawn on `inbox/` entries |
| `decisions` | the runtime will harvest `decisions/` entries into artifacts |

An older adapter ignores capabilities it doesn't recognize; a newer adapter checks for a
capability it needs and degrades gracefully if the runtime hasn't declared it.

---

## Typed DTOs

```csharp
public sealed record WorkspaceContractManifest(
    string ContractVersion, string RuntimeVersion, string GoalId, string WorkUnitId,
    IReadOnlyList<string> Capabilities);

public sealed record WorkspaceContractGoal(
    string GoalId, string Goal, string? SuccessCriteria, string? ParentGoalId);

public sealed record WorkspaceContractWorkUnit(
    string WorkUnitId, string BranchId, IReadOnlyList<string> FileScope,
    IReadOnlyList<string> DependsOn, string? ParentWorkUnitId);

public sealed record WorkspaceContractReviewPolicy(
    string TaskReviewPolicy, string WorkspaceReviewPolicy);

public sealed record WorkspaceContractDecisionEntry(
    string Type, string Title, string Body, IReadOnlyList<string>? Supersedes = null);

public sealed record WorkspaceContractInboxEntry(int Number, string Question);

public sealed record WorkspaceContractOutboxEntry(int Number, string Answer);

public sealed record WorkspaceContractBundle(
    WorkspaceContractManifest Manifest, WorkspaceContractGoal Goal,
    WorkspaceContractWorkUnit WorkUnit, EngineeringStateProjectionPayload EngineeringState,
    WorkspaceContractReviewPolicy ReviewPolicy);
```

`state.json`/`constraints.json` reuse `EngineeringStateProjectionPayload`/`EngineeringStateFact`
from `NodalMerge.Studio.Contracts.Projections` (see
[projection-v1-contract.md](./projection-v1-contract.md)) rather than duplicating the shape.
`decisions/NNNN.md`'s `Type` field must be one of `Research | Decision | Constraint |
Supersession`, matching `ArtifactCommandService.RecordAsync`'s validation.

## Phase A+ addendum

Harness capability flags (`SupportsResume`, `SupportsHooks`, … — Phase C.2, distinct from the
manifest's *runtime* capabilities above) are not part of this v1 surface.

### `plan.json` (Phase D.1, planning mode)

Additive per WC-3 — an adapter that predates Phase D simply never writes this file, and a runtime
that predates Phase D simply never reads it (`.workspace/manifest.json` doesn't gain a new
capability flag for this; a Plan-stage spawn is itself the signal a harness needs to write one).

`.workspace/plan.json` is harness → runtime, written only on a `Mode == Plan` run (never mixed
with a normal Execute run's edits — a Plan-mode kickoff instructs "implement nothing, write only
this file", enforced advisorily via a Write-scoped `--settings` allowlist where the adapter
supports one). Canonical C# type: `NodalMerge.Studio.Contracts.Domain.WorkspaceContractPlan`
(`WorkspaceContract.cs`), mirroring `PlanDocument`/`PlanSlice` (`PlanDocument.cs`) field-for-field
so the runtime's harvest step (`HarnessHarvestPipeline.HarvestPlanAsync`) re-serializes it into
the exact same shape `nm_v1_artifact_record_plan`/`ArtifactRecordPlan` already normalizes for the
native planner — `FanOutService.ReadPlanFromArtifactAsync` folds either source identically.

```csharp
public sealed record WorkspaceContractPlan(
    IReadOnlyList<WorkspaceContractPlanSlice> Slices,
    IReadOnlyList<PlanContract>? Contracts = null);

public sealed record WorkspaceContractPlanSlice(
    string SliceId, string Goal, IReadOnlyList<string> FileScope,
    IReadOnlyList<string> DependsOn, IReadOnlyList<string> Steps,
    PlanSliceKind Kind = PlanSliceKind.Leaf,          // "leaf" (default) | "compound"
    IReadOnlyList<string>? Provides = null,           // contractIds this slice implements
    IReadOnlyList<string>? Consumes = null);          // contractIds this slice depends on

public sealed record PlanContract(
    string ContractId, string Description, IReadOnlyList<string> Schema);
```

JSON shape (property names are the wire contract, not the C# property names above):

```json
{
  "slices": [
    {
      "sliceId": "s1",
      "goal": "Implement Foo.cs",
      "fileScope": ["src/Foo.cs"],
      "dependsOn": [],
      "steps": ["Create Foo.cs"]
    }
  ]
}
```

Validation (WC-3, tolerant of unknown fields; strict on required ones): every slice needs a
non-empty `sliceId` and `goal`; `fileScope`/`dependsOn`/`steps` may be empty arrays. Malformed JSON
or a missing required field fails the run with a clear reason (native replan can pick it up) rather
than silently folding a partial plan — this is the one place WC-3's "producers may add optional
fields" principle does not extend to "consumers accept a structurally invalid document."

**Optional recursive-planning / peer-contract fields (all default to today's flat behavior when
omitted — see `plans/recursive-planning-spike.md`):**

- `"kind"`: `"leaf"` (default) or `"compound"`. A `compound` slice is re-planned by a sub-planner
  instead of run by a worker, subject to the runtime's `Workspace:MaxPlanDepth` cap (default `1` =
  no compound routing, exactly today's behavior). A `compound` slice at the depth ceiling is demoted
  to a worker.
- `"provides"` / `"consumes"`: lists of `contractId` strings a slice implements or calls.
- top-level `"contracts"`: parent-authored interfaces two peer slices agree on. Each is
  `{ "contractId", "description", "schema": [...] }`. The contract is injected into both the producer
  and consumer worker (so disjoint-fileScope peers build against the same declaration) and into the
  reviewer, which rejects a change that does not conform.

```json
{
  "slices": [
    { "sliceId": "api", "goal": "user endpoint", "fileScope": ["src/Api.cs"], "dependsOn": [], "steps": ["…"], "provides": ["c-user"] },
    { "sliceId": "ui",  "goal": "user page",     "fileScope": ["src/Ui.cs"],  "dependsOn": [], "steps": ["…"], "consumes": ["c-user"] }
  ],
  "contracts": [
    { "contractId": "c-user", "description": "user endpoint", "schema": ["GET /api/user -> { id: string, name: string }"] }
  ]
}
```

A non-empty diff outside `.workspace/` on a Plan-mode run (the harness edited a source file despite
the kickoff/allowlist) is discarded — never proposed, never merged — and recorded as a
`HarnessPlanDiffDiscarded` execution event rather than failing the run outright; the plan itself
may still be valid even if the harness also went off-script.
