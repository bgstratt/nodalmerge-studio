namespace NodalMerge.Studio.AgentRuntime;

// Slice 21/22 — cheap, synchronous pre-filter so DomainAgentTriggerService doesn't fire an LLM
// call on every Research/Decision/Constraint artifact recorded system-wide. Only artifacts that
// look relevant to a given domain agent's definition reach a DomainAgentLoop for that definition,
// which then makes the real judgment call.
public static class DomainAgentTriggerHeuristic
{
    public static bool IsRelevant(DomainAgentDefinition definition, string? title, string? body) =>
        definition.Keywords.Any(k =>
            (title?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (body?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false));
}
