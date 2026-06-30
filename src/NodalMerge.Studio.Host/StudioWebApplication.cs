using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NodalMerge.DotNetHost;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Host.Composition;
using NodalMerge.Studio.Core;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Orchestrator;

namespace NodalMerge.Studio.Host;

public static class StudioWebApplication
{
    /// <summary>
    /// Builds an IHost for headless peer mode: all Studio services (agents, projections,
    /// storage, orchestrator) plus RoomPeerClient for optional outbound room presence.
    /// No HTTP server, no MCP-over-HTTP, no WebSocket server is started.
    /// </summary>
    public static IHost BuildPeer(
        string[] args,
        HttpClient? llmHttpClient = null,
        Action<IServiceCollection>? configureServices = null,
        Action<IConfigurationBuilder>? configureConfiguration = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args);

        if (configureConfiguration is not null)
            builder.ConfigureAppConfiguration(configureConfiguration);

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var snap = cfg.Build();
            ApplyCasRootPath(snap, cfg);
        });

        builder.ConfigureServices((ctx, services) =>
        {
            var config = ctx.Configuration;
            services.AddNodalMergeHostProviders(config);
            services.AddNodalMergeRuntimeCore(config);
            services.AddStudioServices(llmHttpClient, includeMcpServer: false);

            services.AddSingleton<HeadlessPeerOptions>(sp =>
            {
                var opts = new HeadlessPeerOptions();
                config.GetSection("Peer").Bind(opts);
                return opts;
            });
            services.AddHostedService<RoomPeerClient>();

            configureServices?.Invoke(services);
        });

        return builder.Build();
    }

    public static WebApplication Build(
        string[] args,
        Action<IWebHostBuilder>? configureWebHost = null,
        HttpClient? llmHttpClient = null,
        Action<IServiceCollection>? configureServices = null,
        Action<ConfigurationManager>? configureConfiguration = null)
    {
        var app = HostApplication.Build(
            args,
            configureWebHost: configureWebHost,
            configureConfiguration: config =>
            {
                configureConfiguration?.Invoke(config);
                ApplyCasRootPath(config, config);
            },
            configureServices: services =>
            {
                services.AddStudioServices(llmHttpClient);
                services.AddSingleton<IRuntimeEventBroadcaster, RuntimeRoomEventBroadcaster>();
                services.AddSingleton<IStudioGraphPromoter, RuntimeGraphPromoter>();
                services.AddSingleton<IStudioCausalGraphService, RuntimeCausalGraphService>();
                services.AddSingleton<IParticipantEventBus, InMemoryParticipantEventBus>();
                services.AddSingleton<IProjectionMaterializer, LocalFilesystemProjectionMaterializer>();
                services.AddSingleton<IStudioParticipantService, StudioParticipantService>();
                services.AddHostedService<StudioCrdtSyncBackgroundService>();
                configureServices?.Invoke(services);
            });

        app.UseCors();

        app.MapGet("/health", () => Results.Ok(new
        {
            service = StudioConstants.ServiceName,
            status = "ok",
            timestampUtc = DateTimeOffset.UtcNow
        }));

        app.MapGet("/studio/health", () => Results.Ok(new
        {
            service = StudioConstants.ServiceName,
            layer = "studio-services",
            mcpContractVersion = StudioConstants.McpContractVersion,
            status = "ok",
            timestampUtc = DateTimeOffset.UtcNow
        }));

        app.MapStudioRestEndpoints();
        app.MapMcp("/mcp");

        // Diagnostic: list all registered endpoint patterns (remove once MCP route is confirmed)
        app.MapGet("/debug/routes", (IEnumerable<EndpointDataSource> sources) =>
        {
            var routes = sources
                .SelectMany(s => s.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(e => new { pattern = e.RoutePattern.RawText, displayName = e.DisplayName })
                .OrderBy(r => r.pattern)
                .ToList();
            return Results.Ok(routes);
        });

        return app;
    }

    // Computes the effective CAS root from Workspace config and injects it into
    // NodalMerge:Storage:FileBlobs:RootPath before AddNodalMergeHostProviders reads it.
    // Priority: explicit CasRootPath > {SeedRepositoryPath}/.nodalmerge/cas > no override.
    private static void ApplyCasRootPath(IConfiguration config, IConfigurationBuilder builder)
    {
        var casRootPath = config["Workspace:CasRootPath"];
        if (string.IsNullOrWhiteSpace(casRootPath))
        {
            var seedPath = config["Workspace:SeedRepositoryPath"];
            if (!string.IsNullOrWhiteSpace(seedPath))
                casRootPath = Path.Combine(seedPath, ".nodalmerge", "cas");
        }

        if (!string.IsNullOrWhiteSpace(casRootPath))
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:FileBlobs:RootPath"] = casRootPath
            });
    }
}
