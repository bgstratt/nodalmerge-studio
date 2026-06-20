using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Merge;
using NodalMerge.Studio.McpServer;
using NodalMerge.Studio.Orchestrator;
using NodalMerge.Studio.Projections;
using NodalMerge.Studio.Storage;
using NodalMerge.Studio.Tasks;

namespace NodalMerge.Studio.Host;

public static class StudioServiceCollectionExtensions
{
    public static IServiceCollection AddStudioServices(this IServiceCollection services, HttpClient? llmHttpClient = null)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                // Studio Host binds to 127.0.0.1 only; permissive CORS is safe for local-only access.
                policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            });
        });

        // Bind WorkspaceOptions from "Workspace" config section; falls back to defaults if absent.
        // Must be registered before AddNodalMergeStorage/AddInMemoryStorage so the factory picks it up.
        services.AddSingleton<WorkspaceOptions>(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            var opts   = new WorkspaceOptions();
            config?.GetSection("Workspace").Bind(opts);
            // Config binding replaces C# defaults with empty strings for missing/blank values.
            if (string.IsNullOrWhiteSpace(opts.RootPath))
                opts.RootPath = Path.Combine(Path.GetTempPath(), "studio-workspace");
            return opts;
        });

        services.AddNodalMergeStorage();
        services.AddStudioProjections();
        services.AddStudioTasks();
        services.AddStudioMerge();
        services.AddStudioAgentRuntime(llmHttpClient);
        services.AddStudioOrchestrator();
        services.AddSingleton<ICounterfactualService, NodalMerge.Studio.Orchestrator.CounterfactualService>();
        services.AddStudioMcpServer();
        return services;
    }
}
