using System.Net;
using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Fake LLM that never ends its turn — exhausts agent max iterations (11e dead-letter path).
/// </summary>
internal sealed class ExhaustingLlmHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "tool_use",
                            id = "tu-exhaust",
                            name = "nm_v1_workunit_get",
                            input = new { workUnitId = "placeholder" },
                        },
                    },
                    stop_reason = "tool_use",
                }),
                Encoding.UTF8,
                "application/json"),
        });
}
