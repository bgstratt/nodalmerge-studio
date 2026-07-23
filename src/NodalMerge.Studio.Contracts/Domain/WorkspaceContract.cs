using System.Text.Json.Serialization;
using NodalMerge.Studio.Contracts.Projections;

namespace NodalMerge.Studio.Contracts.Domain;

// plans/harness-hosting-architecture.md Phase A.2/A.3 — the Workspace Contract: a versioned,
// transport-agnostic surface any harness (native loop, Claude Code, a future adapter) can read and
// write. The directory materialization under `.workspace/` (WorkspaceContractService, Phase A.4)
// is one serializer over these DTOs, not the contract itself — see
// docs/contracts/workspace-contract-v1.md for the design principles this file is evaluated
// against.

/// <summary>
/// First file any adapter reads. Capabilities are RUNTIME capabilities — which contract surfaces
/// this runtime materialized for this run — not harness capabilities (see the future
/// HarnessCapabilities flags in Phase C). An older adapter ignores entries it doesn't know; a
/// newer adapter detects a runtime that doesn't yet expose a surface and degrades.
/// </summary>
public sealed record WorkspaceContractManifest(
    string ContractVersion,
    string RuntimeVersion,
    string GoalId,
    string WorkUnitId,
    IReadOnlyList<string> Capabilities);

public static class WorkspaceContractCapabilities
{
    public const string EngineeringState = "engineering-state";
    public const string ReviewPolicy = "review-policy";
    public const string Inbox = "inbox";
    public const string Decisions = "decisions";

    public static readonly IReadOnlyList<string> All =
        [EngineeringState, ReviewPolicy, Inbox, Decisions];
}

public sealed record WorkspaceContractGoal(
    string GoalId,
    string Goal,
    string? SuccessCriteria,
    string? ParentGoalId);

public sealed record WorkspaceContractWorkUnit(
    string WorkUnitId,
    string BranchId,
    IReadOnlyList<string> FileScope,
    IReadOnlyList<string> DependsOn,
    string? ParentWorkUnitId,
    string Goal,
    string? SuccessCriteria);

/// <summary>What the AP-4 gate will check. TaskReviewPolicy/WorkspaceReviewPolicy mirror the same
/// fields on WorkUnit — see its doc comments for the child-vs-top-level distinction.
/// SelfVerifyBuildRequired/TestRequired (Phase B.3) are sourced from WorkspaceOptions
/// .RequireBuildBeforeProposal/RequireTestBeforeProposal — session-wide, not per-work-unit, today.
/// These are a kickoff-contract *hint* only (efficiency: the harness fixes issues before exiting)
/// — the actual guarantee is WorkspaceExecutionRule's mechanical gate at ProposalCreated, which
/// fires identically whether the branch was edited natively or externally. Same principle as
/// advisory leases: prompt = hint, runtime mechanism = correctness.</summary>
public sealed record WorkspaceContractReviewPolicy(
    string TaskReviewPolicy,
    string WorkspaceReviewPolicy,
    bool SelfVerifyBuildRequired,
    bool SelfVerifyTestRequired);

/// <summary>
/// Harness→runtime shape for one `.workspace/decisions/NNNN.md` entry. Type must be one of
/// Research | Decision | Constraint | Supersession (mirrors ArtifactCommandService.RecordAsync's
/// validation) — carrying a type field per entry, rather than assuming one kind, is what lets an
/// external harness record any of the three knowledge-artifact kinds without a runtime-specific
/// vocabulary (see the "Worker-tool parity audit" note in the architecture doc).
/// </summary>
public sealed record WorkspaceContractDecisionEntry(
    string Type,
    string Title,
    string Body,
    IReadOnlyList<string>? Supersedes = null);

/// <summary>Harness→runtime `.workspace/inbox/NNNN.md` entry — a blocking question. Matched by
/// Number to a runtime→harness `.workspace/outbox/NNNN.md` WorkspaceContractOutboxEntry answer.</summary>
public sealed record WorkspaceContractInboxEntry(int Number, string Question);

public sealed record WorkspaceContractOutboxEntry(int Number, string Answer);

/// <summary>
/// Harness→runtime `.workspace/plan.json` — plans/phase-d-implementation.md D1.b (Phase D
/// planning mode). Mirrors <c>PlanDocument</c>/<c>PlanSlice</c> (see PlanDocument.cs) field-for-
/// field, same JSON property names, so a CLI harness's plan.json deserializes into exactly the
/// same wire shape the native planner already normalizes via
/// <c>nm_v1_artifact_record_plan</c>/ArtifactRecordPlan — <c>FanOutService.ReadPlanFromArtifactAsync</c>
/// folds the resulting Plan artifact with zero changes either way. Kept as a distinct type from
/// PlanDocument (rather than reusing it directly) because this one lives in the versioned,
/// externally-implementable Workspace Contract surface (docs/contracts/workspace-contract-v1.md)
/// — PlanDocument is free to evolve as Studio's own internal planning-scheduler shape without
/// that being a contract-breaking change for an external harness.
/// </summary>
public sealed record WorkspaceContractPlan(
    [property: JsonPropertyName("slices")] IReadOnlyList<WorkspaceContractPlanSlice> Slices,
    [property: JsonPropertyName("contracts")] IReadOnlyList<PlanContract>? Contracts = null);

public sealed record WorkspaceContractPlanSlice(
    [property: JsonPropertyName("sliceId")] string SliceId,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("fileScope")] IReadOnlyList<string> FileScope,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<string> DependsOn,
    [property: JsonPropertyName("steps")] IReadOnlyList<string> Steps,
    // Mirrors PlanSlice field-for-field (see PlanDocument.cs); trailing + defaulted keeps the
    // versioned external Workspace Contract surface back-compatible for existing harness plans.
    [property: JsonPropertyName("kind")] PlanSliceKind Kind = PlanSliceKind.Leaf,
    [property: JsonPropertyName("provides")] IReadOnlyList<string>? Provides = null,
    [property: JsonPropertyName("consumes")] IReadOnlyList<string>? Consumes = null);

/// <summary>
/// The full set of runtime→harness files assembled for one work unit's spawn — what
/// IWorkspaceContractService.AssembleAsync returns, and what MaterializeAsync writes out as
/// `.workspace/*.json` (+ derived `.md` siblings). State/Constraints reuse the EngineeringState
/// projection payload directly rather than duplicating a shape (constraints.json = engineeringState
/// filtered to Type == Constraint).
/// </summary>
public sealed record WorkspaceContractBundle(
    WorkspaceContractManifest Manifest,
    WorkspaceContractGoal Goal,
    WorkspaceContractWorkUnit WorkUnit,
    EngineeringStateProjectionPayload EngineeringState,
    WorkspaceContractReviewPolicy ReviewPolicy);
