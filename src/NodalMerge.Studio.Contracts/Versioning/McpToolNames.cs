namespace NodalMerge.Studio.Contracts.Versioning;

/// <summary>
/// Frozen v1 MCP tool names. Do not rename or remove without bumping contract version.
/// </summary>
public static class McpToolNames
{
    public const string ProjectionGet = "nm.v1.projection.get";
    public const string ProjectionList = "nm.v1.projection.list";

    public const string WorkUnitCreate = "nm.v1.workunit.create";
    public const string WorkUnitGet = "nm.v1.workunit.get";
    public const string WorkUnitUpdate = "nm.v1.workunit.update";
    public const string WorkUnitList = "nm.v1.workunit.list";

    public const string TaskCreate = "nm.v1.task.create";
    public const string TaskUpdate = "nm.v1.task.update";
    public const string TaskList = "nm.v1.task.list";
    public const string TaskAssign = "nm.v1.task.assign";

    public const string BranchCreate = "nm.v1.branch.create";
    public const string BranchCheckout = "nm.v1.branch.checkout";
    public const string BranchList = "nm.v1.branch.list";
    public const string BranchStatus = "nm.v1.branch.status";

    public const string MergePropose = "nm.v1.merge.propose";
    public const string MergeValidate = "nm.v1.merge.validate";
    public const string MergeReview = "nm.v1.merge.review";
    public const string MergeApply = "nm.v1.merge.apply";

    public const string ReplayRange = "nm.v1.replay.range";
    public const string ReplayRollback = "nm.v1.replay.rollback";
    public const string ReplayInspect = "nm.v1.replay.inspect";

    public const string StateMarkKnownGood = "nm.v1.state.markKnownGood";
    public const string StateFindKnownGood = "nm.v1.state.findKnownGood";
    public const string StateCheckoutKnownGood = "nm.v1.state.checkoutKnownGood";

    public const string SnapshotGet = "nm.v1.snapshot.get";
    public const string SnapshotCompare = "nm.v1.snapshot.compare";

    public const string AgentSpawn = "nm.v1.agent.spawn";
    public const string AgentPause = "nm.v1.agent.pause";
    public const string AgentResume = "nm.v1.agent.resume";
    public const string AgentStatus = "nm.v1.agent.status";
    public const string AgentStop = "nm.v1.agent.stop";

    public const string WorkspaceSummary = "nm.v1.workspace.summary";

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
        WorkspaceSummary
    ];
}
