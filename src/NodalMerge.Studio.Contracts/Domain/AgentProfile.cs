namespace NodalMerge.Studio.Contracts.Domain;

public enum PipelineStage
{
    Orchestrate,
    Plan,
    Execute,
    Review,
    Merge,
}

public sealed record AgentProfile(
    string AgentProfileId,
    string Name,
    PipelineStage Stage,
    string SystemPrompt,
    IReadOnlyList<string> AllowedTools,
    int MaxIterations);
