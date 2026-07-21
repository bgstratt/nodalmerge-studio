using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/test-suite-remediation-plan.md A2 — ApplyBranchAsync must survive a target file being held
/// open at the moment it copies over it, the same way ReadAsync/WriteAsync already survive that
/// contention (see FileSystemWorkspaceServiceReadContentionTests).
///
/// ApplyBranchAsync copies each source file over the target branch with File.Copy(overwrite: true),
/// which opens the destination for write. The materializer snapshotting the target branch, or an
/// agent/poll loop reading it, can hold that file open at the same instant — a transient sharing
/// violation. The read/write paths ride this out; the branch-apply copy path did not, so a merge
/// landing while its target was being read failed hard with an IOException. This proves the retry.
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemWorkspaceServiceApplyContentionTests : IAsyncLifetime
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-apply-contention-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_rootPath);

    private IFileWorkspaceService Build()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
        return services.BuildServiceProvider().GetRequiredService<IFileWorkspaceService>();
    }

    /// <summary>
    /// Holds the TARGET branch file exclusively (FileShare.None — harsher than a real reader, so the
    /// copy cannot sneak in) for well under the retry budget, then releases. ApplyBranchAsync must
    /// ride it out and land the source content. Without the retry the File.Copy throws immediately.
    /// </summary>
    [Fact]
    public async Task ApplyBranchAsync_rides_out_a_transient_lock_on_a_target_file()
    {
        var fileWorkspace = Build();

        // Target branch already has the file (so the apply overwrites an existing, lockable file);
        // source has the new content the merge should land.
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "contended.txt", "old target content");
        await fileWorkspace.InitBranchAsync("feature");
        await fileWorkspace.WriteAsync("feature", "contended.txt", "new merged content");

        var targetFullPath = Path.Combine(_rootPath, "main", "contended.txt");
        Assert.True(File.Exists(targetFullPath), $"expected the target branch file at {targetFullPath}");

        var released = new TaskCompletionSource();
        var holding = new TaskCompletionSource();

        var holder = Task.Run(async () =>
        {
            FileStream exclusive;
            try
            {
                exclusive = new FileStream(targetFullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex)
            {
                // Surface it through the handshake so a lock failure fails the test instead of
                // wedging it forever on holding.Task.
                holding.TrySetException(ex);
                throw;
            }

            using (exclusive)
            {
                holding.SetResult();
                // Inside the retry budget (25+50+75+100), longer than one attempt — the copy must
                // actually retry, not get lucky first try.
                await Task.Delay(120);
                released.SetResult();
            }
        });

        await holding.Task;
        await fileWorkspace.ApplyBranchAsync("feature", "main");

        Assert.True(released.Task.IsCompleted, "the apply returned before the lock was released — it never contended");
        var landed = await fileWorkspace.ReadAsync("main", "contended.txt");
        Assert.Equal("new merged content", landed);
        await holder;
    }
}
