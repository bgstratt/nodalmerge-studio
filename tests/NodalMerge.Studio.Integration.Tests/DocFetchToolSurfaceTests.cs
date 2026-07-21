using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class DocFetchToolSurfaceTests : IAsyncLifetime
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-docfetch-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_rootPath);

    private WebApplication BuildTestApp(Action<IServiceCollection>? configure = null)
    {
        return StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions
                {
                    RootPath = _rootPath,
                    DocFetchTools = true,
                    DocFetchAllowedSchemes = ["https"],
                    DocFetchAllowedDomains = ["learn.microsoft.com"],
                    DocFetchDeniedDomains = ["denied.example"],
                    DocFetchMaxContentBytes = 32,
                    DocFetchTimeoutSeconds = 5,
                    DocFetchSummaryMaxChars = 24,
                });
                services.AddSingleton<IExternalDocFetcher>(new FakeExternalDocFetcher(
                    contentType: "text/plain; charset=utf-8",
                    snapshot: "0123456789abcdefghijklmnopqrstuvwxyz",
                    truncated: true));
                configure?.Invoke(services);
            });
    }

    [Fact]
    public async Task Doc_fetch_rest_and_dispatcher_record_artifact_and_event()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var client = app.GetTestClient();
        var restResponse = await client.PostAsJsonAsync("/studio/doc/fetch", new
        {
            url = "https://learn.microsoft.com/en-us/dotnet/api/system.string",
            reason = "Confirm API behavior before changing implementation",
            workUnitId = "WU-123",
            sessionId = "S-123"
        });

        Assert.Equal(HttpStatusCode.OK, restResponse.StatusCode);

        var restBody = await restResponse.Content.ReadFromJsonAsync<DocFetchResult>();
        Assert.NotNull(restBody);
        Assert.Equal("WU-123", restBody!.WorkUnitId);
        Assert.Equal("https://learn.microsoft.com/en-us/dotnet/api/system.string", restBody.NormalizedUrl);
        Assert.Equal("sha256", restBody.HashAlgorithm);
        Assert.NotEmpty(restBody.ContentHash);
        Assert.True(restBody.SnapshotBytes > 0);
        Assert.NotNull(restBody.Summary);

        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var chain = await artifacts.GetChainAsync("WU-123");
        Assert.Contains(chain, a => a.ArtifactId == restBody.ArtifactId && a.Type == ArtifactType.Research);

        var eventStream = app.Services.GetRequiredService<IExecutionEventStream>();
        var events = await eventStream.GetSessionEventsAsync("S-123");
        var fetchedEvent = events.LastOrDefault(e => e.Kind == ExecutionEventKind.ExternalDocFetched);
        Assert.NotNull(fetchedEvent);

        var payload = JsonSerializer.Deserialize<ExternalDocFetchedPayload>(fetchedEvent!.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(restBody.ArtifactId, payload!.ArtifactId);
        Assert.Equal(restBody.ContentHash, payload.ContentHash);

        var runtimeAssembly = app.Services.GetRequiredService<IAgentRuntimeService>().GetType().Assembly;
        var dispatcherType = runtimeAssembly.GetType("NodalMerge.Studio.AgentRuntime.McpToolDispatcher");
        Assert.NotNull(dispatcherType);
        var dispatcher = app.Services.GetService(dispatcherType!);
        Assert.NotNull(dispatcher);

        var dispatch = dispatcherType!.GetMethod("DispatchAsync", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(dispatch);

        var input = JsonSerializer.SerializeToElement(new
        {
            url = "https://learn.microsoft.com/en-us/dotnet/csharp/",
            reason = "Cross-check language behavior",
            workUnitId = "WU-123",
        });

        var dispatchTask = (Task<string>)dispatch!.Invoke(dispatcher, [
            McpToolNames.DocFetch,
            input,
            null!,
            CancellationToken.None,
            "S-123"
        ])!;

        var dispatchJson = await dispatchTask;
        using var dispatchDoc = JsonDocument.Parse(dispatchJson);
        Assert.False(dispatchDoc.RootElement.TryGetProperty("error", out _));
        Assert.Equal("WU-123", dispatchDoc.RootElement.GetProperty("WorkUnitId").GetString());
    }

    [Fact]
    public async Task Doc_fetch_policy_and_flag_are_enforced()
    {
        var app = BuildTestApp();
        await app.StartAsync();

        var docs = app.Services.GetRequiredService<IDocFetchCommandService>();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            docs.FetchAsync(
                "https://denied.example/path",
                "Should be blocked",
                "WU-456"));

        await app.DisposeAsync();

        await using var disabledApp = BuildTestApp(services =>
        {
            services.AddSingleton(new WorkspaceOptions
            {
                RootPath = _rootPath,
                DocFetchTools = false,
                DocFetchAllowedSchemes = ["https"],
                DocFetchAllowedDomains = ["learn.microsoft.com"],
                DocFetchDeniedDomains = [],
                DocFetchMaxContentBytes = 32,
                DocFetchTimeoutSeconds = 5,
                DocFetchSummaryMaxChars = 24,
            });
        });
        await disabledApp.StartAsync();

        var client = disabledApp.GetTestClient();
        var response = await client.PostAsJsonAsync("/studio/doc/fetch", new
        {
            url = "https://learn.microsoft.com/en-us/dotnet/api/system.string",
            reason = "Blocked when disabled",
            workUnitId = "WU-456",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class FakeExternalDocFetcher(
        string contentType,
        string snapshot,
        bool truncated) : IExternalDocFetcher
    {
        public Task<ExternalDocFetchContent> FetchAsync(
            Uri normalizedUrl,
            int maxBytes,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            var bounded = snapshot;
            if (bounded.Length > maxBytes)
                bounded = bounded[..maxBytes];

            return Task.FromResult(new ExternalDocFetchContent(
                contentType,
                bounded,
                truncated,
                bounded.Length));
        }
    }
}
