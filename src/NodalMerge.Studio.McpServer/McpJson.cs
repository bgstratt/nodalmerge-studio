using System.Text.Json;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.McpServer;

internal static class McpJson
{
    public static string Ok(object data) =>
        JsonSerializer.Serialize(new { contractVersion = McpContract.Version, data });

    public static string Error(string tool, string message) =>
        JsonSerializer.Serialize(new
        {
            contractVersion = McpContract.Version,
            tool,
            status = "error",
            message
        });
}
