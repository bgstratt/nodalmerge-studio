namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Proves the teardown helper actually distinguishes a transient handle-release race (which it must
/// wait out and then succeed) from a real leaked handle (which it must surface, not swallow). See
/// plans/test-suite-remediation-plan.md B2.
/// </summary>
[Trait("Category", "Integration")]
public class TestTeardownTests
{
    [Fact]
    public async Task Deletes_a_plain_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"teardown-plain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "hi");

        await TestTeardown.ClearSqlitePoolsAndDeleteAsync(root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Ignores_a_missing_root()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"teardown-missing-{Guid.NewGuid():N}");
        // Must not throw.
        await TestTeardown.ClearSqlitePoolsAndDeleteAsync(missing);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task Clears_read_only_files_then_deletes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"teardown-readonly-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "locked.txt");
        await File.WriteAllTextAsync(file, "x");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        await TestTeardown.ClearSqlitePoolsAndDeleteAsync(root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Succeeds_when_a_handle_is_released_mid_retry()
    {
        // The transient-race case: hold a file open, release it after a delay shorter than the retry
        // budget, and confirm the delete waits it out rather than failing on the first attempt.
        var root = Path.Combine(Path.GetTempPath(), $"teardown-transient-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "busy.txt");
        await File.WriteAllTextAsync(file, "x");

        var handle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Release the handle shortly after the delete starts retrying (well inside the ~1.3s budget).
        var releaser = Task.Run(async () =>
        {
            await Task.Delay(120);
            handle.Dispose();
        });

        await TestTeardown.ClearSqlitePoolsAndDeleteAsync(root);
        await releaser;

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Throws_a_named_error_when_a_handle_never_releases()
    {
        // The real-leak case: a handle held for the entire retry budget must surface as a clear
        // failure naming the path, NOT be silently swallowed.
        var root = Path.Combine(Path.GetTempPath(), $"teardown-leak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "wedged.txt");
        await File.WriteAllTextAsync(file, "x");

        using var handle = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        if (!OperatingSystem.IsWindows())
        {
            // Unix file locks are advisory: an open handle does NOT block unlink, so the delete
            // succeeds and there is no leaked-handle failure to surface. The "throws when wedged"
            // contract is only observable where the OS enforces mandatory locks (Windows), so on
            // other platforms we assert the correct platform behaviour instead — deletion succeeds.
            await TestTeardown.ClearSqlitePoolsAndDeleteAsync(root);
            Assert.False(Directory.Exists(root));
            handle.Dispose();
            return;
        }

        var ex = await Assert.ThrowsAsync<IOException>(
            () => TestTeardown.ClearSqlitePoolsAndDeleteAsync(root));

        Assert.Contains(root, ex.Message, StringComparison.Ordinal);
        Assert.Contains("leaked host or connection", ex.Message, StringComparison.Ordinal);

        // Cleanup after the handle is released so we do not leak the temp dir ourselves.
        handle.Dispose();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
