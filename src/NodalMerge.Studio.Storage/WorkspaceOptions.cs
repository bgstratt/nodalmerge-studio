namespace NodalMerge.Studio.Storage;

public sealed class WorkspaceOptions
{
    public string RootPath { get; set; } = Path.Combine(Path.GetTempPath(), "studio-workspace");
    public string? SeedRepositoryPath { get; set; }
    public long MaxReadBytes  { get; set; } = 524_288;   // 512 KB
    public long MaxWriteBytes { get; set; } = 2_097_152; // 2 MB
    // 2 was too tight in practice: the orchestrator's system prompt offers two read-only
    // inspection tools (workunit_get, projection_get) before the first routing decision, and a
    // model reasonably calling both in a row already burns the entire stall budget before it
    // ever gets to act. 4 leaves room for a couple of inspection calls while still catching a
    // genuinely looping orchestrator well before MaxIterations (25).
    public int StallDetectionCycles { get; set; } = 4;
    public bool UseLlmProfileSelection { get; set; } = false;
    public int MaxConcurrentWorkers { get; set; } = 3;
    public int SchedulerPollIntervalMs { get; set; } = 2_000;
    // Slice 14b — opt-in. False keeps today's behavior byte-for-byte: NonOverlappingFileScopeRule
    // is registered but evaluates to Allowed regardless of overlap. True rejects the second of two
    // overlapping siblings at BeforeEnqueue instead of just warning after the fact.
    public bool BlockOverlappingFileScope { get; set; } = false;

    // ── Slice 16e/16f/16m — workspace execution ───────────────────────────────

    public bool RequireBuildBeforeProposal { get; set; }   // default false
    public bool RequireTestBeforeProposal  { get; set; }   // default false
    public string? BuildCommand { get; set; }               // null = auto-detect
    public string? TestCommand  { get; set; }               // null = auto-detect
    public int ExecutionTimeoutSeconds { get; set; } = 300;

    public int MaxOutputBytes  { get; set; } = 64 * 1024;
    public string TruncationMode { get; set; } = "Tail";   // "Head", "Tail", "HeadTail"

    public string PostMergeExecutionMode { get; set; } = "Disabled"; // "Disabled", "Async", "Blocking"

    // ── Slice 21a — promotion branch ─────────────────────────────────────────

    // When true, auto-apply (and manual merge review) targets CandidateBranchId instead of the
    // work unit's parent branch.  Humans promote candidate → main via POST /studio/branches/candidate/promote.
    public bool UsePromotionBranch { get; set; } = false;
    public string CandidateBranchId { get; set; } = "candidate";
}
