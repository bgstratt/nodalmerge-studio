using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>Covers Phase 7.2 (Slices 22a/22b) — multi-fork experiments.</summary>
[Trait("Category", "Integration")]
public class ExperimentServiceTests
{
    private static async Task<(
        IExperimentService Experiments,
        IWorkUnitService WorkUnits,
        IWorkScheduler Scheduler)> BuildAsync()
    {
        var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());

        return (
            app.Services.GetRequiredService<IExperimentService>(),
            app.Services.GetRequiredService<IWorkUnitService>(),
            app.Services.GetRequiredService<IWorkScheduler>());
    }

    [Fact]
    public async Task CreateAsync_requires_at_least_two_forks()
    {
        var (experiments, _, _) = await BuildAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => experiments.CreateAsync(new ExperimentSpec(
            "Pick a caching layer", "test", HypothesisForkType.Code,
            [new ExperimentForkSpec("worker")])));
    }

    [Fact]
    public async Task CreateAsync_model_experiments_require_every_fork_to_have_a_profile()
    {
        var (experiments, _, _) = await BuildAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => experiments.CreateAsync(new ExperimentSpec(
            "Compare models", "test", HypothesisForkType.Model,
            [new ExperimentForkSpec("gpt-profile"), new ExperimentForkSpec(null)])));
    }

    [Fact]
    public async Task CreateAsync_architecture_experiments_require_every_fork_to_have_constraint_text()
    {
        var (experiments, _, _) = await BuildAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => experiments.CreateAsync(new ExperimentSpec(
            "Pick an architecture", "test", HypothesisForkType.Architecture,
            [new ExperimentForkSpec(null, "event-sourcing"), new ExperimentForkSpec(null)])));
    }

    [Fact]
    public async Task CreateAsync_creates_a_parent_container_and_one_child_per_fork()
    {
        var (experiments, workUnits, _) = await BuildAsync();

        var result = await experiments.CreateAsync(new ExperimentSpec(
            "Pick a caching layer", "test", HypothesisForkType.Library,
            [
                new ExperimentForkSpec(null, "Redis"),
                new ExperimentForkSpec(null, "Memcached"),
            ]));

        Assert.Equal(2, result.ForkWorkUnitIds.Count);

        var parent = await workUnits.GetAsync(result.ParentWorkUnitId);
        Assert.NotNull(parent);
        Assert.Equal(WorkUnitStatus.Created, parent!.Status); // never enqueued/executed

        var children = await workUnits.GetChildrenAsync(result.ParentWorkUnitId);
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(HypothesisForkType.Library, c.ForkType));
        Assert.Contains(children, c => c.Goal.Contains("Redis"));
        Assert.Contains(children, c => c.Goal.Contains("Memcached"));
    }

    [Fact]
    public async Task CreateAsync_auto_enqueues_forks_that_have_a_profileId()
    {
        var (experiments, _, scheduler) = await BuildAsync();

        var result = await experiments.CreateAsync(new ExperimentSpec(
            "Compare models", "test", HypothesisForkType.Model,
            [
                new ExperimentForkSpec("profile-a"),
                new ExperimentForkSpec("profile-b"),
            ]));

        var pending = await scheduler.ListPendingAsync();
        Assert.Equal(2, pending.Count(i => result.ForkWorkUnitIds.Contains(i.WorkUnitId)));
    }

    [Fact]
    public async Task CreateAsync_persists_the_experiment_node_and_GetAsync_returns_it()
    {
        var (experiments, _, _) = await BuildAsync();

        var result = await experiments.CreateAsync(new ExperimentSpec(
            "Pick a strategy", "test", HypothesisForkType.Product,
            [
                new ExperimentForkSpec(null, "freemium"),
                new ExperimentForkSpec(null, "enterprise"),
            ],
            ComparisonMetricHint: "conversion rate"));

        var experimentId = (await experiments.ListAsync()).Single(e => e.ParentWorkUnitId == result.ParentWorkUnitId).ExperimentId;
        var fetched = await experiments.GetAsync(experimentId);

        Assert.NotNull(fetched);
        Assert.Equal(HypothesisForkType.Product, fetched!.ForkType);
        Assert.Equal("conversion rate", fetched.ComparisonMetricHint);
        Assert.Equal(result.ForkWorkUnitIds.OrderBy(x => x), fetched.ForkWorkUnitIds.OrderBy(x => x));
    }

    [Fact]
    public async Task ListAsync_returns_newest_first()
    {
        var (experiments, _, _) = await BuildAsync();

        var first = await experiments.CreateAsync(new ExperimentSpec(
            "Experiment A", "test", HypothesisForkType.Code,
            [new ExperimentForkSpec(null), new ExperimentForkSpec(null)]));
        var second = await experiments.CreateAsync(new ExperimentSpec(
            "Experiment B", "test", HypothesisForkType.Code,
            [new ExperimentForkSpec(null), new ExperimentForkSpec(null)]));

        var all = await experiments.ListAsync();
        var secondNode = all.First(e => e.ParentWorkUnitId == second.ParentWorkUnitId);
        var firstNode = all.First(e => e.ParentWorkUnitId == first.ParentWorkUnitId);

        Assert.True(all.ToList().IndexOf(secondNode) < all.ToList().IndexOf(firstNode));
    }
}
