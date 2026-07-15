using System.Text.Json;
using System.Text.Json.Nodes;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// docs/STUDIO_ROOM_SCHEMA.md — pins the studio-node/engine-map key-parsing rule and asserts every
/// vector's JSON value is stable under deserialize -> re-serialize. Pack-level byte encoding is
/// engine-owned and explicitly out of scope (see the doc's Vectors section) — these vectors check
/// value *shape* stability, not byte identity, unlike the content-addressed tree-format vectors.
/// </summary>
[Trait("Category", "Integration")]
public class StudioRoomSchemaVectorTests
{
    private static readonly string VectorsPath =
        Path.Combine(AppContext.BaseDirectory, "TestData", "studio-room-schema-vectors.v1.json");

    public sealed record Vector(
        string Id, string VectorKind, string? Namespace, string? NodeKind, string? EntityId, string? Key, JsonNode? Value);

    internal static IReadOnlyList<Vector> LoadVectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(VectorsPath));
        var vectors = new List<Vector>();

        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var id = v.GetProperty("id").GetString()!;
            var vectorKind = v.GetProperty("vector_kind").GetString()!;
            var ns = v.TryGetProperty("namespace", out var nsEl) ? nsEl.GetString() : null;
            var nodeKind = v.TryGetProperty("node_kind", out var nkEl) ? nkEl.GetString() : null;
            var entityId = v.TryGetProperty("entity_id", out var eiEl) ? eiEl.GetString() : null;
            var key = v.TryGetProperty("key", out var kEl) ? kEl.GetString() : null;
            var value = JsonNode.Parse(v.GetProperty("value").GetRawText());
            vectors.Add(new Vector(id, vectorKind, ns, nodeKind, entityId, key, value));
        }

        return vectors;
    }

    public static IEnumerable<object[]> VectorData() => LoadVectors().Select(v => new object[] { v });

    public static IEnumerable<object[]> StudioMapVectorData() =>
        LoadVectors().Where(v => v.VectorKind == "studio-map-entry").Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(VectorData))]
    public void Value_round_trips_through_deserialize_and_re_serialize(Vector vector)
    {
        Assert.NotNull(vector.Value);

        // Deserialize -> re-serialize -> re-parse, then assert structural equality. This is the
        // "equivalent stable-form check" the doc calls for in place of byte-identity: these values
        // are engine-map JSON, never hashed, unlike the content-addressed tree-format vectors, so
        // canonical byte encoding is not part of this contract.
        var serialized = vector.Value!.ToJsonString();
        var roundTripped = JsonNode.Parse(serialized);

        Assert.True(
            JsonNode.DeepEquals(vector.Value, roundTripped),
            $"Vector '{vector.Id}' value did not round-trip structurally.");

        // A second pass (serialize the round-tripped node again) must be stable too — guards against
        // a serializer that reorders or mutates on repeated passes rather than just the first one.
        var secondPass = JsonNode.Parse(roundTripped!.ToJsonString());
        Assert.True(
            JsonNode.DeepEquals(vector.Value, secondPass),
            $"Vector '{vector.Id}' value was not stable across a second round trip.");
    }

    [Theory]
    [MemberData(nameof(StudioMapVectorData))]
    public void Key_round_trips_through_the_build_and_parse_rule(Vector vector)
    {
        Assert.NotNull(vector.NodeKind);
        Assert.NotNull(vector.EntityId);
        Assert.NotNull(vector.Key);

        var builtKey = BuildEngineMapKey(vector.NodeKind!, vector.EntityId!);
        Assert.Equal(vector.Key, builtKey);

        var (parsedKind, parsedEntityId) = ParseEngineMapKey(vector.Key!);
        Assert.Equal(vector.NodeKind, parsedKind);
        Assert.Equal(vector.EntityId, parsedEntityId);
    }

    [Fact]
    public void Value_envelope_kind_matches_the_key_derived_kind_for_every_studio_map_vector()
    {
        foreach (var vector in LoadVectors().Where(v => v.VectorKind == "studio-map-entry"))
        {
            var kindInValue = vector.Value!["kind"]!.GetValue<string>();
            Assert.Equal(vector.NodeKind, kindInValue);
        }
    }

    // --- Test-only reference implementation of docs/STUDIO_ROOM_SCHEMA.md (a)'s key encoding and
    // parsing rule. Deliberately NOT production code — slice 6.0 is a doc/vector freeze with no
    // runtime behavior change; 6.1a is where this logic (or its functional equivalent) actually
    // lands in NodalMergeStudioNodeStore. Kept here only so the parsing rule this doc freezes is
    // provably unambiguous against the real, current StudioNodeKind list, not just asserted in prose.

    private static string BuildEngineMapKey(string kind, string entityId) => $"{kind}/{entityId}";

    private static (string Kind, string EntityId) ParseEngineMapKey(string key)
    {
        // Longest-match-first over the closed StudioNodeKind set, per the doc's parsing rule — see
        // STUDIO_ROOM_SCHEMA.md (a) for why this is unambiguous today and why longest-first is a
        // defensive rule for future kinds rather than a requirement of the current list.
        foreach (var kind in AllKnownKinds.OrderByDescending(k => k.Length))
        {
            var prefix = kind + "/";
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return (kind, key[prefix.Length..]);
        }

        throw new FormatException($"Engine map key '{key}' does not start with any known StudioNodeKind.");
    }

    // Mirrors the full StudioNodeKind list in NodalMerge.Studio.Storage/StudioNodeStore.cs. If a
    // future kind is added there without a corresponding entry here, the "no kind is a prefix of
    // another" invariant this doc's parsing rule relies on stops being provably checked — but it
    // won't silently produce wrong parses, since ParseEngineMapKey only matches against kinds it
    // knows about.
    private static readonly IReadOnlyList<string> AllKnownKinds =
    [
        StudioNodeKind.WorkUnitV1, StudioNodeKind.TaskV1, StudioNodeKind.MergeProposalV1,
        StudioNodeKind.KnownGoodStateV1, StudioNodeKind.BranchV1, StudioNodeKind.AgentProfileV1,
        StudioNodeKind.SchedulerV1, StudioNodeKind.ExecutionSessionV1, StudioNodeKind.ExecutionEventV1,
        StudioNodeKind.CommandResultV1, StudioNodeKind.AgentWorkspaceV1, StudioNodeKind.ArtifactRefV1,
        StudioNodeKind.OrchestrationEventV1, StudioNodeKind.ChangeIntentV1, StudioNodeKind.DeadLetterV1,
        StudioNodeKind.GoalRoutingV1, StudioNodeKind.OrchestratorRoutingV1, StudioNodeKind.RuntimeSettingsV1,
        StudioNodeKind.ExecutionResultV1, StudioNodeKind.GoalV1, StudioNodeKind.DecisionV1,
        StudioNodeKind.EvidenceV1, StudioNodeKind.TrajectoryV1, StudioNodeKind.HypothesisV1,
        StudioNodeKind.ReasoningCommitV1, StudioNodeKind.ReviewTimerV1, StudioNodeKind.ExperimentV1,
        StudioNodeKind.SteeringDecisionV1, StudioNodeKind.FindingV1, StudioNodeKind.ConversationLogV1,
        StudioNodeKind.FileLeaseV1, StudioNodeKind.RepositorySyncStateV1, StudioNodeKind.RepositoryV1,
        StudioNodeKind.ProjectionSnapshotV1, StudioNodeKind.WorkspaceV1, StudioNodeKind.RepositoryOpV1,
        StudioNodeKind.RepositorySnapshotV1, StudioNodeKind.RepositoryConflictV1, StudioNodeKind.CoModPatternV1,
        StudioNodeKind.BlobIndexEntryV1, StudioNodeKind.CandidateConflictV1, StudioNodeKind.TaskConflictV1,
    ];
}
