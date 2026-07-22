using System.Text.Json;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 13d — only the runtime-mutable fields exposed via /studio/options today. The file-path
// and byte-limit fields on WorkspaceOptions stay config-file-only, as they are now.
public sealed record RuntimeSettingsSnapshot(
    bool UseLlmProfileSelection,
    int MaxConcurrentWorkers = 3,
    int SchedulerPollIntervalMs = 2_000,
    bool UsePromotionBranch = false,
    string CandidateBranchId = "candidate",
    bool DocFetchTools = false,
    // Recursive planning depth ceiling (plans/recursive-planning-spike.md). Trailing + defaulted to 1
    // so a snapshot persisted before this field deserializes to today's flat behavior.
    int MaxPlanDepth = 1,
    // Automated retry/failure cap before dead-letter. Trailing + defaulted to 3 (the prior hardcoded
    // value) so an older snapshot deserializes unchanged.
    int MaxFailureAttempts = 3);

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
            options.MaxConcurrentWorkers,
            options.SchedulerPollIntervalMs,
            options.UsePromotionBranch,
            options.CandidateBranchId,
            options.DocFetchTools,
            options.MaxPlanDepth,
            options.MaxFailureAttempts);
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
        options.MaxConcurrentWorkers = snapshot.MaxConcurrentWorkers;
        options.SchedulerPollIntervalMs = snapshot.SchedulerPollIntervalMs;
        options.UsePromotionBranch = snapshot.UsePromotionBranch;
        options.CandidateBranchId = snapshot.CandidateBranchId;
        options.DocFetchTools = snapshot.DocFetchTools;
        options.MaxPlanDepth = snapshot.MaxPlanDepth;
        options.MaxFailureAttempts = snapshot.MaxFailureAttempts;
    }
}
