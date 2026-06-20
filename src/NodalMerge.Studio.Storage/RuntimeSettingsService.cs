using System.Text.Json;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 13d — only the runtime-mutable fields exposed via /studio/options today. The file-path
// and byte-limit fields on WorkspaceOptions stay config-file-only, as they are now.
public sealed record RuntimeSettingsSnapshot(
    bool UseLlmProfileSelection,
    bool BlockOverlappingFileScope = false,
    int MaxConcurrentWorkers = 3,
    int SchedulerPollIntervalMs = 2_000,
    bool UsePromotionBranch = false,
    string CandidateBranchId = "candidate");

// Persists WorkspaceOptions's runtime-mutable fields to the node store on every mutation and
// reapplies them on startup, so toggling a setting via REST survives a host restart. A single
// fixed entity id is used since there's only ever one WorkspaceOptions singleton per host.
public sealed class RuntimeSettingsService(IStudioNodeStore nodeStore, WorkspaceOptions options) : IRehydratable
{
    private const string EntityId = "singleton";

    public async Task PersistAsync(CancellationToken ct = default)
    {
        var snapshot = new RuntimeSettingsSnapshot(
            options.UseLlmProfileSelection,
            options.BlockOverlappingFileScope,
            options.MaxConcurrentWorkers,
            options.SchedulerPollIntervalMs,
            options.UsePromotionBranch,
            options.CandidateBranchId);
        await nodeStore.WriteNodeAsync(
            StudioNodeKind.RuntimeSettingsV1,
            EntityId,
            JsonSerializer.Serialize(snapshot),
            ct).ConfigureAwait(false);
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var payloadJson = await nodeStore.ReadNodeAsync(StudioNodeKind.RuntimeSettingsV1, EntityId, ct).ConfigureAwait(false);
        if (payloadJson is null)
            return;

        var snapshot = JsonSerializer.Deserialize<RuntimeSettingsSnapshot>(payloadJson);
        if (snapshot is null)
            return;

        options.UseLlmProfileSelection = snapshot.UseLlmProfileSelection;
        options.BlockOverlappingFileScope = snapshot.BlockOverlappingFileScope;
        options.MaxConcurrentWorkers = snapshot.MaxConcurrentWorkers;
        options.SchedulerPollIntervalMs = snapshot.SchedulerPollIntervalMs;
        options.UsePromotionBranch = snapshot.UsePromotionBranch;
        options.CandidateBranchId = snapshot.CandidateBranchId;
    }
}
