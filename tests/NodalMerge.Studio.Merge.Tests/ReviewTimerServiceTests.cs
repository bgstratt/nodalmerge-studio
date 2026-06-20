using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge.Tests;

public class ReviewTimerServiceTests
{
    [Fact]
    public async Task ScheduleAsync_writes_timer_with_correct_expiry()
    {
        var store = new InMemoryStudioNodeStore();
        var service = new ReviewTimerService(store, new FakeServiceProvider(new FakeMergeCommandService()));

        var before = DateTimeOffset.UtcNow;
        await service.ScheduleAsync("MP-1", "WU-1", TimeSpan.FromMinutes(5));

        var timer = await service.GetAsync("MP-1");
        Assert.NotNull(timer);
        Assert.Equal("MP-1", timer!.ProposalId);
        Assert.Equal("WU-1", timer.WorkUnitId);
        Assert.False(timer.Cancelled);
        Assert.InRange(timer.ExpiresAt, before.AddMinutes(5).AddSeconds(-5), before.AddMinutes(5).AddSeconds(5));
    }

    [Fact]
    public async Task TryCancelAsync_marks_existing_timer_cancelled()
    {
        var store = new InMemoryStudioNodeStore();
        var service = new ReviewTimerService(store, new FakeServiceProvider(new FakeMergeCommandService()));
        await service.ScheduleAsync("MP-1", "WU-1", TimeSpan.FromMinutes(5));

        await service.TryCancelAsync("MP-1");

        Assert.True((await service.GetAsync("MP-1"))!.Cancelled);
    }

    [Fact]
    public async Task TryCancelAsync_on_missing_timer_is_a_noop()
    {
        var store = new InMemoryStudioNodeStore();
        var service = new ReviewTimerService(store, new FakeServiceProvider(new FakeMergeCommandService()));

        await service.TryCancelAsync("MP-does-not-exist");

        Assert.Null(await service.GetAsync("MP-does-not-exist"));
    }

    [Fact]
    public async Task ProcessExpiredAsync_cancels_then_applies_an_expired_timer()
    {
        var store = new InMemoryStudioNodeStore();
        var mergeCommands = new FakeMergeCommandService();
        var service = new ReviewTimerService(store, new FakeServiceProvider(mergeCommands));
        await WriteExpiredTimerAsync(store, "MP-1", "WU-1");

        await service.ProcessExpiredAsync();

        Assert.Equal(["MP-1"], mergeCommands.AppliedProposalIds);
        Assert.True(mergeCommands.LastAutoApplied);
        Assert.True((await service.GetAsync("MP-1"))!.Cancelled);
    }

    [Fact]
    public async Task ProcessExpiredAsync_leaves_a_non_expired_timer_untouched()
    {
        var store = new InMemoryStudioNodeStore();
        var mergeCommands = new FakeMergeCommandService();
        var service = new ReviewTimerService(store, new FakeServiceProvider(mergeCommands));
        await service.ScheduleAsync("MP-1", "WU-1", TimeSpan.FromMinutes(5));

        await service.ProcessExpiredAsync();

        Assert.Empty(mergeCommands.AppliedProposalIds);
        Assert.False((await service.GetAsync("MP-1"))!.Cancelled);
    }

    [Fact]
    public async Task ProcessExpiredAsync_skips_an_already_cancelled_timer()
    {
        var store = new InMemoryStudioNodeStore();
        var mergeCommands = new FakeMergeCommandService();
        var service = new ReviewTimerService(store, new FakeServiceProvider(mergeCommands));
        await store.WriteNodeAsync(
            StudioNodeKind.ReviewTimerV1,
            "MP-1",
            JsonSerializer.Serialize(new ReviewTimer("T-1", "MP-1", "WU-1", DateTimeOffset.UtcNow.AddMinutes(-1), Cancelled: true)));

        await service.ProcessExpiredAsync();

        Assert.Empty(mergeCommands.AppliedProposalIds);
    }

    [Fact]
    public async Task ProcessExpiredAsync_swallows_an_apply_failure_and_keeps_processing_remaining_timers()
    {
        var store = new InMemoryStudioNodeStore();
        var mergeCommands = new FakeMergeCommandService { ThrowOnApply = true };
        var service = new ReviewTimerService(store, new FakeServiceProvider(mergeCommands));
        await WriteExpiredTimerAsync(store, "MP-1", "WU-1");
        await WriteExpiredTimerAsync(store, "MP-2", "WU-2");

        await service.ProcessExpiredAsync();

        Assert.Equal(2, mergeCommands.AppliedProposalIds.Count);
    }

    private static Task WriteExpiredTimerAsync(IStudioNodeStore store, string proposalId, string workUnitId) =>
        store.WriteNodeAsync(
            StudioNodeKind.ReviewTimerV1,
            proposalId,
            JsonSerializer.Serialize(new ReviewTimer($"T-{proposalId}", proposalId, workUnitId, DateTimeOffset.UtcNow.AddMinutes(-1))));

    private sealed class FakeServiceProvider(IMergeCommandService mergeCommands) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMergeCommandService) ? mergeCommands : null;
    }

    private sealed class FakeMergeCommandService : IMergeCommandService
    {
        public List<string> AppliedProposalIds { get; } = [];
        public bool LastAutoApplied { get; private set; }
        public bool ThrowOnApply { get; set; }

        public Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default, bool autoApplied = false)
        {
            AppliedProposalIds.Add(proposalId);
            LastAutoApplied = autoApplied;
            if (ThrowOnApply)
                throw new InvalidOperationException("simulated apply failure");

            return Task.FromResult(new MergeProposal(
                proposalId, "feat/x", "main", "goal", "summary", "desc", null, null, null, MergeProposalStatus.Merged));
        }

        public Task<MergeProposal> ProposeAsync(
            string sourceBranch,
            string targetBranch,
            string summary,
            string? goal = null,
            string? changeDescription = null,
            string? workUnitId = null,
            string? agentId = null,
            string? model = null,
            string? provider = null,
            string? sessionId = null,
            string? commandId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MergeProposal> ReviewAsync(
            string proposalId,
            string decision,
            string? verificationResults = null,
            bool automated = false,
            string? reviewerAgentId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
