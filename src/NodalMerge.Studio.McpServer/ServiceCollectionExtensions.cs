using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
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
    //
    // plans/harness-hosting-architecture.md Phase C.4 (phase-c-implementation.md C3) added a
    // THIRD surface — the harness-scoped nm_v1_* subset (HarnessWorkerTools) mounted at
    // "/mcp-harness", not "/mcp". The SDK constraint that shapes how this is wired: as of
    // ModelContextProtocol[.AspNetCore] 1.4.0, AddMcpServer() registers exactly one
    // process-wide McpServerOptions/tool catalog — there is no "named" or keyed second
    // registration, so calling AddMcpServer().WithTools<HarnessWorkerTools>() a second time
    // would just add those tools to the SAME catalog every mount serves, widening "/mcp" (the
    // one thing this split explicitly must not do — see above). The chosen alternative:
    // HttpServerTransportOptions.ConfigureSessionOptions is invoked per-request in stateless
    // mode with the request's HttpContext AND that request's own (freshly cloned)
    // McpServerOptions instance — so instead of a second AddMcpServer() call, this callback
    // inspects the request path and REPLACES options.ToolCollection with a harness-only
    // collection (built by BuildHarnessToolCollection) only for requests under "/mcp-harness".
    // Requests under "/mcp" never enter that branch, so its tool catalog is byte-for-byte what
    // it was before C3 — the external 5 classes only, nothing added, nothing widened.
    // Bearer-token authorization for the harness tools happens inside each HarnessWorkerTools
    // method (not here), matching McpToolDispatcher's own posture of returning an error payload
    // rather than throwing/blocking the whole session on one bad call.
    public static IServiceCollection AddStudioMcpServer(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<HarnessWorkerTools>();

        services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                options.ConfigureSessionOptions = (httpContext, serverOptions, _) =>
                {
                    if (httpContext.Request.Path.StartsWithSegments("/mcp-harness"))
                    {
                        var harnessTools = httpContext.RequestServices.GetRequiredService<HarnessWorkerTools>();
                        serverOptions.ToolCollection = BuildHarnessToolCollection(harnessTools);
                    }

                    return Task.CompletedTask;
                };
            })
            .WithTools<ExternalClarificationTools>()
            .WithTools<ExternalGoalTools>()
            .WithTools<ExternalRepositoryTools>()
            .WithTools<ExternalResultsTools>()
            .WithTools<ExternalWorkspaceTools>();

        return services;
    }

    private static McpServerPrimitiveCollection<McpServerTool> BuildHarnessToolCollection(HarnessWorkerTools tools)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>
        {
            McpServerTool.Create(
                (Func<string?, string?, int?, int?, int, CancellationToken, Task<string>>)tools.WorkspaceSymbolDefinitionAsync,
                new McpServerToolCreateOptions
                {
                    Name = McpToolNames.WorkspaceSymbolDefinition,
                    Description = "Find symbol definition locations in this run's branch using compiler-backed semantic navigation.",
                }),
            McpServerTool.Create(
                (Func<string?, string?, int?, int?, int, CancellationToken, Task<string>>)tools.WorkspaceSymbolReferencesAsync,
                new McpServerToolCreateOptions
                {
                    Name = McpToolNames.WorkspaceSymbolReferences,
                    Description = "Find symbol reference locations in this run's branch using compiler-backed semantic navigation.",
                }),
            McpServerTool.Create(
                (Func<string?, string?, int?, int?, int, CancellationToken, Task<string>>)tools.WorkspaceSymbolImplementationAsync,
                new McpServerToolCreateOptions
                {
                    Name = McpToolNames.WorkspaceSymbolImplementation,
                    Description = "Find symbol implementation locations in this run's branch using compiler-backed semantic navigation.",
                }),
            McpServerTool.Create(
                (Func<string, string, CancellationToken, Task<string>>)tools.DocFetchAsync,
                new McpServerToolCreateOptions
                {
                    Name = McpToolNames.DocFetch,
                    Description = "Fetch constrained external documentation with provenance metadata, scoped to this run's work unit.",
                }),
            McpServerTool.Create(
                (Func<string, string, string, string?, CancellationToken, Task<string>>)tools.ArtifactRecordAsync,
                new McpServerToolCreateOptions
                {
                    Name = McpToolNames.ArtifactRecord,
                    Description = "Record a durable knowledge note (Research, Decision, or Constraint) for this run's work unit.",
                }),
            McpServerTool.Create(
                (Func<string?, string?, CancellationToken, Task<string>>)tools.ArtifactQueryAsync,
                new McpServerToolCreateOptions
                {
                    Name = McpToolNames.ArtifactQuery,
                    Description = "Search knowledge artifacts for this run's work unit and its ancestors by type and/or keyword.",
                }),
            McpServerTool.Create(
                (Func<string, string?, string[]?, CancellationToken, Task<string>>)tools.ClarificationRequestAsync,
                new McpServerToolCreateOptions
                {
                    Name = McpToolNames.ClarificationRequest,
                    Description = "Ask a blocking clarifying question and wait for the human's answer in this same tool call (true mid-turn pause). Parks and returns if the hold-open window elapses first.",
                }),
        };
        return collection;
    }
}
