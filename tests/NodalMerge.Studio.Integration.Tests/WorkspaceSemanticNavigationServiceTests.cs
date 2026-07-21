using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class WorkspaceSemanticNavigationServiceTests : IAsyncLifetime
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-semnav-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_rootPath);

    private (IFileWorkspaceService FileWorkspace, IWorkspaceSemanticNavigationService Semantic) Build()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
        var provider = services.BuildServiceProvider();
        return (
            provider.GetRequiredService<IFileWorkspaceService>(),
            provider.GetRequiredService<IWorkspaceSemanticNavigationService>());
    }

    [Fact]
    public async Task FindDefinitionsAsync_finds_interface_definition_by_symbol_name()
    {
        var (fileWorkspace, semantic) = Build();
        await SeedAsync(fileWorkspace);

        var (locations, truncated) = await semantic.FindDefinitionsAsync("main", new WorkspaceSymbolQuery(Symbol: "IUserRepository"));

        Assert.False(truncated);
        var definition = Assert.Single(locations, l => l.Path == "src/App/IUserRepository.cs");
        Assert.Equal("IUserRepository", definition.SymbolName);
        Assert.Equal(3, definition.Line);
    }

    [Fact]
    public async Task FindReferencesAsync_finds_symbol_usages_across_files()
    {
        var (fileWorkspace, semantic) = Build();
        await SeedAsync(fileWorkspace);

        var (locations, truncated) = await semantic.FindReferencesAsync("main", new WorkspaceSymbolQuery(Symbol: "IUserRepository"));

        Assert.False(truncated);
        Assert.Contains(locations, l => l.Path == "src/App/UserRepository.cs" && l.Line == 3);
        Assert.Contains(locations, l => l.Path == "src/App/UserService.cs" && l.Line == 3);
        Assert.Contains(locations, l => l.Path == "src/App/UserService.cs" && l.Line == 5);
    }

    [Fact]
    public async Task FindImplementationsAsync_finds_concrete_type_for_interface()
    {
        var (fileWorkspace, semantic) = Build();
        await SeedAsync(fileWorkspace);

        var (locations, truncated) = await semantic.FindImplementationsAsync("main", new WorkspaceSymbolQuery(Symbol: "IUserRepository"));

        Assert.False(truncated);
        var implementation = Assert.Single(locations, l => l.Path == "src/App/UserRepository.cs");
        Assert.Equal("UserRepository", implementation.SymbolName);
        Assert.Equal(3, implementation.Line);
    }

    private static async Task SeedAsync(IFileWorkspaceService fileWorkspace)
    {
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "src/App/App.csproj", Csproj);
        await fileWorkspace.WriteAsync("main", "src/App/IUserRepository.cs", IUserRepositoryCs);
        await fileWorkspace.WriteAsync("main", "src/App/UserRepository.cs", UserRepositoryCs);
        await fileWorkspace.WriteAsync("main", "src/App/UserService.cs", UserServiceCs);
    }

    private const string Csproj = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""";

    private const string IUserRepositoryCs = """
namespace Demo;

public interface IUserRepository
{
    string GetName();
}
""";

    private const string UserRepositoryCs = """
namespace Demo;

public sealed class UserRepository : IUserRepository
{
    public string GetName() => "ok";
}
""";

    private const string UserServiceCs = """
namespace Demo;

public sealed class UserService(IUserRepository repository)
{
    private readonly IUserRepository _repository = repository;

    public string Load() => _repository.GetName();
}
""";
}
