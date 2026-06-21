using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Storage;

// Phase 9c — see plans/phase-9-workspace-profile.md. Owns process lifecycle for "run" specifically,
// separate from WorkspaceExecutionService's build/test execution: a dev server (`npm run dev`,
// `dotnet run` on a Web SDK project) never exits on its own, so it cannot go through the
// Process.Start + WaitForExitAsync(timeout) path build/test use — that just blocks until the
// timeout kills it. Long-running processes here are started detached (no wait), tracked by pid, and
// stopped explicitly. Entirely in-memory and not durable — a Host restart kills whatever it
// started, which is correct: there's nothing to resume a dev server *into*.
internal sealed class RunningProcessRegistry
{
    private const int OutputCapBytes = 16 * 1024;

    private sealed class Entry
    {
        public required Process Process { get; init; }
        public required string BranchId { get; init; }
        public required string ProjectRoot { get; init; }
        public required string Command { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public readonly StringBuilder Output = new();
        public readonly object Lock = new();
    }

    private readonly ConcurrentDictionary<int, Entry> _running = new();

    public BuildResult StartLongRunning(
        string branchId,
        string projectRoot,
        string command,
        string workDir,
        string? buildSystem,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        // Replace, don't leak: a prior process for this exact branch+root is stale once we're
        // about to start a fresh one (e.g. the agent edited code and wants to re-run the server).
        Stop(branchId, pid: null, projectRoot: projectRoot);

        var (fileName, arguments) = WorkspaceExecutionService.SplitCommand(command);
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
        if (environmentVariables is not null)
            foreach (var (key, value) in environmentVariables)
                psi.Environment[key] = value;

        var startedAt = DateTimeOffset.UtcNow;
        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            return new BuildResult(
                false, -1, "", ex.Message, buildSystem, command, startedAt, DateTimeOffset.UtcNow,
                ProjectRoot: projectRoot);
        }

        var entry = new Entry
        {
            Process = process,
            BranchId = branchId,
            ProjectRoot = projectRoot,
            Command = command,
            StartedAt = startedAt,
        };
        _running[process.Id] = entry;

        process.OutputDataReceived += (_, e) => Append(entry, e.Data);
        process.ErrorDataReceived  += (_, e) => Append(entry, e.Data);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _running.TryRemove(process.Id, out _);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new BuildResult(
            true, 0, Snapshot(entry), "", buildSystem, command, startedAt, DateTimeOffset.UtcNow,
            ProjectRoot: projectRoot, Running: true, Pid: process.Id);
    }

    public async Task<BuildResult> RunOnceAsync(
        string projectRoot,
        string command,
        string workDir,
        string? buildSystem,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var (fileName, arguments) = WorkspaceExecutionService.SplitCommand(command);
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
        if (environmentVariables is not null)
            foreach (var (key, value) in environmentVariables)
                psi.Environment[key] = value;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var process = Process.Start(psi)!;
        string stdout, stderr;
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
            return new BuildResult(
                false, -1, "", $"[TIMEOUT] Command exceeded {timeoutSeconds}s limit.",
                buildSystem, command, startedAt, DateTimeOffset.UtcNow, ProjectRoot: projectRoot);
        }

        return new BuildResult(
            process.ExitCode == 0, process.ExitCode, stdout, stderr,
            buildSystem, command, startedAt, DateTimeOffset.UtcNow, ProjectRoot: projectRoot);
    }

    /// <summary>Stops tracked processes for a branch. Narrows by pid and/or projectRoot when given; stops all matches otherwise. Returns the count stopped.</summary>
    public int Stop(string branchId, int? pid = null, string? projectRoot = null)
    {
        var matches = _running.Values
            .Where(e => e.BranchId == branchId
                && (pid is null || e.Process.Id == pid)
                && (projectRoot is null || e.ProjectRoot == projectRoot))
            .ToList();

        foreach (var entry in matches)
        {
            try { entry.Process.Kill(entireProcessTree: true); } catch { }
            _running.TryRemove(entry.Process.Id, out _);
        }
        return matches.Count;
    }

    private static void Append(Entry entry, string? line)
    {
        if (line is null) return;
        lock (entry.Lock)
        {
            entry.Output.AppendLine(line);
            if (entry.Output.Length > OutputCapBytes)
                entry.Output.Remove(0, entry.Output.Length - OutputCapBytes);
        }
    }

    private static string Snapshot(Entry entry)
    {
        lock (entry.Lock) return entry.Output.ToString();
    }
}
