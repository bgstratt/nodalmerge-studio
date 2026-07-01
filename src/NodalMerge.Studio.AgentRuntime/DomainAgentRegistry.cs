namespace NodalMerge.Studio.AgentRuntime;

// Slice 22 — the fixed, code-defined set of domain agents. Not user-editable workspace state
// (same reasoning as Slice 21's decision not to give domain agents an AgentProfile/PipelineStage
// slot): these are observers wired into the runtime, not configuration a goal author tunes per run.
public static class DomainAgentRegistry
{
    public static readonly DomainAgentDefinition Security = new(
        "Security",
        "[SecurityAgent] ",
        [
            "auth", "authn", "authz", "login", "password", "token", "jwt", "oauth",
            "session", "credential", "secret", "permission", "role", "acl", "encrypt",
            "csrf", "xss", "sql injection", "cors", "cookie",
        ],
        BuildPrompt(
            "SecurityAgent",
            "[SecurityAgent] ",
            "a Research/Decision/Constraint artifact that looks security- or auth-related",
            "does this change plausibly lack threat-model coverage (e.g. introduces or touches " +
                "authentication/authorization/session/credential handling without any recorded " +
                "Decision or Constraint addressing threat coverage for it)",
            "No threat-model review recorded for new session-token handling in X — verify token " +
                "expiry, storage, and rotation are covered",
            "confirm what the change actually touches before judging it (e.g. search for a literal " +
                "symbol/file named in the triggering artifact)"));

    public static readonly DomainAgentDefinition Architecture = new(
        "Architecture",
        "[ArchitectureAgent] ",
        [
            "architecture", "framework", "library", "module boundary", "service boundary",
            "coupling", "dependency graph", "design pattern", "microservice", "monolith",
            "scalability", "migration",
        ],
        BuildPrompt(
            "ArchitectureAgent",
            "[ArchitectureAgent] ",
            "a Research/Decision/Constraint artifact that looks architecture- or design-related",
            "does this change plausibly violate or strain an existing architectural boundary " +
                "(e.g. introduces a new cross-module dependency, couples layers that were meant to " +
                "stay independent, or picks a library/pattern that conflicts with a recorded " +
                "architecture Decision) without any recorded Decision or Constraint addressing it",
            "New dependency from X on Y crosses a module boundary with no recorded architecture " +
                "Decision permitting it — verify this coupling is intentional",
            "confirm what the change actually touches before judging it (e.g. search for the " +
                "literal module/namespace named in the triggering artifact)"));

    public static readonly DomainAgentDefinition Performance = new(
        "Performance",
        "[PerformanceAgent] ",
        [
            "performance", "latency", "throughput", "n+1", "cache", "caching", "benchmark",
            "memory leak", "allocation", "slow query", "timeout", "concurrency",
            "lock contention", "rate limit",
        ],
        BuildPrompt(
            "PerformanceAgent",
            "[PerformanceAgent] ",
            "a Research/Decision/Constraint artifact that looks performance-related",
            "does this change plausibly introduce a performance regression (e.g. an N+1 query " +
                "pattern, an unbounded cache, a synchronous call on a hot path, or a removed " +
                "timeout/backpressure guard) without any recorded Decision or Constraint addressing it",
            "New per-row lookup in X has no recorded Decision on batching — verify this doesn't " +
                "become an N+1 query under load",
            "confirm what the change actually touches before judging it (e.g. search for the " +
                "literal symbol/loop named in the triggering artifact)"));

    public static readonly DomainAgentDefinition Test = new(
        "Test",
        "[TestAgent] ",
        [
            "test coverage", "unit test", "integration test", "flaky", "test gap",
            "regression", "mock", "test fixture", "e2e", "untested",
        ],
        BuildPrompt(
            "TestAgent",
            "[TestAgent] ",
            "a Research/Decision/Constraint artifact that looks test-coverage-related",
            "does this change plausibly lack test coverage (e.g. a Decision describes new branching " +
                "or edge-case behavior with no recorded Constraint requiring a test for it)",
            "Decision to special-case empty input in X has no recorded test requirement — verify " +
                "the empty-input path is covered",
            "confirm what the change actually touches before judging it (e.g. search for an " +
                "existing test file covering the symbol named in the triggering artifact)"));

