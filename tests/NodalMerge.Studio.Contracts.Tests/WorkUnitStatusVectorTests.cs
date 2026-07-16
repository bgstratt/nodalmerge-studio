using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Contracts.Tests;

/// <summary>
/// Freezes finding #14 (nodalmerge/plans... see
/// nodalmerge-studio/plans/blob-cas-remediation.md, Phase 0 slice 0.4): the
/// <see cref="WorkUnitStatus"/> enum's ordinals and PascalCase JSON casing
/// were hand-mirrored, unpinned by any vector, in nodalmerge's Rust GC
/// live-hash classifier (nodalmerge:server/server/src/studio_live_hashes.rs).
/// This asserts the real <see cref="WorkUnit"/> record — serialized exactly
/// as <c>InMemoryWorkUnitService</c> does in production (bare
/// <c>JsonSerializer.Serialize(entity)</c>, no naming policy, no
/// <c>JsonStringEnumConverter</c>) — against the canonical vectors shared
/// with the Rust mirror
/// (<c>engine/commands/work-unit-status-vectors.v1.json</c> in the sibling
/// nodalmerge repo). The Rust harness is inline, in
/// <c>server/server/src/studio_live_hashes.rs</c>'s own <c>#[cfg(test)] mod
/// tests</c> (not a separate external <c>tests/</c> crate — that would
/// require a test-only <c>pub</c> surface on the parser, which this repo
/// pair avoids; see <c>blob_layout_vectors_match_s3_key_derivation</c> in
/// <c>server/s3-blobs/src/lib.rs</c> for the established precedent). If you
/// reorder, rename, or insert a <see cref="WorkUnitStatus"/> value, update the
/// vectors and BOTH harnesses together — a drift here means nodalmerge's GC
/// coordinator misclassifies live work-unit-seeded snapshots as garbage and
/// deletes them in production, instead of failing a test.
/// </summary>
public sealed class WorkUnitStatusVectorTests
{
    private static readonly string VectorsPath =
        Path.Combine(AppContext.BaseDirectory, "work-unit-status-vectors.v1.json");

    private sealed record StatusOrdinalVector(string Name, int Ordinal);

    private sealed record EnvelopeVector(
        string Id,
        string WorkUnitId,
        int StatusOrdinal,
        string? RepositoryId,
        IReadOnlyDictionary<string, string>? Metadata);

    private static bool VectorsFileExists() => File.Exists(VectorsPath);

