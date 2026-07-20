using System.Text.Json;

namespace NodalMerge.Studio.AgentRuntime;

// Route B — one-shot claude-cli completion for the insight scan. Non-agentic: no --settings/tools,
// no MCP mount, no workspace. The system prompt is folded into the -p prompt (avoiding reliance on a
// specific --append-system-prompt flag), and --output-format json wraps the model's final text in a
// {"result": "..."} envelope we unwrap. Shares ClaudeCodeExecutorOptions (ExecutablePath/Timeout) so
// a stub-CLI test overrides the same knob the executor tests use.
internal sealed class ClaudeCliOneShotCompleter(ClaudeCodeExecutorOptions options) : IOneShotCliCompleter
{
    public string ProviderKey => "claude-cli";

    // The full prompt (system + context + JSON instruction) is piped to stdin — the idiomatic
    // `<data> | claude -p "<query>"` shape — so nothing multi-line ever hits the command line. The
    // -p query is a single short line safe through cmd.exe.
    private const string QueryArg =
        "Analyze the data provided on standard input and respond with ONLY the JSON object it specifies — no prose, no code fences.";

    public async Task<string> CompleteAsync(OneShotCliRequest request, CancellationToken ct = default)
    {
        var stdin = string.IsNullOrWhiteSpace(request.SystemPrompt)
            ? request.UserPrompt
            : request.SystemPrompt + "\n\n" + request.UserPrompt;

        var args = new List<string> { "-p", QueryArg, "--output-format", "json" };
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.Add("--model");
            args.Add(request.Model);
        }

        // Ambient CLI auth by default; a stored key opts into ANTHROPIC_API_KEY injection — the same
        // convention ClaudeCodeExecutor.BuildProcessStartInfo uses.
        (string, string)? env = string.IsNullOrEmpty(request.ApiKey) ? null : ("ANTHROPIC_API_KEY", request.ApiKey);

        var stdout = await CliProcessRunner.RunCaptureStdoutAsync(
            options.ExecutablePath, args, env, options.TimeoutSeconds, stdinText: stdin, ct).ConfigureAwait(false);

        // claude --output-format json → {"type":"result","result":"<final text>", ...}. Unwrap
        // .result; if the output isn't that envelope (a different CLI version), fall back to raw stdout
        // and let the caller's JSON extraction cope.
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("result", out var resultEl)
                && resultEl.ValueKind == JsonValueKind.String)
                return resultEl.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // not the JSON envelope — use raw stdout
        }
        return stdout;
    }
}
