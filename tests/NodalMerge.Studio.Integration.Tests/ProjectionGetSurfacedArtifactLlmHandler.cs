using System.Net;
using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 23 — scripts a single WorkerAgentLoop turn to call nm_v1_projection_get
/// (projectionType=AgentWorkspace) against the work unit it was given, then end_turn. Used to prove
/// the projection-read path actually emits ArtifactSurfaced events for domain-agent-authored
/// artifacts present in the response.
/// </summary>
internal sealed class ProjectionGetSurfacedArtifactLlmHandler : HttpMessageHandler
{
    private string? _workUnitId;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        var step = (messages.GetArrayLength() - 1) / 2;

        _workUnitId ??= ExtractWorkUnitId(messages);

        var json = step switch
        {
            0 => ToolUse("tu-1", "nm_v1_projection_get", new { projectionType = "AgentWorkspace", workUnitId = _workUnitId }),
            _ => EndTurn(),
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string ExtractWorkUnitId(JsonElement messages)
    {
        var firstMsg = messages[0].GetProperty("content").GetString() ?? "";
        var start = "for work unit ";
        var si = firstMsg.IndexOf(start, StringComparison.Ordinal);
        if (si < 0) return "";
        si += start.Length;
        var ei = firstMsg.IndexOf(".", si, StringComparison.Ordinal);
        return ei < 0 ? firstMsg[si..] : firstMsg[si..ei];
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
}
