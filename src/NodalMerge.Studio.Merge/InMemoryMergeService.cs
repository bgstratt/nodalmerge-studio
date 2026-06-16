using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge;

public sealed class InMemoryMergeService : IMergeService
{
    private readonly ConcurrentDictionary<string, MergeProposal> _proposals = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IFileWorkspaceService _fileWorkspace;
    private readonly WorkspaceOptions _workspaceOptions;
    private readonly IExecutionEventStream _events;

    public InMemoryMergeService(
        IStudioNodeStore nodeStore,
        IFileWorkspaceService fileWorkspace,
        WorkspaceOptions workspaceOptions,
        IExecutionEventStream events)
    {
        _nodeStore        = nodeStore;
        _fileWorkspace    = fileWorkspace;
        _workspaceOptions = workspaceOptions;
        _events           = events;
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

        if (proposal.SessionId is not null)
        {
            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeApplied,
                new MergeAppliedPayload(proposalId, proposal.TargetBranch, string.Empty),
                ct: cancellationToken).ConfigureAwait(false);
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
        services.AddSingleton<IMergeService, InMemoryMergeService>();
        return services;
    }
}
