using System.Net;
using System.Text;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Minimal deterministic fake for the inline child auto-review test: a goal that fans out into exactly
/// ONE leaf child, whose own AgentApproval proposal must inline-auto-review + auto-apply → Merged with
/// no manual step. Single child = no concurrent inline reviews, so the assertion is stable.
/// </summary>
internal sealed class SingleChildAutoReviewLlmHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        var step = (messages.GetArrayLength() - 1) / 2;
        var firstMsg = messages[0].GetProperty("content").GetString() ?? "";

        var json =
            firstMsg.StartsWith("Plan work unit", StringComparison.Ordinal) ? PlannerStep(step, firstMsg) :
            firstMsg.StartsWith("Review merge proposal", StringComparison.Ordinal) ? ReviewerStep(step, firstMsg) :
            firstMsg.StartsWith("Begin orchestrating", StringComparison.Ordinal) ? OrchestratorStep(step, firstMsg) :
            WorkerStep(step, firstMsg, messages);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static string OrchestratorStep(int step, string firstMsg)
    {
        var wuId = ParseBetween(firstMsg, "Begin orchestrating work unit ", ". Your agent ID is");
        return step switch
        {
            0 => ToolUse("tu-o-1", "nm_v1_workunit_get", new { workUnitId = wuId }),
            1 => ToolUse("tu-o-2", "nm_v1_scheduler_enqueue", new { workUnitId = wuId, profileId = "planner" }),
            _ => EndTurn(),
        };
    }

    private static string PlannerStep(int step, string firstMsg)
    {
        var wuId = ParseBetween(firstMsg, "Plan work unit ", ". Your agent ID is");
        var plan = JsonSerializer.Serialize(new
        {
            slices = new object[]
            {
                new
                {
                    sliceId = "only",
                    goal = "Implement Solo.cs",
                    fileScope = new[] { "src/Solo.cs" },
                    dependsOn = Array.Empty<string>(),
                    steps = new[] { "Create Solo.cs" },
                },
            },
        });
        return step switch
        {
            0 => ToolUse("tu-p-1", "nm_v1_workunit_get", new { workUnitId = wuId }),
            1 => ToolUse("tu-p-2", McpToolNames.ArtifactRecordPlan, new { workUnitId = wuId, planContent = plan }),
            _ => EndTurn(),
        };
    }

    private static string WorkerStep(int step, string firstMsg, JsonElement messages)
    {
        var taskId = ParseBetween(firstMsg, "Execute task ", " for work unit ");
        var wuId = ParseBetween(firstMsg, "for work unit ", ". Your agent ID is");
        var agentId = ParseBetween(firstMsg, "Your agent ID is ", ".");
        var branchId = step >= 1 ? ExtractFromToolResult(messages, "BranchId") ?? ExtractFromToolResult(messages, "branchId") : null;

        return step switch
        {
            0 => ToolUse("tu-w-1", "nm_v1_task_update", new { taskId, status = "InProgress" }),
            1 => ToolUse("tu-w-2", "nm_v1_workunit_get", new { workUnitId = wuId }),
            2 => ToolUse("tu-w-3", "nm_v1_workspace_write", new { branchId, path = "src/Solo.cs", content = "class Solo {}" }),
            3 => ToolUse("tu-w-4", "nm_v1_task_update", new { taskId, status = "Completed" }),
            4 => ToolUse("tu-w-5", "nm_v1_merge_propose", new
            {
                sourceBranch = branchId ?? "unknown-branch",
                targetBranch = "main",
                summary = "Completed Solo",
                workUnitId = wuId,
                agentId,
            }),
            5 => ToolUse("tu-w-6", "nm_v1_merge_validate", new
            {
                proposalId = ExtractFromToolResult(messages, "proposalId") ?? "unknown",
            }),
            _ => EndTurn(),
        };
    }

    private static string ReviewerStep(int step, string firstMsg)
    {
        var proposalId = ParseBetween(firstMsg, "Review merge proposal ", " for work unit");
        // Step-count, not a body substring scan: the reviewer's kickoff request already lists
        // nm_v1_merge_review in its available-tools array, so a body.Contains(...) check
        // false-positives on turn 0 and the reviewer EndTurns without ever deciding → Review
        // dead-letter. Mirror AutomatedReviewFanOutLlmHandler: submit on step 0, EndTurn after.
        return step switch
        {
            0 => ToolUse("tu-r-1", "nm_v1_merge_review", new
            {
                proposalId,
                decision = "Approved",
                automated = true,
                verificationResults = "Looks good; goal satisfied.",
            }),
            _ => EndTurn(),
        };
    }

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
                catch { /* skip malformed */ }
            }
        }
        return null;
    }

    private static string ToolUse(string id, string name, object input) =>
        JsonSerializer.Serialize(new { content = new[] { new { type = "tool_use", id, name, input } }, stop_reason = "tool_use" });

    private static string EndTurn() =>
        JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = "Done." } }, stop_reason = "end_turn" });

    private static string ParseBetween(string s, string start, string end)
    {
        var si = s.IndexOf(start, StringComparison.Ordinal);
        if (si < 0) return "";
        si += start.Length;
        var ei = s.IndexOf(end, si, StringComparison.Ordinal);
        return ei < 0 ? s[si..] : s[si..ei];
    }
}
