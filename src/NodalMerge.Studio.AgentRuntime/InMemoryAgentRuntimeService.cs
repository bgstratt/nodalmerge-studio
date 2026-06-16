using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

public sealed class InMemoryAgentRuntimeService : IAgentRuntimeService, ISnapshotService, IAgentControlService
{
    private readonly ConcurrentDictionary<(string AgentId, string WorkUnitId), ExecutionSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, AgentRecord> _agents = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InMemoryAgentRuntimeService> _logger;
    private readonly IAgentProfileService _profileService;

    public InMemoryAgentRuntimeService(
        IServiceProvider serviceProvider,
        ILogger<InMemoryAgentRuntimeService> logger,
        IAgentProfileService profileService)
    {
        _serviceProvider = serviceProvider;
        _logger          = logger;
        _profileService  = profileService;
    }

    private sealed record AgentRecord(
        string AgentId,
        string WorkUnitId,
        string Status,
        string? TaskId = null,
        string? Model = null,
        string? BaseUrl = null,
        string? ApiKey = null,
        string? Provider = null,
        CancellationTokenSource? Cts = null);

    public Task<ExecutionSnapshot> GetSnapshotAsync(
        string agentId,
        string workUnitId,
        CancellationToken cancellationToken = default)
    {
        _snapshots.TryGetValue((agentId, workUnitId), out var snapshot);
        snapshot ??= new ExecutionSnapshot(
            agentId,
            workUnitId,
            null, null, null,
            [], [], 0, 0, null);

        return Task.FromResult(snapshot);
    }

    public Task RecordActionAsync(
        string agentId,
        string workUnitId,
        string action,
        CancellationToken cancellationToken = default)
    {
        var key = (agentId, workUnitId);
        var current = _snapshots.GetOrAdd(
            key,
            _ => new ExecutionSnapshot(agentId, workUnitId, null, null, null, [], [], 0, 0, null));

        var actions = current.RecentActions.ToList();
        actions.Add(action);
        _snapshots[key] = current with { RecentActions = actions };
        return Task.CompletedTask;
    }

    Task<ExecutionSnapshot> ISnapshotService.GetAsync(
        string agentId,
        string workUnitId,
        CancellationToken cancellationToken) =>
        GetSnapshotAsync(agentId, workUnitId, cancellationToken);

