using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using NodalMerge.DotNetHost;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Studio.Core;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Host;

public static class StudioWebApplication
{
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
            configureConfiguration: configureConfiguration,
            configureServices: services =>
            {
                services.AddStudioServices(llmHttpClient);
                services.AddSingleton<IRuntimeEventBroadcaster, RuntimeRoomEventBroadcaster>();
                services.AddSingleton<IStudioGraphPromoter, RuntimeGraphPromoter>();
                services.AddSingleton<IStudioCausalGraphService, RuntimeCausalGraphService>();
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
        app.MapMcp();
        return app;
    }
}
