using System.Text;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

public sealed class MergeReconciliationService(
    IWorkUnitService workUnits,
    IMergeService merge,
    IArtifactLineageService artifacts,
    IFileWorkspaceService fileWorkspace) : IMergeReconciliationService
{
    public const string ConflictReportFileName = "merge-conflict-report.md";

    public async Task<MergeReconciliationResult> TryReconcileAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var parent = await workUnits.GetAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        if (parent is null)
            return new MergeReconciliationResult(MergeReconciliationOutcome.NotApplicable);

        var children = await workUnits.GetChildrenAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        if (children.Count == 0)
            return new MergeReconciliationResult(MergeReconciliationOutcome.NotApplicable);

        var parentChain = await artifacts.GetChainAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        foreach (var proposalRef in parentChain.Where(a => a.Type == ArtifactType.MergeProposal))
        {
            var existing = await merge.GetAsync(proposalRef.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (existing?.ReconciledFrom.Count > 0 &&
                existing.Status is not MergeProposalStatus.Rejected)
            {
                return new MergeReconciliationResult(
                    MergeReconciliationOutcome.AlreadyReconciled,
                    existing.ProposalId);
            }
        }

        var childProposals = new List<(WorkUnit Child, MergeProposal Proposal)>();
        foreach (var child in children)
        {
            if (child.Status is not WorkUnitStatus.Proposed and not WorkUnitStatus.Merged)
                return new MergeReconciliationResult(MergeReconciliationOutcome.WaitingForChildren);

            var chain = await artifacts.GetChainAsync(child.WorkUnitId, cancellationToken).ConfigureAwait(false);
            var proposalRef = chain.LastOrDefault(a => a.Type == ArtifactType.MergeProposal);
            if (proposalRef is null)
                return new MergeReconciliationResult(MergeReconciliationOutcome.WaitingForChildren);

            var proposal = await merge.GetAsync(proposalRef.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (proposal is null || proposal.Status is MergeProposalStatus.Superseded)
                return new MergeReconciliationResult(MergeReconciliationOutcome.WaitingForChildren);

            childProposals.Add((child, proposal));
        }

        var conflicts = await DetectOverlappingFilesAsync(
            childProposals.Select(cp => cp.Proposal).ToList(), cancellationToken).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            var report = BuildConflictReport(conflicts, childProposals);
            await fileWorkspace
                .WriteAsync(parent.BranchId, ConflictReportFileName, report, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await workUnits.UpdateStatusAsync(parent.WorkUnitId, WorkUnitStatus.Reviewing, sessionId, cancellationToken)
                    .ConfigureAwait(false);
                await workUnits.SetCurrentStageAsync(parent.WorkUnitId, PipelineStage.Review, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }

            return new MergeReconciliationResult(
                MergeReconciliationOutcome.Conflict,
                ConflictReportPath: ConflictReportFileName);
        }

        var ordered = OrderByDependencies(children, childProposals);
        var mergeBranch = $"merge/{parentWorkUnitId}";
        await fileWorkspace.InitBranchAsync(mergeBranch, "main", cancellationToken).ConfigureAwait(false);

        foreach (var (_, proposal) in ordered)
        {
            var files = proposal.FilesTouched.Count > 0
                ? proposal.FilesTouched
                : await fileWorkspace.ListAsync(proposal.SourceBranch, ct: cancellationToken).ConfigureAwait(false);

            if (files.Count > 0)
            {
                await fileWorkspace
                    .CopyFilesAsync(proposal.SourceBranch, mergeBranch, files, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var constituentIds = ordered.Select(cp => cp.Proposal.ProposalId).ToList();
        var workspaceChanges = await fileWorkspace
            .DiffAsync(mergeBranch, "main", cancellationToken)
            .ConfigureAwait(false);
        var filesTouched = ParseFilesTouched(workspaceChanges);
        if (filesTouched.Count == 0)
        {
            filesTouched = (await fileWorkspace.ListAsync(mergeBranch, ct: cancellationToken).ConfigureAwait(false)).ToList();
        }

        var reconciledId = $"MP-{Guid.NewGuid():N}";
        var reconciled = new MergeProposal(
            reconciledId,
            mergeBranch,
            "main",
            parent.Goal,
            $"Reconciled merge from {constituentIds.Count} child proposals",
            $"Combined changes from proposals: {string.Join(", ", constituentIds)}",
            null, null, null,
            MergeProposalStatus.Draft,
            WorkspaceChanges: workspaceChanges,
            DiffGeneratedAt: DateTimeOffset.UtcNow,
            WorkUnitId: parent.WorkUnitId,
            FilesTouched: filesTouched,
            ReconciledFrom: constituentIds);

        await merge.ProposeAsync(reconciled, cancellationToken).ConfigureAwait(false);
        await merge.ValidateAsync(reconciledId, cancellationToken).ConfigureAwait(false);

        await artifacts.RecordAsync(new ArtifactRef(
            reconciledId,
            ArtifactType.MergeProposal,
            parent.WorkUnitId,
            ArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            parent.WorkUnitId,
            null), cancellationToken).ConfigureAwait(false);

        foreach (var id in constituentIds)
            await merge.SupersedeAsync(id, reconciledId, cancellationToken).ConfigureAwait(false);

        await workUnits.SetCurrentStageAsync(parent.WorkUnitId, PipelineStage.Merge, cancellationToken)
            .ConfigureAwait(false);

        return new MergeReconciliationResult(
            MergeReconciliationOutcome.Reconciled,
            reconciledId,
            constituentIds);
    }

    private static List<(WorkUnit Child, MergeProposal Proposal)> OrderByDependencies(
        IReadOnlyList<WorkUnit> children,
        List<(WorkUnit Child, MergeProposal Proposal)> childProposals)
    {
        var byId = childProposals.ToDictionary(cp => cp.Child.WorkUnitId);
        var ordered = new List<(WorkUnit Child, MergeProposal Proposal)>();
        var remaining = new HashSet<string>(children.Select(c => c.WorkUnitId));

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(id => byId[id].Child.DependsOn.All(dep => !remaining.Contains(dep)))
                .ToList();
            if (ready.Count == 0)
                break;

            foreach (var id in ready.OrderBy(id => byId[id].Child.CreatedAt))
            {
                ordered.Add(byId[id]);
                remaining.Remove(id);
            }
        }

        foreach (var id in remaining.OrderBy(id => byId[id].Child.CreatedAt))
            ordered.Add(byId[id]);

        return ordered;
    }

    // Two passes: the original whole-file FilesTouched intersection as a cheap filter (no
    // workspace reads for the common case of disjoint file sets), then a line-range-aware
    // refinement for whatever it flags. A file only stays flagged if two proposals' actually
    // *changed* line ranges (vs. their own base/{proposalId} snapshot) intersect — two proposals
    // appending distinct, non-overlapping functions to the same file no longer conflict.
    private async Task<Dictionary<string, List<string>>> DetectOverlappingFilesAsync(
        IReadOnlyList<MergeProposal> proposals, CancellationToken cancellationToken)
    {
        var candidates = DetectTouchedFileOverlap(proposals);
        if (candidates.Count == 0)
            return candidates;

        var byId = proposals.ToDictionary(p => p.ProposalId);
        var refined = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (file, proposalIds) in candidates)
        {
            var ranges = new List<(string ProposalId, List<(int Start, int End)> Ranges)>();
            foreach (var proposalId in proposalIds)
            {
                var proposal = byId[proposalId];
                var before = await fileWorkspace
                    .ReadAsync($"base/{proposalId}", file, cancellationToken).ConfigureAwait(false);
                var after = await fileWorkspace
                    .ReadAsync(proposal.SourceBranch, file, cancellationToken).ConfigureAwait(false);

                var hunks = LineDiffer.Diff(before, after, contextLines: 0);
                var fileRanges = hunks
                    .Select(h => (h.BeforeStart, h.BeforeStart + Math.Max(h.BeforeCount, 1) - 1))
                    .ToList();
                ranges.Add((proposalId, fileRanges));
            }

            var overlapping = new HashSet<string>();
            for (var i = 0; i < ranges.Count; i++)
            {
                for (var j = i + 1; j < ranges.Count; j++)
                {
                    if (!RangesOverlap(ranges[i].Ranges, ranges[j].Ranges))
                        continue;
                    overlapping.Add(ranges[i].ProposalId);
                    overlapping.Add(ranges[j].ProposalId);
                }
            }

            if (overlapping.Count > 1)
                refined[file] = overlapping.ToList();
        }

        return refined;
    }

    private static bool RangesOverlap(List<(int Start, int End)> a, List<(int Start, int End)> b)
    {
        foreach (var ra in a)
        {
            foreach (var rb in b)
            {
                if (ra.Start <= rb.End && rb.Start <= ra.End)
                    return true;
            }
        }

        return false;
    }

    private static Dictionary<string, List<string>> DetectTouchedFileOverlap(IReadOnlyList<MergeProposal> proposals)
    {
        var fileToProposals = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var proposal in proposals)
        {
            foreach (var file in proposal.FilesTouched)
            {
                if (!fileToProposals.TryGetValue(file, out var list))
                    fileToProposals[file] = list = [];
                list.Add(proposal.ProposalId);
            }
        }

        return fileToProposals
            .Where(kv => kv.Value.Distinct().Count() > 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToList());
    }

    private static string BuildConflictReport(
        Dictionary<string, List<string>> conflicts,
        List<(WorkUnit Child, MergeProposal Proposal)> childProposals)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Merge conflict report");
        sb.AppendLine();
        sb.AppendLine("The merger detected overlapping file changes across child proposals.");
        sb.AppendLine("Resolve manually or refine the plan and re-run affected slices.");
        sb.AppendLine();
        foreach (var (file, proposalIds) in conflicts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"## {file}");
            sb.AppendLine($"Conflicting proposals: {string.Join(", ", proposalIds)}");
            foreach (var pid in proposalIds)
            {
                var child = childProposals.First(cp => cp.Proposal.ProposalId == pid);
                sb.AppendLine($"- {pid} (work unit {child.Child.WorkUnitId}, goal: {child.Child.Goal})");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static IReadOnlyList<string> ParseFilesTouched(string? workspaceChanges)
    {
        if (string.IsNullOrEmpty(workspaceChanges))
            return [];

        var files = new List<string>();
        foreach (var rawLine in workspaceChanges.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var file =
                line.StartsWith("+++ ADDED: ", StringComparison.Ordinal)   ? line["+++ ADDED: ".Length..] :
                line.StartsWith("~~~ MODIFIED: ", StringComparison.Ordinal) ? line["~~~ MODIFIED: ".Length..] :
                line.StartsWith("--- DELETED: ", StringComparison.Ordinal) ? line["--- DELETED: ".Length..] :
                null;
            if (file is not null)
                files.Add(file);
        }

        return files;
    }
}
