using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge;

public sealed class InMemoryMergeService : IMergeService, IRehydratable
{
    private readonly ConcurrentDictionary<string, MergeProposal> _proposals = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IFileWorkspaceService _fileWorkspace;
    private readonly WorkspaceOptions _workspaceOptions;
    private readonly IExecutionEventStream _events;
    private readonly IArtifactLineageService _artifacts;
    private readonly IServiceProvider? _serviceProvider;

    // IWorkUnitService is resolved lazily (via IServiceProvider) rather than constructor-injected:
    // its production implementation (InMemoryWorkUnitService) already depends on IMergeService
    // (this service), so a direct dependency here would be a circular constructor graph — same
    // pattern used by WorkSchedulerService for the same interface.
    public InMemoryMergeService(
        IStudioNodeStore nodeStore,
        IFileWorkspaceService fileWorkspace,
        WorkspaceOptions workspaceOptions,
        IExecutionEventStream events,
        IArtifactLineageService artifacts,
        IServiceProvider? serviceProvider = null)
    {
        _nodeStore        = nodeStore;
        _fileWorkspace    = fileWorkspace;
        _workspaceOptions = workspaceOptions;
        _events           = events;
        _artifacts        = artifacts;
        _serviceProvider  = serviceProvider;
    }

    public async Task<MergeProposal> ProposeAsync(MergeProposal proposal, CancellationToken cancellationToken = default)
    {
        var stored = proposal with { Status = MergeProposalStatus.Draft };
        _proposals[proposal.ProposalId] = stored;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            stored.ProposalId,
            JsonSerializer.Serialize(stored),
            cancellationToken).ConfigureAwait(false);

        // Snapshot the target branch's current content as this proposal's base state (S0, 10f).
        // Taken now rather than at apply time so it stays correct regardless of whether the
        // proposal later gets approved, rejected, or applied — none of those touch this copy.
        await _fileWorkspace.InitBranchAsync(
            $"base/{proposal.ProposalId}", proposal.TargetBranch, cancellationToken).ConfigureAwait(false);

