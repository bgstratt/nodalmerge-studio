using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/vision-punchlist-remediation.md — reads must survive overlapping a write, the same way
/// writes already survive overlapping each other.
///
/// Windows write handles are exclusive: a writer holds the file with FileAccess.Write, and a reader
/// opening with FileShare.Read cannot share with that handle. WriteAsync has always treated the
/// resulting sharing violation as transient and retried it (see its comment: "not a real error").
/// Reads did not, so the identical moment of contention threw a hard IOException at whoever happened
/// to be reading — an agent reading a file a sibling worker is writing, the materializer snapshotting
/// a branch mid-apply, or a poll loop waiting for content.
///
/// This surfaced as a recurring integration-suite flake
/// (ReadBeforeWriteEnforcementTests polls ReadAsync while a live WorkerAgentLoop writes the same
/// file), but the bug is in the service, not the test.
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemWorkspaceServiceReadContentionTests : IAsyncLifetime
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-read-contention-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_rootPath);

    private IFileWorkspaceService Build()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
        return services.BuildServiceProvider().GetRequiredService<IFileWorkspaceService>();
    }

    /// <summary>
    /// Holds the file exclusively (FileShare.None — strictly harsher than a real writer, so the read
    /// cannot possibly sneak in) for well under the retry budget, then releases. The read must ride
    /// it out rather than throwing. Without the retry this throws immediately.
    /// </summary>
    [Fact]
    public async Task ReadAsync_rides_out_a_transient_exclusive_lock()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "contended.txt", "settled content");

        var fullPath = Path.Combine(_rootPath, "main", "contended.txt");
        Assert.True(File.Exists(fullPath), $"expected the branch file at {fullPath}");

        var released = new TaskCompletionSource();
        var holding = new TaskCompletionSource();

        var holder = Task.Run(async () =>
        {
            FileStream exclusive;
            try
            {
                exclusive = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex)
            {
                // Surface it through the handshake — otherwise a failure to take the lock leaves the
                // test blocked on holding.Task forever instead of failing.
                holding.TrySetException(ex);
                throw;
            }

            using (exclusive)
            {
                holding.SetResult();
                // Comfortably inside the 250ms retry budget (25+50+75+100), comfortably longer than a
                // single attempt — so the read must actually retry, not just get lucky the first time.
                await Task.Delay(120);
                released.SetResult();
            }
        });

        await holding.Task;
        var content = await fileWorkspace.ReadAsync("main", "contended.txt");

        Assert.True(released.Task.IsCompleted, "the read returned before the lock was released — it never contended");
        Assert.Equal("settled content", content);
        await holder;
    }

    /// <summary>
    /// ReadBytesAsync feeds materialization and snapshotting, where a spurious failure aborts a whole
    /// snapshot rather than one agent tool call — so it needs the same tolerance.
    /// </summary>
    [Fact]
    public async Task ReadBytesAsync_rides_out_a_transient_exclusive_lock()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "bytes.bin", "binary-ish");

        var fullPath = Path.Combine(_rootPath, "main", "bytes.bin");
        Assert.True(File.Exists(fullPath), $"expected the branch file at {fullPath}");
        var holding = new TaskCompletionSource();

        var holder = Task.Run(async () =>
        {
            FileStream exclusive;
            try
            {
                exclusive = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex)
            {
                holding.TrySetException(ex);
                throw;
            }

            using (exclusive)
            {
                holding.SetResult();
                await Task.Delay(120);
            }
        });

        await holding.Task;
        var bytes = await fileWorkspace.ReadBytesAsync("main", "bytes.bin");

        Assert.NotNull(bytes);
        Assert.Equal("binary-ish", System.Text.Encoding.UTF8.GetString(bytes!));
        await holder;
    }
}
