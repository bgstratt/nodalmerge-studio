using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;

namespace NodalMerge.Studio.Contracts.Tests;

/// <summary>
/// Slice 12b — Projection Diffing. Covers ProjectionDelta.Compute's diff logic in isolation,
/// independent of the orchestrator loop / integration harness.
/// </summary>
public class ProjectionDeltaTests
{
    private const string WorkUnitId = "wu-1";

    private static ArtifactRef Artifact(string id, ArtifactType type, ArtifactStatus status) =>
        new(id, type, null, status, DateTimeOffset.UtcNow, WorkUnitId, null);

    private static AgentWorkspaceProjectionPayload Payload(params ArtifactRef[] artifacts) =>
        new(AgentId: null, WorkUnitId, new ArtifactChain(artifacts), InheritedConstraints: []);

    [Fact]
    public void Empty_previous_treats_every_current_artifact_as_added()
    {
        var goal = Artifact("a-1", ArtifactType.Goal, ArtifactStatus.Active);
        var previous = Payload();
        var current = Payload(goal);

        var delta = ProjectionDelta.Compute(WorkUnitId, previous, current);

        Assert.Equal([goal], delta.AddedArtifacts);
        Assert.Empty(delta.RemovedArtifacts);
        Assert.Empty(delta.StatusChangedArtifacts);
        Assert.True(delta.AnyChange);
    }

    [Fact]
    public void Identical_snapshots_report_no_change()
    {
        var goal = Artifact("a-1", ArtifactType.Goal, ArtifactStatus.Active);
        var previous = Payload(goal);
        var current = Payload(goal);

        var delta = ProjectionDelta.Compute(WorkUnitId, previous, current);

        Assert.Empty(delta.AddedArtifacts);
        Assert.Empty(delta.RemovedArtifacts);
        Assert.Empty(delta.StatusChangedArtifacts);
        Assert.Empty(delta.CompletedTaskIds);
        Assert.False(delta.AnyChange);
    }

    [Fact]
    public void Status_change_to_terminal_is_both_status_changed_and_removed()
    {
        var proposal = Artifact("mp-1", ArtifactType.MergeProposal, ArtifactStatus.Active);
        var previous = Payload(proposal);
        var current = Payload(proposal with { Status = ArtifactStatus.Approved });

        var delta = ProjectionDelta.Compute(WorkUnitId, previous, current);

        Assert.Equal(["mp-1"], delta.StatusChangedArtifacts.Select(a => a.ArtifactId));
        Assert.Equal(["mp-1"], delta.RemovedArtifacts.Select(a => a.ArtifactId));
        Assert.True(delta.AnyChange);
    }

    [Fact]
    public void Task_artifact_newly_applied_is_a_completed_task()
    {
        var task = Artifact("t-1", ArtifactType.Task, ArtifactStatus.Active);
        var previous = Payload(task);
        var current = Payload(task with { Status = ArtifactStatus.Applied });

        var delta = ProjectionDelta.Compute(WorkUnitId, previous, current);

        Assert.Equal(["t-1"], delta.CompletedTaskIds);
    }

    [Fact]
    public void Task_added_directly_as_applied_is_a_completed_task()
    {
        var task = Artifact("t-2", ArtifactType.Task, ArtifactStatus.Applied);
        var previous = Payload();
        var current = Payload(task);

        var delta = ProjectionDelta.Compute(WorkUnitId, previous, current);

        Assert.Equal(["t-2"], delta.CompletedTaskIds);
        Assert.Contains(task, delta.AddedArtifacts);
    }
}
