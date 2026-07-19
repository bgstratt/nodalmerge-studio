using System.Diagnostics;

namespace NodalMerge.Studio.AgentRuntime;

// Route B (plans/organizational-knowledge-and-workgroup-scope.md) — a ONE-SHOT structured LLM
// completion over a CLI transport. This is the piece the insight scan needs so a claude-cli /
// codex-cli Model Profile can run it: the agentic IHarnessExecutor can't (it requires a work unit,
// a materialized branch workspace, a fixed kickoff prompt, and a merge/plan/review harvest), and
// LlmClient only speaks OpenAI/Anthropic HTTP. Lives beside the executors in Harnesses/ so future
// maintainers see the CLI spawn is required for Insights to work on CLI-only setups.
internal sealed record OneShotCliRequest(string? Model, string? ApiKey, string SystemPrompt, string UserPrompt);

internal interface IOneShotCliCompleter
{
    // Same provider key the matching IHarnessExecutor advertises (claude-cli / codex-cli), so the
    // analyzer can pick the completer by the request's provider exactly as goals pick an executor.
    string ProviderKey { get; }

    // Runs a single non-agentic completion and returns the model's final text (expected to be the
    // JSON the caller asked for). Throws on spawn/timeout/nonzero-exit; the caller treats a failure
    // as "no findings this run", the same way the HTTP path treats a missing forced-tool call.
    Task<string> CompleteAsync(OneShotCliRequest request, CancellationToken ct = default);
}

internal static class CliProcessRunner
{
    // Spawn the CLI in a throwaway temp working directory (a one-shot has no repo workspace), capture
    // stdout, enforce a wall-clock timeout, and surface stderr on a nonzero exit. Mirrors the
    // executors' BuildProcessStartInfo wrapping (cmd.exe /c on Windows so a PATH shim launches).
    //
    // The prompt goes on STDIN, never as a command-line argument: cmd.exe truncates an argument at its
    // first newline (found live — a multi-line prompt reached the CLI as only its first line), and
    // stdin is passed through the /c wrapper untouched. Written concurrently with the stdout read so a
    // large prompt can't deadlock against a full stdout pipe.
    public static async Task<string> RunCaptureStdoutAsync(
        string executablePath, IReadOnlyList<string> args,
        (string Key, string Value)? envVar, int timeoutSeconds, string? stdinText, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "nm-oneshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(executablePath);
            }
            else
            {
                psi.FileName = executablePath;
            }
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            if (envVar is { } e)
                psi.EnvironmentVariables[e.Key] = e.Value;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start '{executablePath}'.");

            // Feed the prompt to stdin then close it — fire-and-forget so it runs concurrently with the
            // stdout read below (a full stdout pipe would otherwise block a blocking stdin write).
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(stdinText))
                        await process.StandardInput.WriteAsync(stdinText.AsMemory(), cts.Token).ConfigureAwait(false);
                }
                catch { /* child may exit before we finish writing — harmless */ }
                finally { try { process.StandardInput.Close(); } catch { /* already closed */ } }
            }, cts.Token);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw new TimeoutException($"'{executablePath}' exceeded the {timeoutSeconds}s one-shot limit.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"'{executablePath}' exited {process.ExitCode}: {stderr.Trim()}");
            }
            return stdout;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    // Pull the first balanced {...} JSON object out of arbitrary model text — the CLI has no forced
    // tool-call channel, so the model emits JSON amid possible prose/markdown fences. String-aware so
    // braces inside string literals don't throw off the depth count. Returns null if none found.
    public static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var start = text.IndexOf('{');
        if (start < 0)
            return null;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }
        return null;
    }
}
