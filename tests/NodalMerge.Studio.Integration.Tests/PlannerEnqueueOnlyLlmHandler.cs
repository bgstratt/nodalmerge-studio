using System.Net;
using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Fake LLM scripting the orchestrator's actual recommended first-time flow for a brand-new
/// work unit (DefaultSystemPrompt in OrchestratorAgentLoop.cs): <paramref name="readOnlyCalls"/>
/// read-only inspection calls (nm_v1_workunit_get, then nm_v1_projection_get — both are offered
/// by the prompt as valid ways to understand state before routing), then
/// nm_v1_scheduler_enqueue(profileId="planner") on its own workUnitId, then stop. None of the
/// read-only steps touch the artifact chain — that's the sequence that used to trip the stall
/// detector before it could legitimately reach end_turn (2 read-only calls in a row already
/// burned the old 2-cycle budget; see plans/phase-6-policy-routing-conflicts.md history).
///
/// Any other conversation (the planner job this script enqueues, or a scheduler re-invocation
/// of the orchestrator after that) just ends its turn immediately — this fake only cares about
/// exercising the original orchestrator agent's first few calls.
/// </summary>
internal sealed class PlannerEnqueueOnlyLlmHandler(int readOnlyCalls = 1) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        var step = (messages.GetArrayLength() - 1) / 2;
        var firstMsg = messages[0].GetProperty("content").GetString() ?? "";

        var json = firstMsg.StartsWith("Begin orchestrating") && step <= readOnlyCalls
            ? OrchestratorStep(step, firstMsg)
            : EndTurn();

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private string OrchestratorStep(int step, string firstMsg)
    {
        var wuId = ParseBetween(firstMsg, "Begin orchestrating work unit ", ". Your agent ID is");
        if (step < readOnlyCalls)
        {
            return step % 2 == 0
                ? ToolUse($"tu-o-get-{step}", "nm_v1_workunit_get", new { workUnitId = wuId })
                : ToolUse($"tu-o-proj-{step}", "nm_v1_projection_get",
                    new { projectionType = "AgentWorkspace", workUnitId = wuId });
        }

        return ToolUse("tu-o-enqueue", "nm_v1_scheduler_enqueue", new { workUnitId = wuId, profileId = "planner" });
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
