using System.Text;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge;

public sealed class MergeReconciliationService(
    IWorkUnitService workUnits,
    IMergeService merge,
    IArtifactLineageService artifacts,
    IFileWorkspaceService fileWorkspace,
    IWorkspaceExecutionService? execution = null,
    WorkspaceOptions? options = null,
    // Optional so direct-construction test call sites keep compiling — when null, the
    // AgentApproval/Hybrid auto-apply below is simply skipped (same as today's behavior).
    IMergeCommandService? mergeCommands = null,
    // Optional for the same reason — when null, a Conflict outcome falls back to just writing the
    // report file (no TaskConflictRecord, same as before this system existed).
    ITaskConflictService? taskConflicts = null,
    // ITaskReconciliationTrigger can't be a normal constructor parameter here: it depends (via
    // IReconciliationAgentService -> IWorkUnitCommandService) back on IMergeReconciliationService
    // itself, which the DI container correctly rejects as a circular dependency. Resolve it lazily
    // through IServiceProvider instead — the exact same workaround
    // InMemoryMergeService.TryAutoTriggerTaskReconciliationAsync already uses for this identical
    // cycle.
    IServiceProvider? serviceProvider = null) : IMergeReconciliationService
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
            // Only a live reconciled proposal that actually targets "main" counts as "already
            // reconciled". Two false positives used to dead-end convergence here: (1) a Superseded
            // reconciled proposal — its replacement either sits on this same chain and matches on
            // its own merits, or doesn't exist and a fresh reconcile is exactly what's needed; and
            // (2) a task-conflict resolution proposal (ReconciledFrom is set, but it targets the
            // goal's own merge/{id} branch, not main — an intermediate artifact, not the top-level
            // workspace proposal this method exists to produce). Either one made this return
            // AlreadyReconciled while the goal still had NO proposal a human could review and
            // apply to the real repository.
            if (existing?.ReconciledFrom.Count > 0 &&
                existing.TargetBranch == "main" &&
                existing.Status is not MergeProposalStatus.Rejected and not MergeProposalStatus.Superseded)
            {
                return new MergeReconciliationResult(
                    MergeReconciliationOutcome.AlreadyReconciled,
                    existing.ProposalId);
            }
        }

        // A Cancelled child was explicitly abandoned by a human (a zombie reconciliation attempt,
        // a rejected task the user chose not to retry). It contributes nothing to the reconciled
        // result — but treating it as "waiting" blocked convergence forever: once a user cancelled
        // the stragglers, every reinvoke returned WaitingForChildren and the goal could never
        // produce the top-level proposal that gets its content into the real repository. Skip
        // cancelled children entirely; only genuinely in-flight ones block.
        var eligibleChildren = children.Where(c => c.Status is not WorkUnitStatus.Cancelled).ToList();
        if (eligibleChildren.Count == 0)
        {
            return new MergeReconciliationResult(
                MergeReconciliationOutcome.NotApplicable,
                Detail: "Every child of this goal was cancelled — nothing to reconcile.");
        }

        var childProposals = new List<(WorkUnit Child, MergeProposal Proposal)>();
        foreach (var child in eligibleChildren)
        {
            if (child.Status is not WorkUnitStatus.Proposed and not WorkUnitStatus.Merged)
            {
                return new MergeReconciliationResult(
                    MergeReconciliationOutcome.WaitingForChildren,
                    Detail: $"Child {child.WorkUnitId} is {child.Status} — waiting for it to reach Proposed or Merged.");
            }

            var chain = await artifacts.GetChainAsync(child.WorkUnitId, cancellationToken).ConfigureAwait(false);
            var proposalRef = chain.LastOrDefault(a => a.Type == ArtifactType.MergeProposal);
            if (proposalRef is null)
            {
                return new MergeReconciliationResult(
                    MergeReconciliationOutcome.WaitingForChildren,
                    Detail: $"Child {child.WorkUnitId} ({child.Status}) has no merge proposal yet.");
            }

            var proposal = await merge.GetAsync(proposalRef.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (proposal is null)
            {
                return new MergeReconciliationResult(
                    MergeReconciliationOutcome.WaitingForChildren,
                    Detail: $"Child {child.WorkUnitId}'s proposal record '{proposalRef.ArtifactId}' could not be loaded.");
            }

            if (proposal.Status is MergeProposalStatus.Superseded)
            {
                // ITaskReconciliationTrigger superseded this child's own proposal after resolving a
                // sibling-vs-sibling conflict (InMemoryMergeService.TryApplyAdditivelyAsync's
                // task-conflict branch) — follow SupersededBy to the reconciliation proposal that
                // replaced it and use that as this child's stand-in, rather than treating Superseded
                // as still-blocking. The losing and winning side of the same conflict both resolve to
                // the SAME reconciliation proposal here; deduped below before overlap detection and
                // the copy loop. Nothing else in the codebase supersedes a live sibling proposal, so
                // this branch is unreachable outside the task-reconciliation flow.
                var reconciliation = proposal.SupersededBy is { } supersededById
                    ? await merge.GetAsync(supersededById, cancellationToken).ConfigureAwait(false)
                    : null;
                if (reconciliation is not { Status: MergeProposalStatus.Approved or MergeProposalStatus.Merged })
                {
                    return new MergeReconciliationResult(
                        MergeReconciliationOutcome.WaitingForChildren,
                        Detail: $"Child {child.WorkUnitId}'s proposal {proposal.ProposalId} was superseded, " +
                            $"and the superseding reconciliation is not yet Approved/Merged " +
                            $"(currently {reconciliation?.Status.ToString() ?? "missing"}).");
                }
                proposal = reconciliation;
            }
            // A child's own proposal must have already cleared its TaskReviewPolicy gate — a human
            // (or the automated reviewer, for AgentApproval/Hybrid) explicitly approving it via
            // MergeCommandService.ReviewAsync — before its changes are folded into the batch and
            // superseded below. Otherwise a HumanRequired task's edits would get silently baked into
            // the reconciled workspace proposal without a human ever having approved that task.
            // Rejected proposals are excluded too — a rejection means the task is being retried
            // (see AutomatedReviewGateService.HandleHumanRejectionAsync), not ready to fold in.
            else if (proposal.Status is not (MergeProposalStatus.Approved or MergeProposalStatus.Merged))
            {
                return new MergeReconciliationResult(
                    MergeReconciliationOutcome.WaitingForChildren,
                    Detail: $"Child {child.WorkUnitId}'s proposal {proposal.ProposalId} is {proposal.Status} — " +
                        "it must clear its review gate (Approved or Merged) before its changes can be folded in.");
            }

            childProposals.Add((child, proposal));
        }

        // Two children whose conflict was reconciled both point at the SAME proposal here — dedupe
        // by ProposalId before overlap detection (DetectOverlappingFilesAsync.ToDictionary keys on
        // ProposalId and would throw on a duplicate). A second, order-preserving dedupe happens
        // below (orderedDistinctProposals) for the actual copy loop.
        var distinctProposalsForOverlapCheck = childProposals
            .Select(cp => cp.Proposal)
            .DistinctBy(p => p.ProposalId)
            .ToList();

        var conflicts = await DetectOverlappingFilesAsync(distinctProposalsForOverlapCheck, cancellationToken).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            var report = BuildConflictReport(conflicts, childProposals);
            await fileWorkspace
                .WriteAsync(parent.BranchId, ConflictReportFileName, report, cancellationToken)
                .ConfigureAwait(false);

            // Route through the SAME conflict-resolution machinery a fan-out sibling's apply-time
            // conflict already uses (InMemoryMergeService.cs's TaskConflictRecord branch) rather than
            // inventing a second, dead-end status flip — that older approach (parent -> Reviewing,
            // dead-letter as a fallback) had no actionable entity behind it: no Reconcile button, no
            // manual-resolution option, discoverable only via a badge on the Active Goals card.
            // TaskConflictRecord already has real REST endpoints, a real Reconciler agent, and
            // Reconcile/Resolve-Manually buttons wired end-to-end in the extension — reuse it.
            //
            // Unlike the apply-time case (a sibling has already landed, so there's a natural
            // "winner"), every child proposal here is still symmetric — none has touched
            // merge/{parentWorkUnitId} yet. Pick a deterministic base: the earliest-created proposal
            // among every proposal that appears anywhere in the conflict set. Every OTHER conflicting
            // proposal gets its own TaskConflictRecord with WinningProposalId set to the base — unlike
            // the apply-time case, WinningProposalId can never be left null here, because
            // ReconciliationAgentService.TriggerAsync requires >= 2 proposal ids.
            var conflictingProposalIds = conflicts.Values.SelectMany(ids => ids).Distinct().ToList();
            var byProposalId = childProposals.ToDictionary(cp => cp.Proposal.ProposalId, cp => cp);
            var basePair = conflictingProposalIds
                .Select(id => byProposalId[id])
                .OrderBy(cp => cp.Child.CreatedAt)
                .First();

            var createdConflictIds = new List<string>();
            if (taskConflicts is not null)
            {
                var existingOpen = await taskConflicts.GetOpenAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
                foreach (var losingId in conflictingProposalIds.Where(id => id != basePair.Proposal.ProposalId))
                {
                    if (existingOpen.Any(c => c.ProposalId == losingId))
                        continue;

                    var losingPair = byProposalId[losingId];
                    var conflictingPaths = conflicts.Where(kv => kv.Value.Contains(losingId)).Select(kv => kv.Key).ToList();

                    var record = new TaskConflictRecord(
                        $"taskconf-{Guid.NewGuid():N}",
                        parentWorkUnitId,
                        losingPair.Proposal.ProposalId,
                        losingPair.Child.WorkUnitId,
                        conflictingPaths,
                        DateTimeOffset.UtcNow,
                        WinningProposalId: basePair.Proposal.ProposalId);
                    await taskConflicts.RecordAsync(record, cancellationToken).ConfigureAwait(false);
                    createdConflictIds.Add(record.ConflictId);

                    // Best-effort, mirrors InMemoryMergeService.TryAutoTriggerTaskReconciliationAsync
                    // exactly, including its IServiceProvider indirection (see the constructor
                    // comment on serviceProvider — a direct ITaskReconciliationTrigger dependency
                    // here is circular). A trigger failure must never block returning the Conflict
                    // outcome the report/records above already captured.
                    if (losingPair.Child.TaskReviewPolicy == ReviewPolicy.AgentApproval
                        && basePair.Child.TaskReviewPolicy == ReviewPolicy.AgentApproval
                        && serviceProvider?.GetService(typeof(ITaskReconciliationTrigger)) is ITaskReconciliationTrigger trigger)
                    {
                        try
                        {
                            await trigger.TryTriggerAsync(record.ConflictId, ct: cancellationToken).ConfigureAwait(false);
                        }
                        catch { /* best-effort — a human can still trigger reconciliation manually */ }
                    }
                }
            }

            var conflictDetail = createdConflictIds.Count > 0
                ? $"Child proposals changed overlapping lines in: {string.Join(", ", conflicts.Keys)} — " +
                    $"see {ConflictReportFileName} on the goal's branch. Task conflict(s) opened for resolution: " +
                    string.Join(", ", createdConflictIds)
                : $"Child proposals changed overlapping lines in: {string.Join(", ", conflicts.Keys)} — " +
                    $"see {ConflictReportFileName} on the goal's branch.";

            return new MergeReconciliationResult(
                MergeReconciliationOutcome.Conflict,
                ConflictReportPath: ConflictReportFileName,
                Detail: conflictDetail);
        }

        var ordered = OrderByDependencies(eligibleChildren, childProposals);
        // DistinctBy preserves first-occurrence order, so this keeps the topological ordering
        // OrderByDependencies just computed (important for the copy loop below — a later child's
        // copy may intentionally build on an earlier dependency's already-copied content) while
        // still deduping the two children that point at the same reconciliation proposal.
        var orderedDistinctProposals = ordered.Select(cp => cp.Proposal).DistinctBy(p => p.ProposalId).ToList();
        var mergeBranch = $"merge/{parentWorkUnitId}";

        // merge/{parentWorkUnitId} is a fixed name reused across an entire goal's lifetime — after a
        // rejected reconciled proposal sends children back for another attempt (AutomatedReviewGateService
        // .HandleAutomatedRejectionAsync / HandleHumanRejectionAsync), this same branch is reused again.
        // A one-time seed-if-empty (the previous InitBranchAsync call here) would leave the PREVIOUS
        // attempt's content sitting underneath the new one — correct for files the new attempt still
        // touches (last write wins), but any file only the old attempt touched (e.g. the plan changed
        // and a child no longer writes that file) would linger as stale, unreviewed content in what's
        // about to become the reconciled proposal's diff. ApplyBranchAsync is a destructive full mirror
        // instead: reset to exactly parent.BranchId's current state every time this runs (first attempt
        // or retry alike), then let the per-child copy loop below rebuild every constituent's content on
        // top from scratch. Seeded from parent.BranchId, not literal "main", for the same reason as
        // MergeCommandService.ProposeAsync's identical override for a child's own auto-apply target:
        // parent.BranchId is what every child was itself seeded from and may hold pre-fan-out setup work
        // "main" doesn't have yet.
        //
        // A prior attempt's own conflict report (written to parent.BranchId itself below, on the
        // Conflict path) is stale the moment this attempt has no conflicts — clean it up before
        // mirroring, so it never gets pulled into the reconciled proposal's diff as if it were part of
        // someone's actual change.
        if (await fileWorkspace.ExistsAsync(parent.BranchId, ConflictReportFileName, cancellationToken).ConfigureAwait(false))
        {
            await fileWorkspace.DeleteAsync(parent.BranchId, ConflictReportFileName, cancellationToken).ConfigureAwait(false);
        }

        await fileWorkspace.ApplyBranchAsync(parent.BranchId, mergeBranch, cancellationToken).ConfigureAwait(false);

        foreach (var proposal in orderedDistinctProposals)
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

        // Slice 16h — composite execution on the merged branch (opt-in)
        string? executionVerification = null;
        if (execution is not null && options is not null &&
            (options.RequireBuildBeforeProposal || options.RequireTestBeforeProposal))
        {
            try
            {
                var sourceBranchIds = orderedDistinctProposals.Select(p => p.SourceBranch).Distinct().ToList();
                var execResult = await execution.ExecuteCompositeAsync(
                    sourceBranchIds,
                    new WorkspaceExecutionRequest(
                        Build: options.RequireBuildBeforeProposal,
                        Test: options.RequireTestBeforeProposal,
                        BuildCommand: options.BuildCommand,
                        TestCommand: options.TestCommand,
                        TimeoutSeconds: options.ExecutionTimeoutSeconds),
                    cancellationToken).ConfigureAwait(false);
                executionVerification = JsonSerializer.Serialize(execResult);
            }
            catch { /* best-effort — execution failure doesn't block reconciliation */ }
        }

        // Deduped (not ordered.Select(...)) — two children whose conflict was reconciled both point
        // at the same proposal in `ordered`; SupersedeAsync below would throw on the second,
        // already-Superseded attempt at the same id otherwise.
        var constituentIds = orderedDistinctProposals.Select(p => p.ProposalId).ToList();
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
            executionVerification, null, null,
            MergeProposalStatus.Draft,
            WorkspaceChanges: workspaceChanges,
            DiffGeneratedAt: DateTimeOffset.UtcNow,
            WorkUnitId: parent.WorkUnitId,
            FilesTouched: filesTouched,
            ReconciledFrom: constituentIds,
            // Slice 6.3a — parent is the reconciliation's own top-level/parent WorkUnit, already
            // resolved (direct copy, no chain walk needed).
            RepositoryId: parent.RepositoryId);

        await merge.ProposeAsync(reconciled, cancellationToken).ConfigureAwait(false);
        await merge.ValidateAsync(reconciledId, cancellationToken).ConfigureAwait(false);

        // Slice 20b's auto-apply for AgentApproval/Hybrid policies only fires from
        // MergeCommandService.ProposeAsync — the higher-level wrapper individual task proposals go
        // through, which this reconciled proposal deliberately bypasses (see the comment on
        // MergeCommandService.ProposeAsync's fan-out-child target-branch override for why: only the
        // reconciled proposal itself, built here directly via IMergeService.ProposeAsync, is allowed
        // to target "main"). Without this, a reconciled top-level proposal always sits in
        // ReadyForReview regardless of WorkspaceReviewPolicy — the human has to click Approve even
        // when both this goal and everything upstream of it were explicitly set to AgentApproval.
        // Mirrors that same auto-trigger here: fire-and-forget ApplyAsync through the command
        // wrapper so the BeforeMerge gate (AutoReviewRule's inline reviewer) actually runs.
        var effectivePolicy = WorkspaceReviewScope.AppliesToRealRepo(parent) ? parent.WorkspaceReviewPolicy : parent.TaskReviewPolicy;
        if (mergeCommands is not null && effectivePolicy is ReviewPolicy.AgentApproval or ReviewPolicy.Hybrid)
        {
            _ = Task.Run(async () =>
            {
                try { await mergeCommands.ApplyAsync(reconciledId, CancellationToken.None, autoApplied: true).ConfigureAwait(false); }
                catch { /* reviewer rejection or gate block — proposal stays in current state */ }
            }, CancellationToken.None);
        }

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
            var afterContents = new Dictionary<string, string?>();
            foreach (var proposalId in proposalIds)
            {
                afterContents[proposalId] = await fileWorkspace
                    .ReadAsync(byId[proposalId].SourceBranch, file, cancellationToken).ConfigureAwait(false);
            }

            // base/{proposalId} is a snapshot of the proposal's TargetBranch (usually "main"), not
            // the immediate parent/sibling branch each child actually forked from. A file the
            // children only inherited from their common parent doesn't exist in that snapshot, so
            // the line-diff below would see "added from nothing" for every sibling and flag a
            // conflict despite nobody having touched it. Identical content across all flagged
            // proposals is never a real conflict regardless of what the baseline snapshot does or
            // doesn't contain.
            if (afterContents.Values.Distinct().Count() <= 1)
                continue;

            var ranges = new List<(string ProposalId, List<(int Start, int End)> Ranges)>();
            foreach (var proposalId in proposalIds)
            {
                var before = await fileWorkspace
                    .ReadAsync($"base/{proposalId}", file, cancellationToken).ConfigureAwait(false);
                var after = afterContents[proposalId];

                ranges.Add((proposalId, LineRangeConflictDetector.ComputeChangedRanges(before, after)));
            }

            var overlapping = new HashSet<string>();
            for (var i = 0; i < ranges.Count; i++)
            {
                for (var j = i + 1; j < ranges.Count; j++)
                {
                    if (!LineRangeConflictDetector.RangesOverlap(ranges[i].Ranges, ranges[j].Ranges))
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