using System.Net;
using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Extends fan-out flow with automated reviewer pre-gate (Phase 4 slice 11d).
/// </summary>
internal sealed class AutomatedReviewFanOutLlmHandler : HttpMessageHandler
{
    private readonly HttpClient _innerClient;
    private readonly bool _rejectReview;

    public AutomatedReviewFanOutLlmHandler(bool rejectReview = false)
    {
        _rejectReview = rejectReview;
        _innerClient = new HttpClient(new FanOutLlmHandler(), disposeHandler: true);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        var firstMsg = messages[0].GetProperty("content").GetString() ?? "";

        if (firstMsg.StartsWith("Review merge proposal", StringComparison.Ordinal))
            return ReviewerResponse(messages, firstMsg);

        using var forward = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return await _innerClient.SendAsync(forward, cancellationToken).ConfigureAwait(false);
    }

    private HttpResponseMessage ReviewerResponse(JsonElement messages, string firstMsg)
    {
        var proposalId = ParseBetween(firstMsg, "Review merge proposal ", " for work unit");
        var step = (messages.GetArrayLength() - 1) / 2;

        var json = step switch
        {
            0 => ToolUse("tu-r-1", "nm_v1_merge_review", new
            {
                proposalId,
                decision = _rejectReview ? "Rejected" : "Approved",
                automated = true,
                verificationResults = _rejectReview
                    ? "Required file src/Missing.cs not present in proposal."
                    : "File changes match plan scope; goal satisfied.",
            }),
            _ => EndTurn(),
        };

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
