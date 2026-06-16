namespace NodalMerge.Studio.Contracts.Versioning;

/// <summary>
/// Frozen v1 MCP tool names. Do not rename or remove without bumping contract version.
/// Names must match ^[a-zA-Z0-9_-]{1,128}$ (Anthropic tool name constraint).
/// </summary>
public static class McpToolNames
{
    public const string ProjectionGet = "nm_v1_projection_get";
    public const string ProjectionList = "nm_v1_projection_list";

    public const string WorkUnitCreate = "nm_v1_workunit_create";
    public const string WorkUnitGet = "nm_v1_workunit_get";
    public const string WorkUnitUpdate = "nm_v1_workunit_update";
    public const string WorkUnitList = "nm_v1_workunit_list";

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

    public const string SchedulerEnqueue = "nm_v1_scheduler_enqueue";
    public const string SchedulerPending = "nm_v1_scheduler_pending";

    public const string IntentRecord = "nm_v1_intent_record";

    public const string ArtifactRecord = "nm_v1_artifact_record";
    public const string ArtifactQuery  = "nm_v1_artifact_query";
    public const string ArtifactList   = "nm_v1_artifact_list";

    public const string WorkspaceRead   = "nm_v1_workspace_read";
    public const string WorkspaceWrite  = "nm_v1_workspace_write";
    public const string WorkspaceDelete = "nm_v1_workspace_delete";
    public const string WorkspaceList   = "nm_v1_workspace_list";
    public const string WorkspaceDiff   = "nm_v1_workspace_diff";
    public const string WorkspaceExists = "nm_v1_workspace_exists";

    public static IReadOnlyList<string> All { get; } =
    [
        ProjectionGet,
        ProjectionList,
        WorkUnitCreate,
        WorkUnitGet,
        WorkUnitUpdate,
        WorkUnitList,
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
        SchedulerEnqueue,
        SchedulerPending,
        IntentRecord,
        ArtifactRecord,
        ArtifactQuery,
        ArtifactList,
        WorkspaceRead,
        WorkspaceWrite,
        WorkspaceDelete,
        WorkspaceList,
        WorkspaceDiff,
        WorkspaceExists
    ];
}
