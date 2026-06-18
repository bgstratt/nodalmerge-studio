using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class PolicyGateService : IPolicyGateService
{
    private readonly IReadOnlyList<IPolicyRule> _rules;

    public PolicyGateService(IEnumerable<IPolicyRule> rules)
    {
        _rules = [.. rules];
    }

    public async Task<PolicyResult> EvaluateAsync(
        PolicyCheckpoint checkpoint,
        IReadOnlyDictionary<string, object?> context,
        CancellationToken ct = default)
    {
        var violations = new List<PolicyViolation>();
        foreach (var rule in _rules.Where(r => r.Checkpoint == checkpoint))
        {
            var result = await rule.EvaluateAsync(context, ct).ConfigureAwait(false);
            if (!result.Allowed)
                violations.AddRange(result.Violations);
        }

        return new PolicyResult(violations.Count == 0, violations);
    }

    public Task<IReadOnlyList<string>> ListRuleIdsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. _rules.Select(r => r.RuleId)]);
}
