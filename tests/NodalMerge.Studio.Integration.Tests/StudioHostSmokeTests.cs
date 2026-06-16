using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class StudioHostSmokeTests
{
    [Fact]
    public void Build_registers_studio_services()
    {
        var app = StudioWebApplication.Build([]);
        Assert.NotNull(app.Services.GetService(typeof(IProjectionManager)));
        Assert.NotNull(app.Services.GetService(typeof(ITaskService)));
        Assert.NotNull(app.Services.GetService(typeof(IMergeService)));
        Assert.NotNull(app.Services.GetService(typeof(IWorkUnitService)));
        Assert.NotNull(app.Services.GetService(typeof(IArtifactLineageService)));
    }
}