        return stored;
    }

    public Task<MergeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        _proposals.TryGetValue(proposalId, out var proposal);
        return Task.FromResult(proposal);
    }

    public async Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = GetRequired(proposalId);

        if (!MergeProposalTransitions.CanTransition(proposal.Status, MergeProposalStatus.ReadyForReview))
        {
            throw new InvalidOperationException(
                $"Cannot validate proposal '{proposalId}': status {proposal.Status} cannot transition to ReadyForReview.");
        }

        var updated = proposal with { Status = MergeProposalStatus.ReadyForReview };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        if (proposal.SessionId is not null)
        {
            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeProposalStatusChanged,
                new MergeProposalStatusChangedPayload(proposalId, proposal.Status, updated.Status),
                ct: cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task<MergeProposal> ReviewAsync(
        string proposalId,
        MergeProposalStatus decision,
        CancellationToken cancellationToken = default)
    {
        var proposal = GetRequired(proposalId);

        if (!MergeProposalTransitions.CanTransition(proposal.Status, decision))
        {
            throw new InvalidOperationException(
                $"Cannot transition proposal '{proposalId}' from {proposal.Status} to {decision}. " +
                $"Proposals must be in ReadyForReview before human review.");
        }

        var updated = proposal with { Status = decision };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        if (proposal.WorkUnitId is not null)
        {
            var artifactStatus = decision == MergeProposalStatus.Approved
                ? ArtifactStatus.Approved
                : ArtifactStatus.Rejected;
            await _artifacts.UpdateStatusAsync(proposalId, artifactStatus, cancellationToken).ConfigureAwait(false);
        }

        if (proposal.SessionId is not null)
        {
            if (decision == MergeProposalStatus.Approved)
            {
                var approvedEv = await _events.AppendAsync(
                    proposal.SessionId,
                    proposal.WorkUnitId,
                    ExecutionEventKind.ProposalApproved,
                    new ProposalApprovedPayload(proposalId, "user"),
                    ct: cancellationToken).ConfigureAwait(false);

                await _events.AppendAsync(
                    proposal.SessionId,
                    proposal.WorkUnitId,
                    ExecutionEventKind.MergeApproved,
                    new MergeApprovedPayload(proposalId, "user", DateTimeOffset.UtcNow),
                    causedByEventId: approvedEv.EventId,
                    ct: cancellationToken).ConfigureAwait(false);
            }
            else if (decision == MergeProposalStatus.Rejected)
            {
                await _events.AppendAsync(
                    proposal.SessionId,
                    proposal.WorkUnitId,
                    ExecutionEventKind.ProposalRejected,
                    new ProposalRejectedPayload(proposalId, "user", null),
                    ct: cancellationToken).ConfigureAwait(false);
            }

            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeProposalStatusChanged,
                new MergeProposalStatusChangedPayload(proposalId, proposal.Status, updated.Status),
                ct: cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task<MergeProposal> AutomatedReviewAsync(
        string proposalId,
        MergeProposalStatus decision,
        string verificationResults,
        string? reviewerAgentId = null,
        CancellationToken cancellationToken = default)
    {
        if (decision is not (MergeProposalStatus.Approved or MergeProposalStatus.Rejected))
        {
            throw new ArgumentException(
                "Automated review decision must be Approved or Rejected.",
                nameof(decision));
        }

        if (string.IsNullOrWhiteSpace(verificationResults))
        {
            throw new ArgumentException(
                "verificationResults is required for automated review.",
                nameof(verificationResults));
        }

        var proposal = GetRequired(proposalId);

        if (proposal.Status == MergeProposalStatus.ReadyForReview)
        {
            if (!MergeProposalTransitions.CanTransition(proposal.Status, MergeProposalStatus.UnderReview))
            {
                throw new InvalidOperationException(
                    $"Cannot begin automated review for proposal '{proposalId}' in status {proposal.Status}.");
            }

            proposal = proposal with { Status = MergeProposalStatus.UnderReview };
            _proposals[proposalId] = proposal;
        }
        else if (proposal.Status != MergeProposalStatus.UnderReview)
        {
            throw new InvalidOperationException(
                $"Cannot complete automated review for proposal '{proposalId}' in status {proposal.Status}. " +
                "Proposal must be ReadyForReview or UnderReview.");
        }

        var nextStatus = decision == MergeProposalStatus.Approved
            ? MergeProposalStatus.ReadyForReview
            : MergeProposalStatus.Rejected;

        if (!MergeProposalTransitions.CanTransition(proposal.Status, nextStatus))
        {
            throw new InvalidOperationException(
                $"Cannot transition proposal '{proposalId}' from {proposal.Status} to {nextStatus}.");
        }

        var updated = proposal with
        {
            Status = nextStatus,
            VerificationResults = verificationResults,
            AgentId = reviewerAgentId ?? proposal.AgentId,
        };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        if (proposal.SessionId is not null)
        {
            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeProposalStatusChanged,
                new MergeProposalStatusChangedPayload(proposalId, proposal.Status, updated.Status),
                ct: cancellationToken).ConfigureAwait(false);

            if (decision == MergeProposalStatus.Rejected)
            {
                await _events.AppendAsync(
                    proposal.SessionId,
                    proposal.WorkUnitId,
                    ExecutionEventKind.ProposalRejected,
                    new ProposalRejectedPayload(proposalId, reviewerAgentId ?? "reviewer", verificationResults),
                    ct: cancellationToken).ConfigureAwait(false);
            }
        }

        return updated;
    }

    public async Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = GetRequired(proposalId);

        if (!MergeProposalTransitions.CanTransition(proposal.Status, MergeProposalStatus.Merged))
        {
            throw new InvalidOperationException(
                $"Cannot apply proposal '{proposalId}': only Approved proposals can be merged (current: {proposal.Status}).");
        }

        // Copy workspace files: source branch → target branch
        await _fileWorkspace.ApplyBranchAsync(proposal.SourceBranch, proposal.TargetBranch, cancellationToken)
            .ConfigureAwait(false);

        // Write changed files back to disk whenever a repository path is configured
        if (!string.IsNullOrWhiteSpace(_workspaceOptions.SeedRepositoryPath))
        {
            await WriteBackToRepositoryAsync(proposal.SourceBranch, cancellationToken).ConfigureAwait(false);
        }

        var updated = proposal with { Status = MergeProposalStatus.Merged };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        if (proposal.WorkUnitId is not null)
        {
            await _artifacts.UpdateStatusAsync(proposalId, ArtifactStatus.Applied, cancellationToken).ConfigureAwait(false);
            await _artifacts.RecordAsync(new ArtifactRef(
                $"MR-{Guid.NewGuid():N}",
                ArtifactType.MergeResult,
                proposalId,
                ArtifactStatus.Active,
                DateTimeOffset.UtcNow,
                proposal.WorkUnitId,
                proposal.AgentId), cancellationToken).ConfigureAwait(false);
        }

        if (proposal.SessionId is not null)
        {
            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeApplied,
                new MergeAppliedPayload(proposalId, proposal.TargetBranch, string.Empty),
                ct: cancellationToken).ConfigureAwait(false);

            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeProposalStatusChanged,
                new MergeProposalStatusChangedPayload(proposalId, proposal.Status, updated.Status),
                ct: cancellationToken).ConfigureAwait(false);
        }

        if (proposal.WorkUnitId is not null)
        {
            // Best-effort — a proposal applying means the owning work unit's pipeline is done.
            // Not worth failing the merge apply over an illegal transition (e.g. the legacy
            // direct-spawn path never reaches WorkUnitStatus.Proposed).
            var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
            if (workUnits is not null)
            {
                try
                {
                    await workUnits.UpdateStatusAsync(
                        proposal.WorkUnitId, WorkUnitStatus.Merged, proposal.SessionId, cancellationToken).ConfigureAwait(false);
                    await workUnits.SetCurrentStageAsync(proposal.WorkUnitId, null, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException) { }
                catch (KeyNotFoundException) { }
            }
        }

        return updated;
    }

    private async Task WriteBackToRepositoryAsync(string sourceBranchId, CancellationToken ct)
    {
        var repoPath = _workspaceOptions.SeedRepositoryPath!;
        var files = await _fileWorkspace.ListAsync(sourceBranchId, ct: ct).ConfigureAwait(false);
        foreach (var relativePath in files)
        {
            var content = await _fileWorkspace.ReadAsync(sourceBranchId, relativePath, ct).ConfigureAwait(false);
            if (content is null) continue;
            var dest = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(dest)!;
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);
            await File.WriteAllTextAsync(dest, content, ct).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<MergeProposal>> ListAsync(string? sourceBranch = null, CancellationToken cancellationToken = default)
    {
        var items = _proposals.Values
            .Where(p => sourceBranch is null || p.SourceBranch == sourceBranch)
            .ToList();
        return Task.FromResult<IReadOnlyList<MergeProposal>>(items);
    }

    public async Task<MergeProposal> SupersedeAsync(
        string proposalId,
        string supersededByProposalId,
        CancellationToken cancellationToken = default)
    {
        var proposal = GetRequired(proposalId);

        if (!MergeProposalTransitions.CanTransition(proposal.Status, MergeProposalStatus.Superseded))
        {
            throw new InvalidOperationException(
                $"Cannot supersede proposal '{proposalId}': status {proposal.Status} cannot transition to Superseded.");
        }

        var updated = proposal with
        {
            Status = MergeProposalStatus.Superseded,
            SupersededBy = supersededByProposalId,
        };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        if (proposal.WorkUnitId is not null)
            await _artifacts.UpdateStatusAsync(proposalId, ArtifactStatus.Superseded, cancellationToken).ConfigureAwait(false);

        if (proposal.SessionId is not null)
        {
            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeProposalStatusChanged,
                new MergeProposalStatusChangedPayload(proposalId, proposal.Status, updated.Status),
                ct: cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.MergeProposalV1, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (entityId, payloadJson) in records)
        {
            var proposal = JsonSerializer.Deserialize<MergeProposal>(payloadJson);
            if (proposal is not null)
                _proposals[entityId] = proposal;
        }
    }

    private MergeProposal GetRequired(string proposalId)
    {
        if (!_proposals.TryGetValue(proposalId, out var proposal))
            throw new KeyNotFoundException($"Merge proposal '{proposalId}' was not found.");
        return proposal;
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioMerge(this IServiceCollection services)
    {
        // IStudioNodeStore must be registered before this (AddStudioStorage)
        services.AddSingleton<InMemoryMergeService>();
        services.AddSingleton<IMergeService>(sp => sp.GetRequiredService<InMemoryMergeService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryMergeService>());
        services.AddSingleton<IProposalReviewService, ProposalReviewService>();
        services.AddSingleton<IMergeReconciliationService, MergeReconciliationService>();
        services.AddSingleton<IAutomatedReviewGateService, AutomatedReviewGateService>();
        return services;
    }
}
