using System.Net;
using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 1.4 Continue-track — behaves exactly like <see cref="ExhaustingLlmHandler"/> (always
/// tool_use, never end_turn) UNLESS the incoming message array is already longer than any request
/// the original exhausted run could have produced (with MaxIterations: 2 and one tool call per
/// cycle, the original run's longest request has 3 messages: kickoff + assistant + tool-result).
/// A request longer than that can only be the Continue-track's reconstructed-prior-context
/// request, so ending the turn there — instead of exhausting again — proves the resumed
/// conversation actually carried the prior attempt's history forward rather than starting over
/// (a silent restart would also produce a 1-message first request and would exhaust identically).
/// </summary>
internal sealed class ContinueLlmHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");

        var json = messages.GetArrayLength() > 3
            ? EndTurn()
            : ToolUse("tu-cont", "nm_v1_workunit_get", new { workUnitId = "placeholder" });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
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
            content = new[] { new { type = "text", text = "Continuing from where I left off. Done." } },
            stop_reason = "end_turn",
        });
}
