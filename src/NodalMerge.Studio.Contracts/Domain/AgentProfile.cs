namespace NodalMerge.Studio.Contracts.Domain;

public enum PipelineStage
{
    Orchestrate,
    Plan,
    Execute,
    Review,
    Merge,
}

// Slice 14c — glob patterns (e.g. "src/**/*.tsx") declaring this profile's file-scope specialty.
// Empty list = no declared specialty, same as today: routing falls through to
// IProfileSelectionService (heuristic default or LLM, depending on UseLlmProfileSelection).
public sealed record AgentProfile(
    string AgentProfileId,
    string Name,
    PipelineStage Stage,
    string SystemPrompt,
    IReadOnlyList<string> AllowedTools,
    int MaxIterations,
    IReadOnlyList<string> FileScopePatterns);
