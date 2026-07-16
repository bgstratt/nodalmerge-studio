using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public static IServiceCollection AddStudioServices(this IServiceCollection services, HttpClient? llmHttpClient = null, bool includeMcpServer = true)
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
            {
                opts.RootPath = Path.Combine(Path.GetTempPath(), "studio-workspace");
                var logger = sp.GetService<ILogger<WorkspaceOptions>>();
                logger?.LogWarning(
                    "[WorkspaceOptions] Workspace:RootPath is not configured — using temp fallback {Path}. " +
                    "Set Workspace:RootPath in appsettings.json to persist data across reboots.",
                    opts.RootPath);
            }
            return opts;
        });

        services.AddNodalMergeStorage();

        // Phase 5 slice 5.2 — bind RetentionPolicyOptions/BlobGcOptions from config, overriding
        // AddNodalMergeStorage's own default-valued registrations above (last AddSingleton
        // registration wins — same convention WorkspaceOptions's AddInMemoryStorage comment
        // documents, and why these two calls must come AFTER AddNodalMergeStorage rather than
        // alongside WorkspaceOptions's own binding above).
        services.AddSingleton<RetentionPolicyOptions>(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            var opts   = new RetentionPolicyOptions();
            config?.GetSection("Retention").Bind(opts);
            return opts;
        });
        services.AddSingleton<BlobGcOptions>(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            var opts   = new BlobGcOptions();
            config?.GetSection("BlobGc").Bind(opts);
            return opts;
        });

        // Slice 6.4 (plans/cas-distribution-and-storage.md Phase 6, D4 "mode collapse") — bind
        // RoomOptions from the "Room" config section, overriding AddNodalMergeStorage's own
        // default-valued registration above (last-AddSingleton-wins, same pattern as
        // RetentionPolicyOptions/BlobGcOptions immediately above). Back-compat: pre-6.4 configs
        // (docs/guides/headless-peer.md) set "Peer:HostUri" — fall back to it (with a deprecation
        // log) only when "Room:HostUri" itself is unset, so those deployments keep working
        // unchanged.
        services.AddSingleton<RoomOptions>(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            var opts   = new RoomOptions();
            config?.GetSection("Room").Bind(opts);

            if (string.IsNullOrWhiteSpace(opts.HostUri))
            {
                var legacyHostUri = config?["Peer:HostUri"];
                if (!string.IsNullOrWhiteSpace(legacyHostUri))
                {
                    sp.GetService<ILogger<RoomOptions>>()?.LogWarning(
                        "[RoomOptions] \"Peer:HostUri\" is deprecated — set \"Room:HostUri\" instead " +
                        "(same value, new section). Using the legacy value this run: {HostUri}", legacyHostUri);
                    opts.HostUri = legacyHostUri;
                }
            }

            // Config binding overwrites the C# property default with an empty string when
            // Room:Workgroup is present-but-blank; restore "workgroup" so standalone workgroup
            // state (repositories map, cross-repo goal nodes) keeps a stable local room id either
            // way (RoomOptions.Workgroup's own doc comment).
            if (string.IsNullOrWhiteSpace(opts.Workgroup))
                opts.Workgroup = "workgroup";

            return opts;
        });

        services.AddStudioProjections();
        services.AddSingleton<FindingDetectorService>();
        services.AddStudioTasks();
        services.AddStudioMerge();
        services.AddStudioAgentRuntime(llmHttpClient);
        services.AddStudioOrchestrator();
        services.AddSingleton<ICounterfactualService, NodalMerge.Studio.Orchestrator.CounterfactualService>();
        if (includeMcpServer)
            services.AddStudioMcpServer();
        return services;
    }
}
