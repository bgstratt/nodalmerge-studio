using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Contracts.Tests;

/// <summary>
/// Recursive-planning spike S1/S6 — the plan.json wire contract for the new PlanSlice.Kind
/// (leaf/compound routing) and the peer-contract fields (contracts / provides / consumes). The
/// load-bearing guarantee is back-compat: a plan.json that predates these fields must deserialize
/// to exactly today's flat behavior (every slice Leaf, no contracts). Deserialization mirrors
/// FanOutService.JsonOpts (PropertyNameCaseInsensitive).
/// </summary>
public class PlanDocumentContractTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static PlanDocument Parse(string json) =>
        JsonSerializer.Deserialize<PlanDocument>(json, JsonOpts)!;

    [Fact]
    public void Legacy_plan_without_kind_reads_every_slice_as_leaf()
    {
        // The exact shape every existing plan.json / external harness plan emits today.
        const string json = """
        {
          "slices": [
            { "sliceId": "s1", "goal": "do a thing", "fileScope": ["src/A.cs"], "dependsOn": [], "steps": ["step"] }
          ]
        }
        """;

        var plan = Parse(json);

        Assert.Single(plan.Slices);
        Assert.Equal(PlanSliceKind.Leaf, plan.Slices[0].Kind);
        Assert.Null(plan.Slices[0].Provides);
        Assert.Null(plan.Slices[0].Consumes);
        Assert.Null(plan.Contracts);
    }

    [Theory]
    [InlineData("compound", PlanSliceKind.Compound)]
    [InlineData("Compound", PlanSliceKind.Compound)]
    [InlineData("leaf", PlanSliceKind.Leaf)]
    public void Kind_deserializes_case_insensitively_from_string(string kindText, PlanSliceKind expected)
    {
        var json = $$"""
        { "slices": [ { "sliceId": "s1", "goal": "g", "fileScope": [], "dependsOn": [], "steps": [], "kind": "{{kindText}}" } ] }
        """;

        Assert.Equal(expected, Parse(json).Slices[0].Kind);
    }

    [Fact]
    public void Kind_serializes_as_a_lowercase_string_not_an_int()
    {
        var doc = new PlanDocument([
            new PlanSlice("s1", "g", ["src/A.cs"], [], ["step"], PlanSliceKind.Compound)
        ]);

        var json = JsonSerializer.Serialize(doc, JsonOpts);

        Assert.Contains("\"kind\":\"Compound\"", json);
        Assert.DoesNotContain("\"kind\":1", json);
    }

    [Fact]
    public void Contracts_and_provides_consumes_round_trip()
    {
        const string json = """
        {
          "slices": [
            { "sliceId": "api", "goal": "backend", "fileScope": ["src/Api.cs"], "dependsOn": [], "steps": ["s"], "provides": ["c-user"] },
            { "sliceId": "ui",  "goal": "frontend", "fileScope": ["src/Ui.cs"], "dependsOn": [], "steps": ["s"], "consumes": ["c-user"] }
          ],
          "contracts": [
            { "contractId": "c-user", "description": "user endpoint", "schema": ["GET /api/user -> { id: string }"] }
          ]
        }
        """;

        var plan = Parse(json);

        Assert.Equal(["c-user"], plan.Slices[0].Provides!);
        Assert.Equal(["c-user"], plan.Slices[1].Consumes!);
        Assert.Null(plan.Slices[0].Consumes);
        Assert.NotNull(plan.Contracts);
        Assert.Single(plan.Contracts!);
        Assert.Equal("c-user", plan.Contracts![0].ContractId);
        Assert.Equal(["GET /api/user -> { id: string }"], plan.Contracts[0].Schema);

        // A compound producer/consumer plan survives a full serialize→deserialize round-trip.
        var reparsed = Parse(JsonSerializer.Serialize(plan, JsonOpts));
        Assert.Equal("c-user", reparsed.Contracts![0].ContractId);
        Assert.Equal(["c-user"], reparsed.Slices[1].Consumes!);
    }
}
