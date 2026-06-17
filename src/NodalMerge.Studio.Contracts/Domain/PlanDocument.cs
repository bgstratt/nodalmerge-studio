using System.Text.Json.Serialization;

namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Structured planner output written to <c>plan.json</c> on the parent work unit's branch.
/// </summary>
public sealed record PlanDocument(
    [property: JsonPropertyName("slices")] IReadOnlyList<PlanSlice> Slices);

public sealed record PlanSlice(
    [property: JsonPropertyName("sliceId")] string SliceId,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("fileScope")] IReadOnlyList<string> FileScope,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<string> DependsOn,
    [property: JsonPropertyName("steps")] IReadOnlyList<string> Steps);

public static class PlanDocumentPaths
{
    public const string FileName = "plan.json";
}
