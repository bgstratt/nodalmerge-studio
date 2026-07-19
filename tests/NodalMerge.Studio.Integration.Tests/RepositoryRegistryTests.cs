using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.McpServer.Tools;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// RegisterAsync/ListAsync/GetAsync are an informational registry of known repository paths and do
// not change which repository is actually seeded (WorkspaceOptions.SeedRepositoryPath) — see
// RepositoryRegistryService. CreateAsync (multi-repo Phase 1) is the one method that mutates disk:
// it creates the directory and runs `git init` if needed, then delegates to RegisterAsync.
[Trait("Category", "Integration")]
public class RepositoryRegistryTests
{
    private static (IRepositoryRegistryService registry, IServiceProvider services) BuildServices()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());
        return (app.Services.GetRequiredService<IRepositoryRegistryService>(), app.Services);
    }

    [Fact]
    public async Task RegisterAsync_then_ListAsync_round_trips()
    {
        var (registry, _) = BuildServices();

        var registered = await registry.RegisterAsync(@"D:\Repos\Foo", "Foo project");

        var all = await registry.ListAsync();
        var found = Assert.Single(all);
        Assert.Equal(registered.RepositoryId, found.RepositoryId);
        Assert.Equal(@"D:\Repos\Foo", found.Path);
        Assert.Equal("Foo project", found.Label);
    }

    [Fact]
    public async Task RegisterAsync_is_idempotent_by_normalized_path()
    {
        var (registry, _) = BuildServices();

        var first = await registry.RegisterAsync(@"D:\Repos\Foo", "Foo project");
        var second = await registry.RegisterAsync("D:/Repos/Foo/", "Foo project, re-registered");

        Assert.Equal(first.RepositoryId, second.RepositoryId);
        var all = await registry.ListAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task FilterUnregisteredAsync_excludes_already_registered_paths_regardless_of_normalization()
    {
        var (registry, _) = BuildServices();
        await registry.RegisterAsync(@"D:\Repos\Foo", "Foo project");

        var unregistered = await registry.FilterUnregisteredAsync([
            "D:/Repos/Foo/", // same path as registered, different separators/casing/trailing slash
            @"D:\Repos\Bar",
        ]);

        Assert.Equal([@"D:\Repos\Bar"], unregistered);
    }

    [Fact]
    public async Task REST_GET_repositories_unregistered_filters_correctly()
    {
        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        await repositories.RegisterAsync(@"D:\Repos\Foo", "Foo project");

        var response = await client.GetAsync(
            $"/studio/repositories/unregistered?paths={Uri.EscapeDataString(@"D:\Repos\Foo")}&paths={Uri.EscapeDataString(@"D:\Repos\Bar")}");
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var unregistered = doc.GetProperty("unregistered").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal([@"D:\Repos\Bar"], unregistered);
    }

    [Fact]
    public async Task CreateAsync_creates_a_directory_and_initializes_git()
    {
        var (registry, _) = BuildServices();
        var path = Path.Combine(Path.GetTempPath(), $"studio-repo-create-{Guid.NewGuid():N}");
        try
        {
            var repository = await registry.CreateAsync(path, "fresh repo");

            Assert.True(Directory.Exists(path));
            Assert.True(Directory.Exists(Path.Combine(path, ".git")));
            Assert.Equal(path, repository.Path);
            Assert.Equal("fresh repo", repository.Label);

            var all = await registry.ListAsync();
            Assert.Contains(all, r => r.RepositoryId == repository.RepositoryId);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_is_idempotent_for_an_already_initialized_repo()
    {
        var (registry, _) = BuildServices();
        var path = Path.Combine(Path.GetTempPath(), $"studio-repo-create-{Guid.NewGuid():N}");
        try
        {
            var first = await registry.CreateAsync(path, "fresh repo");
            var second = await registry.CreateAsync(path, "fresh repo, re-created");

            Assert.Equal(first.RepositoryId, second.RepositoryId);
            var all = await registry.ListAsync();
            Assert.Single(all);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_initializes_git_in_an_already_existing_plain_directory_without_disturbing_files()
    {
        var (registry, _) = BuildServices();
        var path = Path.Combine(Path.GetTempPath(), $"studio-repo-create-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "existing.txt"), "keep me");
        try
        {
            await registry.CreateAsync(path, null);

            Assert.True(Directory.Exists(Path.Combine(path, ".git")));
            Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(path, "existing.txt")));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task MCP_RepositoryTools_create_round_trip()
    {
        var (registry, services) = BuildServices();
        var tools = ActivatorUtilities.CreateInstance<RepositoryTools>(services);
        var path = Path.Combine(Path.GetTempPath(), $"studio-repo-create-{Guid.NewGuid():N}");
        try
        {
            var json = await tools.CreateAsync(path, "via mcp");
            var doc = JsonDocument.Parse(json).RootElement;
            var repositoryId = doc.GetProperty("data").GetProperty("repositoryId").GetString();

            Assert.NotNull(repositoryId);
            Assert.True(Directory.Exists(Path.Combine(path, ".git")));

            var direct = await registry.GetAsync(repositoryId!);
            Assert.NotNull(direct);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task REST_POST_repositories_creates_and_initializes_git()
    {
        var path = Path.Combine(Path.GetTempPath(), $"studio-repo-create-rest-{Guid.NewGuid():N}");
        try
        {
            await using var app = StudioWebApplication.Build(
                [], configureWebHost: webHost => webHost.UseTestServer(),
                configureServices: services => services.AddInMemoryStorage());
            await app.StartAsync();
            var client = app.GetTestClient();

            var response = await client.PostAsJsonAsync("/studio/repositories", new { path, label = "via rest" });
            response.EnsureSuccessStatusCode();

            Assert.True(Directory.Exists(Path.Combine(path, ".git")));

            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.False(string.IsNullOrEmpty(body.GetProperty("repositoryId").GetString()));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    // Test-only setup helper — creates a real local git repo with one committed file to clone
    // from. Independent of RepositoryRegistryService.CreateAsync (which only inits, never commits).
    private static void InitCommittedRepo(string path, string fileName, string content)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, fileName), content);
        RunGit(path, "init");
        RunGit(path, "-c user.email=test@test.com -c user.name=test add .");
        RunGit(path, "-c user.email=test@test.com -c user.name=test commit -m init");
    }

    private static void RunGit(string workDir, string arguments)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c git {arguments}") { WorkingDirectory = workDir }
            : new ProcessStartInfo("git", arguments) { WorkingDirectory = workDir };
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        using var process = Process.Start(psi)!;
        process.WaitForExit();
    }

    // git marks files under .git/objects read-only on Windows; Directory.Delete throws
    // UnauthorizedAccessException on those unless the attribute is cleared first.
    private static void DeleteGitDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    [Fact]
    public async Task CloneAsync_clones_a_local_repo_and_registers_it()
    {
        var (registry, _) = BuildServices();
        var sourcePath = Path.Combine(Path.GetTempPath(), $"studio-repo-clone-src-{Guid.NewGuid():N}");
        var targetPath = Path.Combine(Path.GetTempPath(), $"studio-repo-clone-dst-{Guid.NewGuid():N}");
        try
        {
            InitCommittedRepo(sourcePath, "Program.cs", "// cloned content");

            var repository = await registry.CloneAsync(sourcePath, targetPath, "cloned repo");

            Assert.True(Directory.Exists(Path.Combine(targetPath, ".git")));
            Assert.Equal("// cloned content", await File.ReadAllTextAsync(Path.Combine(targetPath, "Program.cs")));
            Assert.Equal(targetPath, repository.Path);
            Assert.Equal("cloned repo", repository.Label);

            var all = await registry.ListAsync();
            Assert.Contains(all, r => r.RepositoryId == repository.RepositoryId);
        }
        finally
        {
            DeleteGitDirectory(sourcePath);
            DeleteGitDirectory(targetPath);
        }
    }

    [Fact]
    public async Task REST_POST_repositories_clone_clones_and_registers()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"studio-repo-clone-src-{Guid.NewGuid():N}");
        var targetPath = Path.Combine(Path.GetTempPath(), $"studio-repo-clone-dst-{Guid.NewGuid():N}");
        try
        {
            InitCommittedRepo(sourcePath, "Program.cs", "// cloned via rest");

            await using var app = StudioWebApplication.Build(
                [], configureWebHost: webHost => webHost.UseTestServer(),
                configureServices: services => services.AddInMemoryStorage());
            await app.StartAsync();
            var client = app.GetTestClient();

            var response = await client.PostAsJsonAsync(
                "/studio/repositories/clone", new { url = sourcePath, targetPath });
            response.EnsureSuccessStatusCode();

            Assert.Equal("// cloned via rest", await File.ReadAllTextAsync(Path.Combine(targetPath, "Program.cs")));
        }
        finally
        {
            DeleteGitDirectory(sourcePath);
            DeleteGitDirectory(targetPath);
        }
    }

    [Fact]
    public async Task MCP_RepositoryTools_clone_round_trip()
    {
        var (_, services) = BuildServices();
        var tools = ActivatorUtilities.CreateInstance<RepositoryTools>(services);
        var sourcePath = Path.Combine(Path.GetTempPath(), $"studio-repo-clone-src-{Guid.NewGuid():N}");
        var targetPath = Path.Combine(Path.GetTempPath(), $"studio-repo-clone-dst-{Guid.NewGuid():N}");
        try
        {
            InitCommittedRepo(sourcePath, "Program.cs", "// cloned via mcp");

            var json = await tools.CloneAsync(sourcePath, targetPath, "via mcp");
            var doc = JsonDocument.Parse(json).RootElement;
            Assert.False(string.IsNullOrEmpty(doc.GetProperty("data").GetProperty("repositoryId").GetString()));
            Assert.Equal("// cloned via mcp", await File.ReadAllTextAsync(Path.Combine(targetPath, "Program.cs")));
        }
        finally
        {
            DeleteGitDirectory(sourcePath);
            DeleteGitDirectory(targetPath);
        }
    }

    [Fact]
    public async Task MCP_RepositoryTools_register_and_list_round_trip()
    {
        var (registry, services) = BuildServices();
        var tools = ActivatorUtilities.CreateInstance<RepositoryTools>(services);

        var registerJson = await tools.RegisterAsync(@"D:\Repos\Bar", "Bar project");
        var registerDoc = JsonDocument.Parse(registerJson).RootElement;
        var repositoryId = registerDoc.GetProperty("data").GetProperty("repositoryId").GetString();

        var listJson = await tools.ListAsync();
        var listDoc = JsonDocument.Parse(listJson).RootElement;
        // ListAsync returns the RepositoryV1 records directly (not an anonymous lowercase-property
        // wrapper like RegisterAsync), so System.Text.Json's default serialization keeps the
        // record's PascalCase property names.
        var ids = listDoc.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("RepositoryId").GetString()).ToList();

        Assert.Contains(repositoryId, ids);

        var direct = await registry.ListAsync();
        Assert.Single(direct);
    }

    // ── Slice 7.2 (plans/cas-distribution-and-storage.md Phase 7) — ResolveCasIdentityAsync ──────

    [Fact]
    public async Task ResolveCasIdentityAsync_uses_workgroup_id_for_a_brand_new_repository()
    {
        var (registry, _) = BuildServices();
        var path = Path.Combine(Path.GetTempPath(), $"studio-repo-{Guid.NewGuid():N}");
        var registered = await registry.RegisterAsync(path, "brand new");

        // AddInMemoryStorage wires a real (in-memory) IWorkgroupRepositoryDirectory, so this
        // first-ever registration auto-mints a WorkgroupRepoId equal to its own RepositoryId (6.2's
        // preferred-id continuity — RegisterWorkgroupEntryAsync passes preferredRepoId). With no
        // existing snapshot chain under the path yet (step 1 of ResolveCasIdentityAsync's own doc
        // comment misses), resolution lands on step 2 — the workgroup-portable key — not the raw
        // disk path, even though both happen to be equal here.
        Assert.NotNull(registered.WorkgroupRepoId);
        var resolved = await registry.ResolveCasIdentityAsync(registered.RepositoryId, path);

        Assert.Equal(registered.WorkgroupRepoId, resolved);
    }

    [Fact]
    public async Task ResolveCasIdentityAsync_is_sticky_to_an_already_existing_snapshot_chain()
    {
        var (registry, services) = BuildServices();
        var path = Path.Combine(Path.GetTempPath(), $"studio-repo-{Guid.NewGuid():N}");
        var registered = await registry.RegisterAsync(path, "existing chain");

        var snapshots = services.GetRequiredService<IRepositorySnapshotService>();
        var pathKey = Path.GetFullPath(path);
        await snapshots.CreateAsync(pathKey, new Dictionary<string, string> { ["a.txt"] = "hash-a" }, baseSnapshotId: null);

        // Even though this repository DOES have a WorkgroupRepoId-shaped local candidate id, an
        // already-existing chain under the path key must win — never orphan pre-7.2 history.
        var resolved = await registry.ResolveCasIdentityAsync(registered.RepositoryId, path);

        Assert.Equal(pathKey, resolved);
    }

    [Fact]
    public async Task ResolveCasIdentityAsync_resolves_a_foreign_repositoryId_via_a_replicated_RepositoryV1_row()
    {
        var (registry, services) = BuildServices();
        var nodeStore = services.GetRequiredService<IStudioNodeStore>();

        // Simulates a peer's own local-candidate RepositoryV1 row arriving via replication (the
        // "studio" room is shared upstream pre-7.3) — this peer never called RegisterAsync for it
        // itself, so it's absent from the registry's own in-memory cache.
        var foreignRepositoryId = "repo-foreign0000000000000000000000";
        var foreignRow = new RepositoryV1(
            foreignRepositoryId, @"C:\peer-a-only\path", "peer A's repo", DateTimeOffset.UtcNow,
            WorkgroupRepoId: "repo-workgroup0000000000000000000");
        await nodeStore.WriteNodeAsync(
            StudioNodeKind.RepositoryV1, foreignRepositoryId, JsonSerializer.Serialize(foreignRow));

        var resolved = await registry.ResolveCasIdentityAsync(foreignRepositoryId, repositoryPath: null);

        Assert.Equal("repo-workgroup0000000000000000000", resolved);
    }

    [Fact]
    public async Task ResolveCasIdentityAsync_returns_null_for_a_completely_unknown_repositoryId()
    {
        var (registry, _) = BuildServices();

        var resolved = await registry.ResolveCasIdentityAsync("repo-does-not-exist-anywhere", repositoryPath: null);

        Assert.Null(resolved);
    }
}
