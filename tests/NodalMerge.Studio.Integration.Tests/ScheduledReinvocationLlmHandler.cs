using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Fake HttpMessageHandler for the scheduler-driven re-invocation test. Unlike
/// <see cref="ScriptedLlmHandler"/>, the orchestrator here calls nm_v1_scheduler_enqueue
/// (not the legacy nm_v1_agent_spawn) and is expected to be re-invoked by
/// WorkSchedulerService.ReleaseAsync after the worker completes — a second, independent
/// OrchestratorAgentLoop.RunAsync call with its own fresh conversation (step resets to 0).
///
/// Because each conversation looks identical from a pure step-count view, an external
/// per-work-unit invocation counter distinguishes "first orchestration" from "re-invoked
/// orchestration" so the two get different scripts:
///   Invocation 1: workunit.get → task.create → scheduler.enqueue → end_turn
///   Invocation 2+: projection.get → end_turn (simulating "the proposal already exists, stop")
///
/// Worker script is unchanged from ScriptedLlmHandler: task.update(InProgress) → workunit.get →
/// task.update(Completed) → artifact.record(Research) → merge.propose → merge.validate → end_turn
/// </summary>
internal sealed class ScheduledReinvocationLlmHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _orchestratorInvocations = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");

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

    private string OrchestratorStep(int step, string firstMsg, JsonElement messages)
    {
        var wuId = ParseBetween(firstMsg, "Begin orchestrating work unit ", ". Your agent ID is");

        // A new conversation (step == 0) means a fresh OrchestratorAgentLoop.RunAsync call —
        // either the original spawn or a re-invocation. Bump the counter once per conversation.
        var invocation = step == 0
            ? _orchestratorInvocations.AddOrUpdate(wuId, 1, (_, n) => n + 1)
            : _orchestratorInvocations.GetOrAdd(wuId, 1);

        return invocation == 1
            ? FirstInvocation(step, wuId, messages)
            : ReinvokedInvocation(step, wuId);
    }

    private static string FirstInvocation(int step, string wuId, JsonElement messages) => step switch
    {
        0 => ToolUse("tu-o-1", "nm_v1_workunit_get", new { workUnitId = wuId }),
        1 => ToolUse("tu-o-2", "nm_v1_task_create", new
        {
            workUnitId = wuId,
            title = "Execute the goal",
            description = "Complete all work required for this work unit"
        }),
        2 => ToolUse("tu-o-3", "nm_v1_scheduler_enqueue", new
        {
            workUnitId = wuId,
            profileId = "worker",
            taskId = ExtractFromToolResult(messages, "taskId") ?? "unknown"
        }),
        _ => EndTurn(),
    };

    private static string ReinvokedInvocation(int step, string wuId) => step switch
    {
        0 => ToolUse("tu-o-r1", "nm_v1_projection_get", new { projectionType = "AgentWorkspace", workUnitId = wuId }),
        _ => EndTurn(),
    };

    // ── Worker (identical script to ScriptedLlmHandler) ─────────────────────────

    private static string WorkerStep(int step, string firstMsg, JsonElement messages)
    {
        var taskId = ParseBetween(firstMsg, "Execute task ", " for work unit ");
        var wuId   = ParseBetween(firstMsg, "for work unit ", ". Your agent ID is");
        var agentId = ParseBetween(firstMsg, "Your agent ID is ", ".");
        return step switch
        {
            0 => ToolUse("tu-w-1", "nm_v1_task_update", new { taskId, status = "InProgress" }),
            1 => ToolUse("tu-w-2", "nm_v1_workunit_get", new { workUnitId = wuId }),
            2 => ToolUse("tu-w-3", "nm_v1_task_update", new { taskId, status = "Completed" }),
            3 => ToolUse("tu-w-4", "nm_v1_artifact_record", new
            {
                workUnitId = wuId,
                type = "Research",
                title = "Stack",
                body = "Codebase uses .NET 8; no Redis present."
            }),
            4 => ToolUse("tu-w-5", "nm_v1_merge_propose", new
            {
                sourceBranch = ExtractFromToolResult(messages, "BranchId")
                            ?? ExtractFromToolResult(messages, "branchId")
                            ?? "unknown-branch",
                targetBranch = "main",
                summary = "Completed the assigned task for the work unit",
                workUnitId = wuId,
                agentId
            }),
            5 => ToolUse("tu-w-6", "nm_v1_merge_validate", new
            {
                proposalId = ExtractFromToolResult(messages, "proposalId") ?? "unknown"
            }),
            _ => EndTurn(),
        };
    }

    // ── Helpers (same shape as ScriptedLlmHandler) ──────────────────────────────

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
