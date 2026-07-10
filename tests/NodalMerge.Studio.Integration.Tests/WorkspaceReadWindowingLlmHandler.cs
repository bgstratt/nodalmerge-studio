using System.Net;
using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Scripts a single WorkerAgentLoop turn-by-turn: workunit_get → workspace_read (no offset/limit,
/// exercises the default 2000-line window) → workspace_read (offset past the window, to page
/// through the rest) → end_turn. Captures both tool results so the test can assert on paging.
/// </summary>
internal sealed class WorkspaceReadWindowingLlmHandler : HttpMessageHandler
{
    public required string Path { get; init; }
    public required int SecondOffset { get; init; }

    public string? FirstReadResult { get; private set; }
    public string? SecondReadResult { get; private set; }

    private string? _branchId;
    private string? _workUnitId;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        var step = (messages.GetArrayLength() - 1) / 2;

        _workUnitId ??= ExtractWorkUnitId(messages);
        if (step >= 1 && _branchId is null)
            _branchId = ExtractFromLastToolResult(messages, "branchId") ?? ExtractFromLastToolResult(messages, "BranchId");
        if (step == 2) FirstReadResult = LastToolResultText(messages);
        if (step == 3) SecondReadResult = LastToolResultText(messages);

        var json = step switch
        {
            0 => ToolUse("tu-1", "nm_v1_workunit_get", new { workUnitId = _workUnitId }),
            1 => ToolUse("tu-2", "nm_v1_workspace_read", new { branchId = _branchId, workUnitId = _workUnitId, path = Path }),
            2 => ToolUse("tu-3", "nm_v1_workspace_read", new { branchId = _branchId, workUnitId = _workUnitId, path = Path, offset = SecondOffset }),
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

    private static string? LastToolResultText(JsonElement messages)
    {
        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (!msg.TryGetProperty("role", out var role) || role.GetString() != "user") continue;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type) || type.GetString() != "tool_result") continue;
                if (item.TryGetProperty("content", out var toolContent))
                    return toolContent.GetString();
            }
        }
        return null;
    }

    private static string? ExtractFromLastToolResult(JsonElement messages, string key)
    {
        var text = LastToolResultText(messages);
        if (text is null) return null;
        try
        {
            using var d = JsonDocument.Parse(text);
            return d.RootElement.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String
                ? val.GetString()
                : null;
        }
        catch { return null; }
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
