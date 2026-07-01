using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.McpServer.Tools;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Capability-gap fix — invalidating a Research/Decision/Constraint artifact flags every artifact
// built on top of it in the lineage chain (ParentArtifactId/GetChildrenAsync), without overwriting
// the descendants' own Status. See ArtifactLineageService.InvalidateAsync.
[Trait("Category", "Integration")]
public class ArtifactInvalidationTests
{
    private static (IArtifactLineageService artifacts, IExecutionEventStream events, IServiceProvider services) BuildServices()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());
        return (
            app.Services.GetRequiredService<IArtifactLineageService>(),
            app.Services.GetRequiredService<IExecutionEventStream>(),
            app.Services);
    }

    private static ArtifactRef MakeArtifact(
        string id, ArtifactType type, string? parentArtifactId, string ownedByWorkUnitId,
        ArtifactStatus status = ArtifactStatus.Active) =>
        new(id, type, parentArtifactId, status, DateTimeOffset.UtcNow, ownedByWorkUnitId, null);

    private sealed class RecordingRuntimeEventBroadcaster : IRuntimeEventBroadcaster
    {
        public List<(string? WorkUnitId, string ArtifactId, IReadOnlyList<string> FlaggedArtifactIds, string Reason)> Calls { get; } = [];

        public Task BroadcastWorkUnitStageChangedAsync(
            string workUnitId, PipelineStage? stage, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task BroadcastArtifactInvalidatedAsync(
            string? workUnitId, string artifactId, IReadOnlyList<string> flaggedArtifactIds, string reason,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((workUnitId, artifactId, flaggedArtifactIds, reason));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task InvalidateAsync_broadcasts_to_IRuntimeEventBroadcaster_when_one_is_registered()
    {
        var broadcaster = new RecordingRuntimeEventBroadcaster();
        var artifacts = new ArtifactLineageService(new InMemoryStudioNodeStore(), broadcaster: broadcaster);
        await artifacts.RecordAsync(MakeArtifact("DEC-1", ArtifactType.Decision, "WU-1", "WU-1"));
        await artifacts.RecordAsync(MakeArtifact("TASK-1", ArtifactType.Task, "DEC-1", "WU-1"));

        await artifacts.InvalidateAsync("DEC-1", "library choice was wrong");

        var call = Assert.Single(broadcaster.Calls);
        Assert.Equal("WU-1", call.WorkUnitId);
        Assert.Equal("DEC-1", call.ArtifactId);
        Assert.Contains("TASK-1", call.FlaggedArtifactIds);
        Assert.Equal("library choice was wrong", call.Reason);
    }

    [Fact]
    public async Task InvalidateAsync_sets_target_status_to_Invalidated()
    {
        var (artifacts, _, _) = BuildServices();
        await artifacts.RecordAsync(MakeArtifact("DEC-1", ArtifactType.Decision, "WU-1", "WU-1"));

        var invalidated = await artifacts.InvalidateAsync("DEC-1", "library choice was wrong");

        Assert.Equal(ArtifactStatus.Invalidated, invalidated.Status);
        Assert.Equal(ArtifactStatus.Invalidated, (await artifacts.GetAsync("DEC-1"))!.Status);
    }

    [Fact]
    public async Task InvalidateAsync_flags_direct_and_transitive_descendants_without_changing_their_status()
    {
        var (artifacts, _, _) = BuildServices();
        await artifacts.RecordAsync(MakeArtifact("DEC-1", ArtifactType.Decision, "WU-1", "WU-1"));
        await artifacts.RecordAsync(MakeArtifact("TASK-1", ArtifactType.Task, "DEC-1", "WU-1"));
        await artifacts.RecordAsync(MakeArtifact("MP-1", ArtifactType.MergeProposal, "TASK-1", "WU-1", ArtifactStatus.Applied));

        await artifacts.InvalidateAsync("DEC-1", "library choice was wrong");

        var task = await artifacts.GetAsync("TASK-1");
        var proposal = await artifacts.GetAsync("MP-1");

        Assert.Equal("DEC-1", task!.InvalidatedByArtifactId);
        Assert.Equal(ArtifactStatus.Active, task.Status); // untouched

        Assert.Equal("DEC-1", proposal!.InvalidatedByArtifactId); // transitive (grandchild)
        Assert.Equal(ArtifactStatus.Applied, proposal.Status); // untouched
    }

    [Fact]
    public async Task InvalidateAsync_throws_for_non_knowledge_artifact_types()
    {
        var (artifacts, _, _) = BuildServices();
        await artifacts.RecordAsync(MakeArtifact("TASK-1", ArtifactType.Task, "WU-1", "WU-1"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => artifacts.InvalidateAsync("TASK-1", "reason"));
    }

    [Fact]
    public async Task InvalidateAsync_throws_for_unknown_artifact()
    {
        var (artifacts, _, _) = BuildServices();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => artifacts.InvalidateAsync("no-such-artifact", "reason"));
    }

    [Fact]
    public async Task InvalidateAsync_appends_StatusChanged_and_InvalidationCascaded_events_when_sessionId_given()
    {
        var (artifacts, events, _) = BuildServices();
        await artifacts.RecordAsync(MakeArtifact("DEC-1", ArtifactType.Decision, "WU-1", "WU-1"));
        await artifacts.RecordAsync(MakeArtifact("TASK-1", ArtifactType.Task, "DEC-1", "WU-1"));

        await artifacts.InvalidateAsync("DEC-1", "library choice was wrong", sessionId: "S-1");

        var sessionEvents = await events.GetSessionEventsAsync("S-1");

        var statusEvent = Assert.Single(sessionEvents, e => e.Kind == ExecutionEventKind.ArtifactStatusChanged);
        var statusPayload = JsonSerializer.Deserialize<ArtifactStatusChangedPayload>(statusEvent.PayloadJson);
        Assert.Equal("DEC-1", statusPayload!.ArtifactId);
        Assert.Equal(ArtifactStatus.Active, statusPayload.PreviousStatus);
        Assert.Equal(ArtifactStatus.Invalidated, statusPayload.NewStatus);

        var cascadeEvent = Assert.Single(sessionEvents, e => e.Kind == ExecutionEventKind.ArtifactInvalidationCascaded);
        var cascadePayload = JsonSerializer.Deserialize<ArtifactInvalidationCascadedPayload>(cascadeEvent.PayloadJson);
        Assert.Equal("DEC-1", cascadePayload!.RootArtifactId);
        Assert.Contains("TASK-1", cascadePayload.FlaggedArtifactIds);
    }

    [Fact]
    public async Task MCP_ArtifactInvalidate_round_trip_is_visible_via_ArtifactList()
    {
        var (artifacts, _, services) = BuildServices();
        var artifactTools = ActivatorUtilities.CreateInstance<ArtifactTools>(services);

        await artifacts.RecordAsync(MakeArtifact("DEC-1", ArtifactType.Decision, "WU-1", "WU-1"));
        await artifacts.RecordAsync(MakeArtifact("TASK-1", ArtifactType.Task, "DEC-1", "WU-1"));

        var invalidateJson = await artifactTools.InvalidateAsync("DEC-1", "library choice was wrong");
        var invalidateDoc = JsonDocument.Parse(invalidateJson).RootElement;
        var invalidated = invalidateDoc.GetProperty("data").Deserialize<ArtifactRef>();
        Assert.Equal(ArtifactStatus.Invalidated, invalidated!.Status);

        var listJson = await artifactTools.ListAsync("WU-1");
        var listDoc = JsonDocument.Parse(listJson).RootElement;
        var list = listDoc.GetProperty("data").Deserialize<List<ArtifactRef>>()!;
        var taskEntry = list.Single(a => a.ArtifactId == "TASK-1");
        Assert.Equal("DEC-1", taskEntry.InvalidatedByArtifactId);
    }
}
