using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 3 (plans/organizational-knowledge-and-workgroup-scope.md) — per-peer local constraint
// suppression. Persists a ConstraintToggleV1 node via the 3-arg WriteNodeAsync, which lands in the
// peer-private "studio" room (ConstraintToggleV1 is deliberately absent from RepoScopedKinds and
// WorkgroupScopedKinds), so a user turning a shared constraint off affects only their own agents.
internal sealed record ConstraintToggle(string ArtifactId, bool Disabled);

public sealed class ConstraintToggleService(IStudioNodeStore nodeStore)
    : IConstraintToggleService, IRehydratable
{
    // artifactId -> disabled. A re-enabled toggle is kept as disabled=false rather than removed, so
    // the durable record is idempotent and rehydration reflects the latest explicit choice.
    private readonly ConcurrentDictionary<string, bool> _disabled = new(StringComparer.Ordinal);

    public async Task SetDisabledAsync(string artifactId, bool disabled, CancellationToken ct = default)
    {
        _disabled[artifactId] = disabled;
        await nodeStore.WriteNodeAsync(
            StudioNodeKind.ConstraintToggleV1, artifactId,
            JsonSerializer.Serialize(new ConstraintToggle(artifactId, disabled)), ct).ConfigureAwait(false);
    }

    public Task<IReadOnlySet<string>> GetDisabledIdsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(
            _disabled.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal));

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await nodeStore.ReadAllNodesAsync(StudioNodeKind.ConstraintToggleV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var toggle = JsonSerializer.Deserialize<ConstraintToggle>(payloadJson);
            if (toggle is not null)
                _disabled[toggle.ArtifactId] = toggle.Disabled;
        }
    }
}
