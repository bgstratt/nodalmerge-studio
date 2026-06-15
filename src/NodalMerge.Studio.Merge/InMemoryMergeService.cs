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

    public InMemoryMergeService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
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

        var updated = proposal with { Status = MergeProposalStatus.Merged };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);
        return updated;
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
