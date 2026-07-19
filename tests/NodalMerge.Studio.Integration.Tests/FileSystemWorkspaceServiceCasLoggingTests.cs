using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 2 item 2 follow-up — FileSystemWorkspaceService.CanEmitOps used to silently no-op with
/// zero logging whenever the CAS dual-write couldn't happen (blob store / op service / seed path
/// missing). It now logs once per instance at Information (an unconfigured CAS is often an
/// intentional deployment choice, not a misconfiguration), throttled so hundreds of file ops in a
/// run don't produce hundreds of log lines. Uses an ILoggerProvider (rather than naming
/// FileSystemWorkspaceService directly, which is internal to NodalMerge.Studio.Storage) so
/// Microsoft.Extensions.Logging's own generic ILogger&lt;T&gt; wiring resolves the right category.
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemWorkspaceServiceCasLoggingTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-cas-logging-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath)) Directory.Delete(_rootPath, recursive: true);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);
        public void Dispose() { }

        private sealed class RecordingLogger(RecordingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add((category, logLevel, formatter(state, exception)));
        }

        public IReadOnlyList<(string Category, LogLevel Level, string Message)> ForFileSystemWorkspaceService() =>
            [.. Entries.Where(e => e.Category.Contains("FileSystemWorkspaceService", StringComparison.Ordinal))];
    }

    private sealed class RecordingRepositoryOpService : IRepositoryOpService
    {
        public List<RepositoryOperation> Emitted { get; } = [];
        public Task EmitAsync(RepositoryOperation op, CancellationToken ct = default)
        {
            Emitted.Add(op);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<RepositoryOperation>> GetRecentOpsForPathsAsync(
            string repositoryId, IReadOnlyList<string> paths, int limit = 5, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepositoryOperation>>([]);
        public Task<IReadOnlyList<RepositoryOperation>> GetOpsSinceAsync(
            string repositoryId, DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepositoryOperation>>([]);
    }

    private (IFileWorkspaceService FileWorkspace, RecordingLoggerProvider Logs, WorkspaceOptions Options, RecordingRepositoryOpService Ops)
        Build(bool configureCas, string? seedRepositoryPath = null)
    {
        var options = new WorkspaceOptions { RootPath = _rootPath, SeedRepositoryPath = seedRepositoryPath };
        var logs = new RecordingLoggerProvider();
        var ops = new RecordingRepositoryOpService();
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(options);
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        if (configureCas)
        {
            services.AddSingleton<IBlobStoreProvider>(new InMemoryBlobStoreProvider());
            services.AddSingleton<IRepositoryOpService>(ops);
        }
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IFileWorkspaceService>(), logs, options, ops);
    }

    [Fact]
    public async Task WriteAsync_logs_once_when_CAS_is_unconfigured()
    {
        var (fileWorkspace, logs, _, _) = Build(configureCas: false);
        await fileWorkspace.InitBranchAsync("main");

        await fileWorkspace.WriteAsync("main", "a.txt", "1");
        await fileWorkspace.WriteAsync("main", "b.txt", "2");
        await fileWorkspace.WriteAsync("main", "c.txt", "3");

        Assert.Single(logs.ForFileSystemWorkspaceService());
        Assert.Equal(LogLevel.Information, logs.ForFileSystemWorkspaceService()[0].Level);
    }

    [Fact]
    public async Task DeleteAsync_does_not_log_again_after_WriteAsync_already_logged()
    {
        var (fileWorkspace, logs, _, _) = Build(configureCas: false);
        await fileWorkspace.InitBranchAsync("main");

        await fileWorkspace.WriteAsync("main", "a.txt", "1");
        await fileWorkspace.WriteAsync("main", "b.txt", "2");
        await fileWorkspace.DeleteAsync("main", "b.txt");

        Assert.Single(logs.ForFileSystemWorkspaceService());
    }

    [Fact]
    public async Task WriteAsync_does_not_log_when_CAS_is_fully_configured()
    {
        var seedRepo = Path.Combine(Path.GetTempPath(), $"studio-cas-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(seedRepo);
        try
        {
            var (fileWorkspace, logs, _, _) = Build(configureCas: true, seedRepositoryPath: seedRepo);
            await fileWorkspace.InitBranchAsync("main");

            await fileWorkspace.WriteAsync("main", "a.txt", "1");

            Assert.Empty(logs.ForFileSystemWorkspaceService());
        }
        finally
        {
            if (Directory.Exists(seedRepo)) Directory.Delete(seedRepo, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_dual_write_resumes_without_relogging_once_SeedRepositoryPath_is_set_after_construction()
    {
        var seedRepo = Path.Combine(Path.GetTempPath(), $"studio-cas-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(seedRepo);
        try
        {
            var (fileWorkspace, logs, options, ops) = Build(configureCas: true, seedRepositoryPath: null);

            await fileWorkspace.InitBranchAsync("main");
            await fileWorkspace.WriteAsync("main", "a.txt", "1"); // CAS unconfigured (no seed path) — logs once
            Assert.Single(logs.ForFileSystemWorkspaceService());
            Assert.Empty(ops.Emitted);

            options.SeedRepositoryPath = seedRepo; // mutate the shared instance, same as REST/MCP do at runtime
            await fileWorkspace.WriteAsync("main", "b.txt", "2"); // dual-write should resume silently

            Assert.Single(logs.ForFileSystemWorkspaceService()); // no re-log
            Assert.Single(ops.Emitted); // but the dual-write actually happened this time
        }
        finally
        {
            if (Directory.Exists(seedRepo)) Directory.Delete(seedRepo, recursive: true);
        }
    }
}