    private static (IReadOnlyList<StatusOrdinalVector> Ordinals, IReadOnlyList<string> TerminalNames, IReadOnlyList<EnvelopeVector> Envelopes)
        LoadVectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorsPath));
        var root = doc.RootElement;

        var ordinals = new List<StatusOrdinalVector>();
        foreach (var el in root.GetProperty("status_ordinals").EnumerateArray())
        {
            ordinals.Add(new StatusOrdinalVector(
                el.GetProperty("name").GetString()!,
                el.GetProperty("ordinal").GetInt32()));
        }

        var terminalNames = root.GetProperty("terminal_status_names")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        var envelopes = new List<EnvelopeVector>();
        foreach (var el in root.GetProperty("work_unit_envelope_vectors").EnumerateArray())
        {
            var expected = el.GetProperty("expected");
            var payload = el.GetProperty("envelope").GetProperty("payload");

            Dictionary<string, string>? metadata = null;
            if (expected.TryGetProperty("metadata", out var metaEl) && metaEl.ValueKind == JsonValueKind.Object)
            {
                metadata = new Dictionary<string, string>();
                foreach (var prop in metaEl.EnumerateObject())
                {
                    metadata[prop.Name] = prop.Value.GetString()!;
                }
            }

            envelopes.Add(new EnvelopeVector(
                el.GetProperty("id").GetString()!,
                expected.GetProperty("work_unit_id").GetString()!,
                expected.GetProperty("status_ordinal").GetInt32(),
                expected.TryGetProperty("repository_id", out var repoEl) && repoEl.ValueKind == JsonValueKind.String
                    ? repoEl.GetString()
                    : null,
                metadata));

            // Sanity: the vector's own envelope.payload.Status must match its expected.status_ordinal —
            // guards against an author typo splitting the two apart inside the vector file itself.
            Assert.Equal(expected.GetProperty("status_ordinal").GetInt32(), payload.GetProperty("Status").GetInt32());
        }

        return (ordinals, terminalNames, envelopes);
    }

    [Fact]
    public void Vectors_file_is_present()
    {
        // This is deliberately a loud failure, not a skip: the vectors file lives in the sibling
        // nodalmerge repo (engine/commands/work-unit-status-vectors.v1.json), linked in via
        // Directory.Build.props' NodalMergeEngineCommandsDir + this project's Exists()-guarded
        // <None Include>. If this fails, either the sibling checkout is missing (expected today in
        // nodalmerge-studio's CI runner, which checks out only this repo — see slice 0.4's report
        // for the open cross-repo CI problem) or the two repos have drifted apart locally.
        Assert.True(
            VectorsFileExists(),
            $"Expected {VectorsPath} to exist. This test reads the shared cross-repo contract " +
            "vector file from the sibling nodalmerge repo (engine/commands/work-unit-status-vectors.v1.json), " +
            "linked via the NodalMergeEngineCommandsDir MSBuild property in Directory.Build.props. " +
            "If nodalmerge is not checked out as a sibling of nodalmerge-studio (../nodalmerge), " +
            "this test cannot run — see plans/blob-cas-remediation.md slice 0.4.");
    }

    [Fact]
    public void All_status_ordinals_match_the_frozen_vector()
    {
        if (!VectorsFileExists()) return; // reported by Vectors_file_is_present
        var (ordinals, _, _) = LoadVectors();

        var enumValues = Enum.GetValues<WorkUnitStatus>();
        Assert.Equal(ordinals.Count, enumValues.Length);

        foreach (var vector in ordinals)
        {
            var parsed = Enum.Parse<WorkUnitStatus>(vector.Name);
            Assert.Equal(vector.Ordinal, (int)parsed);
        }
    }

    // NOTE: an earlier draft of this file also asserted that each
    // terminal_status_names entry has zero outgoing WorkUnitTransitions
    // edges, matching studio_live_hashes.rs's doc comment ("`CanTransition`
    // has zero outgoing edges from these three"). That assertion is real but
    // FAILS today: `WorkUnitTransitions.CanTransition(Failed, Cancelled)` is
    // `true` via the `(_, Cancelled) when from is not Completed and not
    // Merged` human-override rule, which does not exclude `Failed`. That is
    // a genuine discrepancy between the Rust module's doc comment and this
    // enum's real transition graph — but it is a *different* contract than
    // finding #14 (ordinals + casing), it is not something 0.4 was scoped to
    // fix, and per this slice's own instructions a Phase-0 pinning slice must
    // ship green today. Removed here and reported separately rather than
    // silently weakened or left red in the deliverable.

    [Theory]
    [MemberData(nameof(EnvelopeVectorIds))]
    public void Real_WorkUnit_serialization_matches_the_frozen_envelope(string vectorId)
    {
        var (_, _, envelopes) = LoadVectors();
        var vector = envelopes.Single(v => v.Id == vectorId);

        var workUnit = new WorkUnit(
            WorkUnitId: vector.WorkUnitId,
            Goal: "goal",
            BranchId: "branch-1",
            Status: (WorkUnitStatus)vector.StatusOrdinal,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Owner: "owner",
            AssignedAgent: null,
            SuccessCriteria: null,
            Metadata: vector.Metadata,
            ParentWorkUnitId: null,
            DependsOn: Array.Empty<string>(),
            FileScope: Array.Empty<string>(),
            RepositoryId: vector.RepositoryId);

        // Bare JsonSerializer.Serialize(entity) — exactly InMemoryWorkUnitService's real write
        // path (no JsonSerializerOptions, no naming policy, no JsonStringEnumConverter). This is
        // the actual bytes nodalmerge's Rust classifier parses.
        var json = JsonSerializer.Serialize(workUnit);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("WorkUnitId", out var workUnitIdEl), "expected exact PascalCase key `WorkUnitId`");
        Assert.Equal(vector.WorkUnitId, workUnitIdEl.GetString());

        Assert.True(root.TryGetProperty("Status", out var statusEl), "expected exact PascalCase key `Status`");
        Assert.Equal(JsonValueKind.Number, statusEl.ValueKind); // integer ordinal, never a string enum name
        Assert.Equal(vector.StatusOrdinal, statusEl.GetInt32());

        Assert.True(root.TryGetProperty("CreatedAt", out _), "expected exact PascalCase key `CreatedAt`");
        Assert.True(root.TryGetProperty("UpdatedAt", out _), "expected exact PascalCase key `UpdatedAt`");

        Assert.True(root.TryGetProperty("RepositoryId", out var repoEl), "expected exact PascalCase key `RepositoryId`");
        if (vector.RepositoryId is null)
        {
            Assert.Equal(JsonValueKind.Null, repoEl.ValueKind);
        }
        else
        {
            Assert.Equal(vector.RepositoryId, repoEl.GetString());
        }

        Assert.True(root.TryGetProperty("Metadata", out var metaEl), "expected exact PascalCase key `Metadata`");
        if (vector.Metadata is null)
        {
            Assert.Equal(JsonValueKind.Null, metaEl.ValueKind);
        }
        else
        {
            foreach (var (key, value) in vector.Metadata)
            {
                Assert.Equal(value, metaEl.GetProperty(key).GetString());
            }
        }
    }

    public static IEnumerable<object[]> EnvelopeVectorIds()
    {
        if (!VectorsFileExists()) yield break; // reported by Vectors_file_is_present
        var (_, _, envelopes) = LoadVectors();
        foreach (var vector in envelopes)
        {
            yield return new object[] { vector.Id };
        }
    }
}
