using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

public sealed class ProposalReviewService(
    IMergeService merge,
    IFileWorkspaceService fileWorkspace) : IProposalReviewService
{
    public async Task<IReadOnlyList<ProposalFileChange>> GetFileChangesAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        var proposal = await merge.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null)
            return [];

        var baseBranch = $"base/{proposalId}";
        var afterBranch = proposal.SourceBranch;
        var changes = new List<ProposalFileChange>();

        var filesToScan = proposal.FilesTouched.Count > 0
            ? proposal.FilesTouched
            : await fileWorkspace.ListAsync(proposal.SourceBranch, ct: cancellationToken).ConfigureAwait(false);

        foreach (var path in filesToScan.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var before = await fileWorkspace.ReadAsync(baseBranch, path, cancellationToken).ConfigureAwait(false);
            var after  = await fileWorkspace.ReadAsync(afterBranch, path, cancellationToken).ConfigureAwait(false);

            var kind = (before, after) switch
            {
                (null, not null) => FileChangeKind.Added,
                (not null, null) => FileChangeKind.Deleted,
                (not null, not null) when before != after => FileChangeKind.Modified,
                (not null, not null) => (FileChangeKind?)null,
                _ => (FileChangeKind?)null,
            };

            if (kind is not null)
            {
                var hunks = LineDiffer.Diff(before, after);
                changes.Add(new ProposalFileChange(path, kind.Value, before, after, hunks));
            }
        }

        return changes;
    }
}
