using System.Text.Json.Serialization;

namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Whether a <see cref="PlanSlice"/> is a leaf (handed straight to a worker) or compound
/// (re-planned by a sub-planner, subject to the <c>MaxPlanDepth</c> cap). Defaults to
/// <see cref="Leaf"/> so every plan.json that predates this field — and every external harness
/// plan — reads back as today's flat-and-wide behavior.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanSliceKind { Leaf, Compound }

/// <summary>
/// A parent-authored interface two peer slices agree on (a producer that <c>provides</c> it and a
/// consumer that <c>consumes</c> it), materialized as a Contract artifact and injected into both
/// workers so disjoint-fileScope peers build against the same declared shape rather than each
/// inventing one. Deliberately a simple structured shape (id + description + a list of declared
/// members / endpoint signatures), not a full IDL/OpenAPI schema.
/// </summary>
public sealed record PlanContract(
    [property: JsonPropertyName("contractId")] string ContractId,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("schema")] IReadOnlyList<string> Schema);

/// <summary>
/// Structured planner output written to <c>plan.json</c> on the parent work unit's branch.
/// </summary>
public sealed record PlanDocument(
    [property: JsonPropertyName("slices")] IReadOnlyList<PlanSlice> Slices,
    [property: JsonPropertyName("contracts")] IReadOnlyList<PlanContract>? Contracts = null);

public sealed record PlanSlice(
    [property: JsonPropertyName("sliceId")] string SliceId,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("fileScope")] IReadOnlyList<string> FileScope,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<string> DependsOn,
    [property: JsonPropertyName("steps")] IReadOnlyList<string> Steps,
    // Trailing + defaulted so a plan.json without these reads exactly as before (back-compat,
    // same discipline as AgentProfile.ModelProfileId). Kind defaults to Leaf; provides/consumes
    // default to null (treated as empty at read sites).
    [property: JsonPropertyName("kind")] PlanSliceKind Kind = PlanSliceKind.Leaf,
    [property: JsonPropertyName("provides")] IReadOnlyList<string>? Provides = null,
    [property: JsonPropertyName("consumes")] IReadOnlyList<string>? Consumes = null);

public static class PlanDocumentPaths
{
    public const string FileName = "plan.json";
}
