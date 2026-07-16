using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage.TreeObjects;

namespace NodalMerge.Studio.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNodalMergeStorage(this IServiceCollection services)
    {
        // Slice 6.1b — default no-op outbound replication; NodalMerge.Studio.Host's
        // StudioWebApplication registers a real implementation (RoomPeerClient) after calling
        // AddStudioServices, which wins (last AddSingleton registration wins — see e.g. the
        // WorkspaceOptions comment in AddInMemoryStorage below for the same pattern/rationale).
        services.AddSingleton<IStudioReplicationOutbound>(NoopStudioReplicationOutbound.Instance);
        // Slice 6.4 — default-valued RoomOptions (no HostUri, Workgroup="workgroup") so
        // WorkgroupRepositoryDirectory/WorkgroupGoalDirectory/RoomReplicationDispatcher/
        // RoomPeerClient always have a room id to bind to, even before NodalMerge.Studio.Host's
        // config-bound override runs (last-AddSingleton-wins, same pattern as
        // RetentionPolicyOptions/BlobGcOptions in AddRehydratableServices below).
        services.AddSingleton(new RoomOptions());
        services.AddSingleton<IStudioNodeStore, NodalMergeStudioNodeStore>();
        // Slice 6.2 — workgroup room repositories map (docs/STUDIO_ROOM_SCHEMA.md (b)), engine-backed
        // like IStudioNodeStore above but a separate room ("workgroup") and namespace
        // ("repositories") — see WorkgroupRepositoryDirectory's own class comment for what
        // upstream replication does/doesn't do yet.
        services.AddSingleton<IWorkgroupRepositoryDirectory, WorkgroupRepositoryDirectory>();
        // Slice 6.3 — D3's workgroup-level cross-repo goal node (minimal, deliberately not wired
        // into goal creation — see WorkgroupGoalDirectory's own class comment).
        services.AddSingleton<IWorkgroupGoalDirectory, WorkgroupGoalDirectory>();
        // Slice 6.1b/6.3 — inbound replication seam (see IStudioNodeStoreReplicationSink's own doc
        // comment). Slice 6.3: routed through RoomReplicationDispatcher rather than
        // NodalMergeStudioNodeStore directly, since RoomPeerClient now applies inbound packs for
        // "workgroup" and repo rooms too, and those are owned by WorkgroupRepositoryDirectory and
        // NodalMergeStudioNodeStore respectively — only registered here (not AddInMemoryStorage):
        // the in-memory doubles have no room_maps/sync-graph split to bridge, so callers must
        // resolve this as optional.
        services.AddSingleton<IStudioNodeStoreReplicationSink>(sp => new RoomReplicationDispatcher(
            (NodalMergeStudioNodeStore)sp.GetRequiredService<IStudioNodeStore>(),
            sp.GetRequiredService<IWorkgroupRepositoryDirectory>(),
            sp.GetRequiredService<RoomOptions>()));
        services.AddSingleton<IRepositoryIdentityHintsService, GitRepositoryIdentityHintsService>();
        services.AddSingleton<IBranchService, NodalMergeBranchService>();
        services.AddSingleton<IReplayService, ReplayService>();
        services.AddSingleton<IStateReconstructionService, StateReconstructionService>();
        services.AddSingleton<IAgentWorkspaceService, AgentWorkspaceService>();
        AddRehydratableServices(services);
        AddFileWorkspaceService(services);
        AddPolicyGate(services);
        return services;
    }

    public static IServiceCollection AddInMemoryStorage(this IServiceCollection services)
    {
        // Slice 6.1b — see AddNodalMergeStorage's identical registration above; InMemoryStudioNodeStore
        // never calls this (no engine bridge to promote/notify through), but every service that
        // constructs NodalMergeStudioNodeStore-shaped test doubles directly still needs it resolvable.
        services.AddSingleton<IStudioReplicationOutbound>(NoopStudioReplicationOutbound.Instance);
        // Slice 6.4 — default-valued RoomOptions, same reasoning as AddNodalMergeStorage's
        // identical registration above; the in-memory doubles below don't consume it themselves,
        // but it's resolvable for any caller/test that does (e.g. constructing a real RoomPeerClient
        // against an otherwise in-memory-storage host).
        services.AddSingleton(new RoomOptions());
        services.AddSingleton<IStudioNodeStore, InMemoryStudioNodeStore>();
        // Slice 6.2 — in-memory workgroup directory (no engine bridge needed here, same reasoning
        // as InMemoryStudioNodeStore above); GitRepositoryIdentityHintsService itself has no engine
        // dependency at all (LibGit2Sharp only), so it's the same registration in both DI paths.
        services.AddSingleton<IWorkgroupRepositoryDirectory>(new InMemoryWorkgroupRepositoryDirectory());
        // Slice 6.3 — in-memory workgroup goal directory, same reasoning as the line above.
        services.AddSingleton<IWorkgroupGoalDirectory>(new InMemoryWorkgroupGoalDirectory());
        services.AddSingleton<IRepositoryIdentityHintsService, GitRepositoryIdentityHintsService>();
        services.AddSingleton<IBranchService, InMemoryBranchService>();
        services.AddSingleton<IReplayService, ReplayService>();
        services.AddSingleton<IStateReconstructionService, StateReconstructionService>();
        services.AddSingleton<IAgentWorkspaceService, AgentWorkspaceService>();
        AddRehydratableServices(services);

        // Without this, WorkspaceOptions falls back to its default RootPath
        // (%TEMP%/studio-workspace), a single fixed directory every test run on the machine
        // shares and never cleans up. Children forked from "main" would inherit whatever files
        // earlier, unrelated test runs left behind, which the merge reconciler's overlapping-file
        // check then sees as siblings stepping on each other.
        //
        // Must be a real AddSingleton, not TryAdd: tests going through the full
        // StudioWebApplication.Build pipeline call AddStudioServices first, which already
        // registers a (non-Try) production-config-bound WorkspaceOptions — TryAdd would silently
        // no-op against that earlier registration and every such test would share the production
        // default path after all (confirmed: this caused real cross-test flakiness — FanOut/
        // DeadLetter/FullAgentCycle-style tests intermittently failing under parallel runs because
        // unrelated tests' branches collided in the same directory). A second AddSingleton call
        // resolves last-wins, fixing isolation for every caller that doesn't explicitly override.
        // A test that *does* want its own WorkspaceOptions (e.g. a fixed RootPath, or to flip
        // EnforceExpectedOutputKind) must register it via AddSingleton *after* calling
        // AddInMemoryStorage() — last registration wins, regardless of Try.
        services.AddSingleton(new WorkspaceOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "studio-workspace-tests", Guid.NewGuid().ToString("N"))
        });

        AddFileWorkspaceService(services);
        AddPolicyGate(services);
        return services;
    }

    // Slice 0a — each of these services owns a node-store-backed dictionary and implements
    // IRehydratable; registering the concrete type once and forwarding both its domain
    // interface and IRehydratable to that same singleton instance is what lets
    // StudioStateRehydrationService (registered here too, so it starts ahead of
    // AddStudioAgentRuntime's scheduler poll loop) repopulate every one of them on startup.
    private static void AddRehydratableServices(IServiceCollection services)
    {
        services.AddSingleton<InMemoryKnownGoodStateService>();
        services.AddSingleton<IKnownGoodStateService>(sp => sp.GetRequiredService<InMemoryKnownGoodStateService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryKnownGoodStateService>());

        services.AddSingleton<AgentProfileService>();
        services.AddSingleton<IAgentProfileService>(sp => sp.GetRequiredService<AgentProfileService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<AgentProfileService>());

        services.AddSingleton<ArtifactLineageService>();
        services.AddSingleton<IArtifactLineageService>(sp => sp.GetRequiredService<ArtifactLineageService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<ArtifactLineageService>());

        services.AddSingleton<ExecutionSessionService>();
        services.AddSingleton<IExecutionSessionService>(sp => sp.GetRequiredService<ExecutionSessionService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<ExecutionSessionService>());

        services.AddSingleton<OrchestrationDecisionLogService>();
        services.AddSingleton<IOrchestrationDecisionLogService>(sp => sp.GetRequiredService<OrchestrationDecisionLogService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<OrchestrationDecisionLogService>());

        services.AddSingleton<IntentGraphService>();
        services.AddSingleton<IIntentGraphService>(sp => sp.GetRequiredService<IntentGraphService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<IntentGraphService>());

        services.AddSingleton<FileLeaseService>();
        services.AddSingleton<IFileLeaseService>(sp => sp.GetRequiredService<FileLeaseService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<FileLeaseService>());

        services.AddSingleton<InMemoryDeadLetterService>();
        services.AddSingleton<IDeadLetterService>(sp => sp.GetRequiredService<InMemoryDeadLetterService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryDeadLetterService>());

        services.AddSingleton<IRuntimeCredentialCache, RuntimeCredentialCache>();

        services.AddSingleton<WorkSchedulerService>();
        services.AddSingleton<IWorkScheduler>(sp => sp.GetRequiredService<WorkSchedulerService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<WorkSchedulerService>());

        // plans/phase-d-implementation.md D3 — plan-staleness signals, consumed by
        // ArtifactCommandService (superseding decisions) and InMemoryDeadLetterService
        // (dead-lettered slices) below. Registered ahead of both so DI resolves it into their
        // optional constructor parameters.
        services.AddSingleton<IPlanStalenessService, PlanStalenessService>();

        // Slice 15f — shared command services that every transport (MCP/REST/dispatcher) calls.
        services.AddSingleton<ISchedulerCommandService, SchedulerCommandService>();
        services.AddSingleton<IArtifactCommandService, ArtifactCommandService>();
        services.AddSingleton<IClarificationCommandService, ClarificationCommandService>();
        services.AddSingleton<IExternalDocFetcher, ExternalDocFetcher>();
        services.AddSingleton<IDocFetchCommandService, DocFetchCommandService>();

        // Slice 16b/16c — workspace execution services
        services.AddSingleton<IWorkspaceExecutionService, WorkspaceExecutionService>();
        services.AddSingleton<IWorkspaceExecutionCommandService, WorkspaceExecutionCommandService>();

        // Phase 9a — sub-project root detection. No persistence/rehydration: a WorkspaceProfile is
        // cheap to recompute and branch directories are recreated identically on InitBranchAsync,
        // so a cold Host just re-detects lazily on first access.
        services.AddSingleton<IWorkspaceProfileService, WorkspaceProfileService>();

        // plans/harness-hosting-architecture.md Phase A.4 — assembled fresh from the
        // EngineeringState projection + work-unit state on every call; no persistence/rehydration,
        // same reasoning as IWorkspaceProfileService above.
        services.AddSingleton<IWorkspaceContractService, WorkspaceContractService>();

        // Phase 15a — Roslyn-backed semantic navigation for definition/reference/implementation
        // lookup in branch workspaces. Read-only and recomputed per call; no persistence.
        services.AddSingleton<IWorkspaceSemanticNavigationService, WorkspaceSemanticNavigationService>();

        // Phase 9c — tracks long-running "run" processes (dev servers). Deliberately not durable:
        // a Host restart kills anything it started, and there's nothing meaningful to resume a
        // dev server into.
        services.AddSingleton<RunningProcessRegistry>();

        // Phase 1 — goal-level pause/resume and clarification timeout
        services.AddSingleton<IGoalControlService, GoalControlService>();
        services.AddSingleton<IClarificationTimerService, ClarificationTimerService>();

        // Phase 6.7b — decision-centric persistent node services
        services.AddSingleton<GoalNodeService>();
        services.AddSingleton<IGoalNodeService>(sp => sp.GetRequiredService<GoalNodeService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<GoalNodeService>());

        services.AddSingleton<DecisionNodeService>();
        services.AddSingleton<IDecisionNodeService>(sp => sp.GetRequiredService<DecisionNodeService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<DecisionNodeService>());

        services.AddSingleton<ConversationLogService>();
        services.AddSingleton<IConversationLogService>(sp => sp.GetRequiredService<ConversationLogService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<ConversationLogService>());

        services.AddSingleton<SteeringDecisionService>();
        services.AddSingleton<ISteeringDecisionService>(sp => sp.GetRequiredService<SteeringDecisionService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<SteeringDecisionService>());

        // Phase 16 — registered ahead of RepositoryRegistryService, which depends on it to attach
        // every newly registered repository to the (currently singleton) workspace.
        services.AddSingleton<WorkspaceRegistryService>();
        services.AddSingleton<IWorkspaceRegistryService>(sp => sp.GetRequiredService<WorkspaceRegistryService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<WorkspaceRegistryService>());

        services.AddSingleton<RepositoryRegistryService>();
        services.AddSingleton<IRepositoryRegistryService>(sp => sp.GetRequiredService<RepositoryRegistryService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<RepositoryRegistryService>());

        services.AddSingleton<ProjectionSnapshotService>();
        services.AddSingleton<IProjectionSnapshotService>(sp => sp.GetRequiredService<ProjectionSnapshotService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<ProjectionSnapshotService>());

        services.AddSingleton<EvidenceNodeService>();
        services.AddSingleton<IEvidenceNodeService>(sp => sp.GetRequiredService<EvidenceNodeService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<EvidenceNodeService>());

        services.AddSingleton<HypothesisNodeService>();
        services.AddSingleton<IHypothesisNodeService>(sp => sp.GetRequiredService<HypothesisNodeService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<HypothesisNodeService>());

        services.AddSingleton<FindingService>();
        services.AddSingleton<IFindingService>(sp => sp.GetRequiredService<FindingService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<FindingService>());

        services.AddSingleton<ExecutionEventStreamService>();
        services.AddSingleton<IExecutionEventStream>(sp => sp.GetRequiredService<ExecutionEventStreamService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<ExecutionEventStreamService>());

        // Phase 14 — derived from the event log above, no persistence of its own.
        services.AddSingleton<IWorkspaceUsageMetricsService, WorkspaceUsageMetricsService>();

        // No domain interface to forward — RuntimeSettingsService is only ever consumed
        // directly (by /studio/options) for its PersistAsync side effect, not through an
        // abstraction.
        services.AddSingleton<RuntimeSettingsService>();
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<RuntimeSettingsService>());

        // Slice 21a — must run after RuntimeSettingsService so UsePromotionBranch is already
        // restored before we try to create the candidate branch.
        services.AddSingleton<CandidateBranchService>();
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<CandidateBranchService>());

        // Repository virtualization — Phase 2/6: snapshot service registered before import service.
        // Phase 6: snapshot service receives IRepositoryOpService for ConsiderCompactionAsync.
        // Slice 1.1: snapshot service receives ISnapshotTreeResolver so CreateAsync can write the
        // tree map into the CAS when a blob store is configured (registered in AddFileWorkspaceService
        // below — registration order doesn't matter, DI resolves lazily).
        services.AddSingleton<InMemoryRepositorySnapshotService>(sp =>
            new InMemoryRepositorySnapshotService(
                sp.GetRequiredService<IStudioNodeStore>(),
                sp,
                sp.GetService<ISnapshotTreeResolver>()));
        services.AddSingleton<IRepositorySnapshotService>(sp => sp.GetRequiredService<InMemoryRepositorySnapshotService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryRepositorySnapshotService>());

        // Phase 5/6: import service receives WorkspaceOptions for threshold-based compaction trigger.
        services.AddSingleton<RepositoryImportService>(sp => new RepositoryImportService(
            sp.GetService<IRepositorySnapshotService>(),
            sp.GetService<IBlobStoreProvider>(),
            sp.GetService<IRepositoryOpService>(),
            sp.GetService<WorkspaceOptions>(),
            sp.GetService<ISnapshotTreeResolver>(),
            sp.GetService<ILogger<RepositoryImportService>>()));
        services.AddSingleton<IRepositoryImportService>(sp => sp.GetRequiredService<RepositoryImportService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<RepositoryImportService>());

        // Phase 8 — workspace cache manager: eviction, startup orphan sweep, live blob hash enumeration.
        // IHostedService StartAsync fires EvictOrphanedAsync as a best-effort background task so that
        // stale branch dirs from prior runs are cleaned up without blocking the startup chain.
        // Phase 5 slice 5.1 — snapshot retention classification. No state of its own (unlike
        // WorkspaceCacheManager it doesn't implement IRehydratable/IHostedService — every read
        // goes straight through IStudioNodeStore at ClassifyAsync call time). Registered ahead of
        // WorkspaceCacheManager below because slice 5.2's LiveHashSource v2 now consumes it
        // directly (registration order doesn't affect factory-based DI resolution, but this keeps
        // the read order in this file matching the dependency order).
        services.AddSingleton(new RetentionPolicyOptions());
        services.AddSingleton<ISnapshotRetentionPolicy>(sp => new SnapshotRetentionPolicy(
            sp.GetRequiredService<IStudioNodeStore>(),
            sp.GetRequiredService<IRepositoryRegistryService>(),
            sp.GetService<WorkspaceOptions>(),
            sp.GetService<RetentionPolicyOptions>()));

        services.AddSingleton<WorkspaceCacheManager>(sp => new WorkspaceCacheManager(
            sp.GetRequiredService<IFileWorkspaceService>(),
            sp,
            sp.GetRequiredService<IRepositorySnapshotService>(),
            sp.GetRequiredService<IMaterializationEngine>(),
            sp.GetRequiredService<IStudioNodeStore>(),
            sp.GetRequiredService<IRepositoryRegistryService>(),
            sp.GetRequiredService<ISnapshotTreeResolver>(),
            sp.GetRequiredService<ISnapshotRetentionPolicy>(),
            sp.GetService<WorkspaceOptions>()));
        services.AddSingleton<IWorkspaceCacheManager>(sp => sp.GetRequiredService<WorkspaceCacheManager>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WorkspaceCacheManager>());

        // Phase 5 slice 5.2 — staged local blob GC (DryRun/MarkOnly/SweepSoft/SweepHard) + run
        // ledger. BlobGcOptions default registration mirrors RetentionPolicyOptions above;
        // NodalMerge.Studio.Host.StudioServiceCollectionExtensions re-registers a config-bound
        // instance after AddNodalMergeStorage/AddInMemoryStorage runs (last AddSingleton wins).
        services.AddSingleton(new BlobGcOptions());
        services.AddSingleton<IBlobGcService>(sp => new BlobGcService(
            sp.GetRequiredService<IWorkspaceCacheManager>(),
            sp.GetRequiredService<ISnapshotRetentionPolicy>(),
            sp.GetRequiredService<IStudioNodeStore>(),
            sp.GetService<WorkspaceOptions>() ?? new WorkspaceOptions(),
            sp.GetService<BlobGcOptions>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<BlobGcService>>()));

        services.AddSingleton<RepositorySyncService>();
        services.AddSingleton<IRepositorySyncService>(sp => sp.GetRequiredService<RepositorySyncService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<RepositorySyncService>());

        // Phase 9 — conflict service registered before op service (op service injects it).
        services.AddSingleton<InMemoryConflictService>();
        services.AddSingleton<IConflictService>(sp => sp.GetRequiredService<InMemoryConflictService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryConflictService>());

        // Candidate-branch cross-goal conflicts (distinct from the CAS-level IConflictService above
        // — see CandidateConflictRecord's doc comment). Detected in InMemoryMergeService, recorded
        // here for the promote UI to query.
        services.AddSingleton<InMemoryCandidateConflictService>();
        services.AddSingleton<ICandidateConflictService>(sp => sp.GetRequiredService<InMemoryCandidateConflictService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryCandidateConflictService>());

        // Fan-out-sibling task-level conflicts (distinct from the above — see TaskConflictRecord's
        // doc comment). Same detection block in InMemoryMergeService, recorded here.
        services.AddSingleton<InMemoryTaskConflictService>();
        services.AddSingleton<ITaskConflictService>(sp => sp.GetRequiredService<InMemoryTaskConflictService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryTaskConflictService>());

        // Phase 11.5 — co-modification pattern service. On-demand compute; rehydrates last
        // computed patterns from node store on startup so projection hints are available immediately.
        services.AddSingleton<InMemoryCoModService>();
        services.AddSingleton<ICoModService>(sp => sp.GetRequiredService<InMemoryCoModService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryCoModService>());

        // Repository virtualization — Phase 3. Phase 9: receives conflict + snapshot services
        // so EmitAsync can detect forks at write time.
        services.AddSingleton<InMemoryRepositoryOpService>(sp => new InMemoryRepositoryOpService(
            sp.GetRequiredService<IStudioNodeStore>(),
            sp.GetService<IConflictService>(),
            sp.GetService<IRepositorySnapshotService>(),
            sp.GetService<WorkspaceOptions>()));
        services.AddSingleton<IRepositoryOpService>(sp => sp.GetRequiredService<InMemoryRepositoryOpService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryRepositoryOpService>());

        // Registered last and ahead of AddStudioAgentRuntime in AddStudioServices, so its
        // StartAsync (which awaits every IRehydratable above) completes before the scheduler
        // poll loop's StartAsync begins.
        services.AddSingleton<StudioStateRehydrationService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<StudioStateRehydrationService>());

        // Slice 6.5 Part 1 — resolves IEnumerable<IRehydratable> (every registration above),
        // exactly like StudioStateRehydrationService, but on-demand (called by RoomPeerClient after
        // an inbound pack) rather than once at startup. Registered in both AddNodalMergeStorage and
        // AddInMemoryStorage (this method runs from both) so it's resolvable in every test/host
        // configuration even though only a connected host (RoomPeerClient) ever actually calls it.
        services.AddSingleton<IStudioCacheRefreshCoordinator>(sp => new RehydratableRefreshCoordinator(
            sp.GetServices<IRehydratable>(),
            sp.GetRequiredService<RoomOptions>(),
            sp.GetRequiredService<ILogger<RehydratableRefreshCoordinator>>()));
    }

    // Slice 14a — no state to rehydrate (rules are resolved fresh from DI each time), so this
    // doesn't go through AddRehydratableServices. Ships with zero IPolicyRule registrations;
    // EvaluateAsync is a no-op (Allowed = true) against every checkpoint until a slice like 14b
    // registers one.
    private static void AddPolicyGate(IServiceCollection services)
    {
        services.AddSingleton<IPolicyGateService, PolicyGateService>();

        // Slice 14b's NonOverlappingFileScopeRule (opt-in reject) lived here; replaced by
        // FanOutService.AutoSequenceOverlappingSiblingsAsync, which is always-on and inserts a
        // dependsOn edge instead of blocking — see that method's own comment for why.

        // Slice 16f — opt-in execution rule; gated by RequireBuildBeforeProposal/RequireTestBeforeProposal.
        services.AddSingleton<IPolicyRule, WorkspaceExecutionRule>();
    }

    private static void AddFileWorkspaceService(IServiceCollection services)
    {
        // Slice 1.1 — resolves a snapshot's tree map (inline legacy TreeEntries or a cas-tree CAS
        // blob) uniformly for every reader below. Always registered (even with no blob store —
        // CanWrite is false, ResolveTreeAsync falls back to inline-only) so every consumer can take
        // it as a required dependency instead of null-checking.
        services.AddSingleton<ISnapshotTreeResolver>(sp => new SnapshotTreeResolver(
            sp.GetService<IBlobStoreProvider>(),
            sp.GetService<ILogger<SnapshotTreeResolver>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SnapshotTreeResolver>.Instance));

        // Slice 6.3 (D3, docs/STUDIO_ROOM_SCHEMA.md (c)) — pinned cross-repo reference resolution:
        // repo room -> generation node -> TreeHash -> CAS walk -> blob bytes. Registered for both
        // DI paths (this method is shared); blob store stays optional (resolver returns null with a
        // logged warning when absent, same optional-collaborator shape as everything above).
        services.AddSingleton<IPinnedReferenceResolver>(sp => new PinnedReferenceResolver(
            sp.GetRequiredService<IStudioNodeStore>(),
            sp.GetRequiredService<IWorkgroupRepositoryDirectory>(),
            sp.GetRequiredService<ISnapshotTreeResolver>(),
            sp.GetService<IBlobStoreProvider>(),
            sp.GetService<ILogger<PinnedReferenceResolver>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PinnedReferenceResolver>.Instance));

        // Phase 7 — materializer registered here so it's available to both AddNodalMergeStorage
        // and AddInMemoryStorage; it has no state to rehydrate so it doesn't go through
        // AddRehydratableServices.
        services.AddSingleton<IMaterializationEngine>(sp =>
        {
            var blobStore = sp.GetService<IBlobStoreProvider>();
            var opts      = sp.GetService<WorkspaceOptions>() ?? new WorkspaceOptions();
            return blobStore is not null
                ? new MaterializationEngine(
                    blobStore, opts, sp.GetRequiredService<ISnapshotTreeResolver>(),
                    sp.GetService<ILogger<MaterializationEngine>>())
                : NullMaterializationEngine.Instance;
        });

        // Phase 2 slice 2.4 — prefetch warms the local blob cache for a work unit's declared
        // FileScope ahead of need; registered alongside the materializer/resolver above since it
        // shares their optional-blob-store shape (no store configured = a no-op service, not a
        // missing registration every caller has to null-check for).
        services.AddSingleton<IBlobPrefetchService>(sp => new WorkUnitPrefetchService(
            sp.GetRequiredService<IRepositorySnapshotService>(),
            sp.GetRequiredService<ISnapshotTreeResolver>(),
            sp.GetService<IBlobStoreProvider>()));

        // Phase 2 slice 2.3 — CAS reconcile sweep: heals gaps in the remote origin. remote is
        // resolved via GetService (may be null — Studio.Core's ICasReconcileService documents a
        // zero-result no-op for that case, same optional-collaborator shape as IBlobPrefetchService
        // above), so registering this always is safe even when no remote origin is configured.
        services.AddSingleton<ICasReconcileService>(sp => new CasReconcileService(
            sp.GetRequiredService<IWorkspaceCacheManager>(),
            sp.GetRequiredService<IBlobStoreProvider>(),
            sp.GetRequiredService<ILogger<CasReconcileService>>(),
            sp.GetService<IRemoteBlobPushTarget>()));

        services.AddSingleton<IFileWorkspaceService>(sp => new FileSystemWorkspaceService(
            sp.GetService<WorkspaceOptions>() ?? new WorkspaceOptions(),
            sp.GetService<IBlobStoreProvider>(),
            sp.GetService<IRepositoryOpService>(),
            sp.GetService<IMaterializationEngine>(),
            sp.GetService<IRepositorySnapshotService>(),
            sp.GetService<ILogger<FileSystemWorkspaceService>>(),
            sp.GetService<ISnapshotTreeResolver>(),
            sp.GetService<IRepositoryRegistryService>(),
            sp));

        // Phase 10 — Roslyn C# syntax validator for AstMergeStrategy.
        services.AddSingleton<ISourceValidator, RoslynSourceValidator>();

        // Phase 14 — Git import/export adapter.
        services.AddSingleton<IGitAdapter>(sp => new GitAdapter(
            sp.GetRequiredService<IRepositorySnapshotService>(),
            sp.GetRequiredService<IRepositoryOpService>(),
            sp.GetService<WorkspaceOptions>(),
            sp.GetService<IBlobStoreProvider>(),
            sp.GetService<IMaterializationEngine>()));
    }
}
