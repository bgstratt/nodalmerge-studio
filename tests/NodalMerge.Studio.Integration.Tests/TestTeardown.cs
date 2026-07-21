using Microsoft.Data.Sqlite;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Shared teardown for integration tests that own a temp directory backed by a file SQLite db.
///
/// The problem this solves (plans/test-suite-remediation-plan.md, B2). Teardown used to be a
/// synchronous <c>Dispose()</c> that called <see cref="SqliteConnection.ClearAllPools"/> and then
/// <c>Directory.Delete(root, recursive: true)</c> with no retry. On Windows a background task or a
/// pooled connection that is a beat behind still holds a handle to <c>nodes.db</c>, so the delete
/// throws <see cref="IOException"/> ("being used by another process") — the test body passed, but the
/// teardown failed and the blame landed on whichever test xUnit reported next. That is the exact
/// "different test each run" flake this suite kept producing.
///
/// The fix is a bounded async retry. It matters that this is a *retry*, not a *swallow*: a genuine
/// transient race clears within a few tens of milliseconds, so a short backing-off retry makes those
/// deletes reliable — while a real, un-fixed leak keeps the handle open past the whole budget and we
/// still throw, loudly and with the path named. Retrying distinguishes the two; catch-and-ignore
/// would hide both.
/// </summary>
internal static class TestTeardown
{
    // ~1.5s total across 8 attempts (25,50,75,100,150,200,300,400 ≈ 1300ms of waiting). A legitimate
    // handle-release race clears in the first attempt or two; anything still locked after this is a
    // real leak, not timing, and should fail the run rather than be waited out.
    private static readonly int[] BackoffMs = [25, 50, 75, 100, 150, 200, 300, 400];

    /// <summary>
    /// Force-closes all pooled SQLite connections in the process, then deletes each root with a
    /// bounded retry. Safe to pass roots that do not exist. Throws if a root is still undeletable
    /// after the full retry budget — that is a real leaked handle, surfaced deliberately.
    /// </summary>
    internal static async Task ClearSqlitePoolsAndDeleteAsync(params string[] roots)
    {
        // Process-wide: releases handles held by pooled connections this test opened. Must happen
        // before the delete or the very handle we need released is still in the pool.
        SqliteConnection.ClearAllPools();

        foreach (var root in roots)
            await DeleteWithRetryAsync(root).ConfigureAwait(false);
    }

    private static async Task DeleteWithRetryAsync(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // UnauthorizedAccessException covers git's read-only .git/objects on Windows, which a
                // couple of tests previously hand-rolled a chmod-then-delete for; clearing read-only
                // and retrying folds that in.
                TryClearReadOnly(root);

                if (attempt >= BackoffMs.Length - 1)
                {
                    throw new IOException(
                        $"Test teardown could not delete '{root}' after {BackoffMs.Length} attempts "
                        + $"(~{BackoffMs.Sum()}ms). A handle is still open — this is a leaked host or "
                        + "connection that outlived its test, not a transient race. See "
                        + "plans/test-suite-remediation-plan.md B2/B3.",
                        ex);
                }

                await Task.Delay(BackoffMs[attempt]).ConfigureAwait(false);
            }
        }
    }

    // Best-effort: recursively clear the read-only attribute (git objects are marked read-only, which
    // makes Directory.Delete throw UnauthorizedAccessException). Never throws — it is a helper for the
    // retry, and the retry itself owns the outcome.
    private static void TryClearReadOnly(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch
        {
            // Swallowed on purpose: the delete retry is the authority on success/failure.
        }
    }
}
