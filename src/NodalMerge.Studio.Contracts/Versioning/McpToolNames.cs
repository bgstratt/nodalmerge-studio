namespace NodalMerge.Studio.Contracts.Versioning;

/// <summary>
/// Frozen v1 MCP tool names. Do not rename or remove without bumping contract version.
/// Names must match ^[a-zA-Z0-9_-]{1,128}$ (Anthropic tool name constraint).
/// </summary>
public static class McpToolNames
{
    public const string ProjectionGet = "nm_v1_projection_get";
    public const string ProjectionList = "nm_v1_projection_list";
    public const string ProjectionSnapshotCapture = "nm_v1_projection_snapshot_capture";
    public const string ProjectionSnapshotGet     = "nm_v1_projection_snapshot_get";
    public const string ProjectionSnapshotList    = "nm_v1_projection_snapshot_list";
    public const string ProjectionStaleCheck      = "nm_v1_projection_stale_check";
    public const string ProjectionCompare         = "nm_v1_projection_compare";
    public const string ProjectionMaterialize     = "nm_v1_projection_materialize";

    public const string WorkUnitCreate = "nm_v1_workunit_create";
    public const string WorkUnitGet = "nm_v1_workunit_get";
    public const string WorkUnitUpdate = "nm_v1_workunit_update";
    public const string WorkUnitList = "nm_v1_workunit_list";
    public const string WorkUnitDependents = "nm_v1_workunit_dependents";

    public const string TaskCreate = "nm_v1_task_create";
    public const string TaskUpdate = "nm_v1_task_update";
    public const string TaskList = "nm_v1_task_list";
    public const string TaskAssign = "nm_v1_task_assign";

    public const string BranchCreate = "nm_v1_branch_create";
    public const string BranchCheckout = "nm_v1_branch_checkout";
    public const string BranchList = "nm_v1_branch_list";
    public const string BranchStatus = "nm_v1_branch_status";

    public const string MergePropose = "nm_v1_merge_propose";
    public const string MergeValidate = "nm_v1_merge_validate";
    public const string MergeReview = "nm_v1_merge_review";
    public const string MergeApply = "nm_v1_merge_apply";

    public const string ReplayRange = "nm_v1_replay_range";
    public const string ReplayRollback = "nm_v1_replay_rollback";
    public const string ReplayInspect = "nm_v1_replay_inspect";

    public const string StateMarkKnownGood = "nm_v1_state_markKnownGood";
    public const string StateFindKnownGood = "nm_v1_state_findKnownGood";
    public const string StateCheckoutKnownGood = "nm_v1_state_checkoutKnownGood";

    public const string SnapshotGet = "nm_v1_snapshot_get";
    public const string SnapshotCompare = "nm_v1_snapshot_compare";

    public const string AgentSpawn = "nm_v1_agent_spawn";
    public const string AgentPause = "nm_v1_agent_pause";
    public const string AgentResume = "nm_v1_agent_resume";
    public const string AgentStatus = "nm_v1_agent_status";
    public const string AgentStop = "nm_v1_agent_stop";

    public const string WorkspaceSummary = "nm_v1_workspace_summary";
    public const string WorkspaceStatus = "nm_v1_workspace_status";
    public const string DocFetch = "nm_v1_doc_fetch";

    public const string SchedulerEnqueue = "nm_v1_scheduler_enqueue";
    public const string SchedulerPending = "nm_v1_scheduler_pending";
    public const string ClarificationRequest = "nm_v1_clarification_request";

    public const string IntentRecord = "nm_v1_intent_record";

    public const string ArtifactRecord     = "nm_v1_artifact_record";
    public const string ArtifactRecordPlan = "nm_v1_artifact_record_plan";
    public const string ArtifactQuery      = "nm_v1_artifact_query";
    public const string ArtifactList       = "nm_v1_artifact_list";
    public const string ArtifactInvalidate = "nm_v1_artifact_invalidate";

    public const string WorkspaceRead    = "nm_v1_workspace_read";
    public const string WorkspaceReadMany = "nm_v1_workspace_read_many";
    public const string WorkspaceWrite   = "nm_v1_workspace_write";
    public const string WorkspaceDelete  = "nm_v1_workspace_delete";
    public const string WorkspaceList    = "nm_v1_workspace_list";
    public const string WorkspaceSearch  = "nm_v1_workspace_search";
    public const string WorkspaceSymbolDefinition = "nm_v1_workspace_symbol_definition";
    public const string WorkspaceSymbolReferences = "nm_v1_workspace_symbol_references";
    public const string WorkspaceSymbolImplementation = "nm_v1_workspace_symbol_implementation";
    public const string WorkspaceReplace = "nm_v1_workspace_replace";
    public const string WorkspaceDiff    = "nm_v1_workspace_diff";
    public const string WorkspaceExists  = "nm_v1_workspace_exists";

