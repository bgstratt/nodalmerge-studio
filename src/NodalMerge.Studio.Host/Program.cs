using NodalMerge.Studio.Host;

// Peer mode: --mode peer  OR  environment variable STUDIO_MODE=peer  OR  Peer:Enabled=true in config.
// In peer mode the process runs all Studio agent services locally but does not start an HTTP server.
// An optional outbound WebSocket connection (Peer:HostUri) provides room presence and broadcasts.
var modArg = args.SkipWhile(a => a != "--mode").Skip(1).FirstOrDefault();
var envMode = Environment.GetEnvironmentVariable("STUDIO_MODE");

if (string.Equals(modArg, "peer", StringComparison.OrdinalIgnoreCase)
    || string.Equals(envMode, "peer", StringComparison.OrdinalIgnoreCase))
{
    using var host = StudioWebApplication.BuildPeer(args);
    await host.RunAsync();
    return;
}

// Config-driven peer mode (Peer:Enabled: true in appsettings / env).
// Build a minimal config reader first so we can check before constructing the full app.
var tempConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

if (tempConfig.GetValue<bool>("Peer:Enabled"))
{
    using var host = StudioWebApplication.BuildPeer(args);
    await host.RunAsync();
    return;
}

// Server mode (default).
var app = StudioWebApplication.Build(args);
var configuredUrl = app.Configuration["Studio:Urls"] ?? "http://127.0.0.1:5080";
app.Run(configuredUrl);
