using System.Net;
using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Fake HttpMessageHandler that replays a deterministic tool-use script for the
/// orchestrator and worker agent loops. Identifies each conversation by its initial
/// user message and drives the full cycle without hitting a real LLM.
///
/// Orchestrator script: workunit.get → task.create → agent.spawn → end_turn
/// Worker script: task.update(InProgress) → workunit.get → task.update(Completed)
///                → merge.propose → merge.validate → end_turn
/// </summary>
internal sealed class ScriptedLlmHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");

        // Step index: 0 on first call, +1 for each subsequent call within the same conversation.
        // Pattern: [user] → step 0, [user,asst,user] → step 1, etc.
        var step = (messages.GetArrayLength() - 1) / 2;
        var firstMsg = messages[0].GetProperty("content").GetString() ?? "";

        var json = firstMsg.StartsWith("Begin orchestrating")
            ? OrchestratorStep(step, firstMsg, messages)
            : WorkerStep(step, firstMsg, messages);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    // ── Orchestrator ──────────────────────────────────────────────────────────

    private static string OrchestratorStep(int step, string firstMsg, JsonElement messages)
    {
        var wuId = ParseBetween(firstMsg, "Begin orchestrating work unit ", ". Your agent ID is");
        return step switch
        {
            0 => ToolUse("tu-o-1", "nm.v1.workunit.get", new { workUnitId = wuId }),
            1 => ToolUse("tu-o-2", "nm.v1.task.create", new
            {
                workUnitId = wuId,
                title = "Execute the goal",
                description = "Complete all work required for this work unit"
            }),
            2 => ToolUse("tu-o-3", "nm.v1.agent.spawn", new
            {
                agentType = "worker",
                workUnitId = wuId,
                taskId = ExtractFromToolResult(messages, "taskId") ?? "unknown",
                model = "fake-model",
                baseUrl = "http://fake-llm",
                apiKey = "fake-key"
            }),
            _ => EndTurn()
        };
    }

    // ── Worker ────────────────────────────────────────────────────────────────

    private static string WorkerStep(int step, string firstMsg, JsonElement messages)
    {
        var taskId = ParseBetween(firstMsg, "Execute task ", " for work unit ");
        var wuId   = ParseBetween(firstMsg, "for work unit ", ". Your agent ID is");
        return step switch
        {
            0 => ToolUse("tu-w-1", "nm.v1.task.update", new { taskId, status = "InProgress" }),
            1 => ToolUse("tu-w-2", "nm.v1.workunit.get", new { workUnitId = wuId }),
            2 => ToolUse("tu-w-3", "nm.v1.task.update", new { taskId, status = "Completed" }),
            3 => ToolUse("tu-w-4", "nm.v1.merge.propose", new
            {
                // WorkUnit serialises BranchId in PascalCase with default JsonSerializer options.
                sourceBranch = ExtractFromToolResult(messages, "BranchId")
                            ?? ExtractFromToolResult(messages, "branchId")
                            ?? "unknown-branch",
                targetBranch = "main",
                summary = "Completed the assigned task for the work unit"
            }),
            4 => ToolUse("tu-w-5", "nm.v1.merge.validate", new
            {
                proposalId = ExtractFromToolResult(messages, "proposalId") ?? "unknown"
            }),
            _ => EndTurn()
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches all tool_result entries in the message history (newest first) for a JSON
    /// property with the given key and returns its string value.
    /// </summary>
    private static string? ExtractFromToolResult(JsonElement messages, string key)
    {
        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (!msg.TryGetProperty("role", out var role) || role.GetString() != "user") continue;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type) || type.GetString() != "tool_result") continue;
                if (!item.TryGetProperty("content", out var toolContent)) continue;
                var s = toolContent.GetString();
                if (s is null) continue;
                try
                {
                    using var d = JsonDocument.Parse(s);
                    if (d.RootElement.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                        return val.GetString();
                }
                catch { /* malformed tool result — skip */ }
            }
        }
        return null;
    }

    private static string ToolUse(string id, string name, object input) =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "tool_use", id, name, input } },
            stop_reason = "tool_use"
        });

    private static string EndTurn() =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Done." } },
            stop_reason = "end_turn"
        });

    private static string ParseBetween(string s, string start, string end)
    {
        var si = s.IndexOf(start, StringComparison.Ordinal);
        if (si < 0) return "";
        si += start.Length;
        var ei = s.IndexOf(end, si, StringComparison.Ordinal);
        return ei < 0 ? s[si..] : s[si..ei];
    }
}
