using System.Net;
using System.Text;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 1.4 re-plan-the-slice — the orchestrator conversation just registers credentials and
/// exits immediately (this test drives everything else directly, not through a real fan-out
/// cycle); the replanner conversation ("Plan work unit ...", same kickoff prefix
/// PlannerAgentLoop always uses) records a two-slice decomposition for the failed goal.
/// </summary>
internal sealed class ReplanFailedSliceLlmHandler : HttpMessageHandler
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
            : EndTurn();

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string PlannerStep(int step, string firstMsg)
    {
        var wuId = ParseBetween(firstMsg, "Plan work unit ", ". Your agent ID is");

        var planJson = JsonSerializer.Serialize(new
        {
            slices = new object[]
            {
                new
                {
                    sliceId = "s-new-1",
                    goal = "Smaller sub-slice A of the failed goal",
                    fileScope = new[] { "src/A.cs" },
                    dependsOn = Array.Empty<string>(),
                    steps = new[] { "Implement A" },
                },
                new
                {
                    sliceId = "s-new-2",
                    goal = "Smaller sub-slice B of the failed goal",
                    fileScope = new[] { "src/B.cs" },
                    dependsOn = Array.Empty<string>(),
                    steps = new[] { "Implement B" },
                },
            }
        });

        return step switch
        {
            0 => ToolUse("tu-rp-1", McpToolNames.ArtifactRecordPlan, new { workUnitId = wuId, planContent = planJson }),
            _ => EndTurn(),
        };
    }

    private static string ToolUse(string id, string name, object input) =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "tool_use", id, name, input } },
            stop_reason = "tool_use",
        });

    private static string EndTurn() =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Done." } },
            stop_reason = "end_turn",
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
