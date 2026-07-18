using System.Text;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Layer 2 L2.4 (plans/room-persistence-bloat.md) — resolving a proposal's diff: inline when present
/// (legacy / no-CAS), else pulled from the CAS blob named by WorkspaceChangesBlobHash.
/// </summary>
[Trait("Category", "Integration")]
public class MergeDiffResolverTests
{
    private static MergeProposal Proposal(string? inline, string? hash) =>
        new("MP-1", "src", "main", "goal", "summary", "desc", null, null, null, MergeProposalStatus.Draft,
            WorkspaceChanges: inline, WorkspaceChangesBlobHash: hash);

    [Fact]
    public async Task ResolveAsync_returns_inline_when_present()
    {
        var resolver = new MergeDiffResolverService(new InMemoryBlobStoreProvider());
        Assert.Equal("inline diff", await resolver.ResolveAsync(Proposal("inline diff", null)));
    }

    [Fact]
    public async Task ResolveAsync_pulls_blob_when_inline_absent()
    {
        var blobs = new InMemoryBlobStoreProvider();
        var bytes = Encoding.UTF8.GetBytes("pulled diff");
        var hash = BlobHasher.ComputeHash(bytes);
        await blobs.PutBlobAsync(hash, bytes, "text/x-diff");

        var resolver = new MergeDiffResolverService(blobs);
        Assert.Equal("pulled diff", await resolver.ResolveAsync(Proposal(null, hash)));
    }

    [Fact]
    public async Task ResolveAsync_returns_null_when_no_inline_and_no_blob_store()
    {
        var resolver = new MergeDiffResolverService(blobStore: null);
        Assert.Null(await resolver.ResolveAsync(Proposal(null, "somehash")));
    }
}
