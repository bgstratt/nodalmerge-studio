using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 6.4 (plans/cas-distribution-and-storage.md Phase 6, D4 "mode collapse") — proves the
/// Room:* config path end to end:
///
///  1. No config at all: standalone by default (empty HostUri) and a stable default workgroup
///     room id ("workgroup") so standalone workgroup state (repositories map, cross-repo goals)
///     still works with nothing configured — the acceptance bar's "no other local behavior
///     difference" half.
///  2. Room:HostUri/Room:Workgroup flow into both RoomOptions and HeadlessPeerOptions (the latter
///     previously bound HostUri from "Peer:HostUri" directly — 6.4 re-sources it from RoomOptions
///     instead, see StudioWebApplication.cs).
///  3. The pre-6.4 "Peer:HostUri" key still works as a fallback when "Room:HostUri" is unset (back
///     compat for docs/guides/headless-peer.md's existing users), and Room:HostUri wins when both
///     are set.
///  4. Room:Workgroup actually changes which engine room WorkgroupRepositoryDirectory writes to —
///     not just that the config value parses, but that the old hardcoded "workgroup" literal is
///     genuinely gone from the write path.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class RoomConfigTests : IAsyncLifetime
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-room-config-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via the shared
    // helper. See TestTeardown for why ClearAllPools + a retrying delete are required on Windows.
    public Task DisposeAsync() => TestTeardown.ClearSqlitePoolsAndDeleteAsync(_tempRoot);

    private WebApplication BuildApp(string name, Dictionary<string, string?> extraConfig)
    {
        var root = Path.Combine(_tempRoot, name);
        var config = new Dictionary<string, string?>
        {
            ["NodalMerge:Storage:Sqlite:DbPath"] = Path.Combine(root, "nodes.db"),
            ["NodalMerge:Storage:FileBlobs:RootPath"] = Path.Combine(root, "blobs"),
            ["Workspace:RootPath"] = Path.Combine(root, "workspace"),
        };
        foreach (var (key, value) in extraConfig)
            config[key] = value;

        return StudioWebApplication.Build([], configureConfiguration: cfg => cfg.AddInMemoryCollection(config));
    }

    [Fact]
    public async Task No_room_config_at_all_means_standalone_and_the_default_workgroup_room_id()
    {
        await using var app = BuildApp("standalone-default", new Dictionary<string, string?>());

        var roomOptions = app.Services.GetRequiredService<RoomOptions>();
        Assert.True(string.IsNullOrEmpty(roomOptions.HostUri));
        Assert.Equal("workgroup", roomOptions.Workgroup);
        Assert.Equal("workgroup", roomOptions.EffectiveWorkgroupRoomId);

        var peerOptions = app.Services.GetRequiredService<HeadlessPeerOptions>();
        Assert.True(string.IsNullOrEmpty(peerOptions.HostUri));

        // Standalone workgroup state still resolves and round-trips with nothing configured.
        var directory = app.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
        var entry = await directory.RegisterAsync("standalone-repo", RepositoryIdentityHints.Empty, preferredRepoId: "repo-standalone");
        Assert.Equal("repo-standalone", entry.RepoId);
        var listed = await directory.ListAsync();
        Assert.Contains(listed, e => e.RepoId == "repo-standalone");
    }

    [Fact]
    public void Room_HostUri_and_Workgroup_config_flow_into_RoomOptions_and_HeadlessPeerOptions()
    {
        using var app = BuildApp("room-hosturi", new Dictionary<string, string?>
        {
            ["Room:HostUri"] = "ws://127.0.0.1:9999",
            ["Room:Workgroup"] = "acme-platform",
        });

        var roomOptions = app.Services.GetRequiredService<RoomOptions>();
        Assert.Equal("ws://127.0.0.1:9999", roomOptions.HostUri);
        Assert.Equal("acme-platform", roomOptions.Workgroup);
        Assert.Equal("acme-platform", roomOptions.EffectiveWorkgroupRoomId);

        // HeadlessPeerOptions.HostUri now sources from RoomOptions, not a second "Peer:HostUri" bind.
        var peerOptions = app.Services.GetRequiredService<HeadlessPeerOptions>();
        Assert.Equal("ws://127.0.0.1:9999", peerOptions.HostUri);
    }

    [Fact]
    public void Legacy_Peer_HostUri_still_works_as_a_deprecated_fallback_when_Room_HostUri_is_unset()
    {
        using var app = BuildApp("legacy-peer-hosturi", new Dictionary<string, string?>
        {
            ["Peer:HostUri"] = "ws://legacy-host:5080",
        });

        var roomOptions = app.Services.GetRequiredService<RoomOptions>();
        Assert.Equal("ws://legacy-host:5080", roomOptions.HostUri);

        var peerOptions = app.Services.GetRequiredService<HeadlessPeerOptions>();
        Assert.Equal("ws://legacy-host:5080", peerOptions.HostUri);
    }

    [Fact]
    public void Room_HostUri_wins_over_legacy_Peer_HostUri_when_both_are_configured()
    {
        using var app = BuildApp("both-set", new Dictionary<string, string?>
        {
            ["Room:HostUri"] = "ws://new-host:5080",
            ["Peer:HostUri"] = "ws://legacy-host:5080",
        });

        var roomOptions = app.Services.GetRequiredService<RoomOptions>();
        Assert.Equal("ws://new-host:5080", roomOptions.HostUri);
    }

    [Fact]
    public async Task Room_Workgroup_config_changes_the_actual_engine_room_id_used_for_workgroup_state()
    {
        await using var app = BuildApp("custom-workgroup-room", new Dictionary<string, string?>
        {
            ["Room:Workgroup"] = "custom-wg-room",
        });

        var directory = app.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
        await directory.RegisterAsync("test-repo", RepositoryIdentityHints.Empty, preferredRepoId: "repo-config-test");

        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();
        // Written under the configured room id, never the old hardcoded "workgroup" literal.
        Assert.NotNull(TryReadEngineMapValue(bridge, "custom-wg-room", "repositories", "repo-config-test"));
        Assert.Null(TryReadEngineMapValue(bridge, "workgroup", "repositories", "repo-config-test"));
    }

    private static JsonNode? TryReadEngineMapValue(IRuntimeCommandBridge bridge, string roomId, string @namespace, string key)
    {
        var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = roomId,
            command = new { MapGet = new { @namespace, key } }
        }));

        if (response.Status != AsStatus.Ok)
            return null;

        using var doc = JsonDocument.Parse(response.EventsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var evt in doc.RootElement.EnumerateArray())
        {
            if (!evt.TryGetProperty("MapValueRead", out var read))
                continue;
            if (!read.TryGetProperty("value", out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            return JsonNode.Parse(value.GetRawText());
        }

        return null;
    }
}
