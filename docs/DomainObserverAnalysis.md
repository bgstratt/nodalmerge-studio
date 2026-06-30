Comprehensive Analysis: Session Defaults, Observers, Constraints, and Headless Peers
1. Session Defaults & Observer Types
ExecutionSession
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Contracts/Domain/ExecutionSession.cs:

ExecutionSession record containing:
SessionId, RootWorkUnitId, Status (Active, Paused, Completed, Abandoned)
ParentSessionId and ParentEventId for hierarchical sessions
StartedAt, PausedAt, CompletedAt timestamps
ModelConfigSnapshotJson and ProfileIdSet capturing the session's LLM configuration
WorkspaceOptions (Session/Default Configuration)
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Storage/WorkspaceOptions.cs:

Key session defaults:

EnabledDomainAgents: List<string> - disabled by default, empty list (lines 104-112)
Named agents like "Security", "Architecture", "Performance", "Test", "Documentation", "UX"
Per-work-unit override takes priority via IAgentControlService.GetEnabledDomainAgents(workUnitId)
SchedulerPollIntervalMs: 2,000 ms default
MaxConcurrentWorkers: 3 default
StallDetectionCycles: 4 (prevents orchestrator infinite loops)
UseLlmProfileSelection: false default
AllowAgentGitCommits, AllowAgentGitPush, AllowAutoRequeue: false defaults (headless CI control)
2. Reactive Observers (Domain Agents)
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.AgentRuntime/DomainAgentRegistry.cs:

Six domain observers are registered statically (not user-editable per WorkspaceOptions comment):

Agent	Keywords	Purpose
Security	auth, authn, authz, login, password, token, jwt, oauth, session, credential, secret, permission, role, acl, encrypt, csrf, xss, sql injection, cors, cookie	Watches for authentication/authorization/session handling gaps
Architecture	architecture, framework, library, module boundary, service boundary, coupling, dependency graph, design pattern, microservice, monolith, scalability, migration	Monitors architectural boundaries and coupling violations
Performance	performance, latency, throughput, n+1, cache, caching, benchmark, memory leak, allocation, slow query, timeout, concurrency, lock contention, rate limit	Detects N+1 queries, caching issues, synchronous hot-path calls
Test	test coverage, unit test, integration test, flaky, test gap, regression, mock, test fixture, e2e, untested	Ensures new code paths have test coverage
Documentation	documentation, docs, readme, api doc, changelog, undocumented, doc gap	Verifies public API changes are documented
UX	ux, usability, accessibility, a11y, user flow, ui, design system, user experience, onboarding, error message, confusing	Checks usability and accessibility gaps
Agent Structure (DomainAgentDefinition in /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.AgentRuntime/DomainAgentDefinition.cs):


public sealed record DomainAgentDefinition(
    string Name,                          // e.g., "Security"
    string TitlePrefix,                   // e.g., "[SecurityAgent] " (used to avoid re-triggering)
    IReadOnlyList<string> Keywords,       // Relevance keywords
    string SystemPrompt,                  // LLM instructions
    int MaxIterations = 8);
3. Constraint & Artifact Types
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Contracts/Domain/ArtifactRef.cs:

ArtifactType enum includes:


Goal, Plan, Task, Research, Decision, Constraint,
BranchChangeset, MergeProposal, MergeResult, ChangeIntent, ExternalChangeset
ArtifactStatus enum:


Active, Approved, Rejected, Superseded, Applied, Invalidated
ArtifactRef record:


public sealed record ArtifactRef(
    string ArtifactId,                    // KA-{GUID}
    ArtifactType Type,                    // Constraint, Research, Decision, etc.
    string? ParentArtifactId,            // Lineage parent (usually workUnitId)
    ArtifactStatus Status,
    DateTimeOffset CreatedAt,
    string? OwnedByWorkUnitId,           // Work unit that created it
    string? OwnedByAgentId,              // Agent that recorded it (for domain agents)
    string? Title = null,
    string? Body = null,
    string? InvalidatedByArtifactId = null);  // Cascade invalidation marker
4. Constraint Creation & Proposal Logic
How Domain Agents Create Constraints
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.AgentRuntime/DomainAgentTriggerService.cs:

Trigger Flow:

