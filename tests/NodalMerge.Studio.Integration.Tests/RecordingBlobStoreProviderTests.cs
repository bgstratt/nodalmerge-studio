using NodalMerge.Host.Abstractions.Providers;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// B2a (plans/test-suite-remediation-plan.md): the recorder is written from concurrent fetch/put
/// fan-outs (WorkUnitPrefetchService, MaterializationEngine). Its record lists must not lose entries
/// under that concurrency — a lost Add is what made
/// ScopedTreeFetchTests.PrefetchScopeAsync_warms_exactly_the_scoped_file_blobs flake (4 recorded
/// instead of 5). This hammers both record paths hard enough that an unsynchronised List.Add fails
/// reliably, not just occasionally.
/// </summary>
[Trait("Category", "Integration")]
public class RecordingBlobStoreProviderTests
{
    // A no-op inner store so the test measures only the recorder's bookkeeping.
    private sealed class NullBlobStore : IBlobStoreProvider
    {
        public ValueTask<BlobReadResult> TryGetBlobAsync(string hashHex, CancellationToken ct = default) =>
            new(BlobReadResult.Missing);

        public ValueTask PutBlobAsync(string hashHex, byte[] bytes, string? contentType, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Records_every_concurrent_get_and_put_without_losing_entries()
    {
        var recorder = new RecordingBlobStoreProvider(new NullBlobStore());

        const int workers = 32;
        const int perWorker = 500;
        const int expected = workers * perWorker;

        // Start all workers together so their Adds genuinely interleave.
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var getTasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            await barrier.Task;
            for (var i = 0; i < perWorker; i++)
                await recorder.TryGetBlobAsync($"g-{w}-{i}");
        }));

        var putTasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            await barrier.Task;
            for (var i = 0; i < perWorker; i++)
                await recorder.PutBlobAsync($"p-{w}-{i}", [], null);
        }));

        var all = getTasks.Concat(putTasks).ToArray();
        barrier.SetResult();
        await Task.WhenAll(all);

        // Exact counts: an unsynchronised List.Add loses entries (and can also throw
        // IndexOutOfRangeException mid-resize). Either way this assertion fails without the lock.
        Assert.Equal(expected, recorder.GetHashes.Count);
        Assert.Equal(expected, recorder.PutHashes.Count);

        // And nothing corrupted: every distinct value is present exactly once.
        Assert.Equal(expected, recorder.GetHashes.Distinct().Count());
        Assert.Equal(expected, recorder.PutHashes.Distinct().Count());
    }
}
