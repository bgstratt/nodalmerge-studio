using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

public sealed class InMemoryMergeService : IMergeService
{
    private readonly ConcurrentDictionary<string, MergeProposal> _proposals = new();

    public Task<MergeProposal> ProposeAsync(MergeProposal proposal, CancellationToken cancellationToken = default)
    {
        _proposals[proposal.ProposalId] = proposal with { Status = MergeProposalStatus.Draft };
        return Task.FromResult(_proposals[proposal.ProposalId]);
    }

    public Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = GetRequired(proposalId);
        var updated = proposal with { Status = MergeProposalStatus.ReadyForReview };
        _proposals[proposalId] = updated;
        return Task.FromResult(updated);
    }

    public Task<MergeProposal> ReviewAsync(
        string proposalId,
        MergeProposalStatus decision,
        CancellationToken cancellationToken = default)
    {
        var proposal = GetRequired(proposalId);
        if (!MergeProposalTransitions.CanTransition(proposal.Status, decision))
        {
            throw new InvalidOperationException($"Cannot transition merge proposal from {proposal.Status} to {decision}.");
        }

        if (proposal.Status != MergeProposalStatus.ReadyForReview)
        {
            throw new InvalidOperationException("Merge proposals must be in ReadyForReview before human review.");
        }

        var updated = proposal with { Status = decision };
        _proposals[proposalId] = updated;
        return Task.FromResult(updated);
    }

    public Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = GetRequired(proposalId);
        if (proposal.Status != MergeProposalStatus.Approved)
        {
            throw new InvalidOperationException("Only approved proposals can be merged.");
        }

        var updated = proposal with { Status = MergeProposalStatus.Merged };
        _proposals[proposalId] = updated;
        return Task.FromResult(updated);
    }

    private MergeProposal GetRequired(string proposalId)
    {
        if (!_proposals.TryGetValue(proposalId, out var proposal))
        {
            throw new KeyNotFoundException($"Merge proposal '{proposalId}' was not found.");
        }

        return proposal;
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioMerge(this IServiceCollection services)
    {
        services.AddSingleton<IMergeService, InMemoryMergeService>();
        return services;
    }
}
