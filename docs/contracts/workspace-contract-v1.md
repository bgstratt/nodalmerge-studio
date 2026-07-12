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

`plan.json` (Phase D, planning mode) and harness capability flags (`SupportsResume`,
`SupportsHooks`, … — Phase C.2, distinct from the manifest's *runtime* capabilities above) are not
part of this v1 surface. They will be added additively when their phases land.
