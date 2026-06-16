using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Merge;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge.Tests;

public class InMemoryMergeServiceTests
{
    private static InMemoryMergeService Build() =>
        new(new InMemoryStudioNodeStore(), new NoopFileWorkspaceService(), new WorkspaceOptions(), new NoopEventStream());

    private sealed class NoopEventStream : NodalMerge.Studio.Core.Services.IExecutionEventStream
    {
        public Task<NodalMerge.Studio.Contracts.Domain.ExecutionEvent> AppendAsync<T>(
            string sessionId, string? workUnitId,
            NodalMerge.Studio.Contracts.Domain.ExecutionEventKind kind, T payload,
            string? causedByEventId = null, string? eventId = null, CancellationToken ct = default) =>
            Task.FromResult(new NodalMerge.Studio.Contracts.Domain.ExecutionEvent(
                eventId ?? Guid.NewGuid().ToString("N"), sessionId, workUnitId, kind, "{}", causedByEventId, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>> GetSessionEventsAsync(
            string sessionId, DateTimeOffset? since = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>>([]);

        public Task<NodalMerge.Studio.Contracts.Domain.ExecutionEvent?> GetAsync(string eventId, CancellationToken ct = default) =>
            Task.FromResult<NodalMerge.Studio.Contracts.Domain.ExecutionEvent?>(null);
    }

    private sealed class NoopFileWorkspaceService : NodalMerge.Studio.Core.Services.IFileWorkspaceService
    {
        public Task InitBranchAsync(string b, string? s = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(string b, string p, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string b, string p, string c, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string b, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string b, string p, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> ListAsync(string b, string? s = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string> DiffAsync(string s, string t, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task ApplyBranchAsync(string s, string t, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetWorkingDirectoryAsync(string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static MergeProposal MakeProposal(string id, string source = "feat/x", string target = "main") =>
        new(id, source, target, "goal", "summary", "desc", null, null, null, MergeProposalStatus.Draft);

    // ── ProposeAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProposeAsync_always_stores_as_Draft()
    {
        var svc = Build();
        var proposal = MakeProposal("MP-1") with { Status = MergeProposalStatus.Approved };

        var result = await svc.ProposeAsync(proposal);

        Assert.Equal(MergeProposalStatus.Draft, result.Status);
    }

    // ── GetAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_returns_stored_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));

        var result = await svc.GetAsync("MP-1");

        Assert.NotNull(result);
        Assert.Equal("MP-1", result.ProposalId);
    }

    [Fact]
    public async Task GetAsync_returns_null_for_unknown_id()
    {
        var result = await Build().GetAsync("no-such-proposal");
        Assert.Null(result);
    }

    // ── ValidateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_transitions_Draft_to_ReadyForReview()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));

        var result = await svc.ValidateAsync("MP-1");

        Assert.Equal(MergeProposalStatus.ReadyForReview, result.Status);
    }

    [Fact]
    public async Task ValidateAsync_rejects_non_Draft_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1"); // → ReadyForReview

        // Validating again (already ReadyForReview) must throw
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ValidateAsync("MP-1"));
    }

    [Fact]
    public async Task ValidateAsync_throws_for_unknown_proposal()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Build().ValidateAsync("no-such"));
    }

    // ── ReviewAsync — human gate ─────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_approves_ReadyForReview_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1");

        var result = await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);

        Assert.Equal(MergeProposalStatus.Approved, result.Status);
    }

    [Fact]
    public async Task ReviewAsync_rejects_ReadyForReview_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1");

        var result = await svc.ReviewAsync("MP-1", MergeProposalStatus.Rejected);

        Assert.Equal(MergeProposalStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task ReviewAsync_cannot_bypass_validate_step()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1")); // still Draft

        // Attempting to approve a Draft proposal must fail (AP-4 gate)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ReviewAsync("MP-1", MergeProposalStatus.Approved));
    }

    [Fact]
    public async Task ReviewAsync_throws_for_unknown_proposal()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Build().ReviewAsync("no-such", MergeProposalStatus.Approved));
    }

    // ── ApplyAsync — human gate ──────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_merges_Approved_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1");
        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);

        var result = await svc.ApplyAsync("MP-1");

        Assert.Equal(MergeProposalStatus.Merged, result.Status);
    }

    [Fact]
    public async Task ApplyAsync_cannot_bypass_human_approval()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1"); // ReadyForReview, not Approved

        // Attempting to apply without human approval must fail (AP-4 gate)
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync("MP-1"));
    }

    [Fact]
    public async Task ApplyAsync_cannot_apply_Draft_directly()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1")); // Draft only

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync("MP-1"));
    }

    [Fact]
    public async Task ApplyAsync_throws_for_unknown_proposal()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Build().ApplyAsync("no-such"));
    }

    // ── ListAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_filters_by_source_branch()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-A", source: "feat/a"));
        await svc.ProposeAsync(MakeProposal("MP-B", source: "feat/b"));

        var results = await svc.ListAsync("feat/a");

        Assert.Single(results);
        Assert.Equal("MP-A", results[0].ProposalId);
    }

    [Fact]
    public async Task ListAsync_returns_all_when_no_branch_filter()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1", source: "feat/x"));
        await svc.ProposeAsync(MakeProposal("MP-2", source: "feat/y"));

        var results = await svc.ListAsync();

        Assert.Equal(2, results.Count);
    }

    // ── Full AP-4 happy path ─────────────────────────────────────────────────

    [Fact]
    public async Task Full_AP4_path_propose_validate_approve_apply()
    {
        var svc = Build();

        var proposed = await svc.ProposeAsync(MakeProposal("MP-full"));
        Assert.Equal(MergeProposalStatus.Draft, proposed.Status);

        var validated = await svc.ValidateAsync("MP-full");
        Assert.Equal(MergeProposalStatus.ReadyForReview, validated.Status);

        var approved = await svc.ReviewAsync("MP-full", MergeProposalStatus.Approved);
        Assert.Equal(MergeProposalStatus.Approved, approved.Status);

        var merged = await svc.ApplyAsync("MP-full");
        Assert.Equal(MergeProposalStatus.Merged, merged.Status);
    }

    [Fact]
    public async Task Full_AP4_rejection_path_propose_validate_reject()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-rej"));
        await svc.ValidateAsync("MP-rej");

        var rejected = await svc.ReviewAsync("MP-rej", MergeProposalStatus.Rejected);
        Assert.Equal(MergeProposalStatus.Rejected, rejected.Status);

        // Cannot apply a rejected proposal
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync("MP-rej"));
    }
}
