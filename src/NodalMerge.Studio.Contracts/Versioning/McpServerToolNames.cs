namespace NodalMerge.Studio.Contracts.Versioning;

/// <summary>
/// External MCP server tool name constants for the nms_v1_* surface.
/// These are for external callers (Claude, other MCP clients) engaging the NodalMerge Studio
/// runtime at a high level. Distinct from the internal nm_v1_* agent surface defined in
/// McpToolNames.cs — do NOT mix or migrate between them.
/// </summary>
public static class McpServerToolNames
{
    // ── Goal lifecycle ────────────────────────────────────────────────────────
    public const string GoalRun               = "nms_v1_goal_run";
    public const string GoalList              = "nms_v1_goal_list";
    public const string GoalStatus            = "nms_v1_goal_status";
    public const string GoalCancel            = "nms_v1_goal_cancel";
    public const string GoalPause             = "nms_v1_goal_pause";
    public const string GoalResume            = "nms_v1_goal_resume";
    public const string GoalRecover            = "nms_v1_goal_recover";

    // ── Human interaction ─────────────────────────────────────────────────────
    public const string ClarificationRespond  = "nms_v1_clarification_respond";

    // ── Results ───────────────────────────────────────────────────────────────
    public const string ResultsGet            = "nms_v1_results_get";
    public const string ResultsApply          = "nms_v1_results_apply";

    // ── Repository ────────────────────────────────────────────────────────────
    public const string RepoRegister          = "nms_v1_repo_register";
    public const string RepoList              = "nms_v1_repo_list";

    // ── Workspace ─────────────────────────────────────────────────────────────
    public const string WorkspaceStatus       = "nms_v1_workspace_status";
    public const string FeedbackRecord        = "nms_v1_feedback_record";
}