    public const string WorkspaceBuild  = "nm_v1_workspace_build";
    public const string WorkspaceTest   = "nm_v1_workspace_test";
    public const string WorkspaceExec   = "nm_v1_workspace_exec";
    public const string WorkspaceRun    = "nm_v1_workspace_run";
    public const string WorkspaceRunStop = "nm_v1_workspace_run_stop";
    public const string WorkspaceExecStatus = "nm_v1_workspace_exec_status";
    public const string WorkspacePath   = "nm_v1_workspace_path";
    public const string WorkspaceProfileGet    = "nm_v1_workspace_profile_get";
    public const string WorkspaceProfileRescan = "nm_v1_workspace_profile_rescan";
    public const string WorkspaceSwitch = "nm_v1_workspace_switch";
    public const string WorkspaceCapabilities = "nm_v1_workspace_capabilities";

    public const string GoalCreate = "nm_v1_goal_create";
    public const string GoalList   = "nm_v1_goal_list";

    public const string DecisionRecord = "nm_v1_decision_record";
    public const string DecisionList   = "nm_v1_decision_list";

    public const string EvidenceAttach = "nm_v1_evidence_attach";
    public const string EvidenceList   = "nm_v1_evidence_list";

    public const string TrajectoryCreate = "nm_v1_trajectory_create";
    public const string TrajectoryReplay = "nm_v1_trajectory_replay";

    public const string HypothesisFork = "nm_v1_hypothesis_fork";
    public const string HypothesisList = "nm_v1_hypothesis_list";

    public const string ReasoningRecord = "nm_v1_reasoning_record";

    public const string ModelCompare = "nm_v1_model_compare";
    public const string ModelReplay  = "nm_v1_model_replay";

    public const string RepositoryRegister = "nm_v1_repository_register";
    public const string RepositoryList     = "nm_v1_repository_list";
    public const string RepositoryCreate   = "nm_v1_repository_create";
    public const string RepositoryClone    = "nm_v1_repository_clone";
    public const string RepositoryReadFile  = "nm_v1_repository_read_file";
    public const string RepositoryListFiles = "nm_v1_repository_list_files";

    public const string CausalGetFrontier         = "nm_v1_causal_get_frontier";
    public const string CausalGetParents          = "nm_v1_causal_get_parents";
    public const string CausalGetResolution       = "nm_v1_causal_get_resolution";
    public const string CausalComputeSyncDiff     = "nm_v1_causal_compute_sync_diff";

    public static IReadOnlyList<string> All { get; } =
    [
        ProjectionGet,
        ProjectionList,
        ProjectionSnapshotCapture,
        ProjectionSnapshotGet,
        ProjectionSnapshotList,
        ProjectionStaleCheck,
        ProjectionCompare,
        ProjectionMaterialize,
        WorkUnitCreate,
        WorkUnitGet,
        WorkUnitUpdate,
        WorkUnitList,
        WorkUnitDependents,
        TaskCreate,
        TaskUpdate,
        TaskList,
        TaskAssign,
        BranchCreate,
        BranchCheckout,
        BranchList,
        BranchStatus,
        MergePropose,
        MergeValidate,
        MergeReview,
        MergeApply,
        ReplayRange,
        ReplayRollback,
        ReplayInspect,
        StateMarkKnownGood,
        StateFindKnownGood,
        StateCheckoutKnownGood,
        SnapshotGet,
        SnapshotCompare,
        AgentSpawn,
        AgentPause,
        AgentResume,
        AgentStatus,
        AgentStop,
        WorkspaceSummary,
        WorkspaceStatus,
        DocFetch,
        SchedulerEnqueue,
        SchedulerPending,
        ClarificationRequest,
        IntentRecord,
        ArtifactRecord,
        ArtifactRecordPlan,
        ArtifactQuery,
        ArtifactList,
        ArtifactInvalidate,
        WorkspaceRead,
        WorkspaceReadMany,
        WorkspaceWrite,
        WorkspaceDelete,
        WorkspaceList,
        WorkspaceSearch,
        WorkspaceSymbolDefinition,
        WorkspaceSymbolReferences,
        WorkspaceSymbolImplementation,
        WorkspaceReplace,
        WorkspaceDiff,
        WorkspaceExists,
        WorkspaceBuild,
        WorkspaceTest,
        WorkspaceExec,
        WorkspaceRun,
        WorkspaceRunStop,
        WorkspaceExecStatus,
        WorkspacePath,
        WorkspaceProfileGet,
        WorkspaceProfileRescan,
        WorkspaceSwitch,
        WorkspaceCapabilities,
        GoalCreate,
        GoalList,
        DecisionRecord,
        DecisionList,
        EvidenceAttach,
        EvidenceList,
        TrajectoryCreate,
        TrajectoryReplay,
        HypothesisFork,
        HypothesisList,
        ReasoningRecord,
        ModelCompare,
        ModelReplay,
        RepositoryRegister,
        RepositoryList,
        RepositoryCreate,
        RepositoryClone,
        RepositoryReadFile,
        RepositoryListFiles,
        CausalGetFrontier,
        CausalGetParents,
        CausalGetResolution,
        CausalComputeSyncDiff
    ];
}
