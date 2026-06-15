using NodalMerge.Studio.Contracts.Domain;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;
using TaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Core.Tests;

public class WorkUnitTransitionTests
{
    [Theory]
    [InlineData(WorkUnitStatus.Created, WorkUnitStatus.Active, true)]
    [InlineData(WorkUnitStatus.Active, WorkUnitStatus.Completed, true)]
    [InlineData(WorkUnitStatus.Completed, WorkUnitStatus.Active, false)]
    public void CanTransition_respects_lifecycle(WorkUnitStatus from, WorkUnitStatus to, bool expected)
    {
        Assert.Equal(expected, WorkUnitTransitions.CanTransition(from, to));
    }
}

public class MergeProposalTransitionTests
{
    [Fact]
    public void Human_review_path_is_enforced()
    {
        Assert.True(MergeProposalTransitions.CanTransition(
            MergeProposalStatus.Draft,
            MergeProposalStatus.ReadyForReview));

        Assert.True(MergeProposalTransitions.CanTransition(
            MergeProposalStatus.ReadyForReview,
            MergeProposalStatus.Approved));

        Assert.False(MergeProposalTransitions.CanTransition(
            MergeProposalStatus.Draft,
            MergeProposalStatus.Merged));
    }
}

public class StudioTaskTests
{
    [Fact]
    public void Task_does_not_include_dag_node_references()
    {
        var task = new StudioTask(
            "task-1",
            "work-1",
            "Title",
            "Description",
            StudioTaskStatus.Open,
            "agent-1",
            1);

        Assert.Equal("work-1", task.WorkUnitId);
        Assert.DoesNotContain("node", task.GetType().GetProperties().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
    }
}

public class TaskTransitionTests
{
    [Theory]
    [InlineData(TaskStatus.Open, TaskStatus.InProgress, true)]
    [InlineData(TaskStatus.InProgress, TaskStatus.Blocked, true)]
    [InlineData(TaskStatus.Blocked, TaskStatus.InProgress, true)]
    [InlineData(TaskStatus.InProgress, TaskStatus.Completed, true)]
    [InlineData(TaskStatus.Open, TaskStatus.Cancelled, true)]
    [InlineData(TaskStatus.InProgress, TaskStatus.Cancelled, true)]
    [InlineData(TaskStatus.Blocked, TaskStatus.Cancelled, true)]
    [InlineData(TaskStatus.Open, TaskStatus.Completed, false)]
    [InlineData(TaskStatus.Open, TaskStatus.Blocked, false)]
    [InlineData(TaskStatus.Completed, TaskStatus.Open, false)]
    [InlineData(TaskStatus.Completed, TaskStatus.Cancelled, false)]
    [InlineData(TaskStatus.Cancelled, TaskStatus.Open, false)]
    public void CanTransition_enforces_task_lifecycle(TaskStatus from, TaskStatus to, bool expected) =>
        Assert.Equal(expected, TaskTransitions.CanTransition(from, to));
}
