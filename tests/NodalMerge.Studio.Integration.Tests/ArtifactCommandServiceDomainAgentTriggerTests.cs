using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 21/22 — verifies ArtifactCommandService.RecordAsync notifies the domain agent trigger
/// for the gated knowledge-artifact types (Research/Decision/Constraint) but not for Plan, and
/// that the recorded artifact instance (not a re-fetched copy) is what's passed through.
/// </summary>
[Trait("Category", "Integration")]
public class ArtifactCommandServiceDomainAgentTriggerTests
{
    private sealed class NoopWorkUnitService : IWorkUnitService
    {
        public Task<WorkUnit> CreateAsync(WorkUnit wu, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> UpdateStatusAsync(string id, WorkUnitStatus s, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetCurrentStageAsync(string id, PipelineStage? stage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string id, string? blockedReason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken ct = default) => Task.FromResult<WorkUnit?>(null);
        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<WorkUnit> SetFileScopeAsync(string workUnitId, IReadOnlyList<string> fileScope, string? sessionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingDomainAgentTriggerService : IDomainAgentTriggerService
    {
        public readonly List<ArtifactRef> Notified = [];
        public Task NotifyArtifactRecordedAsync(ArtifactRef artifact, CancellationToken ct = default)
        {
            Notified.Add(artifact);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("Research")]
    [InlineData("Decision")]
    [InlineData("Constraint")]
    public async Task RecordAsync_notifies_domain_agent_trigger_for_knowledge_artifact_types(string type)
    {
        var trigger = new RecordingDomainAgentTriggerService();
        var svc = new ArtifactCommandService(
            new ArtifactLineageService(new InMemoryStudioNodeStore()), new NoopWorkUnitService(), trigger);

        var recorded = await svc.RecordAsync("wu-1", type, "Some finding", "Some body");

        Assert.Single(trigger.Notified);
        Assert.Equal(recorded.ArtifactId, trigger.Notified[0].ArtifactId);
        Assert.Equal(recorded.Title, trigger.Notified[0].Title);
    }

    [Fact]
    public async Task RecordPlanAsync_does_not_notify_domain_agent_trigger()
    {
        var trigger = new RecordingDomainAgentTriggerService();
        var svc = new ArtifactCommandService(
            new ArtifactLineageService(new InMemoryStudioNodeStore()), new NoopWorkUnitService(), trigger);

        await svc.RecordPlanAsync("wu-1", "1. Do the thing");

        Assert.Empty(trigger.Notified);
    }

    [Fact]
    public async Task RecordAsync_works_unchanged_when_no_trigger_is_supplied()
    {
        // The optional ctor param must default to null without breaking existing direct-construction
        // call sites — RecordAsync should behave exactly as before when no trigger is wired up.
        var svc = new ArtifactCommandService(
            new ArtifactLineageService(new InMemoryStudioNodeStore()), new NoopWorkUnitService());

        var recorded = await svc.RecordAsync("wu-1", "Constraint", "Some finding", "Some body");

        Assert.Equal("Some finding", recorded.Title);
    }
}
