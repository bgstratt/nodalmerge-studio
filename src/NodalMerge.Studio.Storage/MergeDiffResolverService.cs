using System.Text;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// L2.4 (plans/room-persistence-bloat.md) — resolves a proposal's unified diff. InMemoryMergeService
// .ProposeAsync moves the diff bytes to the CAS content plane (WorkspaceChangesBlobHash), nulling the
// inline WorkspaceChanges, so it no longer replicates to every peer. This returns the inline text
// when a proposal still carries it (legacy proposals, or configs with no blob store) and otherwise
// pulls the blob on demand — the same "ref replicates, bytes pulled" pattern as ConversationRef.
public sealed class MergeDiffResolverService : IMergeDiffResolver
{
    private readonly IBlobStoreProvider? _blobStore;

    public MergeDiffResolverService(IBlobStoreProvider? blobStore = null) => _blobStore = blobStore;

    public async Task<string?> ResolveAsync(MergeProposal proposal, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(proposal.WorkspaceChanges))
            return proposal.WorkspaceChanges;

        if (proposal.WorkspaceChangesBlobHash is null || _blobStore is null)
            return proposal.WorkspaceChanges;

        var blob = await _blobStore.TryGetBlobAsync(proposal.WorkspaceChangesBlobHash, cancellationToken)
            .ConfigureAwait(false);
        return blob is { Found: true, Bytes: not null } ? Encoding.UTF8.GetString(blob.Bytes) : null;
    }
}
