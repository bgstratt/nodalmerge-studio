using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.McpServer.Tools;

namespace NodalMerge.Studio.McpServer;

public static class ServiceCollectionExtensions
{
    // This project holds two distinct tool surfaces, deliberately not interchangeable
    // (see McpServerToolNames.cs's own doc comment): the internal nm_v1_* agent tools (~30
    // classes — ArtifactTools, MergeTools, WorkUnitTools, etc.), which in-process agent loops
    // never reach through this registration at all (they call McpToolDispatcher directly,
    // in-process, no MCP protocol involved) — and the external nms_v1_* surface (these 5
    // classes), the only thing meant to be reachable by an external MCP client (Claude, or any
    // other caller) over the actual HTTP MCP endpoint (StudioWebApplication maps app.MapMcp
    // ("/mcp") from this registration). WithToolsFromAssembly used to register every
    // [McpServerTool] in the project — including the entire internal nm_v1_* surface — on that
    // same external HTTP endpoint, with nothing enforcing the split the naming convention
    // implied. Explicit WithTools<T>() per external class is what actually enforces it: an
    // external caller gets goal lifecycle, results, repo registration, workspace status, and
    // clarification response — set-a-goal-and-check-on-it primitives — not the internal
    // orchestration/merge/branch/artifact toolset those 30 other classes expose.
    public static IServiceCollection AddStudioMcpServer(this IServiceCollection services)
    {
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<ExternalClarificationTools>()
            .WithTools<ExternalGoalTools>()
            .WithTools<ExternalRepositoryTools>()
            .WithTools<ExternalResultsTools>()
            .WithTools<ExternalWorkspaceTools>();

        return services;
    }
}
