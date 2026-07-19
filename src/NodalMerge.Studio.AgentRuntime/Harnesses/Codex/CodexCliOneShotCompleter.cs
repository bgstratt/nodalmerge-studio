using System.Text.Json;

namespace NodalMerge.Studio.AgentRuntime;

// Route B — one-shot codex-cli completion for the insight scan. `codex exec --json` emits an
// item.completed event stream; the model's final answer is the last agent_message item's `text`
// (verified event shape, see CodexTranscriptParser: item.completed → item.type=="agent_message" →
// "text"). We take the last such text. stdin MUST be closed (codex hangs otherwise — the same
// verified quirk CodexCliExecutor relies on). Shares CodexCliExecutorOptions.
internal sealed class CodexCliOneShotCompleter(CodexCliExecutorOptions options) : IOneShotCliCompleter
{
    public string ProviderKey => "codex-cli";

    // Short single-line positional prompt safe through cmd.exe; the full prompt (system + context +
    // JSON instruction) is piped to stdin (codex exec reads stdin as input) so nothing multi-line
    // hits the command line — same fix as the claude completer.
    private const string QueryArg =
        "Analyze the data provided on standard input and respond with ONLY the JSON object it specifies — no prose, no code fences.";

    public async Task<string> CompleteAsync(OneShotCliRequest request, CancellationToken ct = default)
    {
        var stdin = string.IsNullOrWhiteSpace(request.SystemPrompt)
            ? request.UserPrompt
            : request.SystemPrompt + "\n\n" + request.UserPrompt;

        // --skip-git-repo-check because the throwaway temp workdir isn't a git repo. Positional prompt
        // goes last (verified arg ordering).
        var args = new List<string>
        {
            "exec", "--json", "--skip-git-repo-check", "-s", options.SandboxMode,
        };
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.Add("-m");
            args.Add(request.Model);
        }
        args.Add(QueryArg);

        (string, string)? env = string.IsNullOrEmpty(request.ApiKey) ? null : ("OPENAI_API_KEY", request.ApiKey);

        var stdout = await CliProcessRunner.RunCaptureStdoutAsync(
            options.ExecutablePath, args, env, options.TimeoutSeconds, stdinText: stdin, ct).ConfigureAwait(false);

        // Scan the JSONL event stream for the last agent_message text — the final model answer.
        string? lastAgentText = null;
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "item.completed"
                    && root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var itemType) && itemType.GetString() == "agent_message"
                    && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    lastAgentText = text.GetString();
            }
            catch (JsonException)
            {
                // a non-JSON or partial line — ignore, same defensive stance CodexTranscriptParser takes
            }
        }
        return lastAgentText ?? string.Empty;
    }
}
