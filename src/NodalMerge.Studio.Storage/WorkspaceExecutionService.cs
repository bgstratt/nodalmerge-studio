using System.Diagnostics;
using System.Text.RegularExpressions;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class WorkspaceExecutionService(
    IFileWorkspaceService fileWorkspace,
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
        {
            var commands = ResolveBuildCommands(request, workDir);
            foreach (var cmd in commands)
            {
                var result = await RunCommandAsync(workDir, cmd, request.TimeoutSeconds, ct);
                builds.Add(result);
            }
        }

        if (request.Test)
        {
            var commands = ResolveTestCommands(request, workDir);
            foreach (var cmd in commands)
            {
                var result = await RunTestCommandAsync(workDir, cmd, request.TimeoutSeconds, ct);
                tests.Add(result);
            }
        }

        return new BranchExecutionResult(
            branchId, builds, tests, [],
            builds.All(b => b.Success) && tests.All(t => t.Success),
            DateTimeOffset.UtcNow);
    }

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
        }
    }

    // ── Auto-detection ───────────────────────────────────────────────────────

    internal static IReadOnlyList<(string Command, string BuildSystem)> ResolveBuildCommands(
        WorkspaceExecutionRequest request, string workDir)
    {
        if (request.BuildCommand is { Length: > 0 })
            return [(request.BuildCommand, null!)];

        if (!request.AllowAutoDetect)
            return [];

        var detected = new List<(string, string)>();

        if (Directory.EnumerateFiles(workDir, "*.csproj", SearchOption.AllDirectories).Any() ||
            Directory.EnumerateFiles(workDir, "*.slnx", SearchOption.AllDirectories).Any())
            detected.Add(("dotnet build", "dotnet"));

        if (File.Exists(Path.Combine(workDir, "Cargo.toml")))
            detected.Add(("cargo build", "cargo"));

        if (File.Exists(Path.Combine(workDir, "package.json")))
            detected.Add((
                Directory.Exists(Path.Combine(workDir, "node_modules"))
                    ? "npm run build"
                    : "npm install && npm run build",
                "npm"));

        if (File.Exists(Path.Combine(workDir, "go.mod")))
            detected.Add(("go build ./...", "go"));

        if (File.Exists(Path.Combine(workDir, "Makefile")))
            detected.Add(("make", "make"));

        if (File.Exists(Path.Combine(workDir, "CMakeLists.txt")))
            detected.Add(("cmake --build build", "cmake"));

        return detected;
    }

    internal static IReadOnlyList<(string Command, string BuildSystem)> ResolveTestCommands(
        WorkspaceExecutionRequest request, string workDir)
    {
        if (request.TestCommand is { Length: > 0 })
            return [(request.TestCommand, null!)];

        if (!request.AllowAutoDetect)
            return [];

        var detected = new List<(string, string)>();

        if (Directory.EnumerateFiles(workDir, "*.csproj", SearchOption.AllDirectories).Any() ||
            Directory.EnumerateFiles(workDir, "*.slnx", SearchOption.AllDirectories).Any())
            detected.Add(("dotnet test", "dotnet"));

        if (File.Exists(Path.Combine(workDir, "Cargo.toml")))
            detected.Add(("cargo test", "cargo"));

        if (File.Exists(Path.Combine(workDir, "package.json")))
            detected.Add(("npm test", "npm"));

        if (File.Exists(Path.Combine(workDir, "go.mod")))
            detected.Add(("go test ./...", "go"));

        if (File.Exists(Path.Combine(workDir, "pyproject.toml")))
            detected.Add(("pytest", "pytest"));

        if (File.Exists(Path.Combine(workDir, "Makefile")))
            detected.Add(("make test", "make"));

        return detected;
    }

    // ── Command execution with truncation ────────────────────────────────────

    private async Task<BuildResult> RunCommandAsync(
        string workDir,
        (string Command, string BuildSystem) cmd,
        int timeoutSec,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var (fileName, arguments) = SplitCommand(cmd.Command);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

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
            return new BuildResult(false, -1, stdout, stderr, cmd.BuildSystem, cmd.Command, startedAt, DateTimeOffset.UtcNow);
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
            wasTruncated);
    }

    private async Task<TestResult> RunTestCommandAsync(
        string workDir,
        (string Command, string BuildSystem) cmd,
        int timeoutSec,
        CancellationToken ct)
    {
        var buildResult = await RunCommandAsync(workDir, cmd, timeoutSec, ct);

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
            buildResult.Truncated);
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
}