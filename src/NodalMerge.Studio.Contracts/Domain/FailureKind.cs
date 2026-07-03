namespace NodalMerge.Studio.Contracts.Domain;

// Structured signal for *why* a work unit was dead-lettered, replacing free-text Reason-string
// matching (the eval-harness session found only two real recovery tracks worth distinguishing —
// see plans/orchestrator-reliability-and-observability.md Phase 1.4):
//   - Retry track (Exception, Stalled, MissingCredentials, ProgressNotVerified): the agent's
//     approach was wrong, or it never got a fair shot at all. Recoverable via human-steered retry
//     or a from-scratch re-plan.
//   - Continue track (MaxIterationsExceeded): nothing here implies the approach was wrong, only
//     that the iteration budget was too small. Recoverable via continuing with more iterations and
//     prior context (blocked on Phase 3 context compaction) or re-planning just this slice.
public enum FailureKind
{
    Exception,
    MaxIterationsExceeded,
    Stalled,
    MissingCredentials,
    ProgressNotVerified,
    ReviewRejected,
}
