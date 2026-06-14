using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Merge;
using NodalMerge.Studio.McpServer;
using NodalMerge.Studio.Orchestrator;
using NodalMerge.Studio.Projections;
using NodalMerge.Studio.Storage;
using NodalMerge.Studio.Tasks;

namespace NodalMerge.Studio.Host;

public static class StudioServiceCollectionExtensions
{
    public static IServiceCollection AddStudioServices(this IServiceCollection services)
    {
        services.AddStudioStorage();
        services.AddStudioProjections();
        services.AddStudioTasks();
        services.AddStudioMerge();
        services.AddStudioAgentRuntime();
        services.AddStudioOrchestrator();
        services.AddStudioMcpServer();
        return services;
    }
}
