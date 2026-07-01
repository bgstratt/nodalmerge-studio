namespace NodalMerge.Studio.AgentRuntime;

// Slice 22 — generalizes Slice 21's hardcoded Security agent into a data shape so additional
// domain agents (Architecture, and later Performance/Test/Documentation) are "write a definition,"
// not "write a new loop class." Every domain agent shares the same fixed observe-and-propose tool
// shape (see DomainAgentLoop) — only the name, prefix, relevance keywords, and prompt vary.
public sealed record DomainAgentDefinition(
    string Name,
    string TitlePrefix,
    IReadOnlyList<string> Keywords,
    string SystemPrompt,
    int MaxIterations = 8);
