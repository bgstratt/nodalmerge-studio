using System.Diagnostics;
using System.Text.RegularExpressions;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class WorkspaceExecutionService(
    IFileWorkspaceService fileWorkspace,
    IWorkspaceProfileService profiles,
    WorkspaceOptions options) : IWorkspaceExecutionService
{
    public async Task<BranchExecutionResult> ExecuteAsync(
        string branchId,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default)
    {
        var workDir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct)
            .ConfigureAwait(false);
        if (workDir is null)
            throw new InvalidOperationException($"Branch '{branchId}' has no working directory.");

        var builds = new List<BuildResult>();
        var tests  = new List<TestResult>();

        if (request.Build)
            builds.AddRange(await RunBuildTargetsAsync(branchId, workDir, request, ct).ConfigureAwait(false));

        if (request.Test)
            tests.AddRange(await RunTestTargetsAsync(branchId, workDir, request, ct).ConfigureAwait(false));

        return new BranchExecutionResult(
            branchId, builds, tests, [],
            builds.All(b => b.Success) && tests.All(t => t.Success),
            DateTimeOffset.UtcNow);
    }

    // ── Phase 9b — per-root command resolution ───────────────────────────────
    //
    // Level 1 (explicit request.BuildCommand/TestCommand) keeps today's behavior exactly: one
    // command at the branch root, no profile lookup. Level 2 (auto-detect) now resolves through
    // the WorkspaceProfile instead of a single flat repo-root scan, so each detected sub-project
    // builds/tests from its own directory.

    private async Task<IReadOnlyList<BuildResult>> RunBuildTargetsAsync(
        string branchId, string workDir, WorkspaceExecutionRequest request, CancellationToken ct)
    {
        if (request.BuildCommand is { Length: > 0 })
            return [await RunCommandAsync(workDir, (request.BuildCommand, null!), request.TimeoutSeconds, ct)];

        if (!request.AllowAutoDetect)
            return [];

        var profile = await profiles.GetOrDetectAsync(branchId, ct).ConfigureAwait(false);
        var results = new List<BuildResult>();
        foreach (var root in FilterRoots(profile.Roots, request.RootPath))
        {
            if (root.BuildCommand is null) continue;
            var result = await RunCommandAsync(
                RootDir(workDir, root.RelativePath), (root.BuildCommand, root.Stack),
                request.TimeoutSeconds, ct, root.RelativePath);
            results.Add(result);
        }
        return results;
    }

    private async Task<IReadOnlyList<TestResult>> RunTestTargetsAsync(
        string branchId, string workDir, WorkspaceExecutionRequest request, CancellationToken ct)
    {
        if (request.TestCommand is { Length: > 0 })
            return [await RunTestCommandAsync(workDir, (request.TestCommand, null!), request.TimeoutSeconds, ct)];

        if (!request.AllowAutoDetect)
            return [];

        var profile = await profiles.GetOrDetectAsync(branchId, ct).ConfigureAwait(false);
        var results = new List<TestResult>();
        foreach (var root in FilterRoots(profile.Roots, request.RootPath))
        {
            if (root.TestCommand is null) continue;
            var result = await RunTestCommandAsync(
                RootDir(workDir, root.RelativePath), (root.TestCommand, root.Stack),
                request.TimeoutSeconds, ct, root.RelativePath);
            results.Add(result);
        }
        return results;
    }

    private static IEnumerable<ProjectRoot> FilterRoots(IReadOnlyList<ProjectRoot> roots, string? rootPath) =>
        rootPath is null ? roots : roots.Where(r => r.RelativePath == rootPath);

    private static string RootDir(string workDir, string relativePath) =>
        relativePath.Length == 0 ? workDir : Path.Combine(workDir, relativePath);

    // ── Composite execution ──────────────────────────────────────────────────

    public async Task<BranchExecutionResult> ExecuteCompositeAsync(
        IReadOnlyList<string> sourceBranchIds,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default)
    {
        if (sourceBranchIds.Count == 0)
            throw new ArgumentException("At least one source branch is required.", nameof(sourceBranchIds));

        var compositeId = $"exec-group-{Guid.NewGuid():N}";
        try
        {
            if (sourceBranchIds.Count == 1)
                return await ExecuteAsync(sourceBranchIds[0], request, ct);

            // Seed from first branch, then apply subsequent ones on top
            await fileWorkspace.InitBranchAsync(compositeId, sourceBranchIds[0], ct).ConfigureAwait(false);

            var conflicts = new List<string>();
            for (int i = 1; i < sourceBranchIds.Count; i++)
            {
                try
                {
                    await fileWorkspace.ApplyBranchAsync(sourceBranchIds[i], compositeId, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    conflicts.Add($"Conflict applying {sourceBranchIds[i]}: {ex.Message}");
                }
            }

            var result = await ExecuteAsync(compositeId, request, ct).ConfigureAwait(false);

            // Attach conflict warnings
            if (conflicts.Count > 0)
            {
                var lintResults = new List<LintResult>();
                foreach (var c in conflicts)
                    lintResults.Add(new LintResult("composite-conflict", "warning", "", null, c));
                result = result with { LintResults = result.LintResults.Concat(lintResults).ToList() };
            }

            return result;
        }
        finally
        {
            try
            {
                var dir = await fileWorkspace.GetWorkingDirectoryAsync(compositeId, ct)
                    .ConfigureAwait(false);
                if (dir is not null && Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort cleanup */ }
            finally
            {
                // The composite branch directory is gone — drop its cached profile too, or the
                // cache grows forever keyed by one-shot GUID branch ids.
                profiles.Invalidate(compositeId);
            }
        }
    }

    // ── Command execution with truncation ────────────────────────────────────

    private async Task<BuildResult> RunCommandAsync(
        string workDir,
        (string Command, string BuildSystem) cmd,
        int timeoutSec,
        CancellationToken ct,
        string projectRoot = "")
    {
        var startedAt = DateTimeOffset.UtcNow;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var psi = CreateProcessStartInfo(cmd.Command, workDir);

        using var process = Process.Start(psi)!;

        string stdout;
        string stderr;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            stdout = stdoutTask.Result;
            stderr = stderrTask.Result;
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            stdout = "";
            stderr = $"[TIMEOUT] Command exceeded {timeoutSec}s limit.";
            return new BuildResult(false, -1, stdout, stderr, cmd.BuildSystem, cmd.Command, startedAt, DateTimeOffset.UtcNow, ProjectRoot: projectRoot);
        }

        var stdoutRaw = stdout;
        var stderrRaw = stderr;
        stdout = TruncateOutput(stdout);
        stderr = TruncateOutput(stderr);
        var wasTruncated = stdout != stdoutRaw || stderr != stderrRaw;

        return new BuildResult(
            process.ExitCode == 0,
            process.ExitCode,
            stdout,
            stderr,
            cmd.BuildSystem,
            cmd.Command,
            startedAt,
            DateTimeOffset.UtcNow,
            wasTruncated,
            projectRoot);
    }

    private async Task<TestResult> RunTestCommandAsync(
        string workDir,
        (string Command, string BuildSystem) cmd,
        int timeoutSec,
        CancellationToken ct,
        string projectRoot = "")
    {
        var buildResult = await RunCommandAsync(workDir, cmd, timeoutSec, ct, projectRoot);

        var total = 0;
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        TryParseTestOutput(buildResult.StdOut, cmd.BuildSystem, ref total, ref passed, ref failed, ref skipped);

        return new TestResult(
            buildResult.Success && failed == 0,
            buildResult.ExitCode,
            total, passed, failed, skipped,
            buildResult.StdOut,
            buildResult.BuildSystem,
            cmd.Command,
            buildResult.StartedAt,
            buildResult.CompletedAt,
            buildResult.Truncated,
            projectRoot);
    }

    internal static void TryParseTestOutput(
        string stdout, string? buildSystem,
        ref int total, ref int passed, ref int failed, ref int skipped)
    {
        switch (buildSystem)
        {
            case "dotnet":
                // "Passed!  - Failed: 0, Passed: 47, Skipped: 0, Total: 47"
                var dotnetMatch = Regex.Match(stdout,
                    @"Failed:\s*(\d+).*?Passed:\s*(\d+).*?Skipped:\s*(\d+).*?Total:\s*(\d+)",
                    RegexOptions.Singleline);
                if (dotnetMatch.Success)
                {
                    failed  = int.Parse(dotnetMatch.Groups[1].Value);
                    passed  = int.Parse(dotnetMatch.Groups[2].Value);
                    skipped = int.Parse(dotnetMatch.Groups[3].Value);
                    total   = int.Parse(dotnetMatch.Groups[4].Value);
                }
                break;

            case "cargo":
                // "test result: ok. 23 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out"
                var cargoMatch = Regex.Match(stdout,
                    @"(\d+)\s+passed;\s*(\d+)\s+failed;\s*(\d+)\s+ignored",
                    RegexOptions.Singleline);
                if (cargoMatch.Success)
                {
                    passed  = int.Parse(cargoMatch.Groups[1].Value);
                    failed  = int.Parse(cargoMatch.Groups[2].Value);
                    skipped = int.Parse(cargoMatch.Groups[3].Value);
                    total   = passed + failed + skipped;
                }
                break;

            case "pytest":
                // "======================= 15 passed, 2 failed in 1.23s ======================="
                var pytestMatch = Regex.Match(stdout,
                    @"(\d+)\s+passed.*?(\d+)\s+failed",
                    RegexOptions.Singleline);
                if (pytestMatch.Success)
                {
                    passed = int.Parse(pytestMatch.Groups[1].Value);
                    failed = int.Parse(pytestMatch.Groups[2].Value);
                    total  = passed + failed + skipped;
                }
                break;

            case "go":
                // "ok  	example.com/pkg	0.123s" or "FAIL	example.com/pkg [build failed]"
                total  = 1;
                passed = stdout.Contains("FAIL") ? 0 : 1;
                failed = total - passed;
                break;
        }
    }

    // ── Output truncation ────────────────────────────────────────────────────

    internal string TruncateOutput(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        var max = options.MaxOutputBytes;
        if (raw.Length <= max)
            return raw;

        var truncatedSize = raw.Length - max;
        var sizeStr = FormatBytes(truncatedSize);

        return options.TruncationMode switch
        {
            "Head" => $"...[truncated {sizeStr}, showing last {FormatBytes(max)}]...\n\n{raw[^max..]}",
            "HeadTail" =>
                $"...[truncated {sizeStr}, showing first {FormatBytes(max / 2)} and last {FormatBytes(max - max / 2)}]...\n\n" +
                $"{raw[..(max / 2)]}\n\n...[snip]...\n\n{raw[^(max - max / 2)..]}",
            _ => $"...[truncated {sizeStr}, showing last {FormatBytes(max)}]...\n\n{raw[^max..]}", // "Tail" default
        };
    }

    internal static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
        };

    // ── Command splitting ────────────────────────────────────────────────────

    internal static (string FileName, string Arguments) SplitCommand(string command)
    {
        if (!command.Contains(' '))
            return (command, "");

        var spaceIndex = command.IndexOf(' ');
        return (command[..spaceIndex], command[(spaceIndex + 1)..]);
    }

    // Windows resolves things like "npm"/"npx"/"yarn" to *.cmd shims that only cmd.exe (or a
    // shell) knows how to launch — CreateProcess called directly with FileName="npm" fails with
    // "The system cannot find the file specified" even though `npm` works fine interactively.
    // Routing every command through "cmd /c" on Windows sidesteps that without having to special-
    // case which commands happen to be batch-file shims today.
    internal static ProcessStartInfo CreateProcessStartInfo(string command, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c {command}";
        }
        else
        {
            var (fileName, arguments) = SplitCommand(command);
            psi.FileName = fileName;
            psi.Arguments = arguments;
        }

        return psi;
    }
}