using Microsoft.Extensions.DependencyInjection;

namespace NodalMerge.Studio.McpServer;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioMcpServer(this IServiceCollection services)
    {
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(ServiceCollectionExtensions).Assembly);

        return services;
    }
}
