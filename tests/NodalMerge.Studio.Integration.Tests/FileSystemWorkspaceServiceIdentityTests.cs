using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 7.2 (plans/cas-distribution-and-storage.md Phase 7) — a cold peer with no
/// Workspace:SeedRepositoryPath configured (no local default clone at all — e.g. the multi-user
/// smoke's host B) must resolve a branch's owning repository via its WorkUnit.RepositoryId instead
/// of silently returning "not found". This covers the two outcomes that RepositoryId resolution can
/// have on such a peer: it resolves (via IRepositoryRegistryService.ResolveCasIdentityAsync), or it
/// doesn't — in which case MaterializeFileAsync must throw RepositoryIdentityUnresolvedException
/// (an identity-aware error), not silently fall through to the ordinary "path not found in the
/// latest repository snapshot" 404 (StudioRestEndpoints' materialize-file handler distinguishes the
/// two — see its own try/catch).
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemWorkspaceServiceIdentityTests
{
    private sealed class FakeWorkUnitLookup(WorkUnit workUnit) : IWorkUnitService
    {
        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>(
                branchId is null || branchId == workUnit.BranchId ? [workUnit] : []);

        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult(workUnitId == workUnit.WorkUnitId ? workUnit : null);

        // Nothing under test below calls any other member.
        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkUnit> CreateWorkUnitAsync(string goal, string owner, string? branchId = null, string? successCriteria = null, string? repositoryPath = null, string? parentWorkUnitId = null, IReadOnlyList<string>? dependsOn = null, IReadOnlyList<string>? fileScope = null, WorkUnitExpectedOutputKind expectedOutputKind = WorkUnitExpectedOutputKind.FileChange, string? repositoryId = null, IReadOnlyList<FileReferenceV1>? referenceFiles = null, string? workspaceId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string workUnitId, string? blockedReason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> IncrementReviewRejectionCountAsync(string workUnitId, bool automated, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> IncrementFailureAttemptCountAsync(string workUnitId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> AmendGoalForSteeredRetryAsync(string workUnitId, string amendedGoal, string steeringContext, string deadLetterEntryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFileScopeAsync(string workUnitId, IReadOnlyList<string> fileScope, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> AddDependencyAsync(string workUnitId, string dependsOnWorkUnitId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // Always reports the given repositoryId as unbindable (step 3's local-path fallback never
    // applies here — no repositoryPath is ever passed) — simulates a peer that has neither its own
    // registration NOR a replicated foreign row for this id anywhere.
    private sealed class UnresolvableRepositoryRegistry : IRepositoryRegistryService
    {
        public Task<RepositoryV1> RegisterAsync(string path, string? label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryV1?> ResolveDisambiguationAsync(string repositoryId, string? chosenRepoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryRelinkResult?> RelinkAsync(string repositoryId, RepositoryRelinkMode mode, string? chosenRepoId = null, bool commit = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryV1> CreateAsync(string path, string? label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryV1> CloneAsync(string url, string targetPath, string? label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> ReadFileAsync(string repositoryId, string relativePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string? subPath = null, string? pattern = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RepositoryV1>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryV1?> GetAsync(string repositoryId, CancellationToken ct = default) => Task.FromResult<RepositoryV1?>(null);
        public Task<IReadOnlyList<string>> FilterUnregisteredAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> ResolveCasIdentityAsync(string? repositoryId, string? repositoryPath = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    private static IFileWorkspaceService Build(WorkUnit owningWorkUnit, out string rootPath)
    {
        rootPath = Path.Combine(Path.GetTempPath(), $"studio-identity-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        // No SeedRepositoryPath — the cold-peer case (no local default clone at all).
        services.AddSingleton(new WorkspaceOptions { RootPath = rootPath });
        // Override with fakes AFTER AddInMemoryStorage so these win at resolution time.
        services.AddSingleton<IWorkUnitService>(new FakeWorkUnitLookup(owningWorkUnit));
        services.AddSingleton<IRepositoryRegistryService>(new UnresolvableRepositoryRegistry());
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IFileWorkspaceService>();
    }

    [Fact]
    public async Task MaterializeFileAsync_throws_identity_aware_error_for_an_unbindable_owning_RepositoryId()
    {
        var workUnit = new WorkUnit(
            "WU-1", "goal", "branch-cold", WorkUnitStatus.Executing, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "owner", null, null, null, null, [], [], RepositoryId: "repo-unbindable-on-this-peer");
        var fileWorkspace = Build(workUnit, out var rootPath);
        try
        {
            var ex = await Assert.ThrowsAsync<RepositoryIdentityUnresolvedException>(
                () => fileWorkspace.MaterializeFileAsync("branch-cold", "README.md"));

            Assert.Equal("repo-unbindable-on-this-peer", ex.RepositoryId);
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task MaterializeFileAsync_returns_false_when_no_repository_context_exists_at_all()
    {
        // No SeedRepositoryPath, and no owning work unit for this branch at all (distinct from the
        // "owning work unit found but its RepositoryId is unbindable" case above) — this is exactly
        // pre-7.2's existing "nothing configured" behavior, unchanged: a plain false, not an
        // exception.
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        var rootPath = Path.Combine(Path.GetTempPath(), $"studio-identity-{Guid.NewGuid():N}");
        services.AddSingleton(new WorkspaceOptions { RootPath = rootPath });
        var provider = services.BuildServiceProvider();
        var fileWorkspace = provider.GetRequiredService<IFileWorkspaceService>();

        try
        {
            var found = await fileWorkspace.MaterializeFileAsync("no-such-branch", "README.md");
            Assert.False(found);
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        }
    }
}
