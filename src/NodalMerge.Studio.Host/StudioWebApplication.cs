using Microsoft.AspNetCore.Hosting;
using NodalMerge.DotNetHost;
using NodalMerge.Studio.Core;

namespace NodalMerge.Studio.Host;

public static class StudioWebApplication
{
    public static WebApplication Build(
        string[] args,
        Action<IWebHostBuilder>? configureWebHost = null,
        HttpClient? llmHttpClient = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var app = HostApplication.Build(
            args,
            configureWebHost: configureWebHost,
            configureServices: services =>
            {
                services.AddStudioServices(llmHttpClient);
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