ArtifactCommandService.RecordAsync() fires after a Research/Decision/Constraint artifact is recorded
DomainAgentTriggerService.NotifyArtifactRecordedAsync() is called with the artifact
Filtering logic:
Only reacts to Research/Decision/Constraint types (line 22-23)
Skips artifacts whose title starts with a domain agent's TitlePrefix (avoid cross-agent loops, line 29)
Checks per-work-unit override: agentControl.GetEnabledDomainAgents(workUnitId) or falls back to global options.EnabledDomainAgents
Relevance check via DomainAgentTriggerHeuristic.IsRelevant(): keyword matching on title + body
Fire-and-forget spawn of DomainAgentLoop (no blocking; failures don't affect artifact recording)
Domain Agent Loop
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.AgentRuntime/DomainAgentLoop.cs:

Tools available to domain agents:

nm_v1_projection_get - Fetch AgentWorkspace projection with artifact chain + inherited constraints
nm_v1_artifact_query - Search for existing Constraints/Research/Decisions (avoid duplicates)
nm_v1_workspace_search - Grep for file contents to corroborate findings
nm_v1_artifact_record - Record a Constraint or Research artifact
Decision gate: Domain agent must decide if a gap exists:

If YES: call nm_v1_artifact_record with type="Constraint" (or "Research" for informational)
If NO (gap already covered or doesn't apply): do nothing, stop
Critical rules:

Title MUST start with exact literal prefix (e.g., [SecurityAgent] ) or will re-trigger itself
Be conservative: only emit Constraint when a concrete, specific gap is identified
Never call workspace write/build/test/merge tools
Max 8 iterations per domain agent run
Constraint Recording
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Storage/ArtifactCommandService.cs:


public async Task<ArtifactRef> RecordAsync(
    string workUnitId,
    string type,              // "Research", "Decision", or "Constraint"
    string title,
    string body,
    string? parentArtifactId = null,
    CancellationToken ct = default)
{
    var artifact = new ArtifactRef(
        $"KA-{Guid.NewGuid():N}",
        artifactType,
        parentArtifactId ?? workUnitId,  // Parent = workUnitId if not specified
        Status.Active,
        DateTimeOffset.UtcNow,
        workUnitId,
        null,                             // OwnedByAgentId = null for human artifacts
        title,
        body);
    
    // Trigger domain agents asynchronously
    if (domainAgentTrigger is not null)
        await domainAgentTrigger.NotifyArtifactRecordedAsync(recorded, ct);
    
    return recorded;
}
5. How Constraints Flow Through the System
Constraint Inheritance Model
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Projections/ProjectionManager.cs (lines 347-362):


// Knowledge artifacts are inherited down the WorkUnit DAG:
// Walk ParentWorkUnitId root-first and fold in every ancestor's own chain
var globalConstraints = await _artifactLineage.GetGlobalConstraintsAsync(ct);
var inheritedConstraints = globalConstraints
    .Concat(ancestorChain.Where(a => a.Type == ArtifactType.Constraint))
    .ToList();
Constraint inheritance sources (in order):

Global constraints - Promoted Knowledge Findings with no owning work unit (apply to all work units)
Ancestor chain constraints - From all parent work units back to the root
Self constraints - The work unit's own recorded constraints
AgentWorkspaceProjection Structure
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Contracts/Projections/ProjectionContracts.cs:


public sealed record AgentWorkspaceProjectionPayload(
    string? AgentId,
    string? WorkUnitId,
    ArtifactChain Artifacts,              // Own chain (Goal, Plan, Task, Decision, etc.)
    IReadOnlyList<ArtifactRef> InheritedConstraints,  // Global + ancestor constraints
    WorkspaceExecutionSummary? Execution = null,
    IReadOnlyList<ProjectRootSummary>? Roots = null,
    IReadOnlyList<FileOpHistory>? RecentFileOps = null,
    IReadOnlyList<CoModHint>? CoModHints = null);
How Agents Use Constraints
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.AgentRuntime/OrchestratorAgentLoop.cs (line 73-77):


// Inherited constraints (global, promoted via Knowledge Promotion, plus this work
// unit's own ancestor chain) rarely change mid-run — fold them into the kickoff message
// once rather than repeating them every cycle alongside the delta.
if (i == 0 && currentProjection.InheritedConstraints.Count > 0)
    AppendConstraintsToOutgoingMessage(messages, currentProjection.InheritedConstraints);
Orchestrator sends inherited constraints in the kickoff message (once per run, at cycle 0).

Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.AgentRuntime/ReviewerAgentLoop.cs (line 154):

Reviewer has nm_v1_artifact_query to search Constraints/Research/Decisions before approval:


"Search knowledge artifacts (Research, Decision, Constraint) for this work unit 
and its ancestors. Check before approving — a change that violates a recorded 
Constraint is grounds for rejection."
6. Goal Management
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Contracts/Domain/GoalNode.cs:


public sealed record GoalNode(
    string GoalId,
    string Goal,                    // Goal description
    string WorkUnitId,
    string BranchId,
    GoalStatus Status,             // Exploring, Converging, Converged, Blocked, Abandoned
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Owner,
    string? ParentGoalId = null,
    IReadOnlyList<string>? ChildGoalIds = null,
    string? SessionId = null);     // Scoped to execution session
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Storage/GoalNodeService.cs:

GoalNodeService persists goals to node store under StudioNodeKind.GoalV1
Goals are separately indexed from the artifact lineage (WorkUnit owns them, but independent DAG)
Goals track hierarchical exploration state across a session
7. Headless Peers
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Host/HeadlessPeerOptions.cs:

HeadlessPeerOptions configuration:


public bool Enabled { get; set; }
public string? HostUri { get; set; }      // WebSocket base URI (ws://localhost:5080)
                                          // null = standalone (no room presence)
public string RoomId { get; set; } = "studio";
public string PeerType { get; set; } = "ephemeral-agent";  // or "persistent-agent"
public string? PeerId { get; set; }       // Stable identity; auto-generated & persisted if null
What Headless Peers Do
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Host/RoomPeerClient.cs:

Standalone mode (HostUri = null): Agent loops run locally with NO room presence
Connected mode (HostUri = "ws://...")):
Maintains outbound WebSocket connection to a nodalmerge host room
Reconnects with exponential backoff (1s initial, max 30s)
Registers as named peer (peerId, peerType)
Runs all Studio services (agents, projections, storage, orchestrator) without HTTP server
Headless Host Build
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Host/StudioWebApplication.cs:


public static IHost BuildPeer(
    string[] args,
    HttpClient? llmHttpClient = null,
    Action<IServiceCollection>? configureServices = null,
    Action<IConfigurationBuilder>? configureConfiguration = null)
{
    // Builds IHost with:
    // - NodalMerge runtime core (agents, storage)
    // - All Studio services
    // - RoomPeerClient for optional outbound room presence
    // - NO HTTP server, NO MCP-over-HTTP, NO WebSocket server
    
    services.AddHostedService<RoomPeerClient>();
}
Typical headless configurations (via WorkspaceOptions):

AllowAgentGitCommits = true - Let agents commit to git (headless CI)
AllowAgentGitPush = true - Let agents push branches
AllowAutoRequeue = true - Auto-retry failed work units
8. Agent Control & Orchestrator Registration
Located at /c/Users/bgstr/source/repos/nodalmerge-studio/src/NodalMerge.Studio.Core/Services/ServiceContracts.cs (lines 251-283):

IAgentControlService interface:


Task<string> SpawnAsync(
    string agentType,                              // "orchestrator", "worker", "planner", etc.
    string workUnitId,
    // ... credentials ...
    IReadOnlyList<string>? enabledDomainAgents = null,  // Per-work-unit override
    CancellationToken cancellationToken = default);

OrchestratorCredentials? GetOrchestratorCredentials(string workUnitId);
IReadOnlyList<string>? GetEnabledDomainAgents(string workUnitId);  // Per-WU override or null
Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, ...);
OrchestratorRegistration (internal to InMemoryAgentRuntimeService):


