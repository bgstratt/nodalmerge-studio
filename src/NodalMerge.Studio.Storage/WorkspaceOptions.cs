namespace NodalMerge.Studio.Storage;

public sealed class WorkspaceOptions
{
    public string RootPath { get; set; } = Path.Combine(Path.GetTempPath(), "studio-workspace");
    public string? SeedRepositoryPath { get; set; }
    // Explicit override for the CAS root. When null, defaults to {SeedRepositoryPath}/.nodalmerge/cas
    // at startup. Configured via NodalMerge:Storage:FileBlobs:RootPath; see StudioWebApplication.
    public string? CasRootPath { get; set; }
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

    // ── Expected output kind enforcement ─────────────────────────────────────

    // Opt-in, default false (matches RequireBuildBeforeProposal's convention above): when true,
    // MergeCommandService.ProposeAsync rejects a proposal whose work unit expects FileChange
    // output but whose diff touched zero files and carried no NoFileChangesJustification.
    public bool EnforceExpectedOutputKind { get; set; } = false;

    // ── Repository virtualization — Phase 14 (git commit/push gating) ────────

    // Opt-in, default false. When false, ExportAsync materializes files to disk but does not
    // call git commit — the user/CI is responsible for committing. Set to true only when you
    // intentionally want agents to create git commits (e.g. headless CI pipelines).
    public bool AllowAgentGitCommits { get; set; } = false;

    // Opt-in, default false. Only meaningful when AllowAgentGitCommits = true.
    // Shells out to `git push origin {branchName}` using the host's existing credential store.
    public bool AllowAgentGitPush { get; set; } = false;

    // Opt-in, default false. When all merge strategies fail on a conflict, the resolve endpoint
    // re-queues the losing work unit (WorkUnitIdB — the second/losing op) as a new work unit
    // with the original goal and parentWorkUnitId set. The re-queued agent has full op-log
    // context about its prior attempt. Enable for automated pipelines; leave off for human review.
    public bool AllowAutoRequeue { get; set; } = false;

    // ── Repository virtualization — Phase 9 ──────────────────────────────────

    // When false (default): conflicting ops are flagged and recorded but both land in the store.
    // When true: the second op of a conflicting pair is rejected with ConflictingOpException.
    // Keep false until Phase 10's resolution path is live so existing flows are unaffected.
    public bool BlockConflictingOps { get; set; } = false;

    // ── Repository virtualization — Phase 2 / 7 ──────────────────────────────

    // Snapshot policy for the repository op log. Exposed here (not buried in a sub-config) so
    // the Studio options panel can surface both knobs as first-class toggles.
    // OpsPerSnapshot: null = no threshold trigger (work-unit-completion and between-run sync
    // are the only triggers). A non-null value enables a Phase 6 compaction threshold.
    public SnapshotPolicy Snapshots { get; set; } = new();

    // Bounded concurrency for parallel CAS blob reads during materialization (Phase 7).
    // Higher values speed up large-repo reconstruction at the cost of more simultaneous I/O.
    public int MaterializerConcurrency { get; set; } = 4;

    // ── Slice 15g — constrained external documentation fetch ─────────────────

    // Runtime gate for nm_v1_doc_fetch.
    public bool DocFetchTools { get; set; } = false;

    // Request guards. Allowlist may be empty (meaning "allow all except denylist").
    public List<string> DocFetchAllowedSchemes { get; set; } = ["https"];
    public List<string> DocFetchAllowedDomains { get; set; } = [];
    public List<string> DocFetchDeniedDomains  { get; set; } = [];

    // Content/latency bounds.
    public int DocFetchMaxContentBytes { get; set; } = 32 * 1024;
    public int DocFetchTimeoutSeconds  { get; set; } = 15;
    public int DocFetchSummaryMaxChars { get; set; } = 400;

    // ── Slice 21/22 — domain agents ──────────────────────────────────────────

    // Opt-in, default empty (matches DocFetchTools' convention above): holds the Name of each
    // domain agent (e.g. "Security", "Architecture" — see DomainAgentRegistry in AgentRuntime)
    // allowed to reactively spawn when a Research/Decision/Constraint artifact it judges relevant
    // is recorded. A per-work-unit override (IAgentControlService.GetEnabledDomainAgents, captured
    // at orchestrator spawn time the same way AutoReviewProfileId is) takes priority over this
    // default when set.
    public List<string> EnabledDomainAgents { get; set; } = [];
}

// Phase 2 — configurable snapshot policy for the repository op log.
// Designed to be surfaced in the Studio options panel as two independent toggles.
public sealed class SnapshotPolicy
{
    // Create a snapshot after between-run sync emits any ops (Case 2). True by default.
    public bool SnapshotOnSync { get; set; } = true;

    // Future Phase 6 threshold: create an intermediate snapshot if a single work unit emits
    // more than this many ops. Null disables threshold-based snapshotting.
    public int? OpsPerSnapshot { get; set; } = null;
}
