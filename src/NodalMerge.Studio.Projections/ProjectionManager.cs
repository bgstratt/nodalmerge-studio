using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;
using TaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Projections;

public sealed class ProjectionManager : IProjectionManager
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly IWorkUnitService _workUnits;
    private readonly ITaskService _tasks;
    private readonly IMergeService _merges;
    private readonly IAgentRuntimeService _agentRuntime;
    private readonly IArtifactRefService _artifactRefs;

    public ProjectionManager(
        IWorkUnitService workUnits,
        ITaskService tasks,
        IMergeService merges,
        IAgentRuntimeService agentRuntime,
        IArtifactRefService artifactRefs)
    {
        _workUnits    = workUnits;
        _tasks        = tasks;
        _merges       = merges;
        _agentRuntime = agentRuntime;
        _artifactRefs = artifactRefs;
    }

    public async Task<ProjectionResult> GetAsync(ProjectionRequest request, CancellationToken cancellationToken = default)
    {
        var dataJson = request.Type switch
        {
            ProjectionType.WorkUnit => await BuildWorkUnitAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.Task => await BuildTaskAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.MergeProposal => await BuildMergeProposalAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.ExecutionSnapshot => await BuildExecutionSnapshotAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.AuthoritativeState => await BuildAuthoritativeStateAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectionType.AgentWorkspace => await BuildAgentWorkspaceAsync(request, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Type, "Unknown projection type.")
        };

        return new ProjectionResult(request.Type, request.Level, dataJson, DateTimeOffset.UtcNow);
    }

    public Task<ProjectionResult> CompactAsync(
        ProjectionType type,
        ProjectionLevel targetLevel,
        CancellationToken cancellationToken = default) =>
        GetAsync(new ProjectionRequest(type, targetLevel), cancellationToken);

    private async Task<string> BuildWorkUnitAsync(ProjectionRequest request, CancellationToken ct)
    {
        if (request.WorkUnitId is null)
        {
            var all = await _workUnits.ListAsync(request.BranchId, ct).ConfigureAwait(false);
            return Serialize(new { workUnitIds = all.Select(w => w.WorkUnitId).ToList(), count = all.Count });
        }

        var workUnit = await _workUnits.GetAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        if (workUnit is null)
            return Serialize(new { error = $"WorkUnit '{request.WorkUnitId}' not found." });

        var allTasks = await _tasks.ListAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        var activeTasks = allTasks
            .Where(t => t.Status is TaskStatus.Open or TaskStatus.InProgress)
            .Select(t => t.TaskId)
            .ToList();

        return request.Level switch
        {
            ProjectionLevel.Emergency => Serialize(new
            {
                workUnitId = workUnit.WorkUnitId,
                status = workUnit.Status.ToString(),
                activeTaskCount = activeTasks.Count
            }),
            ProjectionLevel.Compact => Serialize(new
            {
                workUnitId = workUnit.WorkUnitId,
                goal = workUnit.Goal,
                status = workUnit.Status.ToString(),
                activeTasks,
                assignedAgent = workUnit.AssignedAgent
            }),
            _ => Serialize(new WorkUnitProjectionPayload(
                workUnit.WorkUnitId,
                workUnit.Goal,
                workUnit.BranchId,
                workUnit.Status.ToString(),
                activeTasks,
                [],
                workUnit.SuccessCriteria,
                workUnit.AssignedAgent is not null ? [workUnit.AssignedAgent] : []))
        };
    }

    private async Task<string> BuildTaskAsync(ProjectionRequest request, CancellationToken ct)
    {
        var allTasks = await _tasks.ListAsync(request.WorkUnitId, ct).ConfigureAwait(false);

        var open = allTasks.Where(t => t.Status == TaskStatus.Open).Select(t => t.TaskId).ToList();
        var inProgress = allTasks.Where(t => t.Status == TaskStatus.InProgress).Select(t => t.TaskId).ToList();
        var blocked = allTasks.Where(t => t.Status == TaskStatus.Blocked).Select(t => t.TaskId).ToList();
        var completed = allTasks.Where(t => t.Status == TaskStatus.Completed).Select(t => t.TaskId).ToList();
        var assignments = allTasks
            .Where(t => t.Assignee is not null)
            .ToDictionary(t => t.TaskId, t => t.Assignee!);
        var active = open.Concat(inProgress).ToList();

        return request.Level switch
        {
            ProjectionLevel.Emergency => Serialize(new
            {
                activeCount = active.Count,
                nextTask = active.FirstOrDefault()
            }),
            ProjectionLevel.Compact => Serialize(new
            {
                active,
                blockedCount = blocked.Count,
                completedCount = completed.Count
            }),
            _ => Serialize(new TaskProjectionPayload(active, blocked, completed, assignments))
        };
    }

    private async Task<string> BuildMergeProposalAsync(ProjectionRequest request, CancellationToken ct)
    {
        var proposals = await _merges.ListAsync(request.BranchId, ct).ConfigureAwait(false);
        var pending = proposals
            .Where(p => p.Status is MergeProposalStatus.Draft or MergeProposalStatus.ReadyForReview)
            .ToList();
        var reviewStatus = proposals.ToDictionary(p => p.ProposalId, p => p.Status.ToString());
        var verificationResults = proposals
            .Where(p => p.VerificationResults is not null)
            .Select(p => p.VerificationResults!)
            .ToList();

        return request.Level switch
        {
            ProjectionLevel.Emergency => Serialize(new { pendingCount = pending.Count }),
            ProjectionLevel.Compact => Serialize(new
            {
                pending = pending.Select(p => p.ProposalId).ToList(),
                reviewStatus
            }),
            _ => Serialize(new MergeProposalProjectionPayload(
                pending.Select(p => p.ProposalId).ToList(),
                reviewStatus,
                verificationResults))
        };
    }

    private async Task<string> BuildExecutionSnapshotAsync(ProjectionRequest request, CancellationToken ct)
    {
        if (request.AgentId is null || request.WorkUnitId is null)
            return Serialize(new { error = "ExecutionSnapshot requires agentId and workUnitId." });

        var snapshot = await _agentRuntime.GetSnapshotAsync(request.AgentId, request.WorkUnitId, ct).ConfigureAwait(false);

        return request.Level switch
        {
            ProjectionLevel.Emergency => Serialize(new
            {
                agentId = snapshot.AgentId,
                failureCount = snapshot.FailureCount,
                nextSuggestedAction = snapshot.NextSuggestedAction
            }),
            ProjectionLevel.Compact => Serialize(new
            {
                agentId = snapshot.AgentId,
                currentGoal = snapshot.CurrentGoal,
                failureCount = snapshot.FailureCount,
                recentActions = snapshot.RecentActions.TakeLast(3).ToList()
            }),
            _ => Serialize(new ExecutionSnapshotProjectionPayload(
                snapshot.AgentId,
                snapshot.CurrentGoal,
                snapshot.RecentActions.ToList(),
                snapshot.Constraints.ToList()))
        };
    }

    private async Task<string> BuildAuthoritativeStateAsync(ProjectionRequest request, CancellationToken ct)
    {
        var branchId = request.BranchId ?? string.Empty;
        var workUnits = await _workUnits.ListAsync(branchId.Length > 0 ? branchId : null, ct).ConfigureAwait(false);
        var mergedState = workUnits
            .Where(w => w.Status == WorkUnitStatus.Completed)
            .ToDictionary(w => w.WorkUnitId, w => w.Goal);

        return request.Level switch
        {
            ProjectionLevel.Emergency => Serialize(new { branchId, mergedWorkUnitCount = mergedState.Count }),
            ProjectionLevel.Compact => Serialize(new { branchId, mergedWorkUnits = mergedState.Keys.ToList() }),
            _ => Serialize(new AuthoritativeStateProjectionPayload(branchId, mergedState))
        };
    }

    private async Task<string> BuildAgentWorkspaceAsync(ProjectionRequest request, CancellationToken ct)
    {
        var workUnitId = request.WorkUnitId;
        if (workUnitId is null)
            return Serialize(new { error = "AgentWorkspace projection requires workUnitId." });

        var refs = await _artifactRefs.ListAsync(workUnitId, ct).ConfigureAwait(false);

        // Enrich MergeProposal refs with current status from the merge service.
        var enriched = new List<ArtifactRef>(refs.Count);
        foreach (var r in refs)
        {
            if (r.Type == ArtifactType.MergeProposal)
            {
                var proposal = await _merges.GetAsync(r.ArtifactId, ct).ConfigureAwait(false);
                if (proposal is not null)
                {
                    var status = proposal.Status switch
                    {
                        MergeProposalStatus.Approved => ArtifactStatus.Approved,
                        MergeProposalStatus.Rejected => ArtifactStatus.Rejected,
                        MergeProposalStatus.Merged   => ArtifactStatus.Applied,
                        _                            => ArtifactStatus.Active,
                    };
                    enriched.Add(r with { Status = status });
                    continue;
                }
            }
            enriched.Add(r);
        }

        var payload = new AgentWorkspaceProjectionPayload(
            request.AgentId,
            workUnitId,
            new ArtifactChain(enriched));

        return Serialize(payload);
    }

    private static string Serialize(object data) => JsonSerializer.Serialize(data, JsonOptions);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioProjections(this IServiceCollection services)
    {
        services.AddSingleton<IProjectionManager, ProjectionManager>();
        return services;
    }
}