private sealed record OrchestratorRegistration(
    string Provider, string Model, string BaseUrl, string ApiKey,
    string? ProfileId,
    string? AutoReviewProfileId,
    IReadOnlyDictionary<PipelineStage, OrchestratorCredentials>? StageCredentials = null,
    IReadOnlyList<string>? EnabledDomainAgents = null);  // Captured at spawn time
9. Key Files and Locations
Component	File Path
HeadlessPeerOptions	src/NodalMerge.Studio.Host/HeadlessPeerOptions.cs
WorkspaceOptions (defaults)	src/NodalMerge.Studio.Storage/WorkspaceOptions.cs
DomainAgentRegistry (6 agents)	src/NodalMerge.Studio.AgentRuntime/DomainAgentRegistry.cs
DomainAgentDefinition	src/NodalMerge.Studio.AgentRuntime/DomainAgentDefinition.cs
DomainAgentTriggerService	src/NodalMerge.Studio.AgentRuntime/DomainAgentTriggerService.cs
DomainAgentTriggerHeuristic	src/NodalMerge.Studio.AgentRuntime/DomainAgentTriggerHeuristic.cs
DomainAgentLoop	src/NodalMerge.Studio.AgentRuntime/DomainAgentLoop.cs
ArtifactRef & ArtifactType	src/NodalMerge.Studio.Contracts/Domain/ArtifactRef.cs
ArtifactCommandService	src/NodalMerge.Studio.Storage/ArtifactCommandService.cs
ProjectionManager (constraint inheritance)	src/NodalMerge.Studio.Projections/ProjectionManager.cs
AgentWorkspaceProjection	src/NodalMerge.Studio.Contracts/Projections/ProjectionContracts.cs
OrchestratorAgentLoop (uses constraints)	src/NodalMerge.Studio.AgentRuntime/OrchestratorAgentLoop.cs
ReviewerAgentLoop (checks constraints)	src/NodalMerge.Studio.AgentRuntime/ReviewerAgentLoop.cs
GoalNode & GoalNodeService	src/NodalMerge.Studio.Contracts/Domain/GoalNode.cs, src/NodalMerge.Studio.Storage/GoalNodeService.cs
ExecutionSession	src/NodalMerge.Studio.Contracts/Domain/ExecutionSession.cs
RoomPeerClient	src/NodalMerge.Studio.Host/RoomPeerClient.cs
IAgentControlService	src/NodalMerge.Studio.Core/Services/ServiceContracts.cs
Summary
The nodalmerge-studio system implements a multi-observer reactive architecture where:

