namespace NodalMerge.Studio.Host;

public sealed class HeadlessPeerOptions
{
    public bool Enabled { get; set; }

    /// <summary>WebSocket base URI of the server to connect to (e.g. ws://localhost:5080).
    /// When null the peer runs standalone — agent loops run locally with no room presence.</summary>
    public string? HostUri { get; set; }

    public string RoomId { get; set; } = "studio";

    /// <summary>ephemeral-agent | persistent-agent</summary>
    public string PeerType { get; set; } = "ephemeral-agent";

    /// <summary>Stable peer identity. When null a UUID is generated and persisted to
    /// {WorkspaceRootPath}/.peer-id so it survives restarts.</summary>
    public string? PeerId { get; set; }
}
