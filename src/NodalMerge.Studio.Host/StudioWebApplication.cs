using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodalMerge.DotNetHost;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Host.Composition;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Core;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Orchestrator;
using NodalMerge.Studio.Storage;

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
            AddStudioInboundPackObserver(services);

            services.AddSingleton<HeadlessPeerOptions>(sp =>
            {
                var opts = new HeadlessPeerOptions();
                config.GetSection("Peer").Bind(opts);
                // Slice 6.4 — HostUri now flows from Room:HostUri (RoomOptions, bound by
                // AddStudioServices above; Peer:HostUri back-compat + deprecation log handled
                // there) rather than binding "Peer:HostUri" a second time here.
                opts.HostUri = sp.GetRequiredService<RoomOptions>().HostUri;
                return opts;
            });
            AddRoomPeerClient(services);

            configureServices?.Invoke(services);
        });

        return builder.Build();
    }

    // Slice 6.1b — RoomPeerClient is now registered as itself (not just IHostedService) and as
    // IStudioReplicationOutbound (overriding AddNodalMergeStorage/AddInMemoryStorage's default
    // no-op — last AddSingleton registration wins, the same pattern AddInMemoryStorage's own
    // WorkspaceOptions comment documents), so NodalMergeStudioNodeStore's per-write outbound
    // notification actually reaches a connected room peer. HostUri unset still means the upstream
    // half is entirely inert (StartAsync no-ops, NotifyLocalWriteAsync only enqueues upstream work
    // when HostUri is configured) — registering it unconditionally costs nothing in that case.
    private static void AddRoomPeerClient(IServiceCollection services)
    {
        services.AddSingleton<RoomPeerClient>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RoomPeerClient>());
        services.AddSingleton<IStudioReplicationOutbound>(sp => sp.GetRequiredService<RoomPeerClient>());
    }

    // Slice 7.1b — registered on both Build() (embedded) and BuildPeer() (headless) paths: either
    // kind of host can play the room-SERVER role for some room (a peer connecting to ITS OWN
    // /ws/{room} endpoint), and RuntimeWebSocketLoopRunner's server-side WS handling is the same
    // code either way. See StudioInboundPackObserver's own doc comment for what this bridges and
    // why it needs no private-room special-casing.
    private static void AddStudioInboundPackObserver(IServiceCollection services) =>
        services.AddSingleton<IInboundPackObserver, StudioInboundPackObserver>();

    public static WebApplication Build(
        string[] args,
        Action<IWebHostBuilder>? configureWebHost = null,
        HttpClient? llmHttpClient = null,
        Action<IServiceCollection>? configureServices = null,
        Action<ConfigurationManager>? configureConfiguration = null)
    {
        // ContentRootPath must be the directory the exe/dll actually lives in, not the OS process
        // cwd — the packaged host is spawned with cwd set to the caller's workspace root (so
        // relative DbPath/RootPath config resolves there), which left WebApplication.CreateBuilder's
        // default content-root probe unable to find appsettings.json next to the binary. That
        // silently dropped the whole NodalMerge:Providers section, falling back to NodeStorage=InMemory
        // regardless of what appsettings.json actually said.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        configureWebHost?.Invoke(builder.WebHost);

        builder.Configuration.AddInMemoryCollection([]);
        configureConfiguration?.Invoke(builder.Configuration);
        ApplyCasRootPath(builder.Configuration, builder.Configuration);

        builder.Services.AddNodalMergeHostProviders(builder.Configuration);
        builder.Services.AddNodalMergeRuntimeCore(builder.Configuration);
        builder.Services.AddStudioServices(llmHttpClient);
        AddStudioInboundPackObserver(builder.Services);
        builder.Services.AddSingleton<IRuntimeEventBroadcaster, RuntimeRoomEventBroadcaster>();
        builder.Services.AddSingleton<IStudioGraphPromoter, RuntimeGraphPromoter>();
        builder.Services.AddSingleton<IStudioCausalGraphService, RuntimeCausalGraphService>();
        builder.Services.AddSingleton<IParticipantEventBus, InMemoryParticipantEventBus>();
        builder.Services.AddSingleton<IProjectionMaterializer, LocalFilesystemProjectionMaterializer>();
        builder.Services.AddSingleton<IStudioParticipantService, StudioParticipantService>();

        // Slice 6.1b — the embedded Build() path never registered HeadlessPeerOptions/RoomPeerClient
        // before this slice (only BuildPeer() did — plans/cas-distribution-and-storage.md's Phase 6
        // reality check confirmed this was a genuine gap, not a false alarm). The retired 30 s
        // StudioCrdtSyncBackgroundService/RuntimeGraphPromoter broadcast tick previously covered
        // "does the embedded host talk to a room at all" only for downstream peers connecting to
        // its own /ws/{room} endpoint; RoomPeerClient now also covers the upstream half, so the
        // embedded host gets full room presence here. Slice 6.4 wires the extension's
        // nodalmerge.room.* settings into Room:HostUri/Room:Workgroup, which this HeadlessPeerOptions
        // factory now reads (via RoomOptions, bound in AddStudioServices) instead of "Peer:HostUri"
        // directly — HostUri stays unset (standalone) unless the extension/user actually configures it.
        builder.Services.AddSingleton<HeadlessPeerOptions>(sp =>
        {
            var opts = new HeadlessPeerOptions();
            builder.Configuration.GetSection("Peer").Bind(opts);
            opts.HostUri = sp.GetRequiredService<RoomOptions>().HostUri;
            return opts;
        });
        AddRoomPeerClient(builder.Services);

        // Phase 2 slice 2.3 — one-shot startup healing pass for the CAS reconcile sweep. Build-only
        // (not BuildPeer): a headless peer has no HTTP surface to trigger a manual sweep from, and
        // this same one-shot startup pass would otherwise run redundantly on every peer too.
        builder.Services.AddHostedService<CasReconcileBackgroundService>();

        // Phase 5 slice 5.2 — scheduled local blob GC, off by default (BlobGcOptions.
        // GcIntervalMinutes = 0). Build-only for now, matching CasReconcileBackgroundService's
        // placement above — a headless peer (BuildPeer) could reasonably also want this
        // eventually, but nothing in this slice requires it and adding it there is a one-line
        // follow-up once wanted.
        builder.Services.AddHostedService<BlobGcBackgroundService>();

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseCors();
        app.MapNodalMergeEndpoints();

        app.MapGet("/health", () => Results.Ok(new
        {
            service = StudioConstants.ServiceName,
            status = "ok",
            timestampUtc = DateTimeOffset.UtcNow
        }));

        app.MapGet("/studio/health", (WorkspaceOptions workspaceOptions) => Results.Ok(new
        {
            service = StudioConstants.ServiceName,
            layer = "studio-services",
            mcpContractVersion = StudioConstants.McpContractVersion,
            status = "ok",
            timestampUtc = DateTimeOffset.UtcNow,
            // Lets a caller confirm this host was actually spawned for their workspace before
            // adopting it, rather than blindly reusing whatever answers on the configured port.
            workspaceRootPath = workspaceOptions.RootPath
        }));

        app.MapStudioRestEndpoints();
        app.MapMcp("/mcp");
        // Phase C.4 (phase-c-implementation.md C3) — same MapMcp infrastructure, routed at a second
        // pattern; ServiceCollectionExtensions.AddStudioMcpServer's ConfigureSessionOptions callback
        // swaps in the harness-only tool set for requests under this prefix, leaving "/mcp" above
        // completely unchanged (see that file's own doc comment for why a second AddMcpServer()
        // registration can't be used instead).
        app.MapMcp("/mcp-harness");

        // Phase C.4 (phase-c-implementation.md C3) — ClaudeCodeExecutor needs this process's own
        // listening address to point the spawned CLI's .workspace/mcp.json back at "/mcp-harness".
        // Read once the server has actually bound (a pre-bind config guess would be wrong whenever
        // the port is dynamically assigned via ":0") via IServerAddressesFeature, the same
        // ASP.NET Core mechanism `dotnet run --urls` diagnostics use. Never fires in BuildPeer's
        // headless mode (no Kestrel there) — ClaudeCodeExecutorOptions.HarnessMcpBaseUrl stays null,
        // and RunAsync's own gate treats that as "no MCP mount this run," same as pre-C3 behavior.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            if (address is not null)
                app.Services.GetRequiredService<ClaudeCodeExecutorOptions>().HarnessMcpBaseUrl = address;
        });

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