    public Task<string> CompareAsync(
        string agentId,
        string workUnitId,
        string otherAgentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("[]");

    public Task<string> SpawnAsync(
        string agentType,
        string workUnitId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = $"{agentType}-{Guid.NewGuid():N}";

        AgentProfile? profile = profileId is not null
            ? _profileService.GetAsync(profileId, cancellationToken).GetAwaiter().GetResult()
            : null;

        CancellationTokenSource? cts = null;
        var resolvedProvider = provider ?? "anthropic";
        var canStartLoop = !string.IsNullOrWhiteSpace(baseUrl) && apiKey is not null
            && (!string.IsNullOrWhiteSpace(model)
                || resolvedProvider.Equals("openai", StringComparison.OrdinalIgnoreCase));
        _logger.LogInformation(
            "[Agent {AgentId}] Spawn — agentType={AgentType} provider={Provider} model={Model} baseUrl={BaseUrl} profileId={ProfileId} canStartLoop={CanStart}",
            agentId, agentType, resolvedProvider, model ?? "(none)", baseUrl ?? "(none)", profileId ?? "(none)", canStartLoop);
        if (!canStartLoop)
            _logger.LogWarning("[Agent {AgentId}] Loop will NOT start — missing credentials or model. baseUrl={BaseUrl} model={Model} provider={Provider}",
                agentId, baseUrl ?? "(none)", model ?? "(none)", resolvedProvider);
        if (canStartLoop)
        {
            cts = new CancellationTokenSource();
            var loopModel = model ?? string.Empty;
            if (agentType == "orchestrator")
                StartOrchestratorLoop(agentId, workUnitId, resolvedProvider, loopModel, baseUrl!, apiKey ?? string.Empty, profile, cts);
            else if (agentType == "worker" && taskId is not null)
                StartWorkerLoop(agentId, workUnitId, taskId, resolvedProvider, loopModel, baseUrl!, apiKey ?? string.Empty, profile, cts);
            else
                cts.Dispose();
        }

        _agents[agentId] = new AgentRecord(agentId, workUnitId, "active", taskId, model, baseUrl, apiKey, provider, cts);
        return Task.FromResult(agentId);
    }

    private void StartOrchestratorLoop(
        string agentId,
        string workUnitId,
        string provider,
        string model,
        string baseUrl,
        string apiKey,
        AgentProfile? profile,
        CancellationTokenSource cts)
    {
        _logger.LogInformation("[Agent {AgentId}] Starting orchestrator loop — provider={Provider} model={Model} baseUrl={BaseUrl}",
            agentId, provider, model, baseUrl);
        _ = Task.Run(async () =>
        {
            try
            {
                var dispatcher = _serviceProvider.GetRequiredService<McpToolDispatcher>();
                var llm = _serviceProvider.GetRequiredService<LlmClient>();
                var loop = new OrchestratorAgentLoop(
                    agentId, workUnitId, provider, model, baseUrl, apiKey, dispatcher, llm, profile);
                await loop.RunAsync(cts.Token).ConfigureAwait(false);
                _logger.LogInformation("[Agent {AgentId}] Orchestrator loop completed.", agentId);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agent {AgentId}] Orchestrator loop failed.", agentId);
                if (_agents.TryGetValue(agentId, out var r))
                {
                    var msg = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
                    _agents[agentId] = r with { Status = $"failed:{msg}", Cts = null };
                }
            }
            finally
            {
                if (_agents.TryGetValue(agentId, out var r) && r.Status == "active")
                    _agents[agentId] = r with { Status = "stopped", Cts = null };
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private void StartWorkerLoop(
        string agentId,
        string workUnitId,
        string taskId,
        string provider,
        string model,
        string baseUrl,
        string apiKey,
        AgentProfile? profile,
        CancellationTokenSource cts)
    {
        _logger.LogInformation("[Agent {AgentId}] Starting worker loop — provider={Provider} model={Model} taskId={TaskId}",
            agentId, provider, model, taskId);
        _ = Task.Run(async () =>
        {
            try
            {
                var dispatcher = _serviceProvider.GetRequiredService<McpToolDispatcher>();
                var llm = _serviceProvider.GetRequiredService<LlmClient>();
                var loop = new WorkerAgentLoop(
                    agentId, workUnitId, taskId, provider, model, baseUrl, apiKey, dispatcher, llm, profile);
                await loop.RunAsync(cts.Token).ConfigureAwait(false);
                _logger.LogInformation("[Agent {AgentId}] Worker loop completed.", agentId);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agent {AgentId}] Worker loop failed.", agentId);
                if (_agents.TryGetValue(agentId, out var r))
                {
                    var msg = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
                    _agents[agentId] = r with { Status = $"failed:{msg}", Cts = null };
                }
            }
            finally
            {
                if (_agents.TryGetValue(agentId, out var r) && r.Status == "active")
                    _agents[agentId] = r with { Status = "stopped", Cts = null };
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    public Task PauseAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var current = GetRequired(agentId);
        _agents[agentId] = current with { Status = "paused" };
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var current = GetRequired(agentId);
        _agents[agentId] = current with { Status = "active" };
        return Task.CompletedTask;
    }

    public Task StopAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var current = GetRequired(agentId);
        current.Cts?.Cancel();
        _agents[agentId] = current with { Status = "stopped", Cts = null };
        return Task.CompletedTask;
    }

    public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default)
    {
        _agents.TryGetValue(agentId, out var record);
        return Task.FromResult(record?.Status ?? "unknown");
    }

    public Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var active = _agents.Values
            .Where(a => a.Status == "active")
            .Select(a => new AgentInfo(a.AgentId, a.WorkUnitId, a.Status))
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentInfo>>(active);
    }

    public Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var all = _agents.Values
            .Select(a => new AgentInfo(a.AgentId, a.WorkUnitId, a.Status))
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentInfo>>(all);
    }

    private AgentRecord GetRequired(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var record))
            throw new KeyNotFoundException($"Agent '{agentId}' was not found.");
        return record;
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioAgentRuntime(this IServiceCollection services, HttpClient? llmHttpClient = null)
    {
        services.AddSingleton<InMemoryAgentRuntimeService>();
        services.AddSingleton<IAgentRuntimeService>(sp => sp.GetRequiredService<InMemoryAgentRuntimeService>());
        services.AddSingleton<ISnapshotService>(sp => sp.GetRequiredService<InMemoryAgentRuntimeService>());
        services.AddSingleton<IAgentControlService>(sp => sp.GetRequiredService<InMemoryAgentRuntimeService>());
        services.AddSingleton<McpToolDispatcher>();
        services.AddSingleton<LlmClient>(_ => new LlmClient(llmHttpClient ?? new HttpClient()));
        return services;
    }
}
