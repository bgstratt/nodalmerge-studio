namespace NodalMerge.Studio.Storage;

// Layer 2 L2.2 / L2.2b (plans/room-persistence-bloat.md) — cap uncapped free-text fields before
// they are serialized into a node payload. AssistantText / tool-call InputJson bloat the peer-local
// `studio` room; ArtifactRef.Body and DecisionNode.Rationale ride StudioNodeKind.RepoScopedKinds, so
// a runaway agent body would replicate to every peer. The cap matches ConversationLogService's
// pre-existing 20 KB tool-result cap. A visible marker is appended so a reader knows content was
// elided; when full fidelity is needed it lives in the tier-3 local store / a CAS blob (L2.1/L2.3).
internal static class NodePayloadLimits
{
    public const int MaxFieldLength = 20_000;
    public const string TruncationMarker = "...truncated";

    // Returns the input unchanged when null or within the cap; otherwise the first `max` chars plus
    // the marker. Null-in/null-out keeps optional fields (AssistantText, Body, Rationale) optional.
    public static string? Cap(string? value, int max = MaxFieldLength) =>
        value is not null && value.Length > max ? value[..max] + TruncationMarker : value;
}
