using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

public class NonOverlappingFileScopeRuleTests
{
    private static Dictionary<string, object?> Context(
        IReadOnlyList<string> fileScope,
        IReadOnlyList<FileScopeSibling> activeSiblings) =>
        new()
        {
            ["fileScope"] = fileScope,
            ["activeSiblings"] = activeSiblings,
        };

    [Fact]
    public async Task EvaluateAsync_allows_overlap_when_toggle_is_off()
    {
        var options = new WorkspaceOptions { BlockOverlappingFileScope = false };
        var rule = new NonOverlappingFileScopeRule(options);

        var context = Context(
            ["src/Foo.cs"],
            [new FileScopeSibling("sibling-1", "s1", WorkUnitStatus.Executing, ["src/Foo.cs"])]);

        var result = await rule.EvaluateAsync(context);

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task EvaluateAsync_rejects_overlap_with_an_active_sibling_when_toggle_is_on()
    {
        var options = new WorkspaceOptions { BlockOverlappingFileScope = true };
        var rule = new NonOverlappingFileScopeRule(options);

        var context = Context(
            ["src/Foo.cs"],
            [new FileScopeSibling("sibling-1", "s1", WorkUnitStatus.Executing, ["src/Foo.cs"])]);

        var result = await rule.EvaluateAsync(context);

        Assert.False(result.Allowed);
        Assert.Single(result.Violations);
        Assert.Contains("s1", result.Violations[0].Message);
    }

    [Fact]
    public async Task EvaluateAsync_allows_non_overlapping_file_scope_when_toggle_is_on()
    {
        var options = new WorkspaceOptions { BlockOverlappingFileScope = true };
        var rule = new NonOverlappingFileScopeRule(options);

        var context = Context(
            ["src/Foo.cs"],
            [new FileScopeSibling("sibling-1", "s1", WorkUnitStatus.Executing, ["src/Bar.cs"])]);

        var result = await rule.EvaluateAsync(context);

        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData(WorkUnitStatus.Created)]
    [InlineData(WorkUnitStatus.Proposed)]
    [InlineData(WorkUnitStatus.Merged)]
    public async Task EvaluateAsync_ignores_overlap_with_a_sibling_that_is_not_active(WorkUnitStatus siblingStatus)
    {
        var options = new WorkspaceOptions { BlockOverlappingFileScope = true };
        var rule = new NonOverlappingFileScopeRule(options);

        var context = Context(
            ["src/Foo.cs"],
            [new FileScopeSibling("sibling-1", "s1", siblingStatus, ["src/Foo.cs"])]);

        var result = await rule.EvaluateAsync(context);

        Assert.True(result.Allowed);
    }
}
