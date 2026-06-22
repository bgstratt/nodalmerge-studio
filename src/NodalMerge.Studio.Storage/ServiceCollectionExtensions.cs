using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNodalMergeStorage(this IServiceCollection services)
    {
        services.AddSingleton<IStudioNodeStore, NodalMergeStudioNodeStore>();
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
        services.AddSingleton<IStudioNodeStore, InMemoryStudioNodeStore>();
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

        services.AddSingleton<InMemoryDeadLetterService>();
        services.AddSingleton<IDeadLetterService>(sp => sp.GetRequiredService<InMemoryDeadLetterService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryDeadLetterService>());

        services.AddSingleton<WorkSchedulerService>();
        services.AddSingleton<IWorkScheduler>(sp => sp.GetRequiredService<WorkSchedulerService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<WorkSchedulerService>());

        // Slice 15f — shared command services that every transport (MCP/REST/dispatcher) calls.
        services.AddSingleton<ISchedulerCommandService, SchedulerCommandService>();
        services.AddSingleton<IArtifactCommandService, ArtifactCommandService>();

        // Slice 16b/16c — workspace execution services
        services.AddSingleton<IWorkspaceExecutionService, WorkspaceExecutionService>();
        services.AddSingleton<IWorkspaceExecutionCommandService, WorkspaceExecutionCommandService>();

        // Phase 9a — sub-project root detection. No persistence/rehydration: a WorkspaceProfile is
        // cheap to recompute and branch directories are recreated identically on InitBranchAsync,
        // so a cold Host just re-detects lazily on first access.
        services.AddSingleton<IWorkspaceProfileService, WorkspaceProfileService>();

        // Phase 9c — tracks long-running "run" processes (dev servers). Deliberately not durable:
        // a Host restart kills anything it started, and there's nothing meaningful to resume a
        // dev server into.
        services.AddSingleton<RunningProcessRegistry>();

        // Phase 6.7b — decision-centric persistent node services
        services.AddSingleton<GoalNodeService>();
        services.AddSingleton<IGoalNodeService>(sp => sp.GetRequiredService<GoalNodeService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<GoalNodeService>());

        services.AddSingleton<DecisionNodeService>();
        services.AddSingleton<IDecisionNodeService>(sp => sp.GetRequiredService<DecisionNodeService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<DecisionNodeService>());

        services.AddSingleton<SteeringDecisionService>();
        services.AddSingleton<ISteeringDecisionService>(sp => sp.GetRequiredService<SteeringDecisionService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<SteeringDecisionService>());

        services.AddSingleton<EvidenceNodeService>();
        services.AddSingleton<IEvidenceNodeService>(sp => sp.GetRequiredService<EvidenceNodeService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<EvidenceNodeService>());

        services.AddSingleton<FindingService>();
        services.AddSingleton<IFindingService>(sp => sp.GetRequiredService<FindingService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<FindingService>());

        services.AddSingleton<ExecutionEventStreamService>();
        services.AddSingleton<IExecutionEventStream>(sp => sp.GetRequiredService<ExecutionEventStreamService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<ExecutionEventStreamService>());

        // No domain interface to forward — RuntimeSettingsService is only ever consumed
        // directly (by /studio/options) for its PersistAsync side effect, not through an
        // abstraction.
        services.AddSingleton<RuntimeSettingsService>();
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<RuntimeSettingsService>());

        // Slice 21a — must run after RuntimeSettingsService so UsePromotionBranch is already
        // restored before we try to create the candidate branch.
        services.AddSingleton<CandidateBranchService>();
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<CandidateBranchService>());

        // Registered last and ahead of AddStudioAgentRuntime in AddStudioServices, so its
        // StartAsync (which awaits every IRehydratable above) completes before the scheduler
        // poll loop's StartAsync begins.
        services.AddSingleton<IHostedService, StudioStateRehydrationService>();
    }

    // Slice 14a — no state to rehydrate (rules are resolved fresh from DI each time), so this
    // doesn't go through AddRehydratableServices. Ships with zero IPolicyRule registrations;
    // EvaluateAsync is a no-op (Allowed = true) against every checkpoint until a slice like 14b
    // registers one.
    private static void AddPolicyGate(IServiceCollection services)
    {
        services.AddSingleton<IPolicyGateService, PolicyGateService>();

        // Slice 14b — the first real rule. Always registered; gated by
        // WorkspaceOptions.BlockOverlappingFileScope (default false) inside the rule itself.
        services.AddSingleton<IPolicyRule, NonOverlappingFileScopeRule>();

        // Slice 16f — opt-in execution rule; gated by RequireBuildBeforeProposal/RequireTestBeforeProposal.
        services.AddSingleton<IPolicyRule, WorkspaceExecutionRule>();
    }

    private static void AddFileWorkspaceService(IServiceCollection services)
    {
        services.AddSingleton<IFileWorkspaceService>(sp =>
            new FileSystemWorkspaceService(sp.GetService<WorkspaceOptions>() ?? new WorkspaceOptions()));
    }
}
