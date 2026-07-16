using System.Text.Json;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 6.3a (plans/cas-distribution-and-storage.md Phase 6, D1) — shared helpers for
// denormalizing RepositoryId onto the repo-scoped-indirect kinds at creation time.
//
// Design note (parent-chain resolution): WorkUnit.RepositoryId is nullable — set only for a
// repo-registered top-level goal (Slice 19). A child/forked work unit inherits repo identity from
// its parent instead of carrying its own copy, resolved via a SINGLE hop at the child's own
// creation time (InMemoryWorkUnitService.CreateWorkUnitAsync reads its already-in-memory parent's
// RepositoryId directly). Because every already-created WorkUnit's stored RepositoryId is, by
// construction, already the fully-resolved value (transitively — a grandchild's parent was itself
// resolved this same way at its own creation), every indirect kind below only ever needs to look at
// the WorkUnit named by its own WorkUnitId, never walk further. A real multi-hop walk is needed only
// for MIGRATING pre-6.3a rows, whose WorkUnitV1 payloads may themselves predate the single-hop
// backfill — see NodalMergeStudioNodeStore's migration-time walk, which is necessarily separate
// from this (live-path) helper.
public static class RepositoryIdResolution
{
    // Resolves an indirect kind's RepositoryId from its owning WorkUnitId, at creation time. Never
    // throws; returns null (→ "studio" fallback) when workUnits is unavailable, workUnitId is
    // null/blank, the work unit doesn't exist, or it has no resolvable RepositoryId of its own.
    public static async Task<string?> ResolveFromWorkUnitAsync(
        IWorkUnitService? workUnits, string? workUnitId, CancellationToken ct = default)
    {
        if (workUnits is null || string.IsNullOrWhiteSpace(workUnitId))
            return null;

        var workUnit = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        return workUnit?.RepositoryId;
    }

    // Resolves a KnownGoodState's RepositoryId from its BranchId's own stored BranchV1.RepositoryId
    // — a known-good state has no WorkUnitId of its own to chain through, only a branch name.
    // Reads the node directly via IStudioNodeStore (rather than adding an IWorkUnitService
    // dependency to InMemoryKnownGoodStateService, which would cycle back through
    // InMemoryWorkUnitService's own IKnownGoodStateService dependency). Never throws.
    public static async Task<string?> ResolveFromBranchAsync(
        IStudioNodeStore nodeStore, string? branchId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branchId))
            return null;

        var payloadJson = await nodeStore.ReadNodeAsync(StudioNodeKind.BranchV1, branchId, ct).ConfigureAwait(false);
        return payloadJson is null ? null : TryExtractStringProperty(payloadJson, "RepositoryId");
    }

    // Best-effort extraction of a string JSON property by name — shared by the resolution helpers
    // above and NodalMergeStudioNodeStore's own migration-time payload parsing. Never throws.
    internal static string? TryExtractStringProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propertyName, out var el)
                && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
