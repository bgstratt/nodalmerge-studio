using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

public class PolicyGateServiceTests
{
    private sealed class AlwaysBlockRule(string ruleId, PolicyCheckpoint checkpoint) : IPolicyRule
    {
        public string RuleId => ruleId;

        public PolicyCheckpoint Checkpoint => checkpoint;

        public Task<PolicyResult> EvaluateAsync(IReadOnlyDictionary<string, object?> context, CancellationToken ct = default) =>
            Task.FromResult(new PolicyResult(false, [new PolicyViolation(ruleId, "blocked for test")]));
    }

    [Fact]
    public async Task EvaluateAsync_with_zero_rules_allows_every_checkpoint()
    {
        var gate = new PolicyGateService([]);

        var result = await gate.EvaluateAsync(PolicyCheckpoint.BeforeEnqueue, new Dictionary<string, object?>());

        Assert.True(result.Allowed);
        Assert.Empty(result.Violations);
        Assert.Empty(await gate.ListRuleIdsAsync());
    }

    [Fact]
    public async Task EvaluateAsync_aggregates_violations_from_every_matching_rule()
    {
        var gate = new PolicyGateService([
            new AlwaysBlockRule("rule-a", PolicyCheckpoint.BeforeEnqueue),
            new AlwaysBlockRule("rule-b", PolicyCheckpoint.BeforeEnqueue),
        ]);

        var result = await gate.EvaluateAsync(PolicyCheckpoint.BeforeEnqueue, new Dictionary<string, object?>());

        Assert.False(result.Allowed);
        Assert.Equal(2, result.Violations.Count);
        Assert.Contains(result.Violations, v => v.RuleId == "rule-a");
        Assert.Contains(result.Violations, v => v.RuleId == "rule-b");
    }

    [Fact]
    public async Task EvaluateAsync_ignores_rules_registered_for_a_different_checkpoint()
    {
        var gate = new PolicyGateService([new AlwaysBlockRule("rule-a", PolicyCheckpoint.BeforeMerge)]);

        var result = await gate.EvaluateAsync(PolicyCheckpoint.BeforeEnqueue, new Dictionary<string, object?>());

        Assert.True(result.Allowed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task ListRuleIdsAsync_returns_every_registered_rule_regardless_of_checkpoint()
    {
        var gate = new PolicyGateService([
            new AlwaysBlockRule("rule-a", PolicyCheckpoint.BeforeEnqueue),
            new AlwaysBlockRule("rule-b", PolicyCheckpoint.BeforeMerge),
        ]);

        var ruleIds = await gate.ListRuleIdsAsync();

        Assert.Equal(["rule-a", "rule-b"], ruleIds);
    }
}