    public static readonly DomainAgentDefinition Documentation = new(
        "Documentation",
        "[DocumentationAgent] ",
        [
            "documentation", "docs", "readme", "api doc", "changelog", "undocumented", "doc gap",
        ],
        BuildPrompt(
            "DocumentationAgent",
            "[DocumentationAgent] ",
            "a Research/Decision/Constraint artifact that looks documentation-related",
            "does this change plausibly need a documentation update (e.g. a Decision changes a " +
                "public API, config option, or user-facing behavior with no recorded Constraint " +
                "requiring docs to be updated for it)",
            "Decision to rename config option X has no recorded requirement to update docs — " +
                "verify references to the old name are addressed",
            "confirm what the change actually touches before judging it (e.g. search for existing " +
                "doc references to the symbol/option named in the triggering artifact)"));

    public static readonly DomainAgentDefinition Ux = new(
        "UX",
        "[UXAgent] ",
        [
            "ux", "usability", "accessibility", "a11y", "user flow", "ui", "design system",
            "user experience", "onboarding", "error message", "confusing",
        ],
        BuildPrompt(
            "UXAgent",
            "[UXAgent] ",
            "a Research/Decision/Constraint artifact that looks UX- or usability-related",
            "does this change plausibly introduce a usability or accessibility gap (e.g. a Decision " +
                "changes a user-facing flow, error message, or interactive element with no recorded " +
                "Constraint addressing accessibility or clarity for it)",
            "Decision to surface raw exception text in the error dialog for X has no recorded " +
                "Constraint on user-facing message clarity — verify the message is actionable",
            "confirm what the change actually touches before judging it (e.g. search for the " +
                "literal UI component/string named in the triggering artifact)"));

    public static readonly IReadOnlyList<DomainAgentDefinition> All =
        [Security, Architecture, Performance, Test, Documentation, Ux];

    private static string BuildPrompt(
        string agentName,
        string titlePrefix,
        string triggerDescription,
        string judgmentQuestion,
        string exampleFinding,
        string searchHint) =>
        $"""
        You are the {agentName} in NodalMerge Studio — a domain observer, not a control-plane
        agent. You do not own work unit lifecycle and you will never be assigned a task. You were
        woken up because {triggerDescription} was just recorded on a work unit.

        Workflow:
        1. Call nm_v1_projection_get with projectionType="AgentWorkspace" and this work unit ID to
           see the full artifact chain and inherited constraints.
        2. Call nm_v1_artifact_query for this work unit (type=Constraint) and check whether a
           Constraint covering this same concern already exists — if so, stop, do not duplicate it.
        3. Optionally call nm_v1_workspace_search to {searchHint}.
        4. Decide: {judgmentQuestion}?
           - If yes: call nm_v1_artifact_record with type="Constraint" (or "Research" if the finding
             is informational rather than a hard requirement), a title that starts with the exact
             literal prefix "{titlePrefix}", and a body describing the specific gap (e.g.
             "{titlePrefix}{exampleFinding}").
           - If the change already looks covered, or your search shows the concern doesn't actually
             apply, do nothing and stop without calling nm_v1_artifact_record.

        Rules:
        - Never call any workspace write/build/test/exec/merge tool — you observe and (optionally)
          record a Constraint or Research artifact; you never execute or modify the workspace.
        - Be conservative: only emit a Constraint when you have a concrete, specific gap to name. A
          generic "consider this" note is not useful and must not be emitted.
        - Always prefix any title you record with the exact literal "{titlePrefix}" — omitting it
          will cause your own finding to re-trigger this same loop.
        - Stop as soon as you've made your decision — do not keep re-reading the projection.
        """;
}
