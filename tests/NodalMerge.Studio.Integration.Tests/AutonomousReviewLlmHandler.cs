using System.Net;
using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Fake HttpMessageHandler for AgentApproval/Hybrid review-policy scenarios. Worker script
/// identical to <see cref="ScriptedLlmHandler"/> (copied rather than shared — see
/// <see cref="ScheduledReinvocationLlmHandler"/> for the established precedent of one handler
/// class per scenario), plus a branch for the reviewer agent (<c>ReviewerAgentLoop</c>'s first
/// message: "Review merge proposal {id} for work unit {id}...").
///
/// Since plans/orchestrator-pure-service.md M2 there is no orchestrator LLM turn to script: the
/// deterministic GoalCoordinator enqueues the planner at spawn, so the entry point here is the
/// planner producing a single-slice plan; fan-out then enqueues the worker.
///
/// Planner script: workunit.get → artifact.record_plan(single slice) → end_turn
/// Worker script: task.update(InProgress) → workunit.get → task.update(Completed)
///                → artifact.record(Research) → merge.propose → merge.validate → end_turn
/// Reviewer script: merge.validate → merge.review(automated=true, decision, verificationResults)
///                  → end_turn
///
/// The reviewer's own merge.validate call is a no-op (and may return an error) if the worker's
/// validate already ran first — same race that exists in production between the post-propose
/// auto-trigger and the worker's own subsequent tool call. Step indices don't branch on tool
/// result content, so either ordering reaches the same end_turn.
/// </summary>
internal sealed class AutonomousReviewLlmHandler(string reviewerDecision, string reviewerNotes) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");

        var step = (messages.GetArrayLength() - 1) / 2;
        var firstMsg = messages[0].GetProperty("content").GetString() ?? "";

        var json = firstMsg.StartsWith("Plan work unit")
            ? PlannerStep(step, firstMsg)
            : firstMsg.StartsWith("Review merge proposal")
                ? ReviewerStep(step, firstMsg)
                : WorkerStep(step, firstMsg, messages);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    // ── Planner ───────────────────────────────────────────────────────────────

    private static string PlannerStep(int step, string firstMsg)
    {
        var wuId = ParseBetween(firstMsg, "Plan work unit ", ". Your agent ID is");
        var planJson = JsonSerializer.Serialize(new
        {
            slices = new object[]
            {
                new
                {
                    sliceId = "s1",
                    goal = "Implement the hello world feature",
                    fileScope = new[] { "src/Hello.cs" },
                    dependsOn = Array.Empty<string>(),
                    steps = new[] { "Create Hello.cs" }
                }
            }
        });
        return step switch
        {
            0 => ToolUse("tu-p-1", "nm_v1_workunit_get", new { workUnitId = wuId }),
            1 => ToolUse("tu-p-2", "nm_v1_artifact_record_plan", new
            {
                workUnitId = wuId,
                planContent = planJson
            }),
            _ => EndTurn()
        };
    }

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
            _ => EndTurn()
        };
    }

    // ── Reviewer ──────────────────────────────────────────────────────────────

    private string ReviewerStep(int step, string firstMsg)
    {
        var proposalId = ParseBetween(firstMsg, "Review merge proposal ", " for work unit ");
        return step switch
        {
            0 => ToolUse("tu-r-1", "nm_v1_merge_validate", new { proposalId }),
            1 => ToolUse("tu-r-2", "nm_v1_merge_review", new
            {
                proposalId,
                decision = reviewerDecision,
                verificationResults = reviewerNotes,
                automated = true
            }),
            _ => EndTurn()
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
