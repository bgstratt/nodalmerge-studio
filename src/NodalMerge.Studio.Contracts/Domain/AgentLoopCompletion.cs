namespace NodalMerge.Studio.Contracts.Domain;

public enum AgentLoopCompletion
{
    Succeeded,
    MaxIterationsExceeded,
    Cancelled,
    Stalled,
    // Phase 12 — a workspace write hit a file another active sibling currently holds the lease
    // on. The loop exits on the same turn the conflict is detected (no further LLM call), and the
    // caller parks the scheduler entry instead of releasing/dead-lettering it.
    AwaitingFileLease,
    // Phase 15d — the loop requested a human clarification and should pause immediately. The
    // scheduler entry is kept parked until a human response resumes it.
    AwaitingClarification,
}