Session defaults are configured in WorkspaceOptions, with domain agents disabled by default
Six domain observers (Security, Architecture, Performance, Test, Documentation, UX) react asynchronously to Research/Decision/Constraint artifacts using keyword matching
Constraints are knowledge artifacts that flow through work-unit lineage (global → ancestors → self) and are injected into agent kickoff messages
Domain agent workflow: trigger → heuristic filter → LLM evaluation → conditional Constraint record → fire-and-forget spawn
Constraint usage: Orchestrator receives them once at cycle 0; Reviewer checks them before approval
Goals track exploration state hierarchically within execution sessions
Headless peers run agents locally without HTTP, optionally connecting to a room via WebSocket
Here's the full picture based on what's actually in the code:

What the Default Observers Are
Six domain agents are statically registered in DomainAgentRegistry.cs: Security, Architecture, Performance, Test, Documentation, and UX. They are disabled by default in WorkspaceOptions — you have to opt them in per-workspace or per-work-unit.

What Triggers Them
They fire reactively in DomainAgentTriggerService.cs whenever any Research, Decision, or Constraint artifact is recorded. The trigger pipeline is:

Skip if the artifact title starts with a domain agent's own prefix (loop prevention)
Check which agents are enabled for that work unit
Run a keyword heuristic against the artifact title + body
If a keyword matches → spawn a DomainAgentLoop fire-and-forget
What They Can Actually Do
Each agent gets exactly four tools (DomainAgentLoop.cs):

Tool	Purpose
nm_v1_projection_get	Read the work unit's full artifact chain + inherited constraints
nm_v1_artifact_query	Search existing constraints to avoid duplicates
nm_v1_workspace_search	Grep workspace files to corroborate a concern
nm_v1_artifact_record	Record a Constraint or Research artifact
That's it — no write tools, no goal creation, no task spawning. They can only observe and emit artifacts.

Goals vs. Constraints
Domain agents do NOT create Goals. Goals (GoalNode.cs) live in a separate DAG, track session-level exploration state (Exploring → Converging → Converged → Blocked), and are managed by the orchestrator/human, not observers.
Domain agents produce Constraints (concrete specific gaps they've identified) or Research (informational findings). The instructions tell them to be conservative — only emit a Constraint when a real gap exists, do nothing otherwise.
How Constraints Flow and Are Used
Constraints are not "random" — they participate in a structured inheritance model (ProjectionManager.cs):


Global constraints (promoted, no owner)
  └── Ancestor work unit constraints (root → ... → parent)
        └── Self constraints (this work unit's own)
All three layers merge into InheritedConstraints on the AgentWorkspaceProjection. Then:

Orchestrator (OrchestratorAgentLoop.cs) injects them once into the kickoff message at cycle 0 — so they shape everything the orchestrator does for that run.
Reviewer (ReviewerAgentLoop.cs) queries constraints before approving — a change that violates a recorded constraint is grounds for rejection.
Headless Peers
Headless peers (HeadlessPeerOptions.cs, RoomPeerClient.cs) are full Studio runtimes with no HTTP server — they run all the same agent loops, storage, and projections, but:

In standalone mode (HostUri = null): run entirely locally, no room presence
In connected mode: maintain a WebSocket outbound connection to a nodalmerge host room and participate as a named peer (peerType: "ephemeral-agent" or "persistent-agent")
They'd run the same domain observer logic as a human-driven session, just without a UI peer. Their typical use case is CI-style headless execution with AllowAgentGitCommits/Push/AutoRequeue enabled.

The Bottom Line
The observers are narrow, reactive, read-and-emit agents — not autonomous planners. They can't spawn goals, tasks, or sub-sessions. Their sole output is a Constraint or Research artifact that gets absorbed into the inheritance chain and used as guardrails by orchestrators and reviewers on any downstream work in that lineage.