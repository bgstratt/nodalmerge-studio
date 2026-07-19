namespace NodalMerge.Studio.Storage;

// Slice 6.4 (plans/cas-distribution-and-storage.md Phase 6, D4 "mode collapse") — one config
// section governing both room-connection presence and the workgroup room's name:
//
//   "Room": { "HostUri": "wss://team.example.com" | "ws://127.0.0.1:9090" | "", "Workgroup": "acme-platform" }
//
// D4's rule: standalone vs connected is SOLELY HostUri being empty — never inferred from URL
// shape. See StudioServiceCollectionExtensions.AddStudioServices for the config-binding factory
// (Room:* primary, Peer:HostUri back-compat fallback with a deprecation log) and
// StudioWebApplication.cs for how HeadlessPeerOptions.HostUri is now sourced from here instead of
// binding "Peer:HostUri" a second time.
public sealed class RoomOptions
{
    /// <summary>WebSocket base URI of the room server to connect to (e.g. ws://localhost:5080,
    /// wss://team.example.com). Empty/null = standalone: RoomPeerClient.StartAsync no-ops, no
    /// sockets are ever attempted. Hosting your own server is not a distinct mode — it's this set
    /// to a loopback/LAN address with the server binary run separately (D4); the extension never
    /// spawns it.</summary>
    public string? HostUri { get; set; }

    /// <summary>Names the workgroup room this peer's repositories-map/cross-repo-goal state lives
    /// in and (when connected) joins upstream — replaces the pre-6.4 hardcoded "workgroup" room id
    /// literal. Defaults to "workgroup" so a standalone peer (HostUri empty) still has a stable
    /// local room id for WorkgroupRepositoryDirectory/WorkgroupGoalDirectory even though no server
    /// is ever consulted about it; irrelevant to any actual routing until HostUri is also set.</summary>
    public string Workgroup { get; set; } = "workgroup";

    /// <summary>The room id actually used for the workgroup room. Defensive fallback to
    /// "workgroup" if <see cref="Workgroup"/> is ever blank (config binding can overwrite the C#
    /// property default with an empty string) — the config-bound factory already normalizes this,
    /// this is a second line of defense for anyone constructing <see cref="RoomOptions"/>
    /// directly (e.g. tests).</summary>
    public string EffectiveWorkgroupRoomId => string.IsNullOrWhiteSpace(Workgroup) ? "workgroup" : Workgroup;
}
