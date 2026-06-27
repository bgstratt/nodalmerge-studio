namespace NodalMerge.Studio.Storage;

// A structured snapshot of "what can an agent/extension do here," resolved fresh from
// WorkspaceOptions on every call — never persisted, same determinism contract as a Projection.
// See plans/phase-16-workspace-aggregate.md, Part E. Build/Test/Run/Git/CreateRepository/
// ImportRepository are always true today: their MCP tools (nm_v1_workspace_build/test/exec/run,
// nm_v1_repository_create/clone) are registered unconditionally, not gated by any
// WorkspaceOptions flag. DocFetch and EnabledDomainAgents are the only capabilities this
// instance can actually have turned off.
public sealed record CapabilitiesV1(
    bool Build,
    bool Test,
    bool Run,
    bool Git,
    bool CreateRepository,
    bool ImportRepository,
    bool DocFetch,
    IReadOnlyList<string> EnabledDomainAgents);

public static class WorkspaceCapabilityResolver
{
    public static CapabilitiesV1 Resolve(WorkspaceOptions options) => new(
        Build: true,
        Test: true,
        Run: true,
        Git: true,
        CreateRepository: true,
        ImportRepository: true,
        DocFetch: options.DocFetchTools,
        EnabledDomainAgents: options.EnabledDomainAgents);
}
